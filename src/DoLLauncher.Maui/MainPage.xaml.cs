using System.Collections.ObjectModel;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<VersionInfo> _versions = new();
    private bool _loading;

    // 启动时的自动备份检查只执行一次（返回列表不再重复）。
    private static bool _startupBackupChecked;
    private static bool _startupHelperOpened;

    public MainPage()
    {
        InitializeComponent();
        VersionList.ItemsSource = _versions;
        RootLabel.Text = Paths.Root;
    }

    private async Task ReloadAsync()
    {
        // 自动刷新与下拉刷新可能重叠，直接结束下拉动画避免一直转圈。
        if (_loading)
        {
            Refresher.IsRefreshing = false;
            return;
        }
        _loading = true;
        try
        {
            var list = await Task.Run(VersionService.Scan);
            _versions.Clear();
            foreach (var item in list.Where(v => v.Mode == "http")) _versions.Add(item);
            VersionCountLabel.Text = $"{_versions.Count} 个版本";
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("扫描版本失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            Refresher.IsRefreshing = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnOpenDetailClicked(object? sender, EventArgs e)
    {
        if (VersionOf(sender) is not { } version) return;
        await Navigation.PushAsync(new VersionDetailPage(version));
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await ReloadAsync();

    private async void OnModsClicked(object? sender, EventArgs e)
    {
        if (VersionOf(sender) is not { } version) return;
        await Navigation.PushAsync(new ModsPage(version));
    }

    private async void OnBackupClicked(object? sender, EventArgs e)
    {
        if (VersionOf(sender) is not { } version) return;
        await Navigation.PushAsync(new BackupPage(version));
    }

    private async void OnDiagnosticsClicked(object? sender, EventArgs e)
    {
        if (VersionOf(sender) is not { } version) return;
        await Navigation.PushAsync(new DiagnosticsPage(version));
    }

    private async void OnStoryClicked(object? sender, EventArgs e)
    {
        if (VersionOf(sender) is not { } version) return;
        await Navigation.PushAsync(new StoryPage(version));
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
    }

    private async void OnDownloadClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new DownloadPage());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 删除 / 导入版本后回到列表，重新扫描一次。
        _ = InitializeAsync();
        // 冷启动 / 其它页面期间收到的分享文件，等主界面出现后处理。
        _ = ShareImportHelper.ProcessPendingAsync(this);
    }

    /// <summary>取出列表项上挂的版本：兼容普通按钮与图标操作视图。</summary>
    private static VersionInfo? VersionOf(object? sender) => sender switch
    {
        Button { CommandParameter: VersionInfo version } => version,
        IconActionView { CommandParameter: VersionInfo version } => version,
        _ => null,
    };

    private async Task InitializeAsync()
    {
        await ReloadAsync();
        await RunStartupBackupAsync();
    }

    /// <summary>
    /// 启动后只执行一次：按设置对每个版本做一次自动备份（内部按间隔判断），
    /// 需要时再以 ?auto=1 打开一次存档助手。
    /// </summary>
    private async Task RunStartupBackupAsync()
    {
        if (_startupBackupChecked) return;
        _startupBackupChecked = true;

        try
        {
            var cfg = AppConfig.Load();
            if (!cfg.Backup.Enabled && !cfg.Backup.SaveAuto) return;

            var versions = _versions.ToList();
            if (versions.Count == 0) return;

            if (cfg.Backup.Enabled)
            {
                foreach (var version in versions)
                {
                    var target = version;
                    try
                    {
                        await Task.Run(() => BackupService.AutoBackup(target,
                            msg => LogService.Append("[自动备份] " + msg)));
                    }
                    catch (Exception ex)
                    {
                        // 单个版本失败不影响其它版本和界面。
                        LogService.Error($"自动备份版本 {target.Folder} 失败", ex);
                    }
                }
            }

            if (cfg.Backup.SaveAuto && !_startupHelperOpened)
            {
                _startupHelperOpened = true;
                var version = versions[0];
                var server = await Task.Run(() =>
                    ServerManager.EnsureStarted(version, saveHelperOnly: true));
                LauncherStatusService.Sync();
                await Navigation.PushAsync(
                    new SaveHelperPage(version, server.SaveHelperUrl + "?auto=1"));
                LogService.Append("已打开自动存档备份页（完成后可关闭）。");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("启动时自动备份失败", ex);
        }
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        List<FileResult> picked;
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = "选择版本包 / 模组包（可多选）" });
            picked = files?.OfType<FileResult>().ToList() ?? new List<FileResult>();
        }
        catch (Exception ex)
        {
            LogService.Error("选择导入文件失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开文件选择器：" + ex.Message);
            return;
        }
        if (picked.Count == 0) return;

        SetBusy(true);
        try
        {
            // 与「分享 / 直接打开 zip」共用同一套导入流程。
            await ShareImportHelper.ImportPickedAsync(this, picked);
        }
        catch (Exception ex)
        {
            LogService.Error("导入包失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入失败：" + ex.Message, "导入");
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
    }

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
    }
}
