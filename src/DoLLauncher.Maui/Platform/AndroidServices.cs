using DoLLauncher.Maui.Controls;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>Android 没有标题栏 / DPI 缩放概念，这里只提供系统深浅色判断。</summary>
public sealed class AndroidThemePlatform : IThemePlatform
{
    public bool SystemUsesDarkMode => Application.Current?.RequestedTheme == AppTheme.Dark;

    public void ApplyTitleBar(nint hwnd, bool dark)
    {
    }

    public double GetDpiScale(nint hwnd) => 1.0;

    public void SnapWindowToPixels(nint hwnd, double widthDip)
    {
    }
}

/// <summary>使用应用内自制弹窗呈现提示（视觉规范与 WPF 端一致），全部在主线程执行。</summary>
public sealed class AndroidDialogHost : IDialogHost
{
    public Task InfoAsync(string message, string title = "DoL 启动器") =>
        MainThread.InvokeOnMainThreadAsync(() => DolDialog.AlertAsync(title, message));

    public Task WarnAsync(string message, string title = "DoL 启动器") =>
        MainThread.InvokeOnMainThreadAsync(() =>
            DolDialog.AlertAsync(title, message, DolDialogKind.Warning));

    public Task ErrorAsync(string message, string title = "DoL 启动器") =>
        MainThread.InvokeOnMainThreadAsync(() =>
            DolDialog.AlertAsync(title, message, DolDialogKind.Error));

    public Task<bool> ConfirmAsync(string message, string title = "DoL 启动器",
        string confirmText = "确定", bool danger = false) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            DolDialog.ConfirmAsync(title, message, confirmText, danger));
}

/// <summary>日志落到 Core 的日志文件（logs/launcher.log）。</summary>
public sealed class AndroidLogSink : ILogSink
{
    public void Write(string message) => LogService.Append(message);

    public void WriteError(string context, Exception ex) => LogService.Error(context, ex);
}

/// <summary>Android 用系统浏览器 / 关联程序打开链接；目录没有桌面式「打开」概念。</summary>
public sealed class AndroidExternalLinks : IExternalLinks
{
    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _ = OpenUrlAsync(url);
    }

    private static async Task OpenUrlAsync(string url)
    {
        try
        {
            // 统一走系统 Launcher：没有可处理的应用时记录日志，不静默失败。
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception ex)
        {
            LogService.Error($"打开链接失败：{url}", ex);
        }
    }

    public void OpenPath(string path)
    {
        // Android 没有桌面文件管理器语义；路径在应用私有目录内，其它应用也读不到。
        LogService.Warn($"Android 端无法直接打开路径（请使用「分享」导出）：{path}");
    }

    public void RevealInExplorer(IReadOnlyList<string> paths) =>
        LogService.Warn($"Android 端无法在文件管理器中定位：{string.Join(", ", paths)}");
}
