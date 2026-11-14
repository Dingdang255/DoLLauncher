using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Platform.Windows;

namespace DoLLauncher.Platform;

/// <summary>
/// 组合根：集中暴露平台能力，供应用服务层与 UI 通过接口访问。
/// 目前只有 Windows 实现，属性默认即完成装配；<see cref="Initialize"/> 保留单一入口，
/// 便于后续按配置或测试替换实现。
/// </summary>
public static class PlatformServices
{
    private static readonly object Sync = new();
    private static bool _initialized;

    /// <summary>固定路径提供者。</summary>
    public static IPathProvider Paths { get; private set; } = new WindowsPathProvider();

    /// <summary>主题、DPI 与窗口像素对齐。</summary>
    public static IThemePlatform Theme { get; private set; } = new WindowsThemePlatform();

    /// <summary>统一消息提示。</summary>
    public static IDialogHost Dialogs { get; private set; } = new WindowsDialogHost();

    /// <summary>文件读写。</summary>
    public static IStorage Storage { get; private set; } = new FileStorage();

    /// <summary>日志出口。</summary>
    public static ILogSink Log { get; private set; } = new FileLogSink();

    /// <summary>浏览器承载：打开游戏页 / 存档助手页，并可关闭对应会话。</summary>
    public static IBrowserHost Browser { get; private set; } = new WindowsBrowserHost();

    /// <summary>幂等初始化：重复调用不会重建实现。</summary>
    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;
            Browser = new WindowsBrowserHost();
            _initialized = true;
        }
    }
}
