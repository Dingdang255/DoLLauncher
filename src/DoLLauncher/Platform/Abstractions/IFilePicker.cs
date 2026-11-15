namespace DoLLauncher.Platform.Abstractions;

/// <summary>文件选择入口，把文件对话框从应用层隔离出去。</summary>
public interface IFilePicker
{
    /// <summary>选择单个文件；用户取消时返回 null。</summary>
    string? PickFile(string title, string filter, bool checkFileExists = true);

    /// <summary>选择多个文件；用户取消时返回空集合。</summary>
    IReadOnlyList<string> PickFiles(string title, string filter, bool checkFileExists = true);

    /// <summary>选择保存位置；用户取消时返回 null。</summary>
    string? PickSaveFile(string title, string filter, string suggestedFileName, string defaultExtension);

    /// <summary>选择目录；用户取消时返回 null。</summary>
    string? PickFolder(string title);
}
