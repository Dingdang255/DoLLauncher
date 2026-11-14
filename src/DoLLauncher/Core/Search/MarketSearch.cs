using DoLLauncher.Models;

namespace DoLLauncher.Core.Search;

public enum MarketMatchMode
{
    /// <summary>部分匹配：关键词出现在文本任意位置即可。</summary>
    Contains,
    /// <summary>完全匹配：关键词必须是完整词（两侧是空白、标点或分隔符）。</summary>
    Exact,
    /// <summary>标签范围下：结果标签包含全部搜索标签（允许有多余标签）。</summary>
    TagAll,
    /// <summary>标签范围下：结果标签包含任意一个搜索标签。</summary>
    TagAny,
}

public enum MarketSearchScope
{
    All,
    Title,
    Author,
    Description,
    /// <summary>标签范围：按模组标签列表匹配搜索词。</summary>
    Tags,
}

public enum MarketSortMode
{
    /// <summary>默认：保持 mods.json 里的原始顺序，不做重排。</summary>
    Default,
    UpdateDate,
    Name,
}

/// <summary>模组市场的高级搜索：匹配模式 × 搜索范围 × 排序方式（平台无关）。</summary>
public static class MarketSearch
{
    private static readonly char[] TagSeparators = { ' ', '\t', ',', '，', ';', '；', '/', '|', '·' };
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

    public static List<MarketMod> Sort(IEnumerable<MarketMod> mods, MarketSortMode mode, bool descending)
    {
        var list = mods.ToList();
        switch (mode)
        {
            case MarketSortMode.Default:
                // 默认排序也支持倒序：直接反转索引里的原始顺序
                if (descending) list.Reverse();
                return list;
            case MarketSortMode.Name:
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
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

        // 标签范围：比较的是“标签列表”本身，而不是拼成一段文字
        if (scope == MarketSearchScope.Tags)
        {
            var tags = mod.TagList.Count > 0
                ? mod.TagList
                : mod.Tags.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries).ToList();
            var set = new HashSet<string>(
                tags.Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);

            return mode switch
            {
                // 完全匹配：标签集合与搜索词集合完全一致
                MarketMatchMode.Exact => set.SetEquals(tokens),
                // 部分匹配 · 含全部：结果的标签包含所有搜索词，允许有多余标签
                MarketMatchMode.TagAll => tokens.All(set.Contains),
                // 部分匹配 · 含任一：结果只要命中任意一个搜索词
                _ => tokens.Any(set.Contains),
            };
        }

        var text = (scope switch
        {
            MarketSearchScope.Title => mod.Name,
            MarketSearchScope.Author => mod.Author,
            MarketSearchScope.Description => mod.Description,
            _ => mod.SearchText,
        }).ToLowerInvariant();

        return mode switch
        {
            MarketMatchMode.Exact => tokens.All(t => ContainsWholeWord(text, t)),
            _ => tokens.All(t => text.Contains(t, StringComparison.Ordinal)),
        };
    }

    /// <summary>
    /// 完全匹配：关键字两侧必须是词边界（空白、标点、分隔符）。
    /// 汉字、英文、数字都算词字符，所以「服装」不会命中「服装扩展」，但标签里的「服装」可以命中。
    /// </summary>
    private static bool ContainsWholeWord(string text, string token)
    {
        var index = 0;
        while (index <= text.Length - token.Length)
        {
            var hit = text.IndexOf(token, index, StringComparison.Ordinal);
            if (hit < 0) return false;
            var before = hit == 0 ? ' ' : text[hit - 1];
            var after = hit + token.Length >= text.Length ? ' ' : text[hit + token.Length];
            if (!IsWordChar(before) && !IsWordChar(after)) return true;
            index = hit + token.Length;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    private static string[] SplitTokens(string? keyword) =>
        (keyword ?? string.Empty).Trim().ToLowerInvariant()
        .Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries);
}
