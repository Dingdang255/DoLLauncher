using System.Windows;
using DoLLauncher.Platform.Abstractions;
using Microsoft.Win32;

namespace DoLLauncher.Platform.Windows;

/// <summary>用 WPF 的 OpenFileDialog 实现文件选择，以当前主窗口为所有者保持模态。</summary>
public sealed class WindowsFilePicker : IFilePicker
{
    public string? PickFile(string title, string filter, bool checkFileExists = true)
    {
        var dialog = Create(title, filter, checkFileExists, false);
        return Show(dialog) ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickFiles(string title, string filter, bool checkFileExists = true)
    {
        var dialog = Create(title, filter, checkFileExists, true);
        return Show(dialog) ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickSaveFile(string title, string filter, string suggestedFileName, string defaultExtension)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedFileName,
            DefaultExt = defaultExtension,
            AddExtension = true,
        };
        return Show(dialog) ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return Show(dialog) ? dialog.FolderName : null;
    }

    private static OpenFileDialog Create(string title, string filter, bool checkFileExists, bool multiSelect) =>
        new()
        {
            Title = title,
            Filter = filter,
            CheckFileExists = checkFileExists,
            Multiselect = multiSelect,
        };

    private static bool Show(OpenFileDialog dialog)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == true;
    }

    private static bool Show(SaveFileDialog dialog)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == true;
    }

    private static bool Show(OpenFolderDialog dialog)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return result == true;
    }
}
