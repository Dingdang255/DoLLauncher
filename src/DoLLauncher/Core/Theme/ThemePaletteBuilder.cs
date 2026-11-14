namespace DoLLauncher.Core.Theme;

/// <summary>
/// 主题色自定义：把用户选的主色按百分比混进原始调色板的强调色系。
/// 纯函数，不依赖 WPF；主色为空时结果与原始调色板完全一致。
/// </summary>
public static class ThemePaletteBuilder
{
    /// <summary>主色默认值（与浅色主题 AccentBrush 一致）。</summary>
    public const string DefaultAccentColor = "#FF1370F3";

    /// <summary>参与混合的强调色系键。</summary>
    public static readonly string[] AccentKeys =
    {
        "AccentBrush",
        "AccentHoverBrush",
        "AccentSoftBrush",
        "AccentPaleBrush",
        "AccentStrongBrush",
        "AccentOnBrush",
        "TabChipActiveBgBrush",
        "TabChipActiveFgBrush",
    };

    /// <summary>
    /// 计算最终调色板：以用户选定的主色直接作为强调色（不再与底色混合稀释饱和度），
    /// 再按部件用途派生出一组深浅 / 对比度不同的变体，
    /// 避免「主题色文本」与「主题色背景」取到同一个颜色而糊在一起。
    /// 主色为空 / 非法时原样返回。
    /// </summary>
    public static Dictionary<string, string> Build(
        IReadOnlyDictionary<string, string> original, string? accentColor, int saturationPercent = 100)
    {
        var result = new Dictionary<string, string>(original);
        if (!RgbColor.TryParse(accentColor, out var mixed)) return result;

        // 原色是当前模式的背景底色（浅色接近 #F5F5F5、深色接近 #1C1C1E），只用于派生浅色块与对比度保护
        var baseHex = original.TryGetValue("BgBrush", out var bg) ? bg : "#FFFFFFFF";
        if (!RgbColor.TryParse(baseHex, out var baseRgb)) return result;

        // 1) 主色：直接使用所选颜色；浅色模式下把过淡的颜色补到推荐鲜艳度，作为滑块 100% 的基准
        var accent = mixed;
        if (baseRgb.IsLight) accent = Vivid(accent, 0.72);
        // 滑块值作为相对基准的比例：100 = 上面的推荐鲜艳度，0 = 灰色。
        // 降饱和时同步微调亮度，保证「亮度」滑块与视觉明暗不打架，且相邻百分比过渡连续。
        var saturation = Math.Clamp(saturationPercent, 0, 100);
        if (saturation < 100)
        {
            var (h0, s0, _) = accent.ToHsl();
            accent = WithLuminance(h0, s0 * saturation / 100.0, accent.RelativeLuminance());
        }
        // 2) 悬停色：整体提亮一档，避免比主色更暗、显得发闷
        var hover = RgbColor.Lerp(accent, White, 0.18);
        // 3) 柔和描边 / 很浅的底色：向页面底色靠拢，保证主色文本压在上面仍然清晰
        //    浅色模式下多掺一点主色，避免浅灰一片、看不出主题色
        var soft = RgbColor.Lerp(baseRgb, accent, baseRgb.IsLight ? 0.58 : 0.45);
        var pale = RgbColor.Lerp(baseRgb, accent, baseRgb.IsLight ? 0.22 : 0.14);
        // 主按钮：底色直接用主色（保持亮度），文字自动取对比更强的一侧（白 / 近黑）
        var strong = accent;
        var onAccent = strong.ContrastWith(White) >= strong.ContrastWith(Black) ? White : Black;
        // 4) 分页 / 选中块：底色用主色，前景取与它对比更强的一侧，避免同色叠同色
        var chipBg = accent;
        var chipFg = chipBg.ContrastWith(White) >= chipBg.ContrastWith(Black) ? White : Black;

        // 主色过亮（例如用户选了黄色）导致前景仍不够清晰时，细步压暗到刚好可读
        for (var step = 0; step < 10 && chipFg.ContrastWith(chipBg) < 3.0; step++)
        {
            chipBg = RgbColor.Lerp(chipBg, Black, 0.05);
            chipFg = chipBg.ContrastWith(White) >= chipBg.ContrastWith(Black) ? White : Black;
        }

        Set(result, "AccentBrush", accent);
        Set(result, "AccentHoverBrush", hover);
        Set(result, "AccentSoftBrush", soft);
        Set(result, "AccentPaleBrush", pale);
        // 浅色模式下选中项 / 悬停底也带一点主题色，避免整版发灰
        if (baseRgb.IsLight)
        {
            Set(result, "CardSelectedBrush", RgbColor.Lerp(baseRgb, accent, 0.18));
            Set(result, "NavHoverBrush", RgbColor.Lerp(baseRgb, accent, 0.07));
        }
        Set(result, "AccentStrongBrush", strong);
        Set(result, "AccentOnBrush", onAccent);
        Set(result, "TabChipActiveBgBrush", chipBg);
        Set(result, "TabChipActiveFgBrush", chipFg);
        return result;
    }

    private static void Set(Dictionary<string, string> palette, string key, RgbColor color)
    {
        if (palette.ContainsKey(key)) palette[key] = color.ToArgbHex();
    }

    /// <summary>饱和度低于阈值时按 HSL 补到阈值（保持色相与亮度），让浅色模式下的主题色不发灰。</summary>
    private static RgbColor Vivid(RgbColor color, double minSaturation)
    {
        var (h, s, l) = color.ToHsl();
        if (s >= minSaturation) return color;
        return RgbColor.FromHsl(h, minSaturation, l);
    }

    /// <summary>
    /// 在指定色相 / 饱和度下二分寻找亮度，使相对亮度尽量等于目标值，
    /// 这样改饱和度时视觉明暗不变，不会和亮度滑块互相干扰。
    /// </summary>
    private static RgbColor WithLuminance(double hue, double saturation, double targetLuminance)
    {
        var low = 0.0;
        var high = 1.0;
        var best = RgbColor.FromHsl(hue, saturation, 0.5);
        for (var i = 0; i < 18; i++)
        {
            var mid = (low + high) / 2;
            var candidate = RgbColor.FromHsl(hue, saturation, mid);
            best = candidate;
            if (candidate.RelativeLuminance() < targetLuminance) low = mid;
            else high = mid;
        }
        return best;
    }

    private static RgbColor Black => new(0, 0, 0);
    private static RgbColor White => new(255, 255, 255);

    /// <summary>
    /// 按百分比混合两个十六进制颜色：final = original × (1 - p) + custom × p。
    /// 任一颜色解析失败时原样返回 original，不抛异常。
    /// </summary>
    public static string MixHex(string originalHex, string customHex, int percent)
    {
        if (!RgbColor.TryParse(originalHex, out var from)) return originalHex;
        if (!RgbColor.TryParse(customHex, out var to)) return originalHex;
        var ratio = Math.Clamp(percent, 0, 100) / 100.0;
        return RgbColor.Lerp(from, to, ratio).ToArgbHex();
    }
}
