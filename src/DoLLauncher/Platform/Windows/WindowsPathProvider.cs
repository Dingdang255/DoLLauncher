using System.IO;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Windows;

/// <summary>Windows 下的固定路径：沿用现有 Services.Paths，并补充 Profile / 日志 / 备份目录。</summary>
public sealed class WindowsPathProvider : IPathProvider
{
    public string Root => Paths.Root;

    public string VersionsDir => Paths.VersionsDir;

    public string ConfigFile => Paths.ConfigFile;

    public string ProfilesDir => Path.Combine(Paths.Root, "profiles");

    public string LogsDir => Path.Combine(Paths.Root, "logs");

    public string BackupDir => Path.Combine(Paths.Root, "backups");
}
