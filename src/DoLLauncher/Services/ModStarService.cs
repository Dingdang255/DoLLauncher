using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DoLLauncher.Services;

/// <summary>
/// GitHub Star 数缓存：只有用户选择「按 Star 排序」时才按需拉取，结果缓存 24 小时，
/// 避免频繁请求接口触发限流；拉取失败时保留旧值，不影响其它排序方式。
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

    /// <summary>缓存中的 Star 数（可能已过期）；没有记录时返回 null。</summary>
    public static int? GetCached(string? repo)
    {
        var key = NormalizeRepo(repo);
        if (key == null) return null;
        lock (Sync)
        {
            return Load().Repos.TryGetValue(key, out var entry) ? entry.Stars : null;
        }
    }

    /// <summary>缓存是否仍在 24 小时有效期内。</summary>
    public static bool IsFresh(string? repo)
    {
        var key = NormalizeRepo(repo);
        if (key == null) return false;
        lock (Sync)
        {
            return Load().Repos.TryGetValue(key, out var entry) &&
                   DateTime.UtcNow - entry.FetchedAt < Ttl;
        }
    }

    /// <summary>顺序拉取缺失或过期的仓库，返回成功条数。限流或网络失败时保留旧缓存。</summary>
    public static async Task<int> RefreshAsync(IEnumerable<string> repos, CancellationToken ct = default)
    {
        var keys = repos.Select(NormalizeRepo).Where(r => r != null).Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !IsFresh(r))
            .ToList();
        if (keys.Count == 0) return 0;

        var ok = 0;
        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
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
                    ok++;
                }
            }
            catch (Exception ex)
            {
                LogService.Append($"获取 GitHub Star 失败（{key}）：{ex.Message}");
            }

            // 轻微节流，避免瞬间打满接口
            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
        }
        return ok;
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
            // 写缓存失败不影响本次排序
        }
    }
}
