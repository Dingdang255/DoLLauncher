using DoLLauncher.Core.Theme;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

/// <summary>
/// 把设置里的主色按 WPF 的 ThemePaletteBuilder 算法写入 MAUI 资源字典。
/// 页面用 DynamicResource 引用这些键，改色后按钮 / 选中态 / 标题即时同步。
/// </summary>
internal static class ThemeAccent
{
    public const string DefaultAccent = ThemePaletteBuilder.DefaultAccentColor;

    // 只用于派生强调色的底色，与 WPF 的亮 / 暗调色板保持一致
    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["BgBrush"] = "#FFF5F5F5",
    };

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["BgBrush"] = "#FF1C1C1E",
    };

    /// <summary>当前实际是否深色：跟随系统时按系统主题判断。</summary>
    public static bool IsDark
    {
        get
        {
            var app = Application.Current;
            if (app == null) return false;
            if (app.UserAppTheme == AppTheme.Dark) return true;
            if (app.UserAppTheme == AppTheme.Light) return false;
            return app.RequestedTheme == AppTheme.Dark;
        }
    }

    /// <summary>按配置重新应用主题色。</summary>
    public static void Apply(ResourceDictionary? resources = null)
    {
        var general = AppConfig.Load().General;
        Apply(resources, general.ThemeAccentColor, general.ThemeAccentSaturation);
    }

    /// <summary>用指定主色 / 饱和度应用主题色（设置页预览与保存时调用）。</summary>
    public static void Apply(ResourceDictionary? resources, string? accentHex, int saturationPercent)
    {
        var target = resources ?? Application.Current?.Resources;
        if (target == null) return;

        var palette = Build(string.IsNullOrWhiteSpace(accentHex) ? DefaultAccent : accentHex, saturationPercent);
        // 主色为空 / 非法时 Builder 会原样返回，取不到键就退回默认主色，避免启动崩溃
        string Pick(string key, string fallback) =>
            palette.TryGetValue(key, out var value) ? value : fallback;

        Set(target, "AccentColor", Pick("AccentBrush", DefaultAccent));
        Set(target, "AccentHoverColor", Pick("AccentHoverBrush", "#FF2B7FF5"));
        Set(target, "AccentSoftColor", Pick("AccentSoftBrush", "#FFD5E6FD"));
        Set(target, "AccentPaleColor", Pick("AccentPaleBrush", "#FFEAF3FF"));
        Set(target, "AccentStrongColor", Pick("AccentStrongBrush", DefaultAccent));
        Set(target, "AccentOnColor", Pick("AccentOnBrush", "#FFFFFFFF"));

        // 兼容模板里原有的 Primary 系键（未改成 DynamicResource 的旧控件仍可用）
        Set(target, "Primary", Pick("AccentBrush", DefaultAccent));
        Set(target, "PrimaryDark", Pick("AccentBrush", DefaultAccent));
        Set(target, "Secondary", Pick("AccentPaleBrush", "#FFEAF3FF"));
        Set(target, "Tertiary", Pick("AccentStrongBrush", DefaultAccent));
        Set(target, "SecondaryDarkText", Pick("AccentOnBrush", "#FFFFFFFF"));
    }

    /// <summary>预览最终强调色（不写配置）。</summary>
    public static string PreviewAccent(string? accentHex, int saturationPercent)
    {
        var palette = Build(string.IsNullOrWhiteSpace(accentHex) ? DefaultAccent : accentHex, saturationPercent);
        return palette.TryGetValue("AccentBrush", out var value) ? value : DefaultAccent;
    }

    public static Dictionary<string, string> Build(string? accentHex, int saturationPercent) =>
        ThemePaletteBuilder.Build(IsDark ? DarkPalette : LightPalette, accentHex, saturationPercent);

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = Color.FromArgb(hex);
}
