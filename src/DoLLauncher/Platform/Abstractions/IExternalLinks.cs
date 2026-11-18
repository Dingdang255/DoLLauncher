namespace DoLLauncher.Platform.Abstractions;

/// <summary>用系统默认程序打开链接、文件或目录，避免应用层直接依赖进程启动细节。</summary>
public interface IExternalLinks
{
    /// <summary>用系统默认浏览器打开网址。</summary>
    void OpenUrl(string url);

    /// <summary>用系统关联程序打开本地文件或目录。</summary>
    void OpenPath(string path);

    /// <summary>在文件资源管理器中打开所在文件夹，并选中传入的文件。</summary>
    void RevealInExplorer(IReadOnlyList<string> paths);
}
