using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DoLLauncher.Models;
using Microsoft.Win32;

namespace DoLLauncher.Services;

/// <summary>亮色 / 深色 / 跟随系统主题：运行时改写画刷，并同步 Windows 标题栏的深色样式。</summary>
public static class ThemeService
{
    public const string ModeSystem = "system";
    public const string ModeLight = "light";
    public const string ModeDark = "dark";

    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["BgBrush"] = "#FFF5F5F5",
        ["PanelBrush"] = "#FFFFFFFF",
        ["CardBrush"] = "#FFFFFFFF",
        ["CardHoverBrush"] = "#FFF5F7FA",
        ["CardSelectedBrush"] = "#FFEAF3FF",
        ["BorderBrush"] = "#FFE3E3E3",
        ["TextBrush"] = "#FF222222",
        ["MutedBrush"] = "#FF737373",
        ["AccentBrush"] = "#FF1370F3",
        ["AccentHoverBrush"] = "#FF0B5BCB",
        ["AccentSoftBrush"] = "#FFD5E6FD",
        ["AccentPaleBrush"] = "#FFEAF3FF",
        ["DangerBrush"] = "#FFD93B3B",
        ["WarnBrush"] = "#FFD97706",
        ["WarnPaleBrush"] = "#FFFDF3E3",
        ["NavHoverBrush"] = "#FFF0F2F5",
        ["WhiteBrush"] = "#FFFFFFFF",
        ["TabChipActiveBgBrush"] = "#FF1370F3",
        ["TabChipActiveFgBrush"] = "#FFFFFFFF",
        ["TabChipIdleFgBrush"] = "#FF444444",
        ["TabChipHoverBgBrush"] = "#FFE2E8F0",
        ["DangerBorderBrush"] = "#FFF0C6C4",
        ["DangerPaleBrush"] = "#FFFDF2F1",
        ["WarnBorderBrush"] = "#FFF2D3A8",
        ["CheckBorderBrush"] = "#FFC8C8C8",
        ["SuccessBrush"] = "#FF3F9D63",
        ["RunningBrush"] = "#FF3F9D63",
        ["RunningBgBrush"] = "#FFE9F6EE",
        ["ProgressTrackBrush"] = "#FFEDEDED",
    };

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["BgBrush"] = "#FF1C1C1E",
        ["PanelBrush"] = "#FF242426",
        ["CardBrush"] = "#FF2C2C2F",
        ["CardHoverBrush"] = "#FF35353A",
        ["CardSelectedBrush"] = "#FF2A3B55",
        ["BorderBrush"] = "#FF3E3E42",
        ["TextBrush"] = "#FFEAEAEA",
        ["MutedBrush"] = "#FF9A9A9F",
        ["AccentBrush"] = "#FF4C9AFF",
        ["AccentHoverBrush"] = "#FF6FB2FF",
        ["AccentSoftBrush"] = "#FF2B4A70",
        ["AccentPaleBrush"] = "#FF22344C",
        ["DangerBrush"] = "#FFFF6B6B",
        ["WarnBrush"] = "#FFFFB454",
        ["WarnPaleBrush"] = "#FF3A3224",
        ["NavHoverBrush"] = "#FF313136",
        ["WhiteBrush"] = "#FFFFFFFF",
        ["TabChipActiveBgBrush"] = "#FF22344C",
        ["TabChipActiveFgBrush"] = "#FF4C9AFF",
        ["TabChipIdleFgBrush"] = "#FF9A9A9F",
        ["TabChipHoverBgBrush"] = "#FF313136",
        ["DangerBorderBrush"] = "#FF5C2A2A",
        ["DangerPaleBrush"] = "#FF3A2424",
        ["WarnBorderBrush"] = "#FF5A452B",
        ["CheckBorderBrush"] = "#FF5A5A5E",
        ["SuccessBrush"] = "#FF4CC97E",
        ["RunningBrush"] = "#FF4CC97E",
        ["RunningBgBrush"] = "#FF1E3327",
        ["ProgressTrackBrush"] = "#FF3A3A3E",
    };

    private static readonly List<Window> Windows = new();
    private static bool _initialized;
    private static string _mode = ModeSystem;

    public static bool IsDark { get; private set; }

    public static string Mode => _mode;

    public static void Initialize()
    {
        _mode = Normalize(AppConfig.Load().General.ThemeMode);
        if (!_initialized)
        {
            _initialized = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
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
        catch
        {
            // 配置写入失败不影响本次切换
        }
        Apply();
    }

    public static string Normalize(string? mode)
    {
        var value = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return value is ModeLight or ModeDark ? value : ModeSystem;
    }

    /// <summary>登记窗口：随主题变化同步标题栏，并在窗口创建后立即应用一次。</summary>
    public static void Attach(Window window)
    {
        if (!Windows.Contains(window))
        {
            Windows.Add(window);
            window.Closed += OnWindowClosed;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyTitleBar(window);
        else window.SourceInitialized += (_, _) => ApplyTitleBar(window);
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        Windows.Remove(window);
        window.Closed -= OnWindowClosed;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode != ModeSystem) return;
        Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Normal, new Action(Apply));
    }

    private static void Apply()
    {
        IsDark = _mode == ModeDark || (_mode == ModeSystem && SystemUsesDark());
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var entry in LightPalette)
        {
            var color = ColorFrom((IsDark ? DarkPalette : LightPalette)[entry.Key]);
            if (resources[entry.Key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                resources[entry.Key] = new SolidColorBrush(color);
            }
        }

        foreach (var window in Windows.ToArray())
        {
            if (window.IsLoaded) ApplyTitleBar(window);
        }
    }

    private static Color ColorFrom(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>DWMWA_COLOR_NONE：告诉 DWM 不要绘制窗口边框。</summary>
    private const int DwmColorNone = -2;

    /// <summary>窗口尺寸发生变化后重新应用标题栏与边框样式。</summary>
    public static void Refresh(Window window)
    {
        if (window.IsLoaded) ApplyTitleBar(window);
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var value = IsDark ? 1 : 0;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE，19 = 旧版 Win10 的对应属性
            DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));

            // 34 = DWMWA_BORDER_COLOR：Win11 会给窗口画一圈 1px 边框，颜色不受主题控制时
            // 在浅色背景上就是底部/右侧两条黑线。这里直接用 DWMWA_COLOR_NONE 让 DWM 不画边框。
            var noBorder = DwmColorNone;
            DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(int));
        }
        catch
        {
            // 系统不支持深色标题栏时保持默认
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
