using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
    private const string MarketIndexUrl = "https://dol.alseece.top/release-index.json";
    private const string MarketIndexBackupUrl = "https://dolmod-release-index.johnliao381658675.workers.dev/release-index.json";
    private const string GameAssetPrefix = "DoL-ModLoader-";

    /// <summary>可选的游戏本体来源：整包 zip 与单 HTML 两种形态，安装时统一成一份文件清单。</summary>
    public static readonly GameSource[] GameSources =
    {
        new()
        {
            Id = "i18n",
            Name = "汉化版（整包 zip）",
            Repo = "Eltirosto/Degrees-of-Lewdity-Chinese-Localization",
            Kind = "zip",
            FolderPrefix = GameAssetPrefix,
            Hint = "从 GitHub 汉化仓库（Eltirosto/Degrees-of-Lewdity-Chinese-Localization）的 Release 拉取整包 zip，" +
                   "内含游戏本体与 ModLoader，可选附带 ModI18N 与图片包。安装后自动成为左侧的一个独立版本。",
        },
        new()
        {
            Id = "neo",
            Name = "ModLoader Neo（单 HTML）",
            Repo = "SayakoFurry/ModLoader-Neo",
            Kind = "html",
            FolderPrefix = "Neo-",
            Hint = "从 GitHub「ModLoader Neo」（SayakoFurry/ModLoader-Neo）的 Release 拉取单个 HTML，" +
                   "落成 Degrees of Lewdity.html 并自动创建 mods 目录。" +
                   "勾选 ModI18N / 图片包时会从汉化仓库拉取与游戏版本匹配的附带模组。",
        },
    };

    private static readonly HttpClient Http = CreateHttp();

    /// <summary>单条下载线路等待响应头的时间，超时就换下一条。</summary>
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>下载正文期间超过这么久没收到任何字节，就判定线路卡死并换下一条。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>auto 线路测速：每条线路读取的样本大小与超时。</summary>
    private const int ProbeBytes = 256 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>分段多连接下载：小于该大小直接单连接；大文件按块并发拉取。</summary>
    private const long ParallelThreshold = 2 * 1024 * 1024;

    /// <summary>
    /// auto 测速结果只在同一轮下载内复用：下载本体（含附带模组）算一轮，每个模组下载各算一轮。
    /// 换轮时由 <see cref="BeginRound"/> 清空，下一轮重新测速。
    /// </summary>
    private static List<MirrorOption>? _autoRouteCache;

    /// <summary>下载中心左侧的任务列表：每个文件一条，随下载进度实时更新。</summary>
    public static ObservableCollection<DownloadTaskVm> DownloadTasks { get; } = new();

    /// <summary>开始一轮新的下载任务：丢到上一轮的测速结果，本轮内继续复用。</summary>
    public static void BeginRound() => _autoRouteCache = null;

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DoLLauncher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // ---------- 本体 ----------

    /// <summary>设置里选定的游戏本体来源；未配置或值无效时回落到汉化整包。</summary>
    public static GameSource CurrentGameSource()
    {
        var id = AppConfig.Load().General.GameSource;
        return GameSources.FirstOrDefault(s => s.Id == id) ?? GameSources[0];
    }

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

    public static async Task<List<GameRelease>> FetchGameReleasesAsync(GameSource source)
    {
        var text = await GetGithubApiAsync($"repos/{source.Repo}/releases?per_page=20");
        return ParseReleases(text, source);
    }

    private static List<GameRelease> ParseReleases(string json, GameSource? source = null)
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
                    var accepted = source == null ? IsModPackageName(name) : IsGameAssetName(name, source);
                    if (!accepted) continue;
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
                Source = source ?? new GameSource(),
            });
        }
        return result;
    }

    /// <summary>游戏源认哪些资产：整包源取模组包，单 HTML 源只取 Neo 的 html（APK 不入库）。</summary>
    private static bool IsGameAssetName(string name, GameSource source)
    {
        if (source.IsZip) return IsModPackageName(name);
        return name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("neo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>下载本体并安装成 versions 下的新版本，返回新目录名。</summary>
    public static async Task<string> InstallGameAsync(GameRelease release, bool usePolyfill, bool includeI18n,
        bool includeImagePack, Action<string> log, Action<double> progress)
    {
        BeginRound();
        var plan = BuildInstallPlan(release, usePolyfill, includeI18n, includeImagePack);
        var dest = Path.Combine(Paths.VersionsDir, plan.FolderName);
        if (Directory.Exists(dest)) throw new InvalidOperationException($"版本目录已存在：versions\\{plan.FolderName}");

        Directory.CreateDirectory(Paths.VersionsDir);
        try
        {
            if (plan.ExtractZip)
            {
                await InstallZipAsync(plan, dest, log, progress);
            }
            else
            {
                await InstallFilesAsync(plan, dest, log, progress);
                // Neo 只有单个 HTML，ModI18N 与图片包要从汉化仓库按游戏版本补拉
                foreach (var companion in await ResolveNeoCompanionsAsync(includeI18n, includeImagePack, dest, log))
                    plan.CompanionMods.Add(companion);
            }

            Directory.CreateDirectory(Path.Combine(dest, "mods"));
            foreach (var companion in plan.CompanionMods)
                await InstallCompanionAsync(companion, dest, log, progress);

            ModService.Apply(dest);
            return plan.FolderName;
        }
        catch
        {
            TryDelete(dest, recursive: true);
            throw;
        }
    }

    /// <summary>把两种打包形态都归一成「要落哪些文件」的清单。</summary>
    private static GameInstallPlan BuildInstallPlan(GameRelease release, bool usePolyfill, bool includeI18n,
        bool includeImagePack)
    {
        var asset = release.PickAsset(usePolyfill)
                    ?? throw new InvalidOperationException(release.Source.IsZip
                        ? "这个 Release 里没有找到 DoL-ModLoader 游戏压缩包。"
                        : "这个 Release 里没有找到游戏 HTML 文件。");

        var plan = new GameInstallPlan
        {
            FolderName = GameFolder(release, asset),
            ExtractZip = release.Source.IsZip,
        };
        plan.Files.Add(release.Source.IsZip
            ? (asset.DownloadUrl, "")
            : (asset.DownloadUrl, Paths.HtmlName));

        if (includeI18n && release.I18nAsset != null) plan.CompanionMods.Add(release.I18nAsset);
        if (includeImagePack && release.ImagePackAsset != null) plan.CompanionMods.Add(release.ImagePackAsset);
        return plan;
    }

    private static async Task InstallZipAsync(GameInstallPlan plan, string dest, Action<string> log,
        Action<double> progress)
    {
        var asset = plan.Files[0];
        var temp = Path.Combine(Path.GetTempPath(), "dol-game-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            log($"[下载] {Path.GetFileName(new Uri(asset.Url).AbsolutePath)}");
            await DownloadFileAsync(asset.Url, temp, progress, log, requireZip: true);
            log("[安装] 正在解压游戏本体…");
            ZipFile.ExtractToDirectory(temp, dest);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task InstallFilesAsync(GameInstallPlan plan, string dest, Action<string> log,
        Action<double> progress)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in plan.Files)
        {
            var target = Path.Combine(dest, file.RelativePath);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            log($"[下载] {Path.GetFileName(target)}");
            await DownloadFileAsync(file.Url, target, progress, log);
        }
    }

    private static async Task InstallCompanionAsync(GameAsset asset, string versionDir, Action<string> log, Action<double> progress)
    {
        var modsDir = Path.Combine(versionDir, "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, asset.Name);
        log($"[下载] 附带模组 {asset.Name}（{asset.SizeText}）");
        await DownloadFileAsync(asset.DownloadUrl, dest, progress, log,
            requireZip: asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Neo 单 HTML 源没有附带资产：按 html 里的游戏版本去汉化仓库找同版本的 ModI18N / 图片包。</summary>
    private static async Task<List<GameAsset>> ResolveNeoCompanionsAsync(bool includeI18n, bool includeImagePack,
        string versionDir, Action<string> log)
    {
        var companions = new List<GameAsset>();
        if (!includeI18n && !includeImagePack) return companions;

        var source = GameSources.FirstOrDefault(s => s.Id == "i18n");
        if (source == null) return companions;

        string? gameVersion = null;
        var html = Path.Combine(versionDir, Paths.HtmlName);
        try
        {
            gameVersion = await Task.Run(() => VersionService.DetectGameVersionFromHtml(versionDir, html));
        }
        catch
        {
            // 读不到版本时退回最新 Release
        }
        log(gameVersion is { Length: > 0 }
            ? $"[安装] 识别到游戏版本 {gameVersion}，从汉化仓库获取附带模组…"
            : "[安装] 没能识别游戏版本，使用汉化仓库最新的附带模组…");

        List<GameRelease> releases;
        try
        {
            releases = await FetchGameReleasesAsync(source);
        }
        catch (Exception ex)
        {
            log("[提示] 获取汉化仓库 Release 失败，跳过附带模组：" + ex.Message);
            return companions;
        }

        var release = releases.FirstOrDefault(r => gameVersion is { Length: > 0 } && r.Tag.Contains(gameVersion))
                      ?? releases.FirstOrDefault(r => !r.Prerelease)
                      ?? releases.FirstOrDefault();
        if (release == null)
        {
            log("[提示] 汉化仓库没有可用的 Release，跳过附带模组。");
            return companions;
        }
        if (gameVersion is { Length: > 0 } && !release.Tag.Contains(gameVersion))
            log($"[提示] 汉化仓库没有 {gameVersion} 的 Release，改用 {release.Tag}，附带模组可能与游戏版本不一致。");

        if (includeI18n)
        {
            if (release.I18nAsset != null) companions.Add(release.I18nAsset);
            else log("[提示] 该 Release 里没有 ModI18N。");
        }
        if (includeImagePack)
        {
            if (release.ImagePackAsset != null) companions.Add(release.ImagePackAsset);
            else log("[提示] 该 Release 里没有图片包。");
        }
        return companions;
    }

    private static string GameFolder(GameRelease release, GameAsset asset)
    {
        if (!release.Source.IsZip)
        {
            var version = Sanitize(release.Tag.TrimStart('v', 'V'));
            return version.Length == 0 ? "Neo-" + DateTime.Now.ToString("yyyyMMddHHmm") : release.Source.FolderPrefix + version;
        }
        return GameFolderFromAsset(asset.Name);
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
                PackageRecords = ParsePackageRecords(item),
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
        var usable = list.Where(r => r.Assets.Any(a => IsModPackageName(a.Name) && IsModCandidateName(a.Name)))
            .ToList();

        // 共享仓库会把不同产品放在不同 Release 里（如 AU 的 mod / facemod / hairmod），
        // 只保留资产与本模组包型或名字对得上的 Release，避免下拉框里出现别的产品。
        var matched = usable.Where(r => ReleaseMatchesMod(mod, r)).ToList();
        return matched.Count > 0 ? matched : usable;
    }

    /// <summary>这个 Release 的资产是否属于该模组（优先按包型系列判断）。</summary>
    private static bool ReleaseMatchesMod(MarketMod mod, GameRelease release)
    {
        var candidates = release.Assets
            .Where(a => IsModPackageName(a.Name) && IsModCandidateName(a.Name))
            .ToList();
        if (candidates.Count == 0) return false;

        if (mod.Editions.Count > 0)
        {
            return mod.Editions.Any(e =>
                MatchVerifiedAsset(candidates, e.VerifiedAssets, null) != null ||
                MatchAssetSeries(candidates, e.AssetSeries) != null);
        }
        return PickByNameMatch(mod, candidates) != null;
    }

    /// <summary>模组包候选资产：排除汉化、原版图片包、游戏本体与 APK。</summary>
    private static bool IsModCandidateName(string name) =>
        !name.Contains("GameOriginalImagePack", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("ModI18N", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains(GameAssetPrefix, StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("APK", StringComparison.OrdinalIgnoreCase);

    /// <summary>列出某个 Release 里当前包型可选的具体版本（资产），最新的排在最前。</summary>
    public static async Task<List<ModAssetOption>> FetchModAssetVersionsAsync(MarketMod mod, MarketEdition? edition,
        string? tag)
    {
        if (mod.Repo.Length == 0) return new List<ModAssetOption>();

        var candidates = new List<GameAsset>();
        if (!string.IsNullOrWhiteSpace(tag))
        {
            var one = await TryFetchReleaseByTagAsync(mod.Repo, tag);
            if (one != null) candidates = one.Value.Assets;
        }
        if (candidates.Count == 0)
        {
            var releases = await FetchModReleasesAsync(mod);
            candidates = releases.FirstOrDefault()?.Assets ?? new List<GameAsset>();
        }

        candidates = candidates.Where(a => IsModCandidateName(a.Name)).ToList();
        if (edition != null)
        {
            var series = FilterBySeries(candidates, edition.AssetSeries);
            if (series.Count > 0) candidates = series;
        }

        return candidates
            .Select(a => new ModAssetOption
            {
                AssetName = a.Name,
                Version = AssetVersionText(a.Name),
                Size = a.Size,
            })
            .GroupBy(o => o.Version.Length > 0 ? o.Version : o.AssetName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(o => o.Size).First())
            .OrderByDescending(o => AssetVersion(o.AssetName))
            .ToList();
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
        string versionDir, string? gameVersion, Action<string>? log = null, MarketEdition? edition = null)
    {
        var result = new InstallCheckResult();
        var installed = ModService.Load(versionDir);
        var dependencies = new List<MarketMod>();
        var requiredVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var readmeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDependencies = EffectiveDependencies(mod, edition);
        var catalogKeys = catalog.Mods
            .SelectMany(m => new[] { m.Id, m.Name }.Concat(m.BootNames).Concat(m.Aliases)
                .Select(n => (Key: ModService.NormalizeModName(n), Mod: m)))
            .Where(p => p.Key.Length > 0)
            .GroupBy(p => p.Key)
            .ToDictionary(g => g.Key, g => g.First().Mod);

        void Visit(MarketMod current, HashSet<string> visiting)
        {
            var key = current.Id.Length > 0 ? current.Id : current.Name;
            if (!visiting.Add(key)) return;

            var deps = current == mod ? rootDependencies : current.Dependencies;
            foreach (var dep in deps)
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

                var depKey = ModService.NormalizeModName(dep.Id);
                catalogKeys.TryGetValue(depKey, out var depMod);
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
        await AddRepoBootDependenciesAsync(mod, catalogKeys, installed, dependencies, requiredVersions, result, log);

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

    /// <summary>索引顶层 dependencies 常年为空；依赖实际挂在 packageRecords 的具体资产上，按选中的包型取。</summary>
    private static List<MarketDependency> EffectiveDependencies(MarketMod mod, MarketEdition? edition)
    {
        var result = new List<MarketDependency>(mod.Dependencies);
        if (mod.PackageRecords.Count == 0) return result;

        void Add(IEnumerable<MarketDependency> deps)
        {
            foreach (var dep in deps)
                if (result.All(d => !string.Equals(d.Id, dep.Id, StringComparison.OrdinalIgnoreCase)))
                    result.Add(dep);
        }

        var names = new List<string>();
        if (edition != null) names.AddRange(edition.VerifiedAssets.Select(a => a.FileName));
        if (mod.VerifiedAssetFileName.Length > 0) names.Add(mod.VerifiedAssetFileName);

        var matched = mod.PackageRecords
            .Where(r => names.Any(n => n.Length > 0 && string.Equals(r.FileName, n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (matched.Count > 0)
        {
            foreach (var record in matched) Add(record.Dependencies);
        }
        else if (result.Count == 0)
        {
            foreach (var record in mod.PackageRecords) Add(record.Dependencies);
        }
        return result;
    }

    private static readonly Dictionary<string, List<MarketDependency>> RepoBootCache = new();

    /// <summary>索引完全没有依赖记录时，读仓库根目录 boot.json 的 dependenceInfo 补上。</summary>
    private static async Task AddRepoBootDependenciesAsync(MarketMod mod, Dictionary<string, MarketMod> catalogKeys,
        List<ModInfo> installed, List<MarketMod> dependencies, Dictionary<string, string> requiredVersions,
        InstallCheckResult result, Action<string>? log)
    {
        if (mod.Dependencies.Count > 0 || mod.PackageRecords.Any(r => r.Dependencies.Count > 0)) return;
        if (mod.Repo.Length == 0) return;

        foreach (var dep in await FetchRepoBootDependenciesAsync(mod.Repo))
        {
            if (ModService.IsGameVersion(dep.Id) || ModService.IsBuiltin(dep.Id)) continue;

            if (!catalogKeys.TryGetValue(ModService.NormalizeModName(dep.Id), out var depMod))
            {
                result.Notes.Add($"仓库 boot.json 声明前置「{dep.Id}」，市场里找不到，需要手动安装。");
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
            dependencies.Add(depMod);
        }
    }

    /// <summary>经镜像读取仓库根目录的 boot.json（main / master），结果缓存；失败返回空列表。</summary>
    private static async Task<List<MarketDependency>> FetchRepoBootDependenciesAsync(string repo)
    {
        if (repo.Length == 0) return new List<MarketDependency>();
        lock (RepoBootCache)
        {
            if (RepoBootCache.TryGetValue(repo, out var cached)) return cached;
        }

        var result = new List<MarketDependency>();
        foreach (var branch in new[] { "main", "master" })
        {
            var url = $"https://gh-proxy.org/https://raw.githubusercontent.com/{repo}/{branch}/boot.json";
            try
            {
                using var cts = new CancellationTokenSource(RouteTimeout);
                var text = await Http.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("dependenceInfo", out var deps) &&
                    deps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        var name = PropString(dep, "modName").Trim();
                        if (name.Length > 0)
                            result.Add(new MarketDependency { Id = name, Version = PropString(dep, "version").Trim() });
                    }
                    break;
                }
            }
            catch
            {
                // 分支不存在或线路不可用时换下一条
            }
        }
        lock (RepoBootCache) RepoBootCache[repo] = result;
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

    /// <summary>索引把依赖挂在 packageRecords 的具体资产上，顶层 dependencies 常常是空的。</summary>
    private static List<MarketPackageRecord> ParsePackageRecords(JsonElement item)
    {
        var result = new List<MarketPackageRecord>();
        if (!item.TryGetProperty("packageRecords", out var records) ||
            records.ValueKind != JsonValueKind.Array) return result;
        foreach (var record in records.EnumerateArray())
        {
            var fileName = PropString(record, "fileName");
            var deps = ParseDependencies(record);
            if (fileName.Length == 0 && deps.Count == 0) continue;
            result.Add(new MarketPackageRecord { FileName = fileName, Dependencies = deps });
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
        Action<double> progress, string? preferredTag = null, MarketEdition? edition = null, string? assetName = null)
    {
        if (!Directory.Exists(versionDir)) throw new InvalidOperationException("目标版本目录不存在，请先选中一个版本。");

        BeginRound();
        var asset = await ResolveModAssetAsync(mod, log, preferredTag, edition, assetName);
        var modsDir = Path.Combine(versionDir, "mods");
        Directory.CreateDirectory(modsDir);
        var dest = Path.Combine(modsDir, asset.Name);

        log($"[下载] {mod.Name} ← {asset.Name}（{asset.SizeText}）");
        var temp = Path.Combine(Path.GetTempPath(),
            "dol-mod-" + Guid.NewGuid().ToString("N") + Path.GetExtension(asset.Name));
        try
        {
            await DownloadFileAsync(asset.DownloadUrl, temp, progress, log, mod.Name,
                requireZip: asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

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
            await DownloadFileAsync(asset.DownloadUrl, temp, _ => { }, track: false);
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
        MarketEdition? edition = null, string? assetName = null)
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
            .Where(a => IsModCandidateName(a.Name))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"{mod.Name} 的 Release（{repo}）里没有找到可用的模组包（.zip / .modpack）。");

        // 用户在下拉框里指定了具体资产版本时优先用它
        if (!string.IsNullOrWhiteSpace(assetName))
        {
            var exact = candidates.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            log($"[提示] 指定的资产 {assetName} 不在这个 Release 里，改为自动挑选。");
        }

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
            // 同一包型的多个版本（如 0.8.7 / 0.9.3）都匹配时选最新的
            var hits = candidates.Where(a => pattern.IsMatch(a.Name)).ToList();
            if (hits.Count > 0) return hits.OrderByDescending(a => AssetVersion(a.Name)).First();
        }
        return null;
    }

    /// <summary>从资产名里取出版本号文本（如 0.9.3），没有版本号时返回空串。</summary>
    private static string AssetVersionText(string name)
    {
        var match = Regex.Match(name, @"[0-9]+(?:\.[0-9]+)+");
        return match.Success ? match.Value : "";
    }

    /// <summary>从资产名里取出版本号，用来在多个版本间选最新。</summary>
    private static Version AssetVersion(string name)
    {
        var text = AssetVersionText(name);
        return text.Length > 0 && Version.TryParse(text, out var version) ? version : new Version(0, 0);
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
        return FilterBySeries(candidates, series)
            .OrderBy(a => AssetTokens(a.Name).Count - tokens.Count)
            .ThenByDescending(a => AssetVersion(a.Name))
            .FirstOrDefault();
    }

    /// <summary>资产名包含包型系列里的全部词才留下。</summary>
    private static List<GameAsset> FilterBySeries(List<GameAsset> candidates, string series)
    {
        var tokens = AssetTokens(series);
        if (tokens.Count == 0) return new List<GameAsset>();
        return candidates.Where(a => tokens.All(t => AssetTokens(a.Name).Contains(t))).ToList();
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
                var better = best == null || target.Length > bestScore ||
                             (target.Length == bestScore && AssetVersion(asset.Name) > AssetVersion(best.Name));
                if (!better) continue;
                bestScore = target.Length;
                best = asset;
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
        new("ghproxycom", "GH-Proxy.com 镜像", "https://gh-proxy.com/"),
        new("llkk", "LLKK 镜像", "https://gh.llkk.cc/"),
        new("fastgit", "FastGit 镜像", "https://fastgit.cc/"),
        new("ddlc", "DDLC 镜像", "https://gh.ddlc.top/"),
        new("boki", "Boki 镜像", "https://github.boki.moe/"),
        new("ghproxynet", "GHProxy.net 镜像", "https://ghproxy.net/"),
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

    /// <summary>按设置生成下载线路：具体的源 → 直连兜底；auto 由测速结果决定顺序。</summary>
    private static List<MirrorOption> Routes(string choice)
    {
        var direct = Mirrors[^1];
        if (choice == "auto")
        {
            return Mirrors.ToList();   // 镜像按顺序回退，最后直连
        }
        var picked = Mirrors.FirstOrDefault(m => m.Id == choice) ?? direct;
        return picked.Direct ? new List<MirrorOption> { direct } : new List<MirrorOption> { picked, direct };
    }

    /// <summary>auto 时并发测速，把最快的线路排在最前；测速失败的排在后面兜底。</summary>
    private static async Task<List<MirrorOption>> SortRoutesBySpeedAsync(string url, Action<string>? log,
        CancellationToken ct = default)
    {
        var routes = Mirrors.ToList();
        if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return routes;

        if (_autoRouteCache is { } cached)
        {
            Report(log, $"[网络] 沿用本轮测速结果：{cached[0].Name}");
            return cached;
        }

        Report(log, "[网络] Auto 线路测速中…");
        var results = await Task.WhenAll(routes.Select(async option =>
            (Option: option, Speed: await ProbeSpeedAsync(MirrorUrl(url, option), ct))));

        foreach (var result in results)
        {
            Report(log, result.Speed > 0
                ? $"[网络] {result.Option.Name} 测速：{result.Speed / 1024:0} KB/s"
                : $"[网络] {result.Option.Name} 测速失败");
        }

        var fast = results.Where(r => r.Speed > 0).OrderByDescending(r => r.Speed).Select(r => r.Option).ToList();
        if (fast.Count == 0) return routes;
        fast.AddRange(results.Where(r => r.Speed <= 0).Select(r => r.Option));
        Report(log, $"[网络] 选用最快线路：{fast[0].Name}");
        _autoRouteCache = fast;
        return fast;
    }

    /// <summary>用一小段 Range 请求测线路速度（字节/秒）；失败或返回网页时记为 -1。</summary>
    private static async Task<double> ProbeSpeedAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ProbeBytes - 1);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode) return -1;

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return -1;

            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[32 * 1024];
            long readTotal = 0;
            while (readTotal < ProbeBytes)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, ProbeBytes - readTotal)), cts.Token);
                if (read <= 0) break;
                readTotal += read;
            }
            watch.Stop();

            var seconds = watch.Elapsed.TotalSeconds;
            return readTotal <= 0 || seconds <= 0 ? -1 : readTotal / seconds;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static async Task DownloadFileAsync(string url, string destPath, Action<double> progress,
        Action<string>? log = null, string? taskName = null, bool track = true, bool requireZip = false)
    {
        var task = track ? new DownloadTaskVm(taskName ?? FileNameFromUrl(url)) : null;
        if (task != null) DownloadTasks.Add(task);
        var ct = task?.Token ?? CancellationToken.None;

        var choice = AppConfig.Load().General.ModDownloadSource;
        List<MirrorOption> routes;
        try
        {
            task?.SetStage("测速中");
            routes = choice == "auto" ? await SortRoutesBySpeedAsync(url, log, ct) : Routes(choice);
        }
        catch (OperationCanceledException)
        {
            task?.MarkCanceled();
            throw;
        }

        Exception? last = null;
        // 非 GitHub 地址不会被镜像改写：各线路拼出来是同一个 URL，别重复下同一个地址
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < routes.Count; i++)
        {
            var routeUrl = MirrorUrl(url, routes[i]);
            if (!tried.Add(routeUrl)) continue;
            Report(log, $"[网络] 尝试 {routes[i].Name}（{i + 1}/{routes.Count}）：{routeUrl}");
            try
            {
                await DownloadDirectAsync(routeUrl, destPath, p =>
                {
                    task?.ReportProgress(p);
                    progress(p);
                }, task, ct);
                if (requireZip) EnsureZip(destPath);
                Report(log, $"[网络] {routes[i].Name} 下载成功");
                task?.Complete();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                task?.MarkCanceled();
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                Report(log, $"[网络] {routes[i].Name} 下载失败：{ex.Message}");
                if (i == routes.Count - 1) break;
                task?.ReportProgress(0);
                progress(0);
            }
        }
        task?.MarkFailed(last?.Message ?? "未知错误");
        throw last!;
    }

    /// <summary>下载完检查压缩包是否完整：线路中途断流得到的残缺 zip 必须当作失败换线路。</summary>
    private static void EnsureZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            _ = zip.Entries.Count;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("压缩包不完整（线路可能中途断开）。", ex);
        }
    }

    /// <summary>有界面日志回调就写界面，否则写 launcher.log。</summary>
    private static void Report(Action<string>? log, string message)
    {
        if (log != null) log(message);
        else LogService.Append(message);
    }

    private static async Task DownloadDirectAsync(string url, string destPath, Action<double> progress,
        DownloadTaskVm? task, CancellationToken ct)
    {
        // 先探测总大小与是否支持分段；支持就多连接并发拉取，镜像单连接限速时能明显提速
        var (total, supportsRange) = await ProbeRouteAsync(url, ct);
        task?.SetTotal(total);
        if (supportsRange && total >= ParallelThreshold)
        {
            try
            {
                await DownloadParallelAsync(url, destPath, total, progress, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Append($"[网络] 多连接下载失败，改用单连接：{ex.Message}");
                progress(0);
            }
        }
        await DownloadStreamAsync(url, destPath, progress, ct);
    }

    /// <summary>探测线路总大小与 Range 支持情况（只读响应头）。</summary>
    private static async Task<(long Total, bool SupportsRange)> ProbeRouteAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RouteTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        // 有些镜像/加速服务会返回 200 的错误网页，必须当作失败换线路，不能把网页当压缩包保存
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载线路返回了网页而不是文件。");

        if ((int)response.StatusCode == 206)
        {
            var length = response.Content.Headers.ContentRange?.Length ?? -1;
            return (length, length > 0);
        }
        return (response.Content.Headers.ContentLength ?? -1, false);
    }

    /// <summary>分段并发下载：每段一个连接，边下边写入文件对应偏移。</summary>
    private static async Task DownloadParallelAsync(string url, string destPath, long total, Action<double> progress,
        CancellationToken ct)
    {
        // 镜像普遍按连接限速：连接数随文件大小增加，实测 16 连接可把单连接 100KB/s 提到 2MB/s
        var parts = (int)Math.Clamp(total / (2 * 1024 * 1024), 2, 16);
        var chunk = total / parts;
        LogService.Append($"[网络] 分段下载：{parts} 连接，共 {total / 1024 / 1024} MB");

        await using (var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite,
                         128 * 1024, useAsync: true))
        {
            file.SetLength(total);
        }

        long received = 0;
        long lastReport = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        void AddBytes(long delta)
        {
            var value = Interlocked.Add(ref received, delta);
            var now = watch.ElapsedMilliseconds;
            var last = Interlocked.Read(ref lastReport);
            if (now - last < 100) return;
            if (Interlocked.CompareExchange(ref lastReport, now, last) != last) return;
            progress(Math.Min(1.0, value / (double)total));
        }

        var tasks = Enumerable.Range(0, parts).Select(async index =>
        {
            var start = index * chunk;
            var end = index == parts - 1 ? total - 1 : start + chunk - 1;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await DownloadRangeAsync(url, destPath, start, end, AddBytes, ct);
                    return;
                }
                catch (Exception) when (attempt < 2 && !ct.IsCancellationRequested)
                {
                    await Task.Delay(300, ct);
                }
            }
        });
        await Task.WhenAll(tasks);
        if (Interlocked.Read(ref received) != total)
            throw new IOException($"分段下载未完成：{Interlocked.Read(ref received)}/{total} 字节。");
        progress(1.0);
    }

    /// <summary>下载 [start, end] 这一段并写入文件对应偏移；线路不返回 206 视为不支持分段。</summary>
    private static async Task DownloadRangeAsync(string url, string destPath, long start, long end,
        Action<long> onBytes, CancellationToken ct)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(RouteTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token);
        if ((int)response.StatusCode != 206)
            throw new InvalidOperationException("下载线路不支持分段下载。");
        connect.CancelAfter(Timeout.InfiniteTimeSpan);

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
            128 * 1024, useAsync: true);
        output.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var remaining = end - start + 1;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            while (remaining > 0)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token);
                if (read <= 0) break;
                idle.CancelAfter(IdleTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
                onBytes(read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"超过 {IdleTimeout.TotalSeconds:0} 秒没有收到数据，已换下一条线路。");
        }
        if (remaining != 0) throw new IOException("分段下载未完成。");
    }

    /// <summary>单连接流式下载（不支持分段或分段失败时的兜底）。</summary>
    private static async Task DownloadStreamAsync(string url, string destPath, Action<double> progress,
        CancellationToken ct)
    {
        // 只给「等到响应头」这一步加超时：镜像卡住时尽快换下一条；真正下载正文时不限制
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(RouteTimeout);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connect.Token);
        response.EnsureSuccessStatusCode();
        connect.CancelAfter(Timeout.InfiniteTimeSpan);

        // 有些镜像/加速服务会返回 200 的错误网页，必须当作失败换线路，不能把网页当压缩包保存
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("下载线路返回了网页而不是文件。");

        var total = response.Content.Headers.ContentLength ?? -1L;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, idle.Token);
                if (read <= 0) break;
                idle.CancelAfter(IdleTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                if (total > 0) progress(readTotal / (double)total);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"超过 {IdleTimeout.TotalSeconds:0} 秒没有收到数据，已换下一条线路。");
        }
        // 线路提前断开时也会读到 0，必须按总长度校验，否则会留下残缺文件
        if (total > 0 && readTotal != total)
            throw new IOException($"下载未完成：{readTotal}/{total} 字节。");
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

