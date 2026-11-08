using System.IO;

namespace DoLLauncher.Models;

public sealed class VersionInfo
{
    /// <summary>versions 下的目录名；伪版本用固定标识。</summary>
    public string Folder { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string HtmlPath { get; set; } = string.Empty;
    /// <summary>http（本地服务器隔离）或 file（旧的 file:// 直开数据）。</summary>
    public string Mode { get; set; } = "http";
    public int Port { get; set; }
    public string? GameVersion { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public bool IsFileMode => Mode == "file";
    public string HtmlFileName => Path.GetFileName(HtmlPath);
}
