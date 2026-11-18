using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DoLLauncher.Core.Theme;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Themes;

/// <summary>
/// Avalonia 版主题：把亮 / 暗调色板写入应用级资源字典，支持深色 / 亮色 / 跟随系统与自定义主色。
/// 与 WPF 版共用 Core 的 ThemePaletteBuilder，保证强调色推导结果一致。
/// </summary>
public static class ThemeManager
{
    public const string ModeSystem = "system";
    public const string ModeLight = "light";
    public const string ModeDark = "dark";

    private static ResourceDictionary? _palette;
    private static string _mode = ModeSystem;

    public static bool IsDark { get; private set; }

    public static string Mode => _mode;

    public static IEnumerable<string> PaletteKeys => ThemePalette.Light.Keys;

    public static void Initialize(string? mode)
    {
        _mode = Normalize(mode);
        Apply();
    }

    public static void SetMode(string? mode)
    {
        _mode = Normalize(mode);
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ThemeMode = _mode;
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            LogService.Error("保存主题模式失败", ex);
        }
        Apply();
    }

    public static string Normalize(string? mode)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value is ModeLight or ModeDark ? value : ModeSystem;
    }

    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        IsDark = ResolveDark();
        var palette = BuildPalette(IsDark);

        _palette ??= new ResourceDictionary();
        // 先摘除再回填，确保 DynamicResource 收到资源变更通知
        app.Resources.MergedDictionaries.Remove(_palette);
        foreach (var entry in palette)
        {
            _palette[entry.Key] = new SolidColorBrush(Color.Parse(entry.Value));
        }

        // 让 Fluent 默认控件（复选框、下拉、滚动条等）跟随同一主色
        if (palette.TryGetValue("AccentBrush", out var accentHex))
        {
            _palette["SystemAccentColor"] = Color.Parse(accentHex);
        }

        app.Resources.MergedDictionaries.Add(_palette);
    }

    /// <summary>生成目标调色板：原始配色 + 自定义主色（美化包主色接入留待 P4）。</summary>
    public static Dictionary<string, string> BuildPalette(bool dark)
    {
        string? accent = ThemePaletteBuilder.DefaultAccentColor;
        var saturation = 100;
        try
        {
            var general = AppConfig.Load().General;
            if (!string.IsNullOrWhiteSpace(general.ThemeAccentColor)) accent = general.ThemeAccentColor;
            saturation = general.ThemeAccentSaturation;
        }
        catch (Exception ex)
        {
            LogService.Error("读取主题设置失败", ex);
        }

        return ThemePaletteBuilder.Build(dark ? ThemePalette.Dark : ThemePalette.Light, accent, saturation);
    }

    private static bool ResolveDark()
    {
        if (_mode == ModeDark) return true;
        if (_mode == ModeLight) return false;

        try
        {
            return PlatformServices.IsInitialized && PlatformServices.Theme.SystemUsesDarkMode;
        }
        catch
        {
            return false;
        }
    }
}
