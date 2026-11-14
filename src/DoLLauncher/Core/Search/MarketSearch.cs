using DoLLauncher.Models;

namespace DoLLauncher.Core.Search;

public enum MarketMatchMode
{
    Contains,
    WholeWord,
    AnyTag,
    AllTags,
}

public enum MarketSearchScope
{
    All,
    Title,
    Author,
    Description,
}

public enum MarketSortMode
{
    /// <summary>默认：保持 mods.json 里的原始顺序，不做重排。</summary>
    Default,
    UpdateDate,
    Name,
    Stars,
}

/// <summary>模组市场的高级搜索：匹配模式 × 搜索范围 × 排序方式（平台无关）。</summary>
public static class MarketSearch
{
    private static readonly char[] TagSeparators = { ' ', '\t', ',', '，', ';', '；', '/', '|' };
    private static readonly char[] KeywordSeparators = { ' ', '\t', ',', '，' };

    public static IEnumerable<MarketMod> Filter(IEnumerable<MarketMod> mods, string? keyword,
        MarketMatchMode mode, MarketSearchScope scope)
    {
        var tokens = SplitTokens(keyword);
        foreach (var mod in mods)
        {
            if (Matches(mod, tokens, mode, scope)) yield return mod;
        }
    }

    public static List<MarketMod> Sort(IEnumerable<MarketMod> mods, MarketSortMode mode, bool descending,
        Func<MarketMod, int?>? stars)
    {
        var list = mods.ToList();
        switch (mode)
        {
            case MarketSortMode.Default:
                return list;
            case MarketSortMode.Name:
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                break;
            case MarketSortMode.Stars:
                list.Sort((a, b) => (stars?.Invoke(a) ?? -1).CompareTo(stars?.Invoke(b) ?? -1));
                break;
            default:
                list.Sort((a, b) => string.Compare(a.UpdateDate, b.UpdateDate, StringComparison.OrdinalIgnoreCase));
                break;
        }
        if (descending) list.Reverse();
        return list;
    }

    private static bool Matches(MarketMod mod, string[] tokens, MarketMatchMode mode, MarketSearchScope scope)
    {
        if (tokens.Length == 0) return true;

        if (mode is MarketMatchMode.AnyTag or MarketMatchMode.AllTags)
        {
            var tags = mod.Tags.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .ToArray();
            bool Hit(string token) => tags.Any(t =>
                t.Contains(token, StringComparison.Ordinal) || token.Contains(t, StringComparison.Ordinal));
            return mode == MarketMatchMode.AnyTag ? tokens.Any(Hit) : tokens.All(Hit);
        }

        var text = (scope switch
        {
            MarketSearchScope.Title => mod.Name,
            MarketSearchScope.Author => mod.Author,
            MarketSearchScope.Description => mod.Description,
            _ => mod.SearchText,
        }).ToLowerInvariant();

        return mode == MarketMatchMode.Contains
            ? tokens.All(t => text.Contains(t, StringComparison.Ordinal))
            : tokens.All(t => ContainsWholeWord(text, t));
    }

    /// <summary>全词匹配：关键字两侧必须是分隔符；中文按单字算词边界，避免整段文字匹配不到。</summary>
    private static bool ContainsWholeWord(string text, string token)
    {
        var index = 0;
        while (index <= text.Length - token.Length)
        {
            var hit = text.IndexOf(token, index, StringComparison.Ordinal);
            if (hit < 0) return false;
            var before = hit == 0 ? ' ' : text[hit - 1];
            var after = hit + token.Length >= text.Length ? ' ' : text[hit + token.Length];
            if (!IsAsciiWordChar(before) && !IsAsciiWordChar(after)) return true;
            index = hit + token.Length;
        }
        return false;
    }

    private static bool IsAsciiWordChar(char c) => c < 128 && char.IsLetterOrDigit(c);

    private static string[] SplitTokens(string? keyword) =>
        (keyword ?? string.Empty).Trim().ToLowerInvariant()
        .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries);
}
