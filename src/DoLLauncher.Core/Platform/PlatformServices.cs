using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform;

/// <summary>
/// 一套平台能力的装配：由各前端头工程（WPF / Avalonia 桌面 / Android）在启动时提供具体实现。
/// 平台无关层只依赖本接口，不感知具体操作系统。
/// </summary>
public interface IPlatformBundle
{
    /// <summary>固定路径提供者。</summary>
    IPathProvider Paths { get; }

    /// <summary>主题、DPI 与窗口像素对齐。</summary>
    IThemePlatform Theme { get; }

    /// <summary>统一消息提示。</summary>
    IDialogHost Dialogs { get; }

    /// <summary>文件读写。</summary>
    IStorage Storage { get; }

    /// <summary>日志出口。</summary>
    ILogSink Log { get; }

    /// <summary>浏览器承载：打开游戏页 / 存档助手页，并可关闭对应会话。</summary>
    IBrowserHost Browser { get; }

    /// <summary>用系统默认程序打开链接、文件或目录。</summary>
    IExternalLinks Links { get; }

    /// <summary>文件选择对话框。</summary>
    IFilePicker FilePicker { get; }
}

/// <summary>
/// 组合根：集中暴露平台能力，供应用服务层与 UI 通过接口访问。
/// 具体实现由头工程通过 <see cref="Initialize"/> 注入；未初始化时访问会抛出明确异常，
/// 避免静默回退到其它平台的实现。
/// </summary>
public static class PlatformServices
{
    private static readonly object Sync = new();
    private static IPlatformBundle? _bundle;

    /// <summary>是否已完成装配。</summary>
    public static bool IsInitialized => _bundle != null;

    private static IPlatformBundle Bundle =>
        _bundle ?? throw new InvalidOperationException(
            "平台能力尚未初始化：请先调用 PlatformServices.Initialize(bundle)。");

    /// <summary>固定路径提供者。</summary>
    public static IPathProvider Paths => Bundle.Paths;

    /// <summary>主题、DPI 与窗口像素对齐。</summary>
    public static IThemePlatform Theme => Bundle.Theme;

    /// <summary>统一消息提示。</summary>
    public static IDialogHost Dialogs => Bundle.Dialogs;

    /// <summary>文件读写。</summary>
    public static IStorage Storage => Bundle.Storage;

    /// <summary>日志出口。</summary>
    public static ILogSink Log => Bundle.Log;

    /// <summary>浏览器承载：打开游戏页 / 存档助手页，并可关闭对应会话。</summary>
    public static IBrowserHost Browser => Bundle.Browser;

    /// <summary>用系统默认程序打开链接、文件或目录。</summary>
    public static IExternalLinks Links => Bundle.Links;

    /// <summary>文件选择对话框。</summary>
    public static IFilePicker FilePicker => Bundle.FilePicker;

    /// <summary>注入平台实现；重复调用以最后一次为准。</summary>
    public static void Initialize(IPlatformBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        lock (Sync)
        {
            _bundle = bundle;
        }
    }
}
