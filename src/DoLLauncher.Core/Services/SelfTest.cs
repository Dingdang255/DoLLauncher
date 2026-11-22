using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DoLLauncher.Core.Data;
using DoLLauncher.Core.Skin;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>开发期自检：验证版本扫描、内置服务器、modList、存档助手页、.dolpack 往返。</summary>
public static class SelfTest
{
    private static readonly List<string> Lines = new();

    private static void W(string line) => Lines.Add(line);

    public static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(), "dol-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(temp);
            W("== DoL 启动器自检 ==");
            W("根目录：" + Paths.Root);
            var versions = TestScan();
            TestServer(versions);
            TestModsAndPack(temp);
            TestSkin();
            TestSlotStore(temp);
            TestConflicts();
            TestProblems();
            TestDuplicates();
            W("SELFTEST-PASS");
        }
        catch (Exception ex)
        {
            W("SELFTEST-FAIL: " + ex);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(Paths.Root, "self-test.log"), string.Join(Environment.NewLine, Lines),
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }

    private static List<VersionInfo> TestScan()
    {
        var versions = VersionService.Scan();
        W($"版本数量：{versions.Count}");
        foreach (var v in versions)
        {
            W($"  {v.Folder} | mode={v.Mode} | port={v.Port} | 游戏版本={v.GameVersion} | html={Path.GetFileName(v.HtmlPath)}");
        }
        if (versions.Count == 0) throw new InvalidOperationException("没有扫描到任何版本");
        return versions;
    }

    private static void TestServer(List<VersionInfo> versions)
    {
        var version = versions.FirstOrDefault(v => !v.IsFileMode)
                      ?? throw new InvalidOperationException("没有可测试的 http 版本");

        var port = ServerManager.FindFreePort(8799);
        var server = new StaticServer(version.FullPath, port, version.HtmlFileName, version.DisplayName, version.Folder);
        server.Start();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var url = server.GameUrl;

            var head = client.Send(new HttpRequestMessage(HttpMethod.Head, url));
            W($"HEAD / -> {head.StatusCode}，Content-Length={head.Content.Headers.ContentLength}");

            var modList = client.GetStringAsync(url + "modList.json").Result;
            W("GET /modList.json -> " + modList.Replace(Environment.NewLine, " ").Replace("\n", " "));

            var helper = client.GetStringAsync(url + "__launcher/saves.html").Result;
            W($"GET /__launcher/saves.html -> 长度 {helper.Length}，含 idb={helper.Contains("'idb'")}，含 saves 表={helper.Contains("'saves'")}");

            var mods = ModService.Load(version.FullPath);
            if (mods.Count > 0)
            {
                var request = new HttpRequestMessage(HttpMethod.Get,
                    url + "mods/" + Uri.EscapeDataString(mods[0].FileName));
                request.Headers.Range = new RangeHeaderValue(0, 1023);
                var response = client.Send(request);
                W($"Range 请求 {mods[0].FileName} -> {response.StatusCode}，Content-Range={response.Content.Headers.ContentRange}");
            }

            var encoded = client.Send(new HttpRequestMessage(HttpMethod.Head,
                url + Uri.EscapeDataString(version.HtmlFileName)));
            W($"HEAD 编码路径 {Uri.EscapeDataString(version.HtmlFileName)} -> {encoded.StatusCode}");

            var missing = client.Send(new HttpRequestMessage(HttpMethod.Get, url + "no-such-file.txt"));
            W($"GET 不存在的文件 -> {missing.StatusCode}");

            // 存档备份接口（列表 / 写入 / 读取 / 删除）
            var listJson = client.GetStringAsync(url + "__launcher/api/backup-list").Result;
            W("GET backup-list -> " + listJson.Replace("\n", " ").Replace("\r", " "));
            var payload = "{\"format\":\"dol-saves/1\",\"dbs\":[]}";
            var post = client.PostAsync(url + "__launcher/api/backup?file=saves-selftest.json&auto=0",
                new StringContent(payload, Encoding.UTF8, "text/plain")).Result;
            W($"POST backup -> {post.StatusCode} {post.Content.ReadAsStringAsync().Result}");
            var readBack = client.GetStringAsync(url + "__launcher/api/backup?file=saves-selftest.json").Result;
            W("GET backup -> " + readBack);
            var deleted = client.PostAsync(url + "__launcher/api/backup-delete?file=saves-selftest.json",
                new StringContent("")).Result;
            W($"POST backup-delete -> {deleted.StatusCode} {deleted.Content.ReadAsStringAsync().Result}");

            var traversal = client.Send(new HttpRequestMessage(HttpMethod.Get, url + "../launcher.ps1"));
            W($"GET 路径穿越 -> {traversal.StatusCode}");

            // 游戏页面注入：HEAD 的长度应该比本体大（多出日志采集脚本）
            var bodyLength = new FileInfo(version.HtmlPath).Length;
            var injectedLength = head.Content.Headers.ContentLength ?? 0;
            W($"页面注入检查：HEAD={injectedLength}，本体={bodyLength}，已注入={injectedLength > bodyLength}");

            // 游戏日志接口：POST 后应写入版本目录 logs\game-会话时间.log
            var sid = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var logPost = client.PostAsync(url + "__launcher/api/game-log?sid=" + sid,
                new StringContent("selftest game log line", Encoding.UTF8, "text/plain")).Result;
            var logFile = GameLogService.FileFor(version.FullPath, sid);
            var logged = File.Exists(logFile) && File.ReadAllText(logFile).Contains("selftest game log line");
            W($"POST game-log -> {logPost.StatusCode}，已落盘={logged}（{logFile}）");
            if (File.Exists(logFile)) File.Delete(logFile);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>槽位存档：写入 / 读回 / 清单 / 摘要 / 删除 / 只保留存档库 / 旧镜像迁移 / 汇总缓存。</summary>
    private static void TestSlotStore(string temp)
    {
        try
        {
            var dir = Path.Combine(temp, "slots");
            var save = "{\"slot\":1,\"data\":{\"history\":[]}}";
            var details = "{\"slot\":1,\"data\":{\"date\":123,\"title\":\"t\"}}";
            SlotSaveStore.WriteSlot(dir, 1, save, details, "degrees-of-lewdity");

            var read = SlotSaveStore.TryReadSlot(dir, 1, out var saveText, out var detailsText);
            var manifest = SlotSaveStore.ReadManifest(dir);
            var describe = SlotSaveStore.Describe(dir);
            W($"槽位存储：写入后读取={read}，清单槽位={manifest.Slots.Count}，" +
              $"存档长度={saveText?.Length ?? 0}，详情长度={detailsText?.Length ?? 0}，摘要={describe}");

            var deleted = SlotSaveStore.DeleteSlot(dir, 1);
            var afterDelete = SlotSaveStore.TryReadSlot(dir, 1, out _, out _);
            W($"槽位存储：删除={deleted}，删除后仍可读={afterDelete}");

            const string payload =
                "{\"format\":\"dol-saves/1\",\"exportedAt\":\"2026-01-01T00:00:00Z\",\"dbs\":[" +
                "{\"name\":\"degrees-of-lewdity\",\"version\":1,\"stores\":[" +
                "{\"name\":\"saves\",\"records\":[{\"key\":1,\"value\":{\"slot\":1,\"data\":{\"history\":[]}}}]}," +
                "{\"name\":\"details\",\"records\":[{\"key\":1,\"value\":{\"slot\":1,\"data\":{\"date\":123}}}]}]}," +
                "{\"name\":\"i18n-cache\",\"version\":1,\"stores\":[{\"name\":\"entries\",\"records\":[]}]}]}";
            var filtered = SlotSaveStore.FilterPayloadToSaveDomain(payload, out var db);
            var onlySaveDb = filtered != null &&
                             filtered.Contains("degrees-of-lewdity", StringComparison.Ordinal) &&
                             !filtered.Contains("i18n-cache", StringComparison.Ordinal);
            W($"槽位存储：裁剪后只含存档库={onlySaveDb}，库名={db}，长度={filtered?.Length ?? 0}");

            if (!read || manifest.Slots.Count != 1 || string.IsNullOrWhiteSpace(describe) ||
                !deleted || afterDelete || !onlySaveDb)
                throw new InvalidOperationException("槽位存储自检结果不符合预期");
        }
        catch (Exception ex)
        {
            W("槽位存储自检失败：" + ex.Message);
        }

        // 旧镜像迁移：合成 dol-saves/1 应拆成槽位文件，旧镜像与 legacy 文件均被删除
        try
        {
            var legacyDir = Path.Combine(temp, "legacy");
            var dataDir = Path.Combine(legacyDir, "data");
            Directory.CreateDirectory(dataDir);
            const string legacyPayload =
                "{\"format\":\"dol-saves/1\",\"exportedAt\":\"2026-01-01T00:00:00Z\",\"dbs\":[" +
                "{\"name\":\"degrees-of-lewdity\",\"version\":1,\"stores\":[" +
                "{\"name\":\"saves\",\"records\":[{\"key\":2,\"value\":{\"slot\":2,\"data\":{\"history\":[]}}}]}," +
                "{\"name\":\"details\",\"records\":[{\"key\":2,\"value\":{\"slot\":2,\"data\":{\"date\":123,\"title\":\"t\"}}}]}]}," +
                "{\"name\":\"i18n-cache\",\"version\":1,\"stores\":[{\"name\":\"entries\",\"records\":[]}]}]}";
            File.WriteAllText(Path.Combine(dataDir, "idb-mirror.json"), legacyPayload, new UTF8Encoding(false));

            var migrated = SlotSaveStore.MigrateLegacyMirror(legacyDir, out var migrateMsg);
            var migratedSlots = SlotSaveStore.ReadManifest(legacyDir).Slots.Count;
            var mirrorGone = !File.Exists(Path.Combine(dataDir, "idb-mirror.json"));
            var legacyGone = !File.Exists(Path.Combine(dataDir, "idb-mirror.legacy.json"));
            W($"旧镜像迁移：槽位={migrated}，清单槽位={migratedSlots}，" +
              $"原镜像已删除={mirrorGone}，legacy已删除={legacyGone}，说明={migrateMsg}");

            if (migrated != 1 || migratedSlots != 1 || !mirrorGone || !legacyGone ||
                string.IsNullOrWhiteSpace(migrateMsg))
                throw new InvalidOperationException("旧镜像迁移结果不符合预期");
        }
        catch (Exception ex)
        {
            W("旧镜像迁移自检失败：" + ex.Message);
        }

        // 汇总缓存：只含存档库的 dol-saves/1，体积与槽位文件同量级
        try
        {
            var outDir = Path.Combine(temp, "aggregate");
            const string slotSave = "{\"slot\":3,\"data\":{\"history\":[{\"title\":\"a\"}]}}";
            const string slotDetails = "{\"slot\":3,\"data\":{\"date\":123,\"title\":\"t\"}}";
            SlotSaveStore.WriteSlot(outDir, 3, slotSave, slotDetails, "degrees-of-lewdity");

            var cache = SlotSaveStore.BuildAggregateCache(outDir, "degrees-of-lewdity");
            var exists = !string.IsNullOrWhiteSpace(cache) && File.Exists(cache);
            var text = exists ? File.ReadAllText(cache!) : "";
            var cacheBytes = exists ? new FileInfo(cache!).Length : 0;
            var slotBytes = new FileInfo(SlotSaveStore.SlotFile(outDir, 3)).Length +
                            new FileInfo(SlotSaveStore.DetailsFile(outDir, 3)).Length;
            var sameScale = exists && cacheBytes >= slotBytes && cacheBytes <= slotBytes * 10 + 4096;
            var hasSaveDb = text.Contains("degrees-of-lewdity", StringComparison.Ordinal);
            var noI18n = !text.Contains("i18n-cache", StringComparison.Ordinal);
            W($"汇总缓存：路径存在={exists}，含存档库={hasSaveDb}，不含i18n={noI18n}，" +
              $"大小={cacheBytes}，槽位文件={slotBytes}，同量级={sameScale}");

            if (!exists || !hasSaveDb || !noI18n || !sameScale)
                throw new InvalidOperationException("汇总缓存自检结果不符合预期");
        }
        catch (Exception ex)
        {
            W("汇总缓存自检失败：" + ex.Message);
        }
    }

    /// <summary>简介里的「与简易框架冲突」应识别成警告，且停用一方后消失。</summary>
    private static void TestConflicts()
    {
        var catalog = new MarketCatalog();
        catalog.Mods.Add(new MarketMod
        {
            Id = "maplebirch",
            Name = "秋枫白桦框架",
            Description = "与简易框架冲突，但兼容通过简易框架addto功能与时间事件功能追加内容的模组。",
        });
        catalog.Mods.Add(new MarketMod
        {
            Id = "simple-framework",
            Name = "简易框架",
            BootNames = new List<string> { "Simple Frameworks" },
        });

        var mods = new List<ModInfo>
        {
            new() { Name = "秋枫白桦框架", FileName = "maplebirch.zip", Enabled = true },
            new() { Name = "Simple Frameworks 2.0.5", FileName = "simple.zip", Enabled = true },
        };

        var conflicts = ModConflictService.CheckAsync(mods, catalog).GetAwaiter().GetResult();
        W($"冲突检测：{conflicts.Count} 组");
        foreach (var conflict in conflicts)
            W($"  {conflict.Title}（{conflict.PairKey}）← {conflict.SourceText}：{conflict.Reason}");

        var installWarnings = ModConflictService.CheckAgainstAsync(catalog.Mods[0], mods, catalog)
            .GetAwaiter().GetResult();
        W($"安装前冲突警告：{installWarnings.Count} 条");

        // 反向：装「简易框架」时，冲突声明写在已装的秋枫白桦框架简介里
        var reverseWarnings = ModConflictService.CheckAgainstAsync(catalog.Mods[1], mods, catalog)
            .GetAwaiter().GetResult();
        W($"安装前置时的反向冲突警告：{reverseWarnings.Count} 条");
        foreach (var warning in reverseWarnings)
            W($"  {warning.Title}（{warning.PairKey}）← {warning.SourceText}：{warning.Reason}");

        mods[1].Enabled = false;
        var afterDisable = ModConflictService.CheckAsync(mods, catalog).GetAwaiter().GetResult();
        W($"停用一方后冲突：{afterDisable.Count} 组");

        if (conflicts.Count == 0 || installWarnings.Count == 0 || reverseWarnings.Count == 0 ||
            afterDisable.Count != 0)
            throw new InvalidOperationException("冲突检测结果不符合预期（应检测到 1 组，停用后为 0 组）");
    }

    private static void TestProblems()
    {
        var older = new ModInfo
        {
            FileName = "m-old.zip", Name = "Old Mod", Version = "1.0.0", Enabled = true,
            Dependencies = { new ModDependency { Name = "GameVersion", Version = ">=0.5.12.13" } },
        };
        var orange = ProblemService.VersionMismatches(new[] { older }, "0.5.11.9", 1);
        W($"版本差异（0.5.12 vs 0.5.11，阈值 1）：{orange.Count} 条 / {orange.FirstOrDefault()?.Severity}");

        var red = ProblemService.VersionMismatches(new[] { older }, "0.5.10.9", 1);
        W($"版本差异（0.5.12 vs 0.5.10，阈值 1）：{red.Count} 条 / {red.FirstOrDefault()?.Severity}");

        var threshold2 = ProblemService.VersionMismatches(new[] { older }, "0.5.10.9", 2);
        W($"版本差异（0.5.12 vs 0.5.10，阈值 2）：{threshold2.Count} 条 / {threshold2.FirstOrDefault()?.Severity}");

        var depMod = new ModInfo
        {
            FileName = "dep.zip", Name = "Needs Frontier", Enabled = true,
            Dependencies = { new ModDependency { Name = "Missing Frameworks", Version = ">=1.0.0" } },
        };
        var disabledPrereq = new ModInfo
        {
            FileName = "pre.zip", Name = "Off Framework", Version = "1.0.0", Enabled = false,
        };
        var missing = ProblemService.MissingDependencies(new[] { depMod, disabledPrereq });
        W($"缺少前置：{missing.Count} 条");
        foreach (var issue in missing) W($"  {issue.Title}：{issue.Detail}");

        if (orange.Count != 1 || orange[0].Severity != ProblemSeverity.Orange ||
            red.Count != 1 || red[0].Severity != ProblemSeverity.Red ||
            threshold2.Count != 1 || threshold2[0].Severity != ProblemSeverity.Orange ||
            missing.Count != 1)
            throw new InvalidOperationException("问题检查结果不符合预期");
    }

    private static void TestDuplicates()
    {
        var newest = new ModInfo
        {
            FileName = "dup-b.zip", Name = "Same Mod", Version = "2.0.0", Enabled = true,
        };
        var older = new ModInfo
        {
            FileName = "dup-a.zip", Name = "same-mod!", Version = "1.5.0", Enabled = true,
        };
        var oldest = new ModInfo
        {
            FileName = "dup-c.zip", Name = "SAME MOD", Version = "1.0.0", Enabled = true,
        };
        var disabled = new ModInfo
        {
            FileName = "dup-d.zip", Name = "Same Mod", Version = "3.0.0", Enabled = false,
        };
        var single = new ModInfo
        {
            FileName = "solo.zip", Name = "Solo Mod", Version = "1.0.0", Enabled = true,
        };

        var dups = ProblemService.Duplicates(new[] { newest, older, oldest, disabled, single });
        W($"重复模组：{dups.Count} 组");
        foreach (var dup in dups)
        {
            W($"  {dup.Title} → 最新 {dup.NewestFileName}，成员 " +
              string.Join("、", dup.Members.Select(m => $"{m.FileName}({m.Version})")));
        }

        if (dups.Count != 1 || dups[0].Members.Count != 3 ||
            dups[0].NewestFileName != "dup-b.zip" || dups[0].Members[0].FileName != "dup-b.zip" ||
            dups[0].Members[0].IsNewest == false ||
            dups[0].Members.Count(m => m.IsNewest) != 1)
            throw new InvalidOperationException("重复模组检测结果不符合预期");

        var general = new GeneralSettings();
        if (!general.ShowAnimations || general.ThemeMode != "system")
            throw new InvalidOperationException("界面设置默认值不符合预期");
        W($"界面默认值：动画={general.ShowAnimations}，主题={general.ThemeMode}");
    }

    /// <summary>自检用的脚本宿主：记录动作调用，数据表由测试填入。</summary>
    private sealed class ScriptHostProbe : ISkinScriptHost
    {
        public Dictionary<string, SkinScriptValue> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Actions { get; } = new();

        public SkinScriptValue ResolveData(string path)
            => Data.TryGetValue(path, out var value) ? value : SkinScriptValue.Null;

        public bool InvokeAction(string action, IReadOnlyList<SkinScriptValue> args)
        {
            Actions.Add(action.ToLowerInvariant());
            return true;
        }
    }

    /// <summary>美化包 Core 层：关键帧、状态伪类、动画/过渡/变换、dolskin/3 清单校验与旧包兼容。</summary>
    private static void TestSkin()
    {
        var failures = new List<string>();
        void Check(string name, bool ok, string detail)
        {
            W($"美化包 {name}：{detail}");
            if (!ok) failures.Add(name);
        }

        // 示例皮肤优先从仓库 docs 读取；发布目录里没有 docs 时退回等价内联内容
        var cssPath = FindSampleFile("samples/lantern-skin/skin.css");
        var sheet = SkinStyleSheet.Parse(cssPath != null ? File.ReadAllText(cssPath) : LanternCssFallback);
        var breathe = sheet.Keyframes.TryGetValue("lantern-breathe", out var breatheFrames)
            ? breatheFrames
            : new List<SkinKeyframe>();
        var offsets = breathe.Select(f => f.Offset).ToArray();
        var framesOk = offsets.Length == 3
                       && Math.Abs(offsets[0]) < 1e-6
                       && Math.Abs(offsets[1] - 0.5) < 1e-6
                       && Math.Abs(offsets[2] - 1) < 1e-6;
        Check("关键帧", framesOk,
            $"来源={(cssPath == null ? "内联" : Path.GetFileName(cssPath))}，lantern-breathe 帧数={offsets.Length}，" +
            $"偏移={string.Join("/", offsets)}，告警={sheet.Warnings.Count}");

        const string stateCss =
            "Button:hover { background: #1f35e8d5; }\n" +
            ".PrimaryButton:pressed { transform: scale(.96); }\n" +
            ".Card:disabled { opacity: .5; }\n";
        var stateSheet = SkinStyleSheet.Parse(stateCss);
        bool HasState(string source, string state) => stateSheet.Rules.Any(rule => rule.Source == source
            && rule.Selectors.Count == 1
            && rule.Selectors[0].States.SequenceEqual(new[] { state }));
        var hoverOk = HasState("Button:hover", "hover");
        var pressedOk = HasState(".PrimaryButton:pressed", "pressed");
        var disabledOk = HasState(".Card:disabled", "disabled");
        Check("状态伪类", hoverOk && pressedOk && disabledOk,
            $"hover={hoverOk}，pressed={pressedOk}，disabled={disabledOk}，规则={stateSheet.Rules.Count}");

        const string darkCss =
            "@media (prefers-color-scheme: dark) {\n" +
            "  @keyframes dark-pulse { from { opacity: 0; } to { opacity: 1; } }\n" +
            "  .Card { background: #ff0b1220; }\n" +
            "}\n";
        var darkSheet = SkinStyleSheet.Parse(darkCss);
        var darkFrames = darkSheet.Keyframes.TryGetValue("dark-pulse", out var darkPulse)
            ? darkPulse
            : new List<SkinKeyframe>();
        var darkRules = darkSheet.Rules.Count(rule => rule.RequiresDark == true);
        Check("深色媒体查询", darkFrames.Count == 2 && darkRules > 0,
            $"dark-pulse 帧数={darkFrames.Count}，深色规则={darkRules}");

        var animationOk = SkinAnimationSpec.TryParse("breathe 3.2s ease-in-out infinite", out var animation)
                          && animation.Name == "breathe"
                          && Math.Abs(animation.DurationMs - 3200) < 1e-6
                          && animation.TimingFunction == "ease-in-out"
                          && double.IsPositiveInfinity(animation.IterationCount);
        Check("animation", animationOk,
            $"name={animation.Name}，时长={animation.DurationMs}ms，缓动={animation.TimingFunction}，" +
            $"循环={animation.IterationCount}");

        var transitionOk = SkinTransitionSpec.TryParse("background 180ms ease-out", out var transition)
                           && transition.Properties.Contains("background")
                           && Math.Abs(transition.DurationMs - 180) < 1e-6
                           && transition.TimingFunction == "ease-out";
        Check("transition", transitionOk,
            $"属性={string.Join("/", transition.Properties)}，时长={transition.DurationMs}ms，" +
            $"缓动={transition.TimingFunction}");

        var transformOk = SkinTransformSpec.TryParse("translate(4px,-2px) rotate(6deg) scale(1.05)", out var transform)
                          && transform.Operations.Count == 3
                          && transform.Operations[0].Kind == SkinTransformKind.Translate
                          && transform.Operations[1].Kind == SkinTransformKind.Rotate
                          && transform.Operations[2].Kind == SkinTransformKind.Scale;
        Check("transform", transformOk,
            $"操作数={transform.Operations.Count}，类型={string.Join("/", transform.Operations.Select(op => op.Kind))}");

        var manifestPath = FindSampleFile("samples/lantern-skin/skin.json");
        var manifest = JsonSerializer.Deserialize<SkinManifest>(
            manifestPath != null ? File.ReadAllText(manifestPath) : LanternManifestFallback) ?? new SkinManifest();
        var manifestOk = manifest.Themes.Count == 2
                         && manifest.DefaultTheme == "cyberpunk"
                         && manifest.Effects != null
                         && manifest.Effects.Ambient.Particles == 24
                         && manifest.Settings.Count == 4;
        Check("清单", manifestOk,
            $"来源={(manifestPath == null ? "内联" : Path.GetFileName(manifestPath))}，主题={manifest.Themes.Count}，" +
            $"defaultTheme={manifest.DefaultTheme}，粒子={manifest.Effects?.Ambient.Particles}，" +
            $"设置项={manifest.Settings.Count}");

        var firstComponent = manifest.Components.FirstOrDefault();
        // 示例包除角标外还演示了 Image 组件，这里只要求首个组件仍为角标
        var componentsOk = manifest.Components.Count >= 1
                           && firstComponent != null
                           && firstComponent.Widget == "Badge"
                           && firstComponent.Host == "#LaunchBtn"
                           && firstComponent.Slot == "after"
                           && firstComponent.Class == "skin-badge";
        Check("组件清单", componentsOk,
            $"组件={manifest.Components.Count}，widget={firstComponent?.Widget}，host={firstComponent?.Host}，" +
            $"slot={firstComponent?.Slot}，class={firstComponent?.Class}");

        var validComponentManifest = new SkinManifest { Format = "dolskin/3", Name = "组件合法测试" };
        validComponentManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "ok-badge",
            Host = "#LaunchBtn",
            Slot = "after",
            Widget = "Badge",
            Class = "skin-badge",
            Props = { ["text"] = "灯台" },
        });
        var validComponentWarnings = SkinRules.CollectWarnings(validComponentManifest);
        Check("组件合法", validComponentWarnings.Count == 0,
            $"告警={validComponentWarnings.Count}" + (validComponentWarnings.Count > 0
                ? "：" + string.Join("；", validComponentWarnings)
                : string.Empty));

        var badComponentManifest = new SkinManifest { Format = "dolskin/3", Name = "组件告警测试" };
        badComponentManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "bad-widget", Host = "#LaunchBtn", Slot = "after", Widget = "Marquee",
        });
        badComponentManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "bad-slot", Host = "#LaunchBtn", Slot = "middle", Widget = "Label",
        });
        badComponentManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "dup-component", Host = "#LaunchBtn", Slot = "before", Widget = "Label",
        });
        badComponentManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "dup-component", Host = "#LaunchBtn", Slot = "end", Widget = "Label",
        });
        var deepComponent = new SkinComponentDefinition
        {
            Id = "deep-1", Host = "StackPanel", Slot = "end", Widget = "Card",
        };
        var deepest = deepComponent;
        for (var level = 2; level <= 5; level++)
        {
            var child = new SkinComponentDefinition
            {
                Id = "deep-" + level, Host = "StackPanel", Widget = "Card",
            };
            deepest.Children.Add(child);
            deepest = child;
        }
        badComponentManifest.Components.Add(deepComponent);

        var componentWarnings = SkinRules.CollectWarnings(badComponentManifest);
        bool HasComponentWarning(string keyword) =>
            componentWarnings.Any(warning => warning.Contains(keyword, StringComparison.Ordinal));
        var componentWarningOk = HasComponentWarning("widget") && HasComponentWarning("slot")
                                 && HasComponentWarning("重复") && HasComponentWarning("嵌套");
        Check("组件告警", componentWarningOk,
            $"widget={HasComponentWarning("widget")}，slot={HasComponentWarning("slot")}，" +
            $"重复={HasComponentWarning("重复")}，嵌套={HasComponentWarning("嵌套")}，" +
            $"共 {componentWarnings.Count} 条");
        foreach (var warning in componentWarnings) W("  告警：" + warning);

        // 轻量脚本：let / if / 白名单动作 / 数据读取
        const string scriptSource =
            "let n = version.count; " +
            "if (mods.enabled > 0 && n >= 3) { setParticles(clamp(n, 0, 24)); } else { toast('模组不足'); }";
        var scriptOk = SkinScriptEngine.TryParse(scriptSource, out var script, out var scriptError);
        var probe = new ScriptHostProbe();
        probe.Data["version.count"] = SkinScriptValue.From(5d);
        probe.Data["mods.enabled"] = SkinScriptValue.From(2d);
        var scriptRun = scriptOk && script.Run(probe) && probe.Actions.SequenceEqual(new[] { "setparticles" });
        Check("脚本", scriptRun,
            $"解析={scriptOk}，原因={(scriptOk ? "无" : scriptError)}，动作={string.Join("/", probe.Actions)}");

        var scriptRejected = !SkinScriptEngine.TryParse("while (true) { }", out _, out _)
                             && !SkinScriptEngine.TryParse("for (;;) { }", out _, out _)
                             && !SkinScriptEngine.TryParse("network.get('x');", out _, out _);
        Check("脚本限制", scriptRejected, $"拒绝 while/for/未白名单调用={scriptRejected}");

        var bindingOk = SkinBindingTemplate.TryParse("版本 {{ version.count }} 个（{{ version.name }}）",
                             out var binding, out var bindingError)
                         && binding.Render(probe) == "版本 5 个（）";
        Check("数据绑定", bindingOk,
            $"解析={binding.IsEmpty == false}，原因={(bindingError.Length == 0 ? "无" : bindingError)}，" +
            $"渲染={(bindingOk ? "版本 5 个（）" : "失败")}");

        var eventManifest = new SkinManifest { Format = "dolskin/3", Name = "事件绑定测试" };
        eventManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "theme-switch",
            Host = "#LaunchBtn",
            Slot = "after",
            Widget = "Button",
            Props = { ["text"] = "切换主题" },
            Events = { ["click"] = "setTheme('cyberpunk');" },
            Bind = { ["text"] = "版本 {{ version.count }}" },
        });
        var eventWarnings = SkinRules.CollectWarnings(eventManifest);
        Check("事件绑定合法", eventWarnings.Count == 0,
            $"告警={eventWarnings.Count}" + (eventWarnings.Count > 0
                ? "：" + string.Join("；", eventWarnings)
                : string.Empty));

        var badEventManifest = new SkinManifest { Format = "dolskin/3", Name = "事件绑定告警测试" };
        badEventManifest.Components.Add(new SkinComponentDefinition
        {
            Id = "bad-event",
            Host = "#LaunchBtn",
            Widget = "Button",
            Events = { ["tap"] = "toast('x');", ["click"] = "if (" },
            Bind = { ["style"] = "{{ version.count }}" },
        });
        var badEventWarnings = SkinRules.CollectWarnings(badEventManifest);
        bool HasBadEventWarning(string keyword) =>
            badEventWarnings.Any(warning => warning.Contains(keyword, StringComparison.Ordinal));
        var badEventOk = HasBadEventWarning("事件不支持") && HasBadEventWarning("脚本无效")
                         && HasBadEventWarning("绑定属性不支持");
        Check("事件绑定告警", badEventOk,
            $"事件={HasBadEventWarning("事件不支持")}，脚本={HasBadEventWarning("脚本无效")}，" +
            $"绑定={HasBadEventWarning("绑定属性不支持")}，共 {badEventWarnings.Count} 条");
        foreach (var warning in badEventWarnings) W("  告警：" + warning);

        var (validOk, validReason) = SkinRules.Validate(manifest);
        var manifestWarnings = SkinRules.CollectWarnings(manifest, sheet);
        Check("清单校验", validOk && manifestWarnings.Count == 0,
            $"通过={validOk}，原因={(validOk ? "无" : validReason)}，告警={manifestWarnings.Count}");
        foreach (var warning in manifestWarnings) W("  告警：" + warning);

        var overLimit = new SkinManifest
        {
            Format = "dolskin/3",
            Name = "超限测试",
            DefaultTheme = "missing",
            Themes =
            {
                new SkinTheme { Id = "dup" },
                new SkinTheme { Id = "dup" },
            },
            Effects = new SkinEffectOptions { Ambient = new SkinAmbientOptions { Particles = 40 } },
        };
        var overWarnings = SkinRules.CollectWarnings(overLimit);
        var particleWarn = overWarnings.Any(warning => warning.Contains("particles", StringComparison.OrdinalIgnoreCase));
        var duplicateWarn = overWarnings.Any(warning => warning.Contains("重复", StringComparison.Ordinal));
        Check("超限告警", particleWarn && duplicateWarn,
            $"粒子告警={particleWarn}，主题重复告警={duplicateWarn}，共 {overWarnings.Count} 条");
        foreach (var warning in overWarnings) W("  告警：" + warning);

        var legacy = new SkinManifest { Format = "dolskin/1", Name = "旧版示例" };
        var (legacyOk, legacyReason) = SkinRules.Validate(legacy);
        var legacyWarnings = SkinRules.CollectWarnings(legacy);
        var sampleSkinPath = FindSampleFile("samples/sample-skin/skin.json");
        var sampleSkinOk = true;
        var sampleSkinDetail = "未找到示例文件";
        if (sampleSkinPath != null)
        {
            var oldManifest = JsonSerializer.Deserialize<SkinManifest>(File.ReadAllText(sampleSkinPath));
            var (oldOk, oldReason) = SkinRules.Validate(oldManifest);
            sampleSkinOk = oldOk;
            sampleSkinDetail = oldOk ? "通过" : oldReason;
        }
        Check("旧包兼容", legacyOk && sampleSkinOk && legacy.Components.Count == 0 && legacyWarnings.Count == 0,
            $"最小 dolskin/1 通过={legacyOk}，组件数={legacy.Components.Count}，告警={legacyWarnings.Count}，" +
            $"sample-skin 通过={sampleSkinOk}（{sampleSkinDetail}）");
        if (!legacyOk) W("  dolskin/1 校验原因：" + legacyReason);

        if (failures.Count > 0)
            throw new InvalidOperationException("美化包自检未通过：" + string.Join("、", failures));
    }

    /// <summary>从运行目录向上查找仓库 docs 下的示例美化包；发布目录里没有 docs 时返回 null。</summary>
    private static string? FindSampleFile(string relative)
    {
        foreach (var start in new[] { Paths.Root, Environment.CurrentDirectory })
        {
            var dir = start.TrimEnd(Path.DirectorySeparatorChar);
            for (var i = 0; i < 8 && dir.Length > 3; i++)
            {
                foreach (var prefix in new[] { "docs", Path.Combine("dev", "docs") })
                {
                    var candidate = Path.Combine(dir, prefix, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate)) return candidate;
                }

                var parent = Path.GetDirectoryName(dir);
                if (parent == null || parent.Length == 0 || parent == dir) break;
                dir = parent;
            }
        }
        return null;
    }

    private const string LanternCssFallback = """
        :root { --accent: #ff35e8d5; }
        @keyframes lantern-breathe {
          from { opacity: 0.65; transform: scale(0.98); }
          50% { opacity: 1; transform: scale(1.04); }
          to { opacity: 0.65; transform: scale(0.98); }
        }
        .SectionTitle { color: var(--accent); animation: lantern-breathe 3.2s ease-in-out infinite; }
        .PrimaryButton { transition: background 180ms ease-out, transform 120ms ease-out; }
        .PrimaryButton:pressed { transform: scale(.96); }
        """;

    private const string LanternManifestFallback = """
        {
          "format": "dolskin/3",
          "id": "lantern-skin",
          "name": "灯台夜幕",
          "styleSheet": "skin.css",
          "defaultTheme": "cyberpunk",
          "themes": [
            { "id": "cyberpunk", "name": "赛博夜", "variables": { "accent": "#FF35E8D5" }, "ornament": "ornaments/lantern.png", "slice": 24 },
            { "id": "amber", "name": "琥珀灯", "variables": { "accent": "#FFFFB74A" }, "ornament": "ornaments/lantern.png", "slice": 24 }
          ],
          "effects": {
            "ambient": { "particles": 8, "veil": true, "sky": true, "opacity": 0.6 },
            "motion": { "panelIn": true, "breathe": true, "hairline": true, "ripple": true }
          },
          "settings": [
            { "key": "theme", "type": "select", "label": "主题", "default": "cyberpunk", "optionsFromThemes": true },
            { "key": "motion", "type": "toggle", "label": "动效", "default": true },
            { "key": "particles", "type": "select", "label": "环境粒子", "default": "8", "options": ["0", "4", "8"] },
            { "key": "ornament", "type": "toggle", "label": "主题边饰", "default": true }
          ],
          "components": [
            { "id": "lantern-badge", "host": "#LaunchBtn", "slot": "after", "widget": "Badge", "class": "skin-badge", "props": { "text": "灯台" } }
          ]
        }
        """;

    private static void TestModsAndPack(string temp)
    {
        var versionDir = Path.Combine(temp, "ver");
        Directory.CreateDirectory(Path.Combine(versionDir, "mods"));

        File.WriteAllText(Path.Combine(versionDir, Paths.HtmlName),
            "<!DOCTYPE html><script>const data = { version: \"0.5.12.13\" };</script>", new UTF8Encoding(false));

        var modPath = Path.Combine(versionDir, "mods", "good.mod.zip");
        using (var zip = ZipFile.Open(modPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("boot.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"name\":\"TestMod\",\"version\":\"1.0.0\",\"dependenceInfo\":[{\"modName\":\"GameVersion\",\"version\":\"=0.5.12.13\"},{\"modName\":\"ModLoader\",\"version\":\"^2.31.2\"}]}");
        }

        var mods = ModService.Load(versionDir);
        W($"合成版本读取模组：{mods.Count} 个，首个 name={mods[0].Name} version={mods[0].Version} boot={mods[0].BootParsed} 依赖={mods[0].Dependencies.Count}");

        // 带尾随逗号的 boot.json（Ray 美化这类写法）：ModLoader 能读，启动器也要能解析
        var lenientDir = Path.Combine(temp, "lenient");
        Directory.CreateDirectory(Path.Combine(lenientDir, "mods"));
        var lenientPath = Path.Combine(lenientDir, "mods", "lenient.mod.zip");
        using (var zip = ZipFile.Open(lenientPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("boot.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"name\":\"LenientMod\",\"version\":\"1.0.0\",\"additionFile\":[\"a.txt\",],}");
        }
        var lenient = ModService.Load(lenientDir).Single();
        W($"尾随逗号 boot.json 解析：boot={lenient.BootParsed} name={lenient.Name} version={lenient.Version}");

        ModService.Save(versionDir, mods);
        var list = File.ReadAllText(Path.Combine(versionDir, "modList.json"));
        W("生成 modList.json -> " + list.Replace("\n", " ").Replace("\r", " "));

        mods[0].Enabled = false;
        ModService.Save(versionDir, mods);
        W("关闭模组后 modList.json -> " + File.ReadAllText(Path.Combine(versionDir, "modList.json")).Trim());

        var info = new VersionInfo
        {
            Folder = "synthetic",
            FullPath = versionDir,
            HtmlPath = Path.Combine(versionDir, Paths.HtmlName),
            Mode = "http",
            Port = 0,
            GameVersion = "0.5.12.13",
            DisplayName = "合成版本",
        };
        W("完整性检查：");
        foreach (var line in ModService.Check(info)) W("  " + line);
        foreach (var line in DiagnosticService.Compatibility(info)) W("  [诊断] " + line);

        // 模组配置导出 / 还原
        var configPath = BackupService.ExportModsConfig(info);
        W("导出模组配置 -> " + Path.GetFileName(configPath));
        BackupService.RestoreModsConfig(versionDir, configPath, W);
        W("还原后启用状态：" + ModService.Load(versionDir)[0].Enabled);

        // 模组导入（根 boot.json + 多一层目录）、重命名、删除
        var importRoot = Path.Combine(temp, "import");
        Directory.CreateDirectory(Path.Combine(importRoot, "mods"));
        var rootZip = Path.Combine(temp, "root-mod.zip");
        using (var zip = ZipFile.Open(rootZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("boot.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"name\":\"RootMod\",\"version\":\"1.0.0\"}");
        }
        var nestedZip = Path.Combine(temp, "nested-mod.zip");
        using (var zip = ZipFile.Open(nestedZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("NestedMod/boot.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("{\"name\":\"NestedMod\",\"version\":\"1.0.0\"}");
        }
        W("导入结果：" + ModService.ImportFiles(importRoot, new[] { rootZip, nestedZip }, W) + " 个");
        var imported = ModService.Load(importRoot);
        W("导入后文件：" + string.Join(", ", imported.Select(m => m.FileName + "=" + m.Name)));
        ModService.Rename(importRoot, imported[0].FileName, "renamed-mod");
        W("重命名后文件：" + ModService.Load(importRoot)[0].FileName);
        ModService.Delete(importRoot, ModService.Load(importRoot)[0].FileName);
        W("删除后模组数：" + ModService.Load(importRoot).Count);

        // 快照与诊断包
        var snapshot = BackupService.CreateSnapshot(info, false, W);
        W("快照存在：" + File.Exists(snapshot) + "，文件=" + Path.GetFileName(snapshot));
        var diagPath = Path.Combine(temp, "diag.zip");
        DiagnosticService.ExportPackage(info, diagPath, W);
        W("诊断包存在：" + File.Exists(diagPath));
        try { Directory.Delete(BackupService.VersionDir(info.Folder), recursive: true); } catch { }

        // 导出（仅模组）
        var pack1 = Path.Combine(temp, "mods-only.dolpack");
        PackService.Export(info, includeGame: false, pack1, "自检导出", W);
        using (var zip = ZipFile.OpenRead(pack1))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            W("仅模组包内容：" + string.Join(", ", names));
        }

        // 导入（仅模组）到空版本目录
        var target = Path.Combine(temp, "target");
        Directory.CreateDirectory(target);
        var result = PackService.Import(pack1, PackService.Inspect(pack1), target, W);
        W($"导入结果：hasGame={result.HasGame} mods={result.ModCount}");
        W("目标 modList.json -> " + File.ReadAllText(Path.Combine(target, "modList.json")).Trim());

        // 导出（含本体）
        var pack2 = Path.Combine(temp, "with-game.dolpack");
        PackService.Export(info, includeGame: true, pack2, "自检导出", W);
        using (var zip = ZipFile.OpenRead(pack2))
        {
            W("含本体包条目：" + string.Join(", ", zip.Entries.Select(e => e.FullName)));
        }

        // 导入含本体包（会在 versions 下建目录，随后清理）
        var gameResult = PackService.Import(pack2, PackService.Inspect(pack2), null, W);
        W($"含本体包导入：新目录={gameResult.NewVersionFolder}，模组 {gameResult.ModCount} 个");
        if (gameResult.NewVersionFolder != null)
        {
            var created = Path.Combine(Paths.VersionsDir, gameResult.NewVersionFolder);
            W($"新目录存在 HTML：{File.Exists(Path.Combine(created, Paths.HtmlName))}");
            try { Directory.Delete(created, recursive: true); } catch { }
            VersionService.RemoveEntry(gameResult.NewVersionFolder);
            W($"已清理测试目录 {gameResult.NewVersionFolder}");
        }

        // 未含本体的包在没有目标版本时应报错
        try
        {
            PackService.Import(pack1, PackService.Inspect(pack1), null, W);
            W("异常：仅模组包在没有目标版本时没有报错");
        }
        catch (Exception ex)
        {
            W("仅模组包无目标版本时的报错符合预期：" + ex.Message);
        }
    }
}
