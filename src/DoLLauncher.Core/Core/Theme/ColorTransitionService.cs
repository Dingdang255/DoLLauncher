using System.Globalization;

namespace DoLLauncher.Core.Theme;

/// <summary>sRGB 颜色（0-255）：只做分量线性插值，不带任何 WPF 依赖。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>按 t（0-1）对三个分量线性插值并四舍五入。</summary>
    public static RgbColor Lerp(RgbColor from, RgbColor to, double t)
    {
        if (t <= 0) return from;
        if (t >= 1) return to;
        return new RgbColor(
            Mix(from.R, to.R, t),
            Mix(from.G, to.G, t),
            Mix(from.B, to.B, t));
    }

    private static byte Mix(byte from, byte to, double t) =>
        (byte)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);

    /// <summary>相对亮度（WCAG 公式），用于对比度计算。</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    /// <summary>WCAG 对比度（1–21）。</summary>
    public double ContrastWith(RgbColor other)
    {
        var a = RelativeLuminance();
        var b = other.RelativeLuminance();
        var high = Math.Max(a, b);
        var low = Math.Min(a, b);
        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>是否为偏亮的底色。</summary>
    public bool IsLight => RelativeLuminance() > 0.35;

    /// <summary>转 HSL：h 0–360，s / l 0–1。</summary>
    public (double H, double S, double L) ToHsl()
    {
        var r = R / 255.0;
        var g = G / 255.0;
        var b = B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-6) return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
        return (h * 360, s, l);
    }

    /// <summary>由 HSL 构造，h 0–360，s / l 0–1（越界自动收敛）。</summary>
    public static RgbColor FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s <= 0)
        {
            var v = (byte)Math.Round(l * 255);
            return new RgbColor(v, v, v);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return new RgbColor(ToByte(Hue(p, q, h + 1.0 / 3)), ToByte(Hue(p, q, h)), ToByte(Hue(p, q, h - 1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);

    /// <summary>解析 #RRGGBB / #AARRGGBB（忽略 alpha）；失败返回 false，不抛异常。</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex.Substring(2);
        else if (hex.Length != 6) return false;

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;

        color = new RgbColor(r, g, b);
        return true;
    }

    /// <summary>输出 #AARRGGBB（不透明），与调色板里的写法一致。</summary>
    public string ToArgbHex() => $"#FF{R:X2}{G:X2}{B:X2}";
}

/// <summary>单条颜色过渡：记录起点、终点、开始时间与总时长，按时间点采样。</summary>
public sealed class ColorTransition
{
    public ColorTransition(RgbColor from, RgbColor to, int durationMs, long startMs)
    {
        From = from;
        To = to;
        DurationMs = Math.Max(1, durationMs);
        StartMs = startMs;
    }

    public RgbColor From { get; }

    public RgbColor To { get; }

    public int DurationMs { get; }

    public long StartMs { get; }

    /// <summary>在 nowMs 时刻的颜色：纯数学插值，不修改自身状态。</summary>
    public RgbColor Sample(long nowMs)
    {
        if (IsCompletedAt(nowMs)) return To;
        var t = (nowMs - StartMs) / (double)DurationMs;
        return RgbColor.Lerp(From, To, t);
    }

    public bool IsCompletedAt(long nowMs) => nowMs - StartMs >= DurationMs;

    /// <summary>距离结束还剩多少毫秒（已完成时为 0）。</summary>
    public int RemainingMsAt(long nowMs)
    {
        var remaining = DurationMs - (nowMs - StartMs);
        return remaining <= 0 ? 0 : (int)remaining;
    }
}

/// <summary>
/// 主题色过渡的时间参数与按颜色键登记的活动过渡。
/// 中断续播：新过渡以「此刻实际颜色」为起点，用上一条过渡的剩余时间作为时长，
/// 所以连续快速切换主题不会跳色，也不会重新跑满一整段。
/// </summary>
public static class ColorTransitionService
{
    /// <summary>默认过渡时长（毫秒）。</summary>
    public const int DefaultDurationMs = 260;

    /// <summary>逐帧推进的时间片（毫秒），约 60fps。</summary>
    public const int FrameIntervalMs = 16;

    private static readonly Dictionary<string, ColorTransition> Tracks = new();

    public static bool Any => Tracks.Count > 0;

    public static IReadOnlyCollection<string> Keys => Tracks.Keys;

    /// <summary>登记一条从 from 到 to 的过渡，时长不足 1ms 时按 1ms 处理。</summary>
    public static void Begin(string key, RgbColor from, RgbColor to, int durationMs) =>
        Tracks[key] = new ColorTransition(from, to, durationMs, Environment.TickCount64);

    /// <summary>取消某个键的过渡；返回是否确实存在。</summary>
    public static bool Cancel(string key) => Tracks.Remove(key);

    public static void Clear() => Tracks.Clear();

    public static ColorTransition? Get(string key) =>
        Tracks.TryGetValue(key, out var transition) ? transition : null;

    /// <summary>采样某个键在 nowMs 时刻的颜色；没有过渡时返回 null。</summary>
    public static RgbColor? Sample(string key, long nowMs) =>
        Tracks.TryGetValue(key, out var transition) ? transition.Sample(nowMs) : null;

    /// <summary>
    /// 中断续播的时长：上一条过渡还没跑完就取它的剩余时间，否则用默认时长。
    /// </summary>
    public static int ResumeDurationMs(ColorTransition? running, long nowMs)
    {
        var remaining = running?.RemainingMsAt(nowMs) ?? 0;
        return remaining > 0 ? remaining : DefaultDurationMs;
    }
}
