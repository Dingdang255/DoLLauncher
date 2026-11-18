using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Desktop;

/// <summary>
/// 桌面消息提示：用 Avalonia 窗口自绘（不依赖系统 MessageBox），保持与主界面一致的主题。
/// 全程使用 await 让 Avalonia 正常处理输入；不能在 UI 线程上同步等待弹窗，否则窗口会无响应。
/// </summary>
public sealed class DesktopDialogHost : IDialogHost
{
    private const string DefaultTitle = "DoL 启动器";

    public Task InfoAsync(string message, string title = DefaultTitle) =>
        ShowAsync(message, title, confirm: false, confirmText: "确定", danger: false);

    public Task WarnAsync(string message, string title = DefaultTitle) =>
        ShowAsync(message, title, confirm: false, confirmText: "确定", danger: false);

    public Task ErrorAsync(string message, string title = DefaultTitle) =>
        ShowAsync(message, title, confirm: false, confirmText: "确定", danger: true);

    public Task<bool> ConfirmAsync(string message, string title = DefaultTitle,
        string confirmText = "确定", bool danger = false) =>
        ShowAsync(message, title, confirm: true, confirmText, danger);

    private static async Task<bool> ShowAsync(string message, string title, bool confirm,
        string confirmText, bool danger)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() =>
                ShowAsync(message, title, confirm, confirmText, danger));

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner == null) return false;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 18, 18, 0),
        };
        text.Foreground = Resource<IBrush>("TextBrush") ?? Brushes.Black;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(18, 16, 18, 16),
        };

        var window = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Children = { text, buttons } },
        };
        window.Background = Resource<IBrush>("BgBrush") ?? Brushes.White;

        if (confirm)
        {
            var cancel = new Button { Content = "取消", MinWidth = 84 };
            cancel.Click += (_, _) => window.Close(false);
            buttons.Children.Add(cancel);
        }

        var confirmButton = new Button
        {
            Content = confirm ? confirmText : "确定",
            MinWidth = 84,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var theme = Resource<ControlTheme>("PrimaryButton");
        if (theme != null) confirmButton.Theme = theme;
        confirmButton.Click += (_, _) => window.Close(true);
        buttons.Children.Add(confirmButton);

        // ShowDialog 会禁用主窗口并把输入交给正常调度循环；await 期间界面保持响应
        var result = await window.ShowDialog<bool>(owner);
        return confirm && result;
    }

    /// <summary>从应用资源（含运行时注入的调色板）里取指定类型的资源。</summary>
    private static T? Resource<T>(object key) where T : class
    {
        var app = Application.Current;
        return app != null && app.TryGetResource(key, null, out var value) ? value as T : null;
    }
}
