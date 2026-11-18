using System.Globalization;
using System.Windows.Media;

namespace DoLLauncher.Services;

/// <summary>美化包色值解析：支持 #RGB / #ARGB / #RRGGBB / #AARRGGBB、rgb()、rgba() 与命名颜色。</summary>
public static class SkinColors
{
    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = Colors.Transparent;
            return true;
        }

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            return TryParseFunction(value, out color);

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                // #RGB / #ARGB：每位重复一次展开成 #RRGGBB / #AARRGGBB
                var expanded = new char[hex.Length * 2];
                for (var i = 0; i < hex.Length; i++)
                {
                    expanded[i * 2] = hex[i];
                    expanded[i * 2 + 1] = hex[i];
                }
                value = "#" + new string(expanded);
            }
            return TryHex(value, out color);
        }

        try
        {
            if (ColorConverter.ConvertFromString(value) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // 非法颜色按解析失败处理
        }
        return false;
    }

    public static SolidColorBrush ToBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>转成调色板统一使用的 #AARRGGBB。</summary>
    public static string ToArgbHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryHex(string text, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // 非法颜色按解析失败处理
        }
        return false;
    }

    private static bool TryParseFunction(string text, out Color color)
    {
        color = Colors.Transparent;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open) return false;

        var body = text[(open + 1)..close]
            .Replace('/', ' ')
            .Replace(',', ' ');
        var parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return false;

        if (!TryChannel(parts[0], out var r) || !TryChannel(parts[1], out var g) || !TryChannel(parts[2], out var b))
            return false;

        var a = (byte)255;
        if (parts.Length == 4)
        {
            if (!double.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
                return false;
            if (parts[3].EndsWith('%')) alpha /= 100;
            a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255, MidpointRounding.AwayFromZero);
        }

        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    private static bool TryChannel(string text, out byte value)
    {
        value = 0;
        var raw = text.Trim();
        if (!double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return false;
        if (raw.EndsWith('%')) number = number / 100 * 255;
        value = (byte)Math.Round(Math.Clamp(number, 0, 255), MidpointRounding.AwayFromZero);
        return true;
    }
}
