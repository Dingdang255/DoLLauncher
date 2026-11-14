using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DoLLauncher.Services;

/// <summary>与启动器主题一致的自绘消息框，用来替代系统 MessageBox。</summary>
public static class AppDialog
{
    public const string DefaultTitle = "DoL 启动器";

    /// <summary>普通提示，只有一个「确定」。</summary>
    public static void Info(string message, string title = DefaultTitle) =>
        Show(message, title, "AccentBrush", null);

    /// <summary>需要注意的提示（橙色标识）。</summary>
    public static void Warn(string message, string title = DefaultTitle) =>
        Show(message, title, "WarnBrush", null);

    /// <summary>错误提示（红色标识）。</summary>
    public static void Error(string message, string title = DefaultTitle) =>
        Show(message, title, "DangerBrush", null);

    /// <summary>确认框：返回 true 表示用户点了确认。</summary>
    public static bool Confirm(string message, string title = DefaultTitle,
        string confirmText = "确定", bool danger = false) =>
        Show(message, title, danger ? "DangerBrush" : "AccentBrush", confirmText, danger);

    private static bool Show(string message, string title, string toneKey,
        string? confirmText, bool danger = false)
    {
        var owner = OwnerWindow();
        var win = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            // NoResize：只保留系统标题栏（带关闭按钮），不要 thick frame 的可调整边框
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            Background = ResBrush("BgBrush") ?? Brushes.White,
            Foreground = ResBrush("TextBrush") ?? Brushes.Black,
        };
        win.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.DialogResult = false;
        };
        // SizeToContent 调整完尺寸后 DWM 可能会重置边框设置，尺寸稳定后再刷一次
        AttachDialogChrome(win, 470);
        if (owner != null) win.Owner = owner;
        ThemeService.Attach(win);
        UiAnimations.AttachWindow(win);

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        body.Children.Add(new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = ResBrush(toneKey) ?? Brushes.Gray,
        });

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResBrush("TextBrush"),
        });
        texts.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResBrush("TextBrush"),
        });
        Grid.SetColumn(texts, 1);
        body.Children.Add(texts);
        root.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button? cancel = null;
        if (confirmText != null)
        {
            cancel = new Button
            {
                Content = "取消",
                Style = ResStyle("FlatButton"),
                MinWidth = 84,
                Margin = new Thickness(0, 0, 8, 0),
                IsCancel = true,
            };
            cancel.Click += (_, _) => win.DialogResult = false;
            buttons.Children.Add(cancel);
        }

        var primary = new Button
        {
            Content = confirmText ?? "确定",
            Style = ResStyle("FlatButton"),
            MinWidth = 88,
            Foreground = ResBrush(danger ? "DangerBrush" : "AccentBrush"),
            BorderBrush = ResBrush(danger ? "DangerBorderBrush" : "AccentSoftBrush"),
        };
        primary.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(primary);
        root.Children.Add(buttons);

        // 危险操作的默认按钮放在「取消」上，避免回车误删
        if (cancel != null && danger) cancel.IsDefault = true;
        else primary.IsDefault = true;

        win.Content = root;
        UiAnimations.ShowPage(root);
        return win.ShowDialog() == true;
    }

    private static Window? OwnerWindow()
    {
        var current = Application.Current;
        if (current == null) return null;

        if (current.MainWindow is { IsLoaded: true, IsVisible: true } main) return main;

        foreach (Window window in current.Windows)
        {
            if (window.IsLoaded && window.IsVisible) return window;
        }
        return null;
    }

    /// <summary>
    /// 把窗口宽高对齐到整数物理像素：125% 缩放下 470 DIP 是 587.5px，
    /// 最后的半像素由窗口黑底填充，看起来就是边缘的一条深色线。
    /// 已对齐的值再次传入时保持原样，避免 Loaded / ContentRendered 重复调用时逐次放大。
    /// </summary>
    public static void SnapWindowSize(Window window, double widthDip)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var sx = dpi.DpiScaleX;
        var sy = dpi.DpiScaleY;
        if (sx <= 0 || sy <= 0) return;

        var width = SnapDip(widthDip > 0 ? widthDip : window.Width, sx);
        if (width > 0 && !SameLength(window.Width, width))
        {
            window.Width = width;
            // 宽度变化会改变文本换行，先完成布局再量高度，否则量到的是旧宽度下的高度
            if (window.IsLoaded) window.UpdateLayout();
        }

        if (window.SizeToContent != SizeToContent.Manual)
        {
            // 自适应高度的窗口：用测量出来的实际高度对齐，再转为固定高度
            var height = SnapDip(window.ActualHeight, sy);
            if (height <= 0) return; // 还没完成测量时先放弃，等 ContentRendered 再来一次
            window.SizeToContent = SizeToContent.Manual;
            if (!SameLength(window.Height, height)) window.Height = height;
            return;
        }

        // 固定尺寸窗口的 Height 同样是 DIP，不齐时也会出现半像素黑线
        if (!double.IsNaN(window.Height) && window.Height > 0)
        {
            var height = SnapDip(window.Height, sy);
            if (!SameLength(window.Height, height)) window.Height = height;
        }
    }

    /// <summary>向上对齐到整数物理像素；减去容差抵消 DIP→px 的浮点误差，保证重复调用幂等。</summary>
    private static double SnapDip(double dip, double scale)
    {
        if (dip <= 0 || scale <= 0) return dip;
        return Math.Ceiling(dip * scale - 1e-6) / scale;
    }

    private static bool SameLength(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>
    /// 自绘弹窗的统一外观处理：尺寸按 DPI 对齐物理像素、刷新 DWM 标题栏/边框、再按窗口矩形重新居中。
    /// 尺寸稳定前后各调一次即可。
    /// </summary>
    public static void ApplyDialogChrome(Window window, double widthDip)
    {
        SnapWindowSize(window, widthDip);
        ThemeService.Refresh(window);
        CenterOnOwner(window);
    }

    private static readonly ConditionalWeakTable<Window, object> ChromeAttached = new();

    /// <summary>
    /// 给弹窗接上「Loaded / ContentRendered 尺寸稳定后对齐」与「DpiChanged 重新对齐」，
    /// 重复调用只挂一次，避免同一窗口多次抖动。
    /// </summary>
    public static void AttachDialogChrome(Window window, double widthDip)
    {
        if (ChromeAttached.TryGetValue(window, out _)) return;
        ChromeAttached.Add(window, new object());
        window.Loaded += (_, _) => ApplyDialogChrome(window, widthDip);
        window.ContentRendered += (_, _) => ApplyDialogChrome(window, widthDip);
        window.DpiChanged += (_, _) => ApplyDialogChrome(window, widthDip);
    }

    /// <summary>
    /// 窗口以 SizeToContent 自适应高度时，WPF 会在内容测量完成前就算好 CenterOwner 的位置，
    /// 结果小窗口会偏出主窗口中心。等真实尺寸稳定后按窗口像素矩形重新居中一次。
    /// </summary>
    public static void CenterOnOwner(Window window)
    {
        var owner = window.Owner;
        if (owner == null || !owner.IsLoaded) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var ownerHwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero) return;
            if (!GetWindowRect(ownerHwnd, out var outer) || !GetWindowRect(hwnd, out var inner)) return;

            var x = outer.Left + (outer.Right - outer.Left - (inner.Right - inner.Left)) / 2;
            var y = outer.Top + (outer.Bottom - outer.Top - (inner.Bottom - inner.Top)) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            // 取不到窗口句柄时保持 WPF 的默认居中
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out PixelRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    private static Brush? ResBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;

    private static Style? ResStyle(string key) =>
        Application.Current?.TryFindResource(key) as Style;
}
