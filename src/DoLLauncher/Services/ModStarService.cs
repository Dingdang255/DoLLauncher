using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DoLLauncher.Services;

/// <summary>
/// GitHub Star 数缓存：只在打开模组详情时按需拉取单个仓库，结果缓存 24 小时，
/// 避免频繁请求接口触发限流；拉取失败时保留旧值并返回 null。
/// </summary>
public static class ModStarService
{
    private sealed class StarEntry
    {
        [JsonPropertyName("stars")] public int Stars { get; set; }
        [JsonPropertyName("fetchedAt")] public DateTime FetchedAt { get; set; }
    }

    private sealed class StarCache
    {
        [JsonPropertyName("repos")] public Dictionary<string, StarEntry> Repos { get; set; } = new();
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private static readonly object Sync = new();
    private static readonly Regex RepoPattern = new(@"^[\w.-]+/[\w.-]+$", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(@"github\.com/([\w.-]+)/([\w.-]+)", RegexOptions.Compiled);
    private static StarCache? _cache;

    private static string CacheFile => Path.Combine(Paths.Root, "cache", "mod-stars.json");

    /// <summary>把仓库地址规范化为 owner/repo；无法识别时返回 null。</summary>
    public static string? NormalizeRepo(string? repo)
    {
        var text = (repo ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        var match = UrlPattern.Match(text);
        if (match.Success) return match.Groups[1].Value + "/" + match.Groups[2].Value.TrimEnd('.', '/');
        text = text.TrimEnd('/');
        return RepoPattern.IsMatch(text) ? text : null;
    }

    /// <summary>
    /// 取仓库的 Star 数：24 小时缓存内直接用缓存，过期或没有记录时拉取一次并落盘。
    /// 取不到（无仓库、限流、网络失败）返回 null。
    /// </summary>
    public static async Task<int?> TryGetAsync(string? repo, CancellationToken ct = default)
    {
        var key = NormalizeRepo(repo);
        if (key == null) return null;

        lock (Sync)
        {
            if (Load().Repos.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
                return cached.Stars;
        }

        try
        {
            var json = await DownloadService.GetGithubApiAsync("repos/" + key);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("stargazers_count", out var stars) &&
                stars.TryGetInt32(out var count))
            {
                lock (Sync)
                {
                    var cache = Load();
                    cache.Repos[key] = new StarEntry { Stars = count, FetchedAt = DateTime.UtcNow };
                    Save(cache);
                }
                return count;
            }
        }
        catch (Exception ex)
        {
            LogService.Append($"获取 GitHub Star 失败（{key}）：{ex.Message}");
        }

        return null;
    }

    private static StarCache Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(CacheFile))
            {
                var text = File.ReadAllText(CacheFile);
                _cache = JsonSerializer.Deserialize<StarCache>(text) ?? new StarCache();
                return _cache;
            }
        }
        catch
        {
            // 缓存损坏时重建
        }
        _cache = new StarCache();
        return _cache;
    }

    private static void Save(StarCache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // 写缓存失败不影响本次显示
        }
    }
}
