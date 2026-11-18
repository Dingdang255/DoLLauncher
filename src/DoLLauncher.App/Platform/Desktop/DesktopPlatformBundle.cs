using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Desktop;

/// <summary>桌面（Windows / Linux / macOS）平台能力装配。</summary>
public sealed class DesktopPlatformBundle : IPlatformBundle
{
    public IPathProvider Paths { get; } = new DesktopPathProvider();

    public IThemePlatform Theme { get; } = new DesktopThemePlatform();

    public IDialogHost Dialogs { get; } = new DesktopDialogHost();

    public IStorage Storage { get; } = new DesktopStorage();

    public ILogSink Log { get; } = new DesktopLogSink();

    public IBrowserHost Browser { get; } = new DesktopBrowserHost();

    public IExternalLinks Links { get; } = new DesktopExternalLinks();

    public IFilePicker FilePicker { get; } = new DesktopFilePicker();
}

/// <summary>固定路径：沿用 Core 的 Paths 解析规则，补充 Profile / 日志 / 备份目录。</summary>
public sealed class DesktopPathProvider : IPathProvider
{
    public string Root => Paths.Root;

    public string VersionsDir => Paths.VersionsDir;

    public string ConfigFile => Paths.ConfigFile;

    public string ProfilesDir => Path.Combine(Paths.Root, "profiles");

    public string LogsDir => Path.Combine(Paths.Root, "logs");

    public string BackupDir => Path.Combine(Paths.Root, "backups");
}

/// <summary>文件读写：直接落到本地文件系统。</summary>
public sealed class DesktopStorage : IStorage
{
    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public bool Exists(string path) => File.Exists(path);

    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", bool recursive = false) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, searchPattern,
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
}

/// <summary>日志出口：复用 Core 的 LogService，落盘格式与 WPF 版一致。</summary>
public sealed class DesktopLogSink : ILogSink
{
    public void Write(string message) => LogService.Append(message);

    public void WriteError(string context, Exception ex) => LogService.Error(context, ex);
}

/// <summary>用系统默认程序打开链接 / 文件 / 目录。</summary>
public sealed class DesktopExternalLinks : IExternalLinks
{
    public void OpenUrl(string url) => Start(url);

    public void OpenPath(string path) => Start(path);

    public void RevealInExplorer(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var target = File.Exists(paths[0]) ? Path.GetDirectoryName(paths[0])! : paths[0];
        if (Directory.Exists(target)) Start(target);
    }

    private static void Start(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogService.Error("打开失败：" + target, ex);
        }
    }
}

/// <summary>
/// 主题平台：深浅色读取 Avalonia 的平台设置；标题栏与 DPI 对齐交给 Avalonia 原生窗口装饰，
/// 不再需要 DWM 与物理像素修正。
/// </summary>
public sealed class DesktopThemePlatform : IThemePlatform
{
    public bool SystemUsesDarkMode
    {
        get
        {
            try
            {
                var settings = Application.Current?.PlatformSettings;
                return settings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
            }
            catch
            {
                return false;
            }
        }
    }

    public void ApplyTitleBar(nint hwnd, bool dark)
    {
        // Avalonia 使用系统原生标题栏，深浅色由平台主题跟随
    }

    public double GetDpiScale(nint hwnd) => 1d;

    public void SnapWindowToPixels(nint hwnd, double widthDip)
    {
        // 布局按 DIP 计算，缩放由 Avalonia 后端处理，无需手工对齐物理像素
    }
}
