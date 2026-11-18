using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// Android 没有独立 Profile 的外部浏览器，游戏页由应用内 WebView 承载。
/// 这里只负责把「要打开哪个地址」转发给界面，不在 Core 里直接引用 MAUI 页面。
/// </summary>
public sealed class AndroidBrowserHost : IBrowserHost
{
    /// <summary>界面订阅此事件后负责创建 WebView 页面。</summary>
    public static event Action<BrowserLaunchRequest>? LaunchRequested;

    public BrowserCapabilities Capabilities => BrowserCapabilities.None;

    public event Action<string>? SessionClosed;

    public Task<BrowserSession?> OpenAsync(BrowserLaunchRequest request, CancellationToken ct = default)
    {
        LaunchRequested?.Invoke(request);
        return Task.FromResult<BrowserSession?>(
            new BrowserSession(0, request.ProfileDir, BrowserCapabilities.None));
    }

    public void Close(string versionFolder) => SessionClosed?.Invoke(versionFolder);
}

/// <summary>
/// Android 的文件选择必须走 SAF 异步回调，而 Core 的 IFilePicker 目前是同步契约。
/// 首期没有导入入口，这里只记录告警；接入导入时再把契约改为异步。
/// </summary>
public sealed class AndroidFilePicker : DoLLauncher.Platform.Abstractions.IFilePicker
{
    public string? PickFile(string title, string filter, bool checkFileExists = true)
    {
        LogService.Warn($"Android 端文件选择尚未接入：{title}");
        return null;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool checkFileExists = true)
    {
        LogService.Warn($"Android 端文件选择尚未接入：{title}");
        return Array.Empty<string>();
    }

    public string? PickSaveFile(string title, string filter, string suggestedFileName, string defaultExtension)
    {
        LogService.Warn($"Android 端保存位置选择尚未接入：{title}");
        return null;
    }

    public string? PickFolder(string title)
    {
        LogService.Warn($"Android 端目录选择尚未接入：{title}");
        return null;
    }
}
