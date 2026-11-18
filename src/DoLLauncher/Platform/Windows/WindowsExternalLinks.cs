using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Windows;

/// <summary>通过 Shell 打开链接、文件或目录。</summary>
public sealed class WindowsExternalLinks : IExternalLinks
{
    public void OpenUrl(string url) => Start(url);

    public void OpenPath(string path) => Start(path);

    public void RevealInExplorer(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            var dir = paths.Count > 0 ? Path.GetDirectoryName(paths[0]) : null;
            if (dir != null) OpenPath(dir);
            return;
        }
        // 单个文件用 explorer /select 最稳定；多个文件走 Shell，能在一个窗口里同时选中
        if (files.Count == 1 || !TryShellSelect(files))
            StartWithArgs("explorer.exe", $"/select,\"{files[0]}\"");
    }

    private static bool TryShellSelect(IReadOnlyList<string> files)
    {
        var folder = Path.GetDirectoryName(files[0]);
        if (folder == null) return false;

        var pidls = new IntPtr[files.Count];
        var folderPidl = IntPtr.Zero;
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                if (SHParseDisplayName(files[i], IntPtr.Zero, out var pidl, 0, out _) != 0) return false;
                pidls[i] = pidl;
            }
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderPidl, 0, out _) != 0) return false;
            return SHOpenFolderAndSelectItems(folderPidl, (uint)pidls.Length, pidls, 0) == 0;
        }
        finally
        {
            foreach (var pidl in pidls)
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            if (folderPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl,
        [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, uint dwFlags);

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private static void StartWithArgs(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
}
