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

/// <summary>使用 MAUI 的 DisplayAlert 呈现提示，全部在主线程执行。</summary>
public sealed class AndroidDialogHost : IDialogHost
{
    public Task InfoAsync(string message, string title = "DoL 启动器") => ShowAsync(title, message);

    public Task WarnAsync(string message, string title = "DoL 启动器") => ShowAsync(title, message);

    public Task ErrorAsync(string message, string title = "DoL 启动器") => ShowAsync(title, message);

    public Task<bool> ConfirmAsync(string message, string title = "DoL 启动器",
        string confirmText = "确定", bool danger = false) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = CurrentPage;
            if (page == null) return false;
            return await page.DisplayAlertAsync(title, message, confirmText, "取消");
        });

    private static Task ShowAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var page = CurrentPage;
            if (page == null) return;
            await page.DisplayAlertAsync(title, message, "确定");
        });

    private static Page? CurrentPage => Application.Current?.Windows.FirstOrDefault()?.Page;
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
        _ = Launcher.Default.OpenAsync(url);
    }

    public void OpenPath(string path) => LogService.Warn($"Android 端暂不支持直接打开路径：{path}");

    public void RevealInExplorer(IReadOnlyList<string> paths) =>
        LogService.Warn($"Android 端暂不支持在文件管理器中定位：{string.Join(", ", paths)}");
}
