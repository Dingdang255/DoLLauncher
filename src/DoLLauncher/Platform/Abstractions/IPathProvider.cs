namespace DoLLauncher.Platform.Abstractions;

/// <summary>启动器使用的固定路径，供应用服务层与平台实现共同依赖。</summary>
public interface IPathProvider
{
    /// <summary>启动器根目录（含 versions 的目录）。</summary>
    string Root { get; }

    /// <summary>版本目录。</summary>
    string VersionsDir { get; }

    /// <summary>版本配置文件。</summary>
    string ConfigFile { get; }

    /// <summary>浏览器独立 Profile 根目录。</summary>
    string ProfilesDir { get; }

    /// <summary>日志目录。</summary>
    string LogsDir { get; }

    /// <summary>备份目录。</summary>
    string BackupDir { get; }
}
