using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Windows;

/// <summary>WPF 前端的平台能力装配：全部使用 Windows 实现。</summary>
public sealed class WindowsPlatformBundle : IPlatformBundle
{
    public IPathProvider Paths { get; } = new WindowsPathProvider();

    public IThemePlatform Theme { get; } = new WindowsThemePlatform();

    public IDialogHost Dialogs { get; } = new WindowsDialogHost();

    public IStorage Storage { get; } = new FileStorage();

    public ILogSink Log { get; } = new FileLogSink();

    public IBrowserHost Browser { get; } = new WindowsBrowserHost();

    public IExternalLinks Links { get; } = new WindowsExternalLinks();

    public IFilePicker FilePicker { get; } = new WindowsFilePicker();
}
