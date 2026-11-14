using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DoLLauncher.Core.Theme;
using DoLLauncher.Models;
using Microsoft.Win32;

namespace DoLLauncher.Services;

/// <summary>
/// 亮色 / 深色 / 跟随系统主题：运行时改写画刷，并同步 Windows 标题栏的深色样式。
/// 切换主题时按当前实际颜色过渡，动画关闭时直接跳转。
/// </summary>
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
        ["AccentStrongBrush"] = "#FF1370F3",
        ["AccentOnBrush"] = "#FFFFFFFF",
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
        ["AccentStrongBrush"] = "#FF4C9AFF",
        ["AccentOnBrush"] = "#FF111417",
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

    /// <summary>统一推进所有画刷颜色过渡的计时器，全部完成后停止。</summary>
    private static DispatcherTimer? _frameTimer;

    /// <summary>每个画刷键当前实际显示的颜色，是下一次过渡的起点。</summary>
    private static readonly Dictionary<string, RgbColor> DisplayedColors = new();

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

        var palette = BuildPalette(IsDark);
        if (UiAnimations.Active) ApplyAnimated(resources, palette);
        else ApplyImmediate(resources, palette);

        // 标题栏与边框不参与颜色过渡，始终立即更新
        RefreshTitleBars();
    }

    /// <summary>刷新所有已打开窗口的标题栏与边框样式（过渡结束、窗口尺寸变化后调用）。</summary>
    private static void RefreshTitleBars()
    {
        foreach (var window in Windows.ToArray())
        {
            if (window.IsLoaded) ApplyTitleBar(window);
        }
    }

    /// <summary>按当前配置重新应用主题（设置页修改主色 / 占比后调用），走正常的过渡动画。</summary>
    public static void Reapply() => Apply();

    /// <summary>
    /// 生成目标调色板：先取亮 / 暗原始配色，再用设置里的主色替换强调色系。
    /// </summary>
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
        catch
        {
            // 配置读取失败时保持默认配色
        }

        return ThemePaletteBuilder.Build(dark ? DarkPalette : LightPalette, accent, saturation);
    }

    /// <summary>按指定主色与饱和度预览强调色（设置页实时预览用，不写配置）。</summary>
    public static string PreviewAccent(string? accentHex, int saturationPercent)
    {
        var palette = ThemePaletteBuilder.Build(IsDark ? DarkPalette : LightPalette, accentHex, saturationPercent);
        return palette.TryGetValue("AccentBrush", out var hex) ? hex : ThemePaletteBuilder.DefaultAccentColor;
    }

    /// <summary>动画关闭：直接落到目标色，并丢弃所有未完成的过渡。</summary>
    private static void ApplyImmediate(ResourceDictionary resources, Dictionary<string, string> palette)
    {
        ColorTransitionService.Clear();
        StopFrameTimer();

        foreach (var entry in palette)
        {
            var color = ColorFrom(entry.Value);
            DisplayedColors[entry.Key] = ToRgb(color);
            SetBrushColor(resources, entry.Key, color);
        }
    }

    /// <summary>动画开启：首次应用直接落位；之后每次切换都从当前实际颜色续播到新目标。</summary>
    private static void ApplyAnimated(ResourceDictionary resources, Dictionary<string, string> palette)
    {
        var now = Environment.TickCount64;
        foreach (var entry in palette)
        {
            var target = ColorFrom(entry.Value);
            var targetRgb = ToRgb(target);

            // 首次应用（或动画刚从关闭变为开启）：直接取目标色，不播放过渡
            if (!DisplayedColors.TryGetValue(entry.Key, out var current))
            {
                ColorTransitionService.Cancel(entry.Key);
                DisplayedColors[entry.Key] = targetRgb;
                SetBrushColor(resources, entry.Key, target);
                continue;
            }

            // 上一段动画还没跑完：以它此刻的颜色为新起点，并沿用剩余的时长
            var running = ColorTransitionService.Get(entry.Key);
            if (running != null) current = running.Sample(now);

            // 每个键只保留一条过渡
            ColorTransitionService.Cancel(entry.Key);
            if (current == targetRgb)
            {
                DisplayedColors[entry.Key] = targetRgb;
                SetBrushColor(resources, entry.Key, target);
                continue;
            }

            ColorTransitionService.Begin(entry.Key, current, targetRgb,
                ColorTransitionService.ResumeDurationMs(running, now));
        }

        if (ColorTransitionService.Any) StartFrameTimer();
        else StopFrameTimer();
    }

    private static void OnFrameTick(object? sender, EventArgs e)
    {
        var resources = Application.Current?.Resources;
        if (resources == null)
        {
            ColorTransitionService.Clear();
            StopFrameTimer();
            return;
        }

        var now = Environment.TickCount64;
        foreach (var key in ColorTransitionService.Keys.ToArray())
        {
            var transition = ColorTransitionService.Get(key);
            if (transition == null) continue;

            var rgb = transition.Sample(now);
            DisplayedColors[key] = rgb;
            SetBrushColor(resources, key, Color.FromRgb(rgb.R, rgb.G, rgb.B));
            if (transition.IsCompletedAt(now)) ColorTransitionService.Cancel(key);
        }

        if (!ColorTransitionService.Any)
        {
            StopFrameTimer();
            RefreshTitleBars();
        }
    }

    private static void StartFrameTimer()
    {
        if (_frameTimer == null)
        {
            _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(ColorTransitionService.FrameIntervalMs),
            };
            _frameTimer.Tick += OnFrameTick;
        }

        if (!_frameTimer.IsEnabled) _frameTimer.Start();
    }

    private static void StopFrameTimer()
    {
        if (_frameTimer is { IsEnabled: true }) _frameTimer.Stop();
    }

    /// <summary>写入画刷颜色；画刷被冻结时替换为新画刷，DynamicResource 会自动重新取值。</summary>
    private static void SetBrushColor(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static RgbColor ToRgb(Color color) => new(color.R, color.G, color.B);

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
            ApplyTitleBar(hwnd, IsDark);
        }
        catch
        {
            // 系统不支持深色标题栏时保持默认
        }
    }

    /// <summary>按窗口句柄设置标题栏样式，供平台层在没有 Window 对象时复用。</summary>
    public static void ApplyTitleBar(nint hwnd, bool dark)
    {
        if (hwnd == 0) return;
        try
        {
            var value = dark ? 1 : 0;
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
