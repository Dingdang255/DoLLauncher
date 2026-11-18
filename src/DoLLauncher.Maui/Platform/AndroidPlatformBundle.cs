using System.IO;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Maui.Platform;

/// <summary>Android 端的固定路径：根目录由应用私有目录提供，目录结构与 Windows 一致。</summary>
public sealed class AndroidPathProvider : IPathProvider
{
    public AndroidPathProvider(string root) => Root = root;

    public string Root { get; }

    public string VersionsDir => Path.Combine(Root, "versions");

    public string ConfigFile => Path.Combine(Root, "launcher-versions.json");

    public string ProfilesDir => Path.Combine(Root, "profiles");

    public string LogsDir => Path.Combine(Root, "logs");

    public string BackupDir => Path.Combine(Root, "backups");
}

/// <summary>基于 System.IO 的简单文件读写，Android 应用私有目录可直接访问。</summary>
public sealed class AndroidStorage : IStorage
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

/// <summary>Android 前端的平台能力装配。</summary>
public sealed class AndroidPlatformBundle : IPlatformBundle
{
    public AndroidPlatformBundle(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "backups"));
        // Core 内部仍有直接读 Services.Paths 的服务，必须在任何服务访问前指定根目录
        DoLLauncher.Services.Paths.OverrideRoot(root);
        Paths = new AndroidPathProvider(root);
    }

    public IPathProvider Paths { get; }

    public IThemePlatform Theme { get; } = new AndroidThemePlatform();

    public IDialogHost Dialogs { get; } = new AndroidDialogHost();

    public IStorage Storage { get; } = new AndroidStorage();

    public ILogSink Log { get; } = new AndroidLogSink();

    public IBrowserHost Browser { get; } = new AndroidBrowserHost();

    public IExternalLinks Links { get; } = new AndroidExternalLinks();

    public DoLLauncher.Platform.Abstractions.IFilePicker FilePicker { get; } = new AndroidFilePicker();
}
