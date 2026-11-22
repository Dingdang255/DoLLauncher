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
/// 解析 Lyra 资产名：<c>DoL-{游戏版本}-Lyra-{汉化版本}-{组合}-{日期}.zip</c>，
/// 同时支持 <c>polyfill-</c> 前缀与无组合的基包。解析失败时调用方回退到原文件名。
/// </summary>
public static class LyraPackName
{
    private static readonly Regex VersionToken = new(@"\d+(?:\.\d+){2,3}", RegexOptions.Compiled);
    private static readonly Regex DateToken = new(@"^\d{4}(?:\.\d+)?$", RegexOptions.Compiled);

    public static bool TryParse(string? fileName, out LyraPackInfo info)
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
        if (lyraIndex <= 0) return false;

        var game = VersionToken.Matches(stem[..lyraIndex]).LastOrDefault()?.Value;
        if (string.IsNullOrEmpty(game)) return false;

        var rest = stem[(lyraIndex + "-Lyra-".Length)..];
        var tokens = rest.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count == 0) return false;

        var chs = tokens[0];
        if (chs.Length == 0 || !char.IsDigit(chs[0])) return false;
        tokens.RemoveAt(0);

        var date = string.Empty;
        if (tokens.Count > 0 && DateToken.IsMatch(tokens[^1]))
        {
            date = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
        }

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
