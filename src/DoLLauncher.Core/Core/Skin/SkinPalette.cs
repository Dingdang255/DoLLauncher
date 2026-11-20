using DoLLauncher.Core.Theme;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 美化包变量的合并与调色板覆盖计算（纯函数，不依赖任何 UI 框架）。
/// 优先级从高到低：<c>android.variables</c> → 顶层 <c>variables</c> → CSS <c>:root</c>。
/// </summary>
public static class SkinPalette
{
    /// <summary>
    /// 按当前深浅色合并三处变量。键统一去掉 <c>--</c> 前缀并忽略大小写，
    /// 值保持原样（可能是颜色、长度或其它 CSS 片段）。
    /// </summary>
    public static Dictionary<string, string> Merge(SkinManifest? manifest, SkinStyleSheet? sheet, bool dark)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (sheet != null)
        {
            foreach (var pair in sheet.VariablesFor(dark))
                merged[NormalizeKey(pair.Key)] = pair.Value;
        }

        Add(merged, manifest?.Variables);
        Add(merged, manifest?.Android?.Variables);
        return merged;
    }

    /// <summary>
    /// 从合并后的变量里挑出可用于覆盖主题调色板的颜色项。
    /// 返回的键是 Core 使用的调色板键（如 <c>AccentBrush</c>），由平台再做名称适配。
    /// </summary>
    public static Dictionary<string, string> ResolveColorOverrides(IReadOnlyDictionary<string, string> variables)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in variables)
        {
            var normalized = NormalizeKey(pair.Key);
            if (!PaletteMap.TryGetValue(normalized, out var target)) continue;
            if (!RgbColor.TryParse(pair.Value, out _)) continue;
            result[target] = pair.Value.Trim();
        }
        return result;
    }

    /// <summary>取美化包主色：优先 <c>AccentBrush</c>，其次 <c>AccentColor</c> / <c>accent</c>。</summary>
    public static bool TryGetAccentColor(IReadOnlyDictionary<string, string> variables, out string hex)
    {
        hex = string.Empty;
        foreach (var key in new[] { "accentbrush", "accentcolor", "accent", "primary" })
        {
            if (!variables.TryGetValue(key, out var value)) continue;
            if (!RgbColor.TryParse(value, out _)) continue;
            hex = value.Trim();
            return true;
        }
        return false;
    }

    /// <summary>把变量名规整成只含小写字母与数字的形式（<c>--Accent-Brush</c> → <c>accentbrush</c>）。</summary>
    public static string NormalizeKey(string? key)
    {
        var text = (key ?? string.Empty).Trim().TrimStart('-');
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static void Add(Dictionary<string, string> target, Dictionary<string, string>? source)
    {
        if (source == null) return;
        foreach (var pair in source)
        {
            var key = NormalizeKey(pair.Key);
            if (key.Length == 0) continue;
            target[key] = pair.Value ?? string.Empty;
        }
    }

    private static readonly Dictionary<string, string> PaletteMap = BuildPaletteMap();

    private static Dictionary<string, string> BuildPaletteMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 调色板键本身、去掉 Brush 后缀的写法、以及常见别名
        foreach (var key in ThemePaletteBuilder.AccentKeys)
        {
            map[NormalizeKey(key)] = key;
            if (key.EndsWith("Brush", StringComparison.OrdinalIgnoreCase))
                map[NormalizeKey(key[..^5])] = key;
        }

        void Alias(string name, string key) => map[NormalizeKey(name)] = key;

        Alias("accent", "AccentBrush");
        Alias("accentcolor", "AccentBrush");
        Alias("primary", "AccentBrush");
        Alias("primarydark", "AccentBrush");
        Alias("secondary", "AccentPaleBrush");
        Alias("tertiary", "AccentStrongBrush");
        Alias("secondarydarktext", "AccentOnBrush");
        Alias("accentforeground", "AccentOnBrush");
        Alias("accenttext", "AccentOnBrush");

        return map;
    }
}
