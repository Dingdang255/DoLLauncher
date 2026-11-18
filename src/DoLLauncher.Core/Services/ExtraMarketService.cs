using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 启动器自有市场列表（覆盖层）：从 GitHub 拉取 market-ext.json，
/// 与 ModHub 索引按字段级合并去重；网络失败时用本地缓存，缓存也没有就静默跳过。
/// </summary>
public static class ExtraMarketService
{
    // 自有列表所在仓库（owner/repo）与分支；换仓库时只改这三行
    private const string OverlayRepo = "Dingdang255/DoLLauncher";
    private const string OverlayBranch = "main";
    private const string OverlayFile = "market/market-ext.json";

    /// <summary>成功拉取后内存缓存的有效期，避免频繁请求。</summary>
    private static readonly TimeSpan MemoryTtl = TimeSpan.FromHours(6);

    /// <summary>失败后的冷静期：这段时间内不再重试，直接用缓存或跳过。</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new() { Timeout = RouteTimeout };
    private static readonly object Sync = new();

    private static string? _memoryJson;
    private static DateTime _memoryAt;
    private static DateTime _failedAt = DateTime.MinValue;

    private static string CacheFile => Path.Combine(Paths.Root, "cache", "market-ext.json");
    private static string MetaFile => Path.Combine(Paths.Root, "cache", "market-ext.meta.json");

    /// <summary>按顺序尝试：jsDelivr CDN → GitHub Raw → gh-proxy 代理 Raw。</summary>
    private static string[] Routes =>
    [
        $"https://cdn.jsdelivr.net/gh/{OverlayRepo}@{OverlayBranch}/{OverlayFile}",
        $"https://raw.githubusercontent.com/{OverlayRepo}/{OverlayBranch}/{OverlayFile}",
        $"https://gh-proxy.org/https://raw.githubusercontent.com/{OverlayRepo}/{OverlayBranch}/{OverlayFile}",
    ];

    /// <summary>拉取覆盖层 JSON（与主索引并行调用）；失败返回本地缓存或 null，不抛异常。</summary>
    public static async Task<string?> FetchOverlayAsync(bool forceRefresh, Action<string>? log = null)
    {
        try
        {
            return await TryGetOverlayAsync(forceRefresh, log);
        }
        catch (Exception ex)
        {
            Report(log, "[网络] 自有市场列表获取失败（忽略）：" + ex.Message);
            return null;
        }
    }

    private static async Task<string?> TryGetOverlayAsync(bool forceRefresh, Action<string>? log)
    {
        lock (Sync)
        {
            if (!forceRefresh && _memoryJson != null && DateTime.UtcNow - _memoryAt < MemoryTtl)
                return _memoryJson;
            if (!forceRefresh && DateTime.UtcNow - _failedAt < FailureCooldown)
                return LoadCache();
        }

        var cached = LoadCache();
        var etag = LoadEtag();
        var routes = Routes;
        for (var i = 0; i < routes.Length; i++)
        {
            try
            {
                var url = forceRefresh
                    ? routes[i] + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    : routes[i];
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // 只有首选的 CDN 线路做条件请求，其余线路直接取
                if (!forceRefresh && cached != null && etag.Length > 0 && i == 0)
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);

                using var response = await Http.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
                {
                    MarkFetched(cached);
                    return cached;
                }
                if (!response.IsSuccessStatusCode) continue;

                var text = await response.Content.ReadAsStringAsync();
                if (text.Trim().Length == 0) continue;

                SaveCache(text, response.Headers.ETag?.Tag ?? "");
                MarkFetched(text);
                Report(log, "[网络] 自有市场列表已更新");
                return text;
            }
            catch
            {
                // 当前线路不可用，换下一条
            }
        }

