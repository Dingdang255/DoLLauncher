using System.Diagnostics;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Windows;

/// <summary>通过 Shell 打开链接、文件或目录。</summary>
public sealed class WindowsExternalLinks : IExternalLinks
{
    public void OpenUrl(string url) => Start(url);

    public void OpenPath(string path) => Start(path);

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
