using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        }
        finally
        {
            server.Stop();
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
        var result = PackService.Import(pack1, target, W);
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
        var gameResult = PackService.Import(pack2, null, W);
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
            PackService.Import(pack1, null, W);
            W("异常：仅模组包在没有目标版本时没有报错");
        }
        catch (Exception ex)
        {
            W("仅模组包无目标版本时的报错符合预期：" + ex.Message);
        }
    }
}
