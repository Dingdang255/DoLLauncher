using System.IO;

namespace DoLLauncher.Services;

/// <summary>启动器涉及的固定路径。根目录 = 含有 versions 的目录（开发时自动向上查找）。</summary>
public static class Paths
{
    public const string HtmlName = "Degrees of Lewdity.html";
    public const string VersionsDirName = "versions";
    public const string GameDirName = "game";

    private static string? _root;

    public static string Root => _root ??= ResolveRoot();

    public static string VersionsDir => Path.Combine(Root, VersionsDirName);
    public static string ConfigFile => Path.Combine(Root, "launcher-versions.json");

    /// <summary>
    /// 指定根目录（Android 等无法从程序目录向上查找的平台使用）。
    /// 必须在任何服务读取 <see cref="Root"/> 之前调用。
    /// </summary>
    public static void OverrideRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(_root, VersionsDirName));
    }

    private static string ResolveRoot()
    {
        var start = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var dir = start;
        for (var i = 0; i < 8 && dir.Length > 3; i++)
        {
            if (Directory.Exists(Path.Combine(dir, VersionsDirName))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent.Length == 0 || parent == dir) break;
            dir = parent;
        }

        // 退而求其次：找 DoL.bat / launcher.ps1
        dir = start;
        for (var i = 0; i < 8 && dir.Length > 3; i++)
        {
            if (File.Exists(Path.Combine(dir, "DoL.bat")) || File.Exists(Path.Combine(dir, "launcher.ps1")) ||
                File.Exists(Path.Combine(dir, "README.md")))
            {
                return dir;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent.Length == 0 || parent == dir) break;
            dir = parent;
        }

        return start;
    }
}
