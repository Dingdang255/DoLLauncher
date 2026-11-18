using System.Collections.ObjectModel;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class BackupPage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly ObservableCollection<BackupEntry> _entries = new();
    private bool _loading;
    private bool _busy;

    public BackupPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        Title = "备份 · " + version.DisplayName;
        BackupList.ItemsSource = _entries;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            // 快照与存档都来自启动器 backups 目录，一起列出方便管理。
            var entries = await Task.Run(() => BackupService.List(_version.Folder, includeSaves: true));
            _entries.Clear();
            foreach (var entry in entries) _entries.Add(entry);
        }
        catch (Exception ex)
        {
            LogService.Error("读取备份列表失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取备份列表失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnBackupClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"为「{_version.DisplayName}」创建一份备份？", "立即备份", "开始备份"))
            return;

        SetBusy(true);
        try
        {
            var path = await Task.Run(() => BackupService.CreateSnapshot(
                _version, includeGame: false, msg => LogService.Append("[备份] " + msg)));
            await ReloadAsync();
            await PlatformServices.Dialogs.InfoAsync("已创建备份：" + Path.GetFileName(path), "立即备份");
        }
        catch (Exception ex)
        {
            LogService.Error("创建备份失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("创建备份失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: BackupEntry entry } || _busy) return;
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"删除备份「{entry.FileName}」？此操作不可恢复。", "删除备份", "删除", danger: true))
            return;

        SetBusy(true);
        try
        {
            // 存档在 saves 子目录，快照与模组配置在版本备份目录，按条目类型选择。
            var removed = await Task.Run(() =>
                BackupService.Delete(_version.Folder, entry.FileName, entry.IsSaves));
            await ReloadAsync();
            if (!removed)
                await PlatformServices.Dialogs.WarnAsync("文件已经不存在：" + entry.FileName);
        }
        catch (Exception ex)
        {
            LogService.Error("删除备份失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("删除备份失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveHelperClicked(object? sender, EventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        try
        {
            var server = await Task.Run(() =>
                ServerManager.EnsureStarted(_version, saveHelperOnly: true));
            await Navigation.PushAsync(new SaveHelperPage(_version, server.SaveHelperUrl));
        }
        catch (Exception ex)
        {
            LogService.Error($"为版本 {_version.Folder} 打开存档助手失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开存档助手：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        BackupItem.IsEnabled = !busy;
    }
}