        lock (Sync) _failedAt = DateTime.UtcNow;
        if (cached != null)
        {
            MarkFetched(cached);
            Report(log, "[网络] 自有市场列表使用本地缓存");
        }
        return cached;
    }

    private static void MarkFetched(string json)
    {
        lock (Sync)
        {
            _memoryJson = json;
            _memoryAt = DateTime.UtcNow;
            _failedAt = DateTime.MinValue;
        }
    }

    // ---------- 合并 ----------

    private sealed class OverlayEntry
    {
        public string Id = "";
        public string Repo = "";
        public string Name = "";
        public string Version = "";
        public string Category = "";
        public string Description = "";
        public string Author = "";
        public string UpdateDate = "";
        public string ReleaseUrl = "";
        public string VerifiedAsset = "";
        public bool Hidden;
        public bool? Prerelease;
        public List<string>? Tags;
        public List<string>? BootNames;
        public List<string>? Aliases;
        public List<MarketDependency>? Dependencies;
        public List<MarketEdition>? Editions;
    }

    /// <summary>把覆盖层 JSON 合并进市场索引并重建分类。</summary>
    public static void Apply(MarketCatalog catalog, string json, Action<string>? log)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        var withdrawn = new HashSet<string>(PropStrings(root, "withdrawnIds") ?? [], StringComparer.OrdinalIgnoreCase);
        if (withdrawn.Count > 0)
            catalog.Mods.RemoveAll(m => withdrawn.Contains(m.Id));

        if (!root.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return;

        var applied = 0;
        foreach (var item in mods.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var entry = ParseEntry(item);
            if (entry == null) continue;

            if (entry.Hidden)
            {
                var hidden = FindMatch(catalog.Mods, entry);
                if (hidden != null) catalog.Mods.Remove(hidden);
                continue;
            }

            var target = FindMatch(catalog.Mods, entry);
            if (target == null)
            {
                if (entry.Name.Length == 0 || entry.Repo.Length == 0) continue;
                target = new MarketMod { Id = entry.Id, Name = entry.Name, Repo = entry.Repo };
                catalog.Mods.Add(target);
            }
            ApplyFields(target, entry);
            applied++;
        }

        RebuildCategories(catalog);
        if (applied > 0) Report(log, $"[网络] 自有市场列表已合并：{applied} 条");
    }

    private static void ApplyFields(MarketMod mod, OverlayEntry entry)
    {
        if (entry.Name.Length > 0) mod.Name = entry.Name;
        if (entry.Version.Length > 0) mod.Version = entry.Version;
        if (entry.Category.Length > 0) mod.Category = entry.Category;
        if (entry.Description.Length > 0) mod.Description = entry.Description;
        if (entry.Author.Length > 0) mod.Author = entry.Author;
        if (entry.Repo.Length > 0) mod.Repo = entry.Repo;
        if (entry.ReleaseUrl.Length > 0) mod.ReleaseUrl = entry.ReleaseUrl;
        if (entry.UpdateDate.Length > 0) mod.UpdateDate = entry.UpdateDate;
        if (entry.VerifiedAsset.Length > 0) mod.VerifiedAssetFileName = entry.VerifiedAsset;
        if (entry.Prerelease.HasValue) mod.Prerelease = entry.Prerelease.Value;

        if (entry.Tags is { Count: > 0 })
        {
            mod.TagList = entry.Tags;
            mod.Tags = string.Join(" · ", entry.Tags);
        }
        if (entry.BootNames is { Count: > 0 }) mod.BootNames = entry.BootNames;
        if (entry.Aliases is { Count: > 0 }) mod.Aliases = entry.Aliases;
        if (entry.Dependencies is { Count: > 0 }) mod.Dependencies = entry.Dependencies;
        if (entry.Editions is { Count: > 0 }) mod.Editions = entry.Editions;
    }

    /// <summary>匹配优先级：id → (repo, 产品线) → 校验资产名 → bootNames/aliases → 名称。</summary>
    private static MarketMod? FindMatch(List<MarketMod> mods, OverlayEntry entry)
    {
        if (entry.Id.Length > 0)
        {
            var byId = mods.FirstOrDefault(m => m.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }

        if (entry.Repo.Length > 0)
        {
            var candidates = mods.Where(m => RepoEquals(m.Repo, entry.Repo)).ToList();
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1)
            {
                var line = candidates.Where(m => ProductLineMatches(m, entry)).ToList();
                if (line.Count == 1) return line[0];
                if (entry.Name.Length > 0)
                    return line.FirstOrDefault(m => m.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
                           ?? candidates.FirstOrDefault(m => m.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                return null;
            }
        }

        if (entry.VerifiedAsset.Length > 0)
        {
            var hit = mods.FirstOrDefault(m =>
                m.VerifiedAssetFileName.Equals(entry.VerifiedAsset, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entry.BootNames != null) foreach (var s in entry.BootNames) keys.Add(s);
        if (entry.Aliases != null) foreach (var s in entry.Aliases) keys.Add(s);
        if (keys.Count > 0)
        {
            var hit = mods.FirstOrDefault(m => m.BootNames.Concat(m.Aliases).Any(keys.Contains));
            if (hit != null) return hit;
        }

        return entry.Name.Length > 0
            ? mods.FirstOrDefault(m => m.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>同仓库多产品线（如 mod / facemod）时，用包型标识区分。</summary>
    private static bool ProductLineMatches(MarketMod mod, OverlayEntry entry)
    {
        if (entry.VerifiedAsset.Length > 0 &&
            mod.VerifiedAssetFileName.Equals(entry.VerifiedAsset, StringComparison.OrdinalIgnoreCase)) return true;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (entry.BootNames != null) foreach (var s in entry.BootNames) keys.Add(s);
        if (entry.Aliases != null) foreach (var s in entry.Aliases) keys.Add(s);
        if (entry.Editions != null)
        {
            foreach (var e in entry.Editions)
            {
                if (e.BootName.Length > 0) keys.Add(e.BootName);
                if (e.AssetSeries.Length > 0) keys.Add(e.AssetSeries);
            }
        }
        if (keys.Count == 0) return false;

        return mod.BootNames
            .Concat(mod.Aliases)
            .Concat(mod.Editions.Select(e => e.BootName))
            .Concat(mod.Editions.Select(e => e.AssetSeries))
            .Any(keys.Contains);
    }

    private static bool RepoEquals(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static void RebuildCategories(MarketCatalog catalog)
    {
        catalog.Categories.Clear();
        foreach (var category in catalog.Mods.Select(m => m.Category)
                     .Where(c => c.Length > 0).Distinct().OrderBy(c => c))
            catalog.Categories.Add(category);
    }

    // ---------- 解析 ----------

    private static OverlayEntry? ParseEntry(JsonElement item)
    {
        var name = PropString(item, "name");
        var repo = ModStarService.NormalizeRepo(PropString(item, "repo")) ?? "";
        var id = PropString(item, "id");
        if (name.Length == 0 && repo.Length == 0 && id.Length == 0) return null;

        var entry = new OverlayEntry
        {
            Id = id,
            Repo = repo,
            Name = name,
            Version = PropString(item, "version"),
            Category = PropString(item, "category"),
            Description = PropString(item, "description"),
            Author = PropString(item, "author"),
            UpdateDate = PropString(item, "updateDate"),
            ReleaseUrl = PropString(item, "releaseUrl"),
            Hidden = item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
            Tags = PropStrings(item, "tags"),
            BootNames = PropStrings(item, "bootNames"),
            Aliases = PropStrings(item, "aliases"),
            Dependencies = ParseDependencies(item),
            Editions = ParseEditions(item),
        };

        if (item.TryGetProperty("prerelease", out var pre))
            entry.Prerelease = pre.ValueKind == JsonValueKind.True;

        if (item.TryGetProperty("verifiedReleaseAsset", out var verified) &&
            verified.ValueKind == JsonValueKind.Object)
        {
            var fileName = PropString(verified, "fileName");
            if (fileName.Length == 0) fileName = FileNameFromUrl(PropString(verified, "sourceUrl"));
            entry.VerifiedAsset = fileName;
        }

        return entry;
    }

    private static List<MarketDependency>? ParseDependencies(JsonElement item)
    {
        if (!item.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Array)
            return null;
        var result = new List<MarketDependency>();
        foreach (var dep in deps.EnumerateArray())
        {
            var id = PropString(dep, "id");
            if (id.Length == 0) id = PropString(dep, "name");
            if (id.Length > 0)
                result.Add(new MarketDependency { Id = id, Version = PropString(dep, "version") });
        }
        return result;
    }

    private static List<MarketEdition>? ParseEditions(JsonElement item)
    {
        if (!item.TryGetProperty("editions", out var editions) &&
            !item.TryGetProperty("packageEditions", out editions))
            return null;
        if (editions.ValueKind != JsonValueKind.Array) return null;

        var result = new List<MarketEdition>();
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

    private static string PropString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static List<string>? PropStrings(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(name, out var array) ||
            array.ValueKind != JsonValueKind.Array) return null;
        var result = new List<string>();
        foreach (var value in array.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                result.Add(text);
        return result;
    }

    private static string FileNameFromUrl(string url)
    {
        if (url.Length == 0) return "";
        var clean = url.Split('?')[0].TrimEnd('/');
        var index = clean.LastIndexOf('/');
        return index >= 0 ? Uri.UnescapeDataString(clean[(index + 1)..]) : "";
    }

    // ---------- 缓存 ----------

    private sealed class CacheMeta
    {
        public string Etag { get; set; } = "";
        public DateTime FetchedAt { get; set; }
    }

    private static string? LoadCache()
    {
        try
        {
            return File.Exists(CacheFile) ? File.ReadAllText(CacheFile) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string json, string etag)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, json);
            File.WriteAllText(MetaFile, JsonSerializer.Serialize(new CacheMeta
            {
                Etag = etag,
                FetchedAt = DateTime.UtcNow,
            }));
        }
        catch
        {
            // 写缓存失败不影响本次合并
        }
    }

    private static string LoadEtag()
    {
        try
        {
            if (!File.Exists(MetaFile)) return "";
            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(MetaFile));
            return meta?.Etag ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static void Report(Action<string>? log, string message) => log?.Invoke(message);
}
