using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 下载中心的数据与安装逻辑：本体来自汉化仓库的 GitHub Release，
/// 模组来自 ModHub 市场索引（dol.alseece.top），下载时再用 GitHub API 解析出 zip 资产。
/// </summary>
public static class DownloadService
{
    private const string GameRepo = "Eltirosto/Degrees-of-Lewdity-Chinese-Localization";
    private const string MarketIndexUrl = "https://dol.alseece.top/release-index.json";
    private const string MarketIndexBackupUrl = "https://dolmod-release-index.johnliao381658675.workers.dev/release-index.json";
    private const string GameAssetPrefix = "DoL-ModLoader-";

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>单条下载线路等待响应头的时间，超时就换下一条。</summary>
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DoLLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // ---------- 本体 ----------

    /// <summary>GitHub API 线路：直连 / GH-Proxy 代理，遇到 403/429 自动换下一条。</summary>
    public static async Task<string> GetGithubApiAsync(string path)
    {
        var urls = ApiRoutes(path);
        Exception? last = null;
        for (var i = 0; i < urls.Count; i++)
        {
            LogService.Append($"[网络] GitHub 接口请求：{urls[i]}");
            try
            {
                using var cts = new CancellationTokenSource(RouteTimeout);
                var text = await Http.GetStringAsync(urls[i], cts.Token);
                var head = text.TrimStart();
                if (head.StartsWith("<"))
                    throw new InvalidOperationException("接口返回了网页而不是 JSON。");
                LogService.Append($"[网络] GitHub 接口成功：{urls[i]}");
                return text;
            }
            catch (Exception ex)
            {
                last = ex;
                LogService.Append($"[网络] GitHub 接口失败：{urls[i]}（{ex.Message}）");
                if (i == urls.Count - 1) break;
            }
        }
        throw last!;
    }

    private static List<string> ApiRoutes(string path)
    {
        var api = $"https://api.github.com/{path}";
        var proxy = "https://gh-proxy.org/" + api;
        return AppConfig.Load().General.GithubApiSource switch
        {
            "github" => new List<string> { api },
            "ghproxy" => new List<string> { proxy, api },
            _ => new List<string> { proxy, api },
        };
    }

    public static async Task<List<GameRelease>> FetchGameReleasesAsync()
    {
        var text = await GetGithubApiAsync($"repos/{GameRepo}/releases?per_page=20");
        return ParseReleases(text);
    }

    private static List<GameRelease> ParseReleases(string json)
    {
        var result = new List<GameRelease>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

            var assets = new List<GameAsset>();
            if (item.TryGetProperty("assets", out var assetArray) && assetArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetArray.EnumerateArray())
                {
                    var name = PropString(asset, "name");
                    if (!IsModPackageName(name)) continue;
                    assets.Add(new GameAsset
                    {
                        Name = name,
                        DownloadUrl = PropString(asset, "browser_download_url"),
                        Size = PropLong(asset, "size"),
                    });
                }
            }

