namespace DoLLauncher.Platform.Abstractions;

/// <summary>打开游戏页面的承载方式。</summary>
public enum BrowserLaunchMode
{
    /// <summary>独立 Profile 的浏览器（Edge / Chrome）。</summary>
    Profile,

    /// <summary>系统默认浏览器。</summary>
    Default,

    /// <summary>用户自选的可执行文件。</summary>
    Custom,

    /// <summary>内置 WebView2 承载（占位，暂未实现）。</summary>
    WebView2,
}

/// <summary>承载方式能提供的能力，供上层决定缓存清理、关闭检测等策略。</summary>
[Flags]
public enum BrowserCapabilities
{
    None = 0,

    /// <summary>可在启动前静默清理缓存。</summary>
    SilentCacheClear = 1,

    /// <summary>能可靠检测页面 / 窗口关闭。</summary>
    ReliableCloseDetection = 2,

    /// <summary>数据落在磁盘上的固定目录。</summary>
    DiskDataStore = 4,

    /// <summary>能跟踪浏览器进程。</summary>
    ProcessTracking = 8,
}

/// <summary>一次打开游戏页面的请求，不包含任何 WPF 或具体浏览器细节。</summary>
/// <param name="Url">要打开的页面地址。</param>
/// <param name="VersionFolder">所属版本目录名，用于关闭时定位会话。</param>
/// <param name="ProfileDir">独立 Profile 目录（可选）。</param>
/// <param name="ExecutablePath">自定义浏览器可执行文件路径（可选）。</param>
/// <param name="Arguments">附加命令行参数原文（可选）。</param>
/// <param name="Mode">承载方式：Profile / Default / Custom / WebView2。</param>
public sealed record BrowserLaunchRequest(
    string Url,
    string VersionFolder,
    string? ProfileDir = null,
    string? ExecutablePath = null,
    string? Arguments = null,
    BrowserLaunchMode Mode = BrowserLaunchMode.Profile);

/// <summary>打开成功后的浏览器会话信息。</summary>
/// <param name="ProcessId">浏览器进程 ID，取不到时为 0。</param>
/// <param name="ProfileDir">本次使用的 Profile 目录（可选）。</param>
/// <param name="Capabilities">本次会话实际可用的能力。</param>
public sealed record BrowserSession(
    int ProcessId,
    string? ProfileDir,
    BrowserCapabilities Capabilities);

/// <summary>浏览器承载层：只声明契约，具体启动与关闭逻辑由后续任务实现。</summary>
public interface IBrowserHost
{
    /// <summary>当前实现能提供的能力。</summary>
    BrowserCapabilities Capabilities { get; }

    /// <summary>按请求打开页面；无法打开时返回 null。</summary>
    Task<BrowserSession?> OpenAsync(BrowserLaunchRequest request, CancellationToken ct = default);

    /// <summary>关闭指定版本对应的会话。</summary>
    void Close(string versionFolder);

    /// <summary>被跟踪的浏览器进程自行退出时触发，参数为版本目录名。</summary>
    event Action<string>? SessionClosed;
}
