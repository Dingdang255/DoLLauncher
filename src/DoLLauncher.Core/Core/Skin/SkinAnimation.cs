using System.Globalization;
using System.Text;
using static DoLLauncher.Core.Skin.SkinCss;

namespace DoLLauncher.Core.Skin;

/// <summary>
/// 一个关键帧：偏移量已换算为 0..1，声明保持原始属性/值，由平台层负责解释。
/// </summary>
public sealed class SkinKeyframe
{
    public double Offset { get; init; }

    public List<SkinDeclaration> Declarations { get; } = new();
}

/// <summary>过渡声明（<c>transition</c> 简写）的解析结果。</summary>
public sealed class SkinTransitionSpec
{
    /// <summary>参与过渡的属性名；未显式给出时默认 <c>all</c>。</summary>
    public string[] Properties { get; init; } = new[] { "all" };

    public double DurationMs { get; init; }

    public double DelayMs { get; init; }

    /// <summary>保留原始字符串：ease / ease-out / linear / cubic-bezier(...)。</summary>
    public string TimingFunction { get; init; } = "ease";

    /// <summary>
    /// 解析 <c>transition</c> 简写，例如 <c>background 180ms ease-out</c>。
    /// 没有时间值或时间单位非法时返回 false。
    /// </summary>
    public static bool TryParse(string? raw, out SkinTransitionSpec spec)
    {
        spec = new SkinTransitionSpec();
        var tokens = Tokenize(raw);
        if (tokens.Count == 0) return false;

        var properties = new List<string>();
        var times = new List<double>();
        var timing = "ease";

        foreach (var token in tokens)
        {
            if (TryParseTime(token, out var milliseconds))
            {
                if (times.Count < 2) times.Add(milliseconds);
                continue;
            }

            if (IsTimingFunction(token))
            {
                timing = token;
                continue;
            }

            properties.Add(token);
        }

        if (times.Count == 0) return false;

        spec = new SkinTransitionSpec
        {
            Properties = properties.Count == 0 ? new[] { "all" } : properties.ToArray(),
            DurationMs = times[0],
            DelayMs = times.Count > 1 ? times[1] : 0,
            TimingFunction = timing,
        };
        return true;
    }
}

/// <summary>动画声明（<c>animation</c> 简写）的解析结果。</summary>
public sealed class SkinAnimationSpec
{
    public string Name { get; init; } = string.Empty;

    public double DurationMs { get; init; }

    public double DelayMs { get; init; }

    /// <summary>保留原始字符串：ease / ease-in-out / linear / cubic-bezier(...)。</summary>
    public string TimingFunction { get; init; } = "ease";

    /// <summary>循环次数；<c>infinite</c> 使用 <see cref="double.PositiveInfinity"/>。</summary>
    public double IterationCount { get; init; } = 1;

    public string Direction { get; init; } = "normal";

    public string FillMode { get; init; } = "none";

    /// <summary>
    /// 解析 <c>animation</c> 简写，例如 <c>breathe 3.2s ease-in-out infinite</c>。
    /// 缺少动画名或时间单位非法时返回 false。
    /// </summary>
    public static bool TryParse(string? raw, out SkinAnimationSpec spec)
    {
        spec = new SkinAnimationSpec();
        var tokens = Tokenize(raw);
        if (tokens.Count == 0) return false;

        var name = string.Empty;
        var times = new List<double>();
        var timing = "ease";
        var iteration = 1d;
        var direction = "normal";
        var fill = "none";

        foreach (var token in tokens)
        {
            var lower = token.ToLowerInvariant();

            if (TryParseTime(token, out var milliseconds))
            {
                if (times.Count < 2) times.Add(milliseconds);
                continue;
            }

            if (IsTimingFunction(token))
            {
                timing = token;
                continue;
            }

            if (lower == "infinite")
            {
                iteration = double.PositiveInfinity;
                continue;
            }

            if (TryParseDirection(lower, out var parsedDirection))
            {
                direction = parsedDirection;
                continue;
            }

            if (lower is "running" or "paused") continue;

            if (lower == "none")
            {
                if (name.Length == 0) name = "none";
                else fill = "none";
                continue;
            }

            if (TryParseFillMode(lower, out var parsedFill))
            {
                fill = parsedFill;
                continue;
            }

            if (name.Length == 0 && !double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                name = token;
                continue;
            }

            if (double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number >= 0)
                iteration = number;
        }

        if (name.Length == 0) return false;

        spec = new SkinAnimationSpec
        {
            Name = name,
            DurationMs = times.Count > 0 ? times[0] : 0,
            DelayMs = times.Count > 1 ? times[1] : 0,
            TimingFunction = timing,
            IterationCount = iteration,
            Direction = direction,
            FillMode = fill,
        };
        return true;
    }

    private static bool TryParseDirection(string lower, out string direction)
    {
        direction = lower;
        return lower is "normal" or "reverse" or "alternate" or "alternate-reverse";
    }

    private static bool TryParseFillMode(string lower, out string fillMode)
    {
        fillMode = lower;
        return lower is "none" or "forwards" or "backwards" or "both";
    }
}

public enum SkinTransformKind
{
    Translate,
    Rotate,
    Scale,
}

/// <summary>单个变换操作；未参与该操作的字段保持 0。</summary>
public readonly record struct SkinTransformOp(
    SkinTransformKind Kind,
    double X,
    double Y,
    double Angle,
    double ScaleX,
    double ScaleY);

