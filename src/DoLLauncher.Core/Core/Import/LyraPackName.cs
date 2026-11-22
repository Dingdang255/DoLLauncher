using System.IO;
using System.Text.RegularExpressions;

namespace DoLLauncher.Core.Import;

/// <summary>Lyra 整合包资产名解析结果。</summary>
public sealed class LyraPackInfo
{
    public string GameVersion { get; init; } = string.Empty;
    public string ChsVersion { get; init; } = string.Empty;
    public string ReleaseDate { get; init; } = string.Empty;
    public bool IsPolyfill { get; init; }
    public List<string> ComboTokens { get; init; } = new();

    /// <summary>可读的组合标签，例如 BESC+UCB；无组合时显示「基础」。</summary>
    public string ComboLabel
    {
        get
        {
            var parts = ComboTokens.Select(TokenLabel).ToList();
            var label = parts.Count == 0 ? "基础" : string.Join("+", parts);
            return IsPolyfill ? label + " · 兼容版" : label;
        }
    }

    private static string TokenLabel(string token) => token.ToLowerInvariant() switch
    {
        "besc" => "BESC",
        "hikari" => "HIKARI",
        "goose" => "GOOSE",
        "ucb" => "UCB",
        "au-f" => "AU-F",
        "au-m" => "AU-M",
        "au-a" => "AU-A",
        "base" => "基础",
        "polyfill" => "兼容版",
        _ => token.ToUpperInvariant(),
    };
}

/// <summary>
/// 解析 Lyra 资产名。当前命名：<c>DoL-{游戏版本}-Lyra-{汉化版本}-{组合}-{日期}.zip</c>；
/// 历史命名：<c>DoL-{游戏版本}-chsmods-{汉化版本}-…</c>、<c>dol-{游戏版本}-chs-…</c>、
/// <c>dol-chs-…</c>、<c>dol-we-chs-…</c>（后两种资产名里没有游戏版本，需要 release tag 兜底）。
/// 解析失败时调用方回退到原文件名。
/// </summary>
public static class LyraPackName
{
    private static readonly Regex VersionToken = new(@"\d+(?:\.\d+){2,3}", RegexOptions.Compiled);
    private static readonly Regex DateToken = new(@"^\d{4}(?:\.\d+)?$", RegexOptions.Compiled);

    public static bool TryParse(string? fileName, out LyraPackInfo info) => TryParse(fileName, null, out info);

    public static bool TryParse(string? fileName, string? releaseTag, out LyraPackInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var name = Path.GetFileName(fileName.Trim());
        // 只去掉已知包扩展名：目录名里本身带版本点号，不能当成扩展名裁掉
        var stem = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".dolpack", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
        if (stem.Length == 0) return false;
        var polyfill = false;
        if (stem.StartsWith("polyfill-", StringComparison.OrdinalIgnoreCase))
        {
            polyfill = true;
            stem = stem["polyfill-".Length..];
        }

        var lyraIndex = stem.IndexOf("-Lyra-", StringComparison.OrdinalIgnoreCase);
        if (lyraIndex > 0)
            return TryBuild(stem[..lyraIndex], stem[(lyraIndex + "-Lyra-".Length)..], releaseTag, polyfill, false, out info);

        // 旧命名：chsmods / chs 标记；chsmods 一定带游戏版本，chs 可能没有
        var markerIndex = stem.IndexOf("-chsmods-", StringComparison.OrdinalIgnoreCase);
        var markerLength = "-chsmods-".Length;
        if (markerIndex <= 0)
        {
            markerIndex = stem.IndexOf("-chs-", StringComparison.OrdinalIgnoreCase);
            markerLength = "-chs-".Length;
        }
        if (markerIndex <= 0) return false;

        return TryBuild(stem[..markerIndex], stem[(markerIndex + markerLength)..], releaseTag, polyfill, false, out info);
    }

    /// <summary>把「版本前缀 + 版本之后的部分」拼成解析结果。</summary>
    private static bool TryBuild(string prefix, string rest, string? releaseTag, bool polyfill,
        bool requireCombos, out LyraPackInfo info)
    {
        info = null!;

        var game = VersionToken.Matches(prefix).LastOrDefault()?.Value;
        // 早期资产名不含游戏版本（如 dol-chs-a1.4.1-…），用 release tag 里的第一个版本号兜底
        if (string.IsNullOrEmpty(game))
            game = VersionToken.Matches(releaseTag ?? string.Empty).FirstOrDefault()?.Value ?? string.Empty;

        var tokens = rest.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0) return false;

        var chs = tokens[0];
        if (chs.Length == 0 || !(char.IsDigit(chs[0]) || chs[0] is 'a' or 'A')) return false;
        tokens.RemoveAt(0);

        var date = string.Empty;
        if (tokens.Count > 0 && DateToken.IsMatch(tokens[^1]))
        {
            date = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
        }

        // 旧汉化产物（dol-{版本}-chs-{汉化版本}）与旧 Lyra 包同形，用「是否有组合标记」区分
        if (requireCombos && tokens.Count == 0) return false;

        info = new LyraPackInfo
        {
            GameVersion = game,
            ChsVersion = chs,
            ReleaseDate = date,
            IsPolyfill = polyfill,
            ComboTokens = MergeComboTokens(tokens),
        };
        return true;
    }

    /// <summary>资产名用连字符分隔，`au-a` 会被拆成两段，这里合并回一个组合 token。</summary>
    private static List<string> MergeComboTokens(List<string> tokens)
    {
        var merged = new List<string>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals("au", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Count &&
                tokens[i + 1].ToLowerInvariant() is "f" or "m" or "a")
            {
                merged.Add("au-" + tokens[i + 1].ToLowerInvariant());
                i++;
                continue;
            }
            merged.Add(tokens[i]);
        }
        return merged;
    }
}