            var published = PropString(item, "published_at");
            result.Add(new GameRelease
            {
                Tag = PropString(item, "tag_name"),
                Name = PropString(item, "name"),
                Prerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                PublishedAt = DateTime.TryParse(published, out var dt) ? dt : null,
                Assets = assets,
            });
        }
        return result;
    }

    /// <summary>下载本体并安装成 versions 下的新版本，返回新目录名。</summary>
    public static async Task<string> InstallGameAsync(GameRelease release, bool usePolyfill, bool includeI18n,
        bool includeImagePack, Action<string> log, Action<double> progress)
    {
        var asset = release.PickAsset(usePolyfill)
                    ?? throw new InvalidOperationException("这个 Release 里没有找到 DoL-ModLoader 游戏压缩包。");

        var folder = GameFolderFromAsset(asset.Name);
        var dest = Path.Combine(Paths.VersionsDir, folder);
        if (Directory.Exists(dest)) throw new InvalidOperationException($"版本目录已存在：versions\\{folder}");

        Directory.CreateDirectory(Paths.VersionsDir);
        var temp = Path.Combine(Path.GetTempPath(), "dol-game-" + Guid.NewGuid().ToString("N") + ".zip");
        var extracted = false;
        try
        {
            log($"[下载] {asset.Name}（{asset.SizeText}）");
            await DownloadFileAsync(asset.DownloadUrl, temp, progress, log);
            log("[安装] 正在解压游戏本体…");
            ZipFile.ExtractToDirectory(temp, dest);
            extracted = true;
            Directory.CreateDirectory(Path.Combine(dest, "mods"));
        }
        catch
        {
            if (extracted) TryDelete(dest, recursive: true);
            throw;
        }
        finally
        {
            TryDelete(temp);
        }

        if (includeI18n && release.I18nAsset != null)
            await InstallCompanionAsync(release.I18nAsset, dest, log, progress);
        if (includeImagePack && release.ImagePackAsset != null)
            await InstallCompanionAsync(release.ImagePackAsset, dest, log, progress);

        ModService.Apply(dest);
        return folder;
    }

    private static async Task InstallCompanionAsync(GameAsset asset, string versionDir, Action<string> log, Action<double> progress)
    {
        var modsDir = Path.Combine(versionDir, "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, asset.Name);
        log($"[下载] 附带模组 {asset.Name}（{asset.SizeText}）");
        await DownloadFileAsync(asset.DownloadUrl, dest, progress, log);
    }

    private static string GameFolderFromAsset(string assetName)
    {
        var name = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? assetName[..^4] : assetName;
        if (name.StartsWith(GameAssetPrefix, StringComparison.OrdinalIgnoreCase))
            name = name[GameAssetPrefix.Length..];
        name = Sanitize(name);
        return name.Length == 0 ? "game-" + DateTime.Now.ToString("yyyyMMddHHmm") : name;
    }

    // ---------- 模组市场 ----------

    public static async Task<MarketCatalog> FetchMarketAsync(Action<string>? log = null)
    {
        string text;
        var source = AppConfig.Load().General.MarketSource;
        if (source == "backup")
        {
            Report(log, $"[网络] 拉取模组索引（备用）：{MarketIndexBackupUrl}");
            using var backupCts = new CancellationTokenSource(RouteTimeout);
            text = await Http.GetStringAsync(MarketIndexBackupUrl, backupCts.Token);
        }
        else if (source == "primary")
        {
            Report(log, $"[网络] 拉取模组索引（主）：{MarketIndexUrl}");
            using var primaryCts = new CancellationTokenSource(RouteTimeout);
            text = await Http.GetStringAsync(MarketIndexUrl, primaryCts.Token);
        }
        else
        {
            Report(log, $"[网络] 拉取模组索引（主）：{MarketIndexUrl}");
            try
            {
                using var cts = new CancellationTokenSource(RouteTimeout);
                text = await Http.GetStringAsync(MarketIndexUrl, cts.Token);
            }
            catch (Exception ex)
            {
                Report(log, "[网络] 主索引不可用（" + ex.Message + "），改用备用地址…");
                using var backupCts = new CancellationTokenSource(RouteTimeout);
                text = await Http.GetStringAsync(MarketIndexBackupUrl, backupCts.Token);
            }
        }
        var catalog = ParseMarket(text);
        Report(log, "[网络] 模组索引获取成功");
        return catalog;
    }

    private static MarketCatalog ParseMarket(string json)
    {
        var catalog = new MarketCatalog();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        catalog.GeneratedAt = PropString(root, "generatedAt");

        var withdrawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("withdrawnIds", out var withdrawnArray) && withdrawnArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in withdrawnArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id) withdrawn.Add(id);
            }
        }

        if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return catalog;

        foreach (var item in mods.EnumerateArray())
        {
            var id = PropString(item, "id");
            if (withdrawn.Contains(id)) continue;

            var status = PropString(item, "releaseStatus");
            if (status.Equals("withdrawn", StringComparison.OrdinalIgnoreCase)) continue;
            var contentType = PropString(item, "contentType");
            if (contentType.Length > 0 && !contentType.Equals("package", StringComparison.OrdinalIgnoreCase)) continue;

            var description = PropString(item, "description");
            if (description.Length == 0) description = PropString(item, "wikiDescription");

            var repo = PropString(item, "repo");
            if (repo.Length == 0) repo = RepoFromUrl(PropString(item, "sourceUrl"));
            if (repo.Length == 0) repo = RepoFromUrl(PropString(item, "githubUrl"));
            if (repo.Length == 0) continue;

            var verifiedName = "";
            if (item.TryGetProperty("verifiedReleaseAsset", out var verified) &&
                verified.ValueKind == JsonValueKind.Object)
            {
                verifiedName = PropString(verified, "fileName");
                if (verifiedName.Length == 0) verifiedName = FileNameFromUrl(PropString(verified, "sourceUrl"));
            }

            var tags = PropStrings(item, "tags");
            var mod = new MarketMod
            {
                Id = id,
                Name = PropString(item, "name"),
                Version = PropString(item, "version"),
                Category = PropString(item, "category"),
                Tags = string.Join(" · ", tags),
                TagList = tags,
                Description = description,
                Author = PropString(item, "author"),
                Repo = repo,
                ReleaseUrl = PropString(item, "releaseUrl"),
                UpdateDate = PropString(item, "updateDate"),
                Prerelease = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                BootNames = PropStrings(item, "bootNames"),
                Aliases = PropStrings(item, "aliases"),
                Dependencies = ParseDependencies(item),
                VerifiedAssetFileName = verifiedName,
                Editions = ParseEditions(item),
            };
            if (mod.Name.Length == 0) continue;
            catalog.Mods.Add(mod);
        }

        foreach (var category in catalog.Mods.Select(m => m.Category).Where(c => c.Length > 0).Distinct().OrderBy(c => c))
            catalog.Categories.Add(category);

        return catalog;
    }

    /// <summary>获取某个模组仓库的 Release 列表，供用户选择旧版本下载。</summary>
    public static async Task<List<GameRelease>> FetchModReleasesAsync(MarketMod mod)
    {
        if (mod.Repo.Length == 0) throw new InvalidOperationException("这个模组没有配置 GitHub 仓库。");
        var text = await GetGithubApiAsync($"repos/{mod.Repo}/releases?per_page=20");
        var list = ParseReleases(text);
        return list.Where(r =>
            r.Assets.Any(a => IsModPackageName(a.Name) &&
                              !a.Name.Contains("GameOriginalImagePack", StringComparison.OrdinalIgnoreCase) &&
                              !a.Name.Contains("ModI18N", StringComparison.OrdinalIgnoreCase) &&
                              !a.Name.Contains("APK", StringComparison.OrdinalIgnoreCase) &&
                              !a.Name.Contains(GameAssetPrefix, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>ModLoader 能加载的模组包：普通 zip、原生 .modpack，以及加密的 .modpack.crypt。</summary>
    private static bool IsModPackageName(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".modpack", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".modpack.crypt", StringComparison.OrdinalIgnoreCase);

    /// <summary>目标版本里与这个市场条目同名的已装模组；返回多个说明会重复加载。</summary>
    public static List<ModInfo> FindInstalledMatches(MarketMod mod, IEnumerable<ModInfo> installed)
    {
        var list = installed.ToList();
        var targets = new List<string> { NormalizeName(mod.Name), NormalizeName(mod.Id) };
        targets.AddRange(mod.BootNames.Select(NormalizeName));
        targets.AddRange(mod.Aliases.Select(NormalizeName));
        targets.AddRange(mod.Editions.Select(e => NormalizeName(e.BootName)));
        targets.RemoveAll(t => t.Length == 0);

        var exact = list.Where(local =>
        {
            var localName = NormalizeName(local.Name);
            return localName.Length > 0 && targets.Any(t => t == localName);
        }).ToList();
        if (exact.Count > 0) return exact;

        return list.Where(local =>
        {
            var localName = NormalizeName(local.Name);
            return localName.Length >= 4 &&
                   targets.Any(t => t.Length >= 4 && (t.Contains(localName) || localName.Contains(t)));
        }).ToList();
    }

    /// <summary>目标版本里已安装的这个市场模组的版本号；未安装时返回空串。</summary>
    public static string FindInstalledVersion(MarketMod mod, IEnumerable<ModInfo> installed)
    {
        return FindInstalledMatches(mod, installed).Select(m => m.Version)
            .FirstOrDefault(v => v.Length > 0) ?? "";
    }

    /// <summary>安装前检查：递归前置链、README 里声明的依赖、与本地已装模组的冲突。</summary>
    public static async Task<InstallCheckResult> CheckInstallAsync(MarketMod mod, MarketCatalog catalog,
        string versionDir, string? gameVersion, Action<string>? log = null)
    {
        var result = new InstallCheckResult();
        var installed = ModService.Load(versionDir);
        var dependencies = new List<MarketMod>();
        var requiredVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var readmeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(MarketMod current, HashSet<string> visiting)
        {
            var key = current.Id.Length > 0 ? current.Id : current.Name;
            if (!visiting.Add(key)) return;

            foreach (var dep in current.Dependencies)
            {
                if (ModService.IsGameVersion(dep.Id))
                {
                    if (gameVersion is { Length: > 0 } && dep.Version.Length > 0 &&
                        !VersionUtil.Satisfies(dep.Version, gameVersion))
                    {
                        result.Notes.Add($"「{current.Name}」要求的游戏版本是 {dep.Version}，当前是 {gameVersion}，可能不兼容。");
                    }
                    continue;
                }

                if (ModService.IsBuiltin(dep.Id)) continue;

                var depMod = catalog.Mods.FirstOrDefault(m =>
                        string.Equals(m.Id, dep.Id, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.Mods.FirstOrDefault(m =>
                        string.Equals(m.Name, dep.Id, StringComparison.OrdinalIgnoreCase) ||
                        m.BootNames.Any(b => string.Equals(b, dep.Id, StringComparison.OrdinalIgnoreCase)));
                if (depMod == null)
                {
                    result.Notes.Add($"市场里找不到前置「{dep.Id}」，需要手动安装。");
                    continue;
                }

                var installedVersion = FindInstalledVersion(depMod, installed);
                if (installedVersion.Length > 0 && VersionUtil.Satisfies(dep.Version, installedVersion))
                {
                    result.Satisfied.Add($"前置「{depMod.Name}」已满足（本地 {installedVersion}）。");
                    continue;
                }

                if (dependencies.Any(d => string.Equals(d.Id, depMod.Id, StringComparison.OrdinalIgnoreCase))) continue;
                if (dep.Version.Length > 0) requiredVersions[depMod.Id] = dep.Version;
                Visit(depMod, visiting);
                dependencies.Add(depMod);
            }
        }

        Visit(mod, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // 索引的 dependencies 只覆盖了少量条目，很多模组把前置写在 README 里
        if (catalog.Mods.Count > 0 && mod.Repo.Length > 0)
        {
            try
            {
                var readme = await FetchRepoReadmeAsync(mod.Repo);
                var section = ReadmeSectionFor(readme, mod);
                foreach (var readmeDep in ParseReadmeDependencies(section, catalog, mod))
                {
                    if (dependencies.Any(d => string.Equals(d.Id, readmeDep.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        readmeIds.Add(readmeDep.Id);
                        continue;
                    }

                    var installedVersion = FindInstalledVersion(readmeDep, installed);
                    if (installedVersion.Length > 0)
                    {
                        result.Satisfied.Add($"README 提到前置「{readmeDep.Name}」，本地已安装 {installedVersion}。");
                        continue;
                    }

                    readmeIds.Add(readmeDep.Id);
                    dependencies.Add(readmeDep);
                }
                AddLoadOrderNote(section, result.Notes);
            }
            catch (Exception ex)
            {
                log?.Invoke("README 前置识别失败：" + ex.Message);
            }
        }

        foreach (var dependency in dependencies)
        {
            result.Dependencies.Add(new InstallDependency
            {
                Mod = dependency,
                RequiredVersion = requiredVersions.TryGetValue(dependency.Id, out var requirement) ? requirement : "",
                InstalledVersion = FindInstalledVersion(dependency, installed),
                FromReadme = readmeIds.Contains(dependency.Id),
            });
        }

        CollectConflicts(mod, installed, versionDir, result.Conflicts);
        foreach (var dependency in dependencies)
            CollectConflicts(dependency, installed, versionDir, result.Conflicts);

        // 简介 / README 里写明的「与某某冲突」只作为警告，不阻止安装
        var declaredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declared in await ModConflictService.CheckAgainstAsync(mod, installed, catalog, log))
            AddDeclaredConflict(declared, declaredKeys, result.Conflicts);
        foreach (var dependency in dependencies)
        {
            foreach (var declared in await ModConflictService.CheckAgainstAsync(dependency, installed, catalog, log))
                AddDeclaredConflict(declared, declaredKeys, result.Conflicts);
        }

        return result;
    }

    private static void AddDeclaredConflict(ModConflict declared, HashSet<string> keys,
        List<InstallConflict> conflicts)
    {
        if (!keys.Add(declared.PairKey)) return;
        conflicts.Add(new InstallConflict
        {
            Title = declared.Title,
            Detail = declared.Detail,
        });
    }

    /// <summary>本地已装同名模组 / 版本不一致 / 文件并存之类的问题。</summary>
    private static void CollectConflicts(MarketMod item, List<ModInfo> installed, string versionDir,
        List<InstallConflict> conflicts)
    {
        var matches = FindInstalledMatches(item, installed);
        if (matches.Count > 1)
        {
            conflicts.Add(new InstallConflict
            {
                Title = $"本地已有 {matches.Count} 个同名模组「{matches[0].Name}」",
                Detail = "文件：" + string.Join("、", matches.Select(m => m.FileName)) +
                         "，会被重复加载，建议先只保留一个。",
            });
        }

        var local = matches.FirstOrDefault();
        if (local == null)
        {
            var existing = item.VerifiedAssetFileName;
            if (existing.Length > 0 && File.Exists(Path.Combine(ModService.ModsDir(versionDir), existing)))
            {
                conflicts.Add(new InstallConflict
                {
                    Title = $"将覆盖已有文件 {existing}",
                    Detail = "这个文件已在 mods 目录里，但启动器没能从它读出模组信息。",
                });
            }
            return;
        }

        if (item.Version.Length == 0 || local.Version.Length == 0 ||
            VersionUtil.Compare(item.Version, local.Version) == 0) return;

        var detail = VersionUtil.Compare(local.Version, item.Version) > 0
            ? "本地版本更高，安装后会被降级。"
            : "安装后本地版本会被更新。";
        var newFile = item.VerifiedAssetFileName;
        detail += newFile.Length > 0
            ? (newFile.Equals(local.FileName, StringComparison.OrdinalIgnoreCase)
                ? $"将覆盖 {local.FileName}。"
                : $"新包 {newFile} 会与已装 {local.FileName} 并存，可能重复加载。")
            : $"已装文件 {local.FileName}，新包文件名要下载时才确定，可能并存。";

        conflicts.Add(new InstallConflict
        {
            Title = $"「{item.Name}」版本不一致：本地 {local.Version} · 市场 {item.Version}",
            Detail = detail,
        });
    }

    private static readonly Dictionary<string, string> RepoReadmeCache = new();

    private static readonly string[] DependencyKeywords =
        { "前置", "依赖", "需要", "require", "depend", "need install", "before install" };

    private static readonly string[] GenericNames =
        { "汉化", "汉化mod", "modloader", "modhub", "游戏本体", "readme", "dol", "dolmods" };

    /// <summary>只走 GitHub API 读仓库 README，失败返回空串（检查阶段不下载 zip，避免卡住）。</summary>
    public static async Task<string> FetchRepoReadmeAsync(string repo)
    {
        if (repo.Length == 0) return "";
        lock (RepoReadmeCache)
        {
            if (RepoReadmeCache.TryGetValue(repo, out var cached)) return cached;
        }

        var text = "";
        try
        {
            var json = await GetGithubApiAsync($"repos/{repo}/readme");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var raw = (content.GetString() ?? "").Replace("\n", "").Replace("\r", "");
                if (raw.Length > 0) text = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            }
        }
        catch
        {
            text = "";
        }

        lock (RepoReadmeCache) RepoReadmeCache[repo] = text;
        return text;
    }

    /// <summary>共享仓库的 README 里有很多模组，只取当前模组所在的那一节。</summary>
    internal static string ReadmeSectionFor(string readme, MarketMod self)
    {
        if (readme.Length == 0) return readme;

        var aliases = new[] { self.Name }.Concat(self.Aliases).Concat(self.BootNames)
            .Where(n => n.Length >= 3).ToList();
        var lines = readme.Split('\n');

        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#")) continue;
            if (aliases.Any(a => line.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                start = i;
                break;
            }
        }
        if (start < 0) return readme;

        var builder = new StringBuilder();
        for (var i = start; i < lines.Length; i++)
        {
            if (i > start && lines[i].TrimStart().StartsWith("#")) break;
            builder.AppendLine(lines[i]);
        }
        return builder.ToString();
    }

    /// <summary>
    /// 只在写着前置 / 需要的句子里匹配市场已知模组的名称、别名与 bootName，
    /// 避免把 README 正文里随便提到的模组都当成依赖。
    /// </summary>
    private static List<MarketMod> ParseReadmeDependencies(string readme, MarketCatalog catalog, MarketMod self)
    {
        var hits = new List<MarketMod>();
        if (readme.Length == 0) return hits;

        var selfKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeName(self.Name), NormalizeName(self.Id),
        };
        selfKeys.UnionWith(self.BootNames.Select(NormalizeName));
        selfKeys.UnionWith(self.Aliases.Select(NormalizeName));
        selfKeys.Remove("");

        var candidates = new List<(string Text, MarketMod Mod)>();
        foreach (var mod in catalog.Mods)
        {
            if (string.Equals(mod.Id, self.Id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var raw in new[] { mod.Name }.Concat(mod.Aliases).Concat(mod.BootNames))
            {
                var text = raw.Trim();
                if (text.Length < 3) continue;
                if (GenericNames.Contains(text.ToLowerInvariant())) continue;
                if (selfKeys.Contains(NormalizeName(text))) continue;
                if (text.All(c => c < 128) && text.Length < 4) continue;
                candidates.Add((text, mod));
            }
        }
        candidates = candidates.OrderByDescending(c => c.Text.Length).ToList();

        foreach (var sentence in DependencySentences(readme))
        {
            foreach (var candidate in candidates)
            {
                if (!ContainsName(sentence, candidate.Text)) continue;
                if (hits.Any(h => string.Equals(h.Id, candidate.Mod.Id, StringComparison.OrdinalIgnoreCase))) continue;
                hits.Add(candidate.Mod);
            }
        }
        return hits;
    }

    private static bool ContainsName(string text, string name) =>
        name.All(c => c < 128)
            ? Regex.IsMatch(text, "(?<![A-Za-z0-9])" + Regex.Escape(name) + "(?![A-Za-z0-9])",
                RegexOptions.IgnoreCase)
            : text.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> DependencySentences(string readme)
    {
        foreach (var rawLine in readme.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // 「加载顺序：A => B => C」里的模组不是前置，切掉再匹配
            foreach (var marker in new[] { "加载顺序", "loadorder", "load order" })
            {
                var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index > 0) line = line[..index];
            }

            foreach (var sentence in Regex.Split(line, @"[。；;!！?？]"))
            {
                var text = sentence.Trim();
                if (text.Length == 0) continue;
                if (!DependencyKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                yield return text;
            }
        }
    }

    private static void AddLoadOrderNote(string readme, List<string> notes)
    {
        var lines = readme.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            foreach (var marker in new[] { "加载顺序", "loadorder", "load order" })
            {
                var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                var text = line[index..];
                // 只有「3. **加载顺序：**」这种标题时，把下一行的说明带进来
                var tail = text.Replace("*", "").Replace("：", "").Replace(":", "").Trim();
                if (tail.Length <= marker.Length)
                {
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var next = lines[j].Trim();
                        if (next.Length == 0) continue;
                        text = marker + "：" + next;
                        break;
                    }
                }

                text = text.Replace("**", "");
                if (text.Length > 150) text = text[..150] + "…";
                notes.Add("README 要求：" + text);
                return;
            }
        }
    }

    private static List<MarketDependency> ParseDependencies(JsonElement item)
    {
        var result = new List<MarketDependency>();
        if (!item.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var dep in deps.EnumerateArray())
        {
            var id = PropString(dep, "id");
            if (id.Length == 0) id = PropString(dep, "name");
            var version = PropString(dep, "version");
            if (id.Length > 0) result.Add(new MarketDependency { Id = id, Version = version });
        }
        return result;
    }

    /// <summary>解析同一条目的不同包型 / 包体系列。</summary>
    private static List<MarketEdition> ParseEditions(JsonElement item)
    {
        var result = new List<MarketEdition>();
        if (!item.TryGetProperty("packageEditions", out var editions) ||
            editions.ValueKind != JsonValueKind.Array) return result;

        foreach (var edition in editions.EnumerateArray())
        {
            var id = PropString(edition, "id");
            var label = PropString(edition, "label");
            if (id.Length == 0 && label.Length == 0) continue;

            var parsed = new MarketEdition
            {
                Id = id,
                Label = label,
                BootName = PropString(edition, "bootName"),
                AssetSeries = PropString(edition, "assetSeries"),
            };

            if (edition.TryGetProperty("verifiedAssets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var url = PropString(asset, "sourceUrl");
                    var fileName = PropString(asset, "fileName");
                    if (fileName.Length == 0) fileName = FileNameFromUrl(url);
                    if (fileName.Length == 0) continue;
                    parsed.VerifiedAssets.Add(new MarketAssetRef
                    {
                        FileName = fileName,
                        SourceUrl = url,
                        Version = PropString(asset, "version"),
                    });
                }
            }

            result.Add(parsed);
        }
        return result;
    }

    private static string FileNameFromUrl(string url)
    {
        if (url.Length == 0) return "";
        var clean = url.Split('?')[0].TrimEnd('/');
        var index = clean.LastIndexOf('/');
        return index >= 0 ? Uri.UnescapeDataString(clean[(index + 1)..]) : "";
    }

    /// <summary>下载一个市场模组到指定版本的 mods 目录，返回写入的文件名。</summary>
    public static async Task<string> InstallModAsync(MarketMod mod, string versionDir, Action<string> log,
        Action<double> progress, string? preferredTag = null, MarketEdition? edition = null)
    {
        if (!Directory.Exists(versionDir)) throw new InvalidOperationException("目标版本目录不存在，请先选中一个版本。");

        var asset = await ResolveModAssetAsync(mod, log, preferredTag, edition);
        var modsDir = Path.Combine(versionDir, "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, asset.Name);

        log($"[下载] {mod.Name} ← {asset.Name}（{asset.SizeText}）");
        var temp = Path.Combine(Path.GetTempPath(),
            "dol-mod-" + Guid.NewGuid().ToString("N") + Path.GetExtension(asset.Name));
        try
        {
            await DownloadFileAsync(asset.DownloadUrl, temp, progress, log);

            var bootName = ReadBootName(temp);
            if (bootName != null && !NamesMatch(mod, bootName))
                log($"[提示] 包内 boot.json 的模组名是「{bootName}」，与市场条目「{mod.Name}」不完全一致，请留意。");

            File.Copy(temp, dest, overwrite: true);
            ModService.Apply(versionDir);
            log($"[完成] 已安装到 mods\\{asset.Name}");
            return asset.Name;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 读取模组的 README：优先用 GitHub 仓库根目录的 README，取不到时再下载 Release 里的 zip 解出来。
    /// </summary>
    public static async Task<string> FetchReadmeAsync(MarketMod mod)
    {
        if (mod.Repo.Length > 0)
        {
            var fromRepo = await FetchRepoReadmeAsync(mod.Repo);
            if (fromRepo.Length > 0) return fromRepo;
        }

        var asset = await ResolveModAssetAsync(mod, _ => { }, null);
        // 只有 zip 才能解出 README；原生 .modpack 直接放弃
        if (!asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return "";
        var temp = Path.Combine(Path.GetTempPath(),
            "dol-readme-" + Guid.NewGuid().ToString("N") + Path.GetExtension(asset.Name));
        try
        {
            await DownloadFileAsync(asset.DownloadUrl, temp, _ => { });
            using var zip = ZipFile.OpenRead(temp);
            var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals("readme.md", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e =>
                {
                    var name = Path.GetFileName(e.FullName).ToLowerInvariant();
                    return name is "readme.md" or "readme.txt" or "readme";
                });
            if (entry == null) return "";

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<GameAsset> ResolveModAssetAsync(MarketMod mod, Action<string> log, string? preferredTag,
        MarketEdition? edition = null)
    {
        var repo = mod.Repo;
        var tag = !string.IsNullOrWhiteSpace(preferredTag) ? preferredTag! : ExtractTag(mod.ReleaseUrl);

        List<(string Tag, List<GameAsset> Assets)> releases = new();
        if (tag.Length > 0)
        {
            try
            {
                var one = await TryFetchReleaseByTagAsync(repo, tag);
                if (one != null) releases.Add(one.Value);
            }
            catch
            {
                // 该 tag 取不到时就退回 Release 列表
            }
        }

        if (releases.Count == 0)
        {
            var text = await GetGithubApiAsync($"repos/{repo}/releases?per_page=10");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"仓库 {repo} 没有可用的 Release。");
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
                releases.Add((PropString(item, "tag_name"), ReleaseAssets(item, onlyZip: true)));
            }
        }

        var scoped = !string.IsNullOrWhiteSpace(preferredTag)
            ? releases.Where(r => string.Equals(r.Tag, preferredTag, StringComparison.OrdinalIgnoreCase)).ToList()
            : releases;
        if (scoped.Count > 0) releases = scoped;

        var candidates = releases
            .SelectMany(r => r.Assets)
            .Where(a => !a.Name.Contains("GameOriginalImagePack", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains("ModI18N", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains(GameAssetPrefix, StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains("APK", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"{mod.Name} 的 Release（{repo}）里没有找到可用的模组包（.zip / .modpack）。");

        var best = PickBestAsset(mod, candidates, edition, tag);
        if (best == null)
        {
            log($"[提示] {mod.Name} 有 {candidates.Count} 个候选资产，已选择 {candidates[0].Name}；如果不对请到 Release 页面手动确认。");
            best = candidates[0];
        }
        return best;
    }

    private static GameAsset? PickBestAsset(MarketMod mod, List<GameAsset> candidates,
        MarketEdition? edition, string? tag)
    {
        // 指定了包型：先按该系列已验证资产的文件名匹配，失败再按系列名分词匹配
        if (edition != null)
        {
            var verified = MatchVerifiedAsset(candidates, edition.VerifiedAssets, tag);
            if (verified != null) return verified;
            var series = MatchAssetSeries(candidates, edition.AssetSeries);
            if (series != null) return series;
        }
        else
        {
            // 没选包型时优先相信索引推荐的资产
            var refs = mod.VerifiedAssetFileName.Length > 0
                ? new List<MarketAssetRef> { new() { FileName = mod.VerifiedAssetFileName } }
                : new List<MarketAssetRef>();
            var verified = MatchVerifiedAsset(candidates, refs, tag);
            if (verified != null) return verified;
        }

        return PickByNameMatch(mod, candidates);
    }

    /// <summary>按已验证资产的文件名找候选，版本号段当作通配，兼容同一个包型的新版本。</summary>
    private static GameAsset? MatchVerifiedAsset(List<GameAsset> candidates,
        IEnumerable<MarketAssetRef> refs, string? tag)
    {
        var ordered = refs.ToList();
        if (ordered.Count == 0) return null;

        var sameTag = tag is { Length: > 0 }
            ? ordered.Where(r => r.SourceUrl.Contains("/" + tag + "/", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<MarketAssetRef>();
        if (sameTag.Count > 0)
            ordered = sameTag.Concat(ordered.Where(r => !sameTag.Contains(r))).ToList();

        foreach (var reference in ordered)
        {
            var exact = candidates.FirstOrDefault(a =>
                string.Equals(a.Name, reference.FileName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }

        foreach (var reference in ordered)
        {
            if (reference.FileName.Length == 0) continue;
            var pattern = VersionWildcard(reference.FileName);
            var hit = candidates.FirstOrDefault(a => pattern.IsMatch(a.Name));
            if (hit != null) return hit;
        }
        return null;
    }

    private static Regex VersionWildcard(string fileName)
    {
        const string marker = "@@VER@@";
        var wildcard = Regex.Replace(fileName, @"[0-9]+(?:\.[0-9]+)+", marker);
        var escaped = Regex.Escape(wildcard).Replace(marker, @"[0-9]+(?:\.[0-9]+)*");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>按包体系列分词匹配：系列里的每个词都要出现在资产名里，多出来的词越少越优先。</summary>
    private static GameAsset? MatchAssetSeries(List<GameAsset> candidates, string series)
    {
        var tokens = AssetTokens(series);
        if (tokens.Count == 0) return null;

        GameAsset? best = null;
        var bestExtra = int.MaxValue;
        foreach (var asset in candidates)
        {
            var nameTokens = AssetTokens(asset.Name);
            if (!tokens.All(t => nameTokens.Contains(t))) continue;
            var extra = nameTokens.Count - tokens.Count;
            if (extra < bestExtra)
            {
                bestExtra = extra;
                best = asset;
            }
        }
        return best;
    }

    private static HashSet<string> AssetTokens(string value) =>
        Regex.Matches(value.ToLowerInvariant(), @"[a-z]+|[0-9]+")
            .Select(m => m.Value)
            .ToHashSet();

    private static GameAsset? PickByNameMatch(MarketMod mod, List<GameAsset> candidates)
    {
        var targets = new List<string> { Normalize(mod.Name) };
        targets.AddRange(mod.BootNames.Select(Normalize));
        targets.Add(Normalize(mod.Id));
        targets.RemoveAll(t => t.Length == 0);

        GameAsset? best = null;
        var bestScore = 0;
        foreach (var asset in candidates)
        {
            var name = Normalize(asset.Name);
            foreach (var target in targets)
            {
                if (target.Length < 2) continue;
                if (!name.Contains(target)) continue;
                if (target.Length > bestScore)
                {
                    bestScore = target.Length;
                    best = asset;
                }
            }
        }
        if (best != null) return best;
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static async Task<(string Tag, List<GameAsset> Assets)?> TryFetchReleaseByTagAsync(string repo, string tag)
    {
        try
        {
            var text = await GetGithubApiAsync($"repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return (PropString(root, "tag_name"), ReleaseAssets(root, onlyZip: true));
        }
        catch
        {
            return null;
        }
    }

    private static List<GameAsset> ReleaseAssets(JsonElement release, bool onlyZip)
    {
        var assets = new List<GameAsset>();
        if (!release.TryGetProperty("assets", out var array) || array.ValueKind != JsonValueKind.Array) return assets;
        foreach (var asset in array.EnumerateArray())
        {
            var name = PropString(asset, "name");
            if (onlyZip && !IsModPackageName(name)) continue;
            assets.Add(new GameAsset
            {
                Name = name,
                DownloadUrl = PropString(asset, "browser_download_url"),
                Size = PropLong(asset, "size"),
            });
        }
        return assets;
    }

    // ---------- 通用下载 ----------

    /// <summary>GitHub 加速镜像（前缀拼在原始资产地址前面；worker 走 ModHub 下载服务）。</summary>
    public sealed record MirrorOption(string Id, string Name, string Prefix, bool Worker = false, bool Direct = false);

    public static readonly MirrorOption[] Mirrors =
    {
        new("ghproxy", "GH-Proxy 镜像", "https://gh-proxy.org/"),
        new("llkk", "LLKK 镜像", "https://gh.llkk.cc/"),
        new("fastgit", "FastGit 镜像", "https://fastgit.cc/"),
        new("ddlc", "DDLC 镜像", "https://gh.ddlc.top/"),
        new("boki", "Boki 镜像", "https://github.boki.moe/"),
        new("worker", "ModHub 加速", "", Worker: true),
        new("github", "GitHub 直连", "", Direct: true),
    };

    /// <summary>把一个 GitHub 资产地址换成所选下载源的地址；非 GitHub 地址原样返回。</summary>
    public static string MirrorUrl(string url, MirrorOption option)
    {
        if (option.Direct || option.Prefix.Length == 0 && !option.Worker) return url;
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return url;
        if (option.Worker) return $"https://dol.alseece.top/download?url={Uri.EscapeDataString(url)}";
        return option.Prefix + url;
    }

    /// <summary>按设置生成下载线路：具体的源 → 直连兜底；auto 依次回退。</summary>
    private static List<MirrorOption> Routes()
    {
        var choice = AppConfig.Load().General.ModDownloadSource;
        var direct = Mirrors[^1];
        if (choice == "auto")
        {
            return Mirrors.ToList();   // 镜像按顺序回退，最后直连
        }
        var picked = Mirrors.FirstOrDefault(m => m.Id == choice) ?? direct;
        return picked.Direct ? new List<MirrorOption> { direct } : new List<MirrorOption> { picked, direct };
    }

    private static async Task DownloadFileAsync(string url, string destPath, Action<double> progress,
        Action<string>? log = null)
    {
        var routes = Routes();
        Exception? last = null;
        for (var i = 0; i < routes.Count; i++)
        {
            var routeUrl = MirrorUrl(url, routes[i]);
            Report(log, $"[网络] 尝试 {routes[i].Name}（{i + 1}/{routes.Count}）：{routeUrl}");
            try
            {
                await DownloadDirectAsync(routeUrl, destPath, progress);
                Report(log, $"[网络] {routes[i].Name} 下载成功");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Report(log, $"[网络] {routes[i].Name} 下载失败：{ex.Message}");
                if (i == routes.Count - 1) break;
                progress(0);
            }
        }
        throw last!;
    }

    /// <summary>有界面日志回调就写界面，否则写 launcher.log。</summary>
    private static void Report(Action<string>? log, string message)
    {
        if (log != null) log(message);
        else LogService.Append(message);
    }

    private static async Task DownloadDirectAsync(string url, string destPath, Action<double> progress)
    {
        // 只给「等到响应头」这一步加超时：镜像卡住时尽快换下一条；真正下载正文时不限制
        using var connect = new CancellationTokenSource(RouteTimeout);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connect.Token);
        response.EnsureSuccessStatusCode();
        connect.CancelAfter(Timeout.InfiniteTimeSpan);

        // 有些镜像/加速服务会返回 200 的错误网页，必须当作失败换线路，不能把网页当压缩包保存
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载线路返回了网页而不是文件。");

        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total > 0) progress(readTotal / (double)total);
        }
        progress(1.0);
    }

    private static string? ReadBootName(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase))
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/boot.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream, ModService.BootJsonOptions);
            return PropString(doc.RootElement, "name");
        }
        catch
        {
            return null;
        }
    }

    private static bool NamesMatch(MarketMod mod, string bootName)
    {
        var target = Normalize(bootName);
        if (target.Length == 0) return true;
        if (Normalize(mod.Name).Contains(target) || target.Contains(Normalize(mod.Name))) return true;
        return mod.BootNames.Any(n => Normalize(n).Contains(target) || target.Contains(Normalize(n)));
    }

    // ---------- 小工具 ----------

    public static string NormalizeName(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");

    private static string Normalize(string value) => NormalizeName(value);

    private static string ExtractTag(string releaseUrl)
    {
        var match = Regex.Match(releaseUrl, @"/releases/tag/([^/?#]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : "";
    }

    private static string RepoFromUrl(string url)
    {
        var match = Regex.Match(url, @"github\.com/([^/]+)/([^/?#]+)");
        if (!match.Success) return "";
        var repo = match.Groups[2].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return $"{match.Groups[1].Value}/{repo}";
    }

    private static string PropString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    private static long PropLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
    }

    private static List<string> PropStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return new List<string>();
        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? "")
            .Where(v => v.Length > 0)
            .ToList();
    }

    private static string Sanitize(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDelete(string path, bool recursive)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive); } catch { }
    }
}
