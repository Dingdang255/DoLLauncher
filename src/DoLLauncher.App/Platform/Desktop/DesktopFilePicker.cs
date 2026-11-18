using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Desktop;

/// <summary>
/// 桌面文件选择：走 Avalonia StorageProvider，同步等待原生对话框返回。
/// Core 的 IFilePicker 是同步契约，这里在 UI 线程上跑嵌套调度循环。
/// </summary>
public sealed class DesktopFilePicker : IFilePicker
{
    public string? PickFile(string title, string filter, bool checkFileExists = true) =>
        RunUi(async () =>
        {
            var provider = Provider();
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = BuildTypes(filter),
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        });

    public IReadOnlyList<string> PickFiles(string title, string filter, bool checkFileExists = true) =>
        RunUi(async () =>
        {
            var provider = Provider();
            if (provider == null) return (IReadOnlyList<string>)Array.Empty<string>();
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = BuildTypes(filter),
            });
            return files.Select(f => f.TryGetLocalPath())
                .Where(p => p != null)
                .Select(p => p!)
                .ToList() as IReadOnlyList<string>;
        });

    public string? PickSaveFile(string title, string filter, string suggestedFileName, string defaultExtension) =>
        RunUi(async () =>
        {
            var provider = Provider();
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = defaultExtension,
                FileTypeChoices = BuildTypes(filter),
            });
            return file?.TryGetLocalPath();
        });

    public string? PickFolder(string title) =>
        RunUi(async () =>
        {
            var provider = Provider();
            if (provider == null) return null;
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        });

    private static IStorageProvider? Provider() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.StorageProvider;

    /// <summary>把 "压缩包|*.zip;*.modpack" 这类 WPF 过滤串转成 Avalonia 文件类型。</summary>
    private static List<FilePickerFileType>? BuildTypes(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        var list = new List<FilePickerFileType>();
        foreach (var part in filter.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.Contains('*')) continue;
            var patterns = part.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (patterns.Count == 0) continue;
            list.Add(new FilePickerFileType("文件") { Patterns = patterns });
        }
        return list.Count > 0 ? list : null;
    }

    private static T RunUi<T>(Func<Task<T>> action)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(action).GetAwaiter().GetResult();
        }

        var task = action();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogService.Error("文件选择失败", ex);
            return default!;
        }
    }
}
