using System.Text.Json;

namespace DoLLauncher.Services;

public sealed record LauncherRelease(string Tag, string Url);

/// <summary>启动器自身的更新检查：读取 GitHub 最新 Release，与当前 tag 比较。</summary>
public static class UpdateService
{
    private const string Repo = "Dingdang255/DoLLauncher";

    public static async Task<LauncherRelease> CheckAsync()
    {
        var text = await DownloadService.GetGithubApiAsync($"repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        return new LauncherRelease(tag.Trim(), url.Trim());
    }

    /// <summary>
    /// 远端 tag 是否比当前版本新。允许 v 前缀，按数字段比较；
    /// 任一方不是数字版本时，只要字符串不同就按「有更新」处理。
    /// </summary>
    public static bool IsNewer(string remoteTag, string currentTag)
    {
        var remote = ParseVersion(remoteTag);
        var current = ParseVersion(currentTag);
        if (remote == null || current == null) return remoteTag != currentTag;

        var count = Math.Max(remote.Count, current.Count);
        for (var i = 0; i < count; i++)
        {
            var r = i < remote.Count ? remote[i] : 0;
            var c = i < current.Count ? current[i] : 0;
            if (r != c) return r > c;
        }
        return false;
    }

    private static List<int>? ParseVersion(string tag)
    {
        var text = tag.Trim().TrimStart('v', 'V');
        var parts = text.Split('.');
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            // 只接受纯数字段；带后缀（如 1.0.0-beta）时退回字符串比较
            if (!int.TryParse(part, out var value)) return null;
            numbers.Add(value);
        }
        return numbers.Count == 0 ? null : numbers;
    }
}