/// <summary>下载中心左侧列表里的一条下载任务：一个文件一条，带大小、速度与进度。</summary>
public sealed class DownloadTaskVm : INotifyPropertyChanged
{
    private string _state = "准备中";
    private double _progress;
    private long _total;
    private long _received;
    private double _speed;
    private long _sampleMs = -1;
    private long _sampleBytes;
    private bool _finished;

    internal DownloadTaskVm(string name) => Name = name;

    public string Name { get; }

    /// <summary>下载完成或被取消时触发一次，界面据此延后移除该条目。</summary>
    public event EventHandler? Finished;

    internal CancellationTokenSource Cts { get; } = new();
    internal CancellationToken Token => Cts.Token;

    public string State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State), nameof(InfoText), nameof(CanCancel));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (Math.Abs(_progress - value) < 0.0001) return;
            _progress = value;
            Raise(nameof(Progress));
        }
    }

    /// <summary>「45.6 MB / 120 MB · 3.2 MB/s · 下载中」这样的一行信息。</summary>
    public string InfoText
    {
        get
        {
            var parts = new List<string>(3);
            if (_total > 0)
                parts.Add(_received > 0 && _state != "完成"
                    ? $"{FormatSize(_received)} / {FormatSize(_total)}"
                    : FormatSize(_total));
            if (_speed > 0 && _state == "下载中") parts.Add($"{FormatSize((long)_speed)}/s");
            parts.Add(_state);
            return string.Join(" · ", parts);
        }
    }

    public bool CanCancel =>
        (_state is "准备中" or "测速中" or "下载中") && !Cts.IsCancellationRequested;

    public void Cancel()
    {
        if (!CanCancel) return;
        try { Cts.Cancel(); } catch { }
        _state = "正在取消…";
        Raise(nameof(State), nameof(InfoText), nameof(CanCancel));
    }

    internal void SetStage(string stage)
    {
        if (_state is "完成" or "已取消" || Cts.IsCancellationRequested) return;
        State = stage;
    }

    internal void SetTotal(long total)
    {
        _total = total;
        Raise(nameof(InfoText));
    }

    internal void ReportProgress(double value)
    {
        if (Cts.IsCancellationRequested) return;
        State = "下载中";
        value = Math.Clamp(value, 0, 1);
        Progress = value;
        _received = _total > 0 ? (long)(_total * value) : 0;

        var now = Environment.TickCount64;
        if (_sampleMs < 0)
        {
            _sampleMs = now;
            _sampleBytes = _received;
        }
        else if (now - _sampleMs >= 500)
        {
            var seconds = (now - _sampleMs) / 1000.0;
            var delta = _received - _sampleBytes;
            if (delta >= 0) _speed = delta / seconds;
            _sampleMs = now;
            _sampleBytes = _received;
        }
        Raise(nameof(InfoText));
    }

    internal void Complete()
    {
        if (_received == 0) _received = _total;
        Progress = 1;
        _speed = 0;
        State = "完成";
        RaiseFinished();
    }

    internal void MarkCanceled()
    {
        _speed = 0;
        State = "已取消";
        RaiseFinished();
    }

    private void RaiseFinished()
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    internal void MarkFailed(string message)
    {
        _speed = 0;
        State = "失败：" + message;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    private void Raise(params string[] names)
    {
        var handler = PropertyChanged;
        if (handler == null) return;
        foreach (var name in names) handler(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