/// <summary>
/// <c>transform</c> 的解析结果：支持 translate / translateX / translateY / rotate / scale，
/// 最多 8 个操作；任一项非法时整体返回 false。
/// </summary>
public sealed class SkinTransformSpec
{
    public List<SkinTransformOp> Operations { get; } = new();

    public static bool TryParse(string? raw, out SkinTransformSpec spec)
    {
        spec = new SkinTransformSpec();
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return false;
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;

        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == ',')) index++;
            if (index >= text.Length) break;

            var start = index;
            while (index < text.Length && (char.IsLetter(text[index]) || text[index] == '-')) index++;
            if (index == start) return false;

            var function = text[start..index].ToLowerInvariant();
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index >= text.Length || text[index] != '(') return false;

            var close = FindClosingParen(text, index);
            if (close < 0) return false;

            var arguments = text[(index + 1)..close];
            index = close + 1;

            if (!TryBuildOp(function, arguments, out var operation)) return false;
            spec.Operations.Add(operation);
            if (spec.Operations.Count > SkinRules.MaxTransformOps) return false;
        }

        return spec.Operations.Count > 0;
    }

    private static bool TryBuildOp(string function, string arguments, out SkinTransformOp operation)
    {
        operation = default;
        var parts = arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        switch (function)
        {
            case "translate":
                if (parts.Length is < 1 or > 2) return false;
                if (!TryParseNumber(parts[0], out var x)) return false;
                var y = 0d;
                if (parts.Length > 1 && !TryParseNumber(parts[1], out y)) return false;
                operation = new SkinTransformOp(SkinTransformKind.Translate, x, y, 0, 1, 1);
                return true;

            case "translatex":
                if (parts.Length != 1 || !TryParseNumber(parts[0], out var tx)) return false;
                operation = new SkinTransformOp(SkinTransformKind.Translate, tx, 0, 0, 1, 1);
                return true;

            case "translatey":
                if (parts.Length != 1 || !TryParseNumber(parts[0], out var ty)) return false;
                operation = new SkinTransformOp(SkinTransformKind.Translate, 0, ty, 0, 1, 1);
                return true;

            case "rotate":
                if (parts.Length != 1 || !TryParseAngle(parts[0], out var angle)) return false;
                operation = new SkinTransformOp(SkinTransformKind.Rotate, 0, 0, angle, 1, 1);
                return true;

            case "scale":
                if (parts.Length is < 1 or > 2) return false;
                if (!TryParseScale(parts[0], out var sx)) return false;
                var sy = sx;
                if (parts.Length > 1 && !TryParseScale(parts[1], out sy)) return false;
                operation = new SkinTransformOp(SkinTransformKind.Scale, 0, 0, 0, sx, sy);
                return true;
        }

        return false;
    }

    private static int FindClosingParen(string text, int openIndex)
    {
        var depth = 1;
        for (var i = openIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static bool TryParseNumber(string text, out double value) => TryParseNumber(text, out value, false);

    private static bool TryParseNumber(string text, out double value, bool percentToRatio)
    {
        value = 0;
        var raw = text.Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;

        var scale = 1d;
        if (raw.EndsWith("px", StringComparison.Ordinal)) raw = raw[..^2];
        else if (raw.EndsWith("%", StringComparison.Ordinal))
        {
            raw = raw[..^1];
            if (percentToRatio) scale = 0.01;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
        value = number * scale;
        return double.IsFinite(value);
    }

    private static bool TryParseScale(string text, out double value) => TryParseNumber(text, out value, true);

    private static bool TryParseAngle(string text, out double degrees)
    {
        degrees = 0;
        var raw = text.Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;

        if (raw.EndsWith("rad", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var radians))
                return false;
            degrees = radians * 180 / Math.PI;
            return double.IsFinite(degrees);
        }

        if (raw.EndsWith("deg", StringComparison.Ordinal)) raw = raw[..^3];
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees)) return false;
        return double.IsFinite(degrees);
    }
}

/// <summary>css 名称/时间解析的共享小工具。</summary>
internal static class SkinCss
{
    /// <summary>按空白与顶层逗号切分，括号内的逗号/空格保持在同一 token（如 cubic-bezier(...)）。</summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        var source = text ?? string.Empty;
        var builder = new StringBuilder();
        var depth = 0;

        foreach (var c in source)
        {
            if (depth > 0)
            {
                builder.Append(c);
                if (c == '(') depth++;
                else if (c == ')') depth--;
                continue;
            }

            if (c == '(')
            {
                depth++;
                builder.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) || c == ',')
            {
                Flush(tokens, builder);
                continue;
            }

            builder.Append(c);
        }

        Flush(tokens, builder);
        return tokens;
    }

    public static bool TryParseTime(string? text, out double milliseconds)
    {
        milliseconds = 0;
        var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0) return false;

        double value;
        if (raw.EndsWith("ms", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        }
        else if (raw.EndsWith("s", StringComparison.Ordinal))
        {
            if (!double.TryParse(raw[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            value *= 1000;
        }
        else
        {
            return false;
        }

        if (!double.IsFinite(value) || value < 0) return false;
        milliseconds = value;
        return true;
    }

    public static bool IsTimingFunction(string token)
    {
        var lower = token.ToLowerInvariant();
        if (lower.StartsWith("cubic-bezier(", StringComparison.Ordinal)
            || lower.StartsWith("steps(", StringComparison.Ordinal)) return true;

        return lower is "ease" or "linear" or "ease-in" or "ease-out" or "ease-in-out"
            or "step-start" or "step-end";
    }

    private static void Flush(List<string> tokens, StringBuilder builder)
    {
        if (builder.Length == 0) return;
        tokens.Add(builder.ToString());
        builder.Clear();
    }
}
