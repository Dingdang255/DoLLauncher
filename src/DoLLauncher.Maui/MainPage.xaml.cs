using System.Collections.ObjectModel;
using DoLLauncher.Core.Ui;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<MainVersionRow> _versions = new();
    private bool _loading;

    private DownloadPage? _downloadPanel;
    private BackupPage? _backupPanel;
    private StoryPage? _storyPanel;
    private DiagnosticsPage? _diagnosticsPanel;
    private View? _activePanel;

    // 启动时的自动备份检查只执行一次（返回列表不再重复）。
    private static bool _startupBackupChecked;
    private static bool _startupHelperOpened;

    public MainPage()
    {
        InitializeComponent();
        // 主界面不需要再叠一条「DoL 启动器」应用栏，内容直接从屏幕顶部开始。
        NavigationPage.SetHasNavigationBar(this, false);
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
            foreach (var item in list.Where(v => v.Mode == "http"))
                _versions.Add(new MainVersionRow(item));
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

    // ---------- 版本行 ----------

    /// <summary>整行单击 = 打开版本详情（与 WPF 一致）。</summary>
    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element { BindingContext: MainVersionRow row }) return;
        await Navigation.PushAsync(new VersionDetailPage(row.Version));
    }

    private async void OnRowModsClicked(object? sender, EventArgs e)
    {
        if (sender is not IconActionView { CommandParameter: MainVersionRow row }) return;
        await Navigation.PushAsync(new ModsPage(row.Version));
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await ReloadAsync();

    // ---------- 左侧导航：右侧区域替换显示 ----------

    private void OnVersionsNavClicked(object? sender, EventArgs e) => ShowVersions();

    private async void OnDownloadNavClicked(object? sender, EventArgs e)
    {
        _downloadPanel ??= new DownloadPage();
        await ShowPanelAsync(_downloadPanel, NavDownload);
    }

    private async void OnBackupNavClicked(object? sender, EventArgs e)
    {
        if (FirstVersion() is not { } version) return;
        _backupPanel ??= new BackupPage(version);
        await ShowPanelAsync(_backupPanel, NavBackup);
    }

    private async void OnStoryNavClicked(object? sender, EventArgs e)
    {
        if (FirstVersion() is not { } version) return;
        if (_storyPanel == null)
        {
            _storyPanel = new StoryPage(version);
            _storyPanel.OpenMarketRequested += OnStoryOpenMarket;
        }
        await ShowPanelAsync(_storyPanel, NavStory);
    }

    private async void OnDiagnosticNavClicked(object? sender, EventArgs e)
    {
        if (FirstVersion() is not { } version) return;
        _diagnosticsPanel ??= new DiagnosticsPage(version);
        await ShowPanelAsync(_diagnosticsPanel, NavDiagnostics);
    }

    private async void OnStoryOpenMarket(object? sender, EventArgs e)
    {
        _downloadPanel ??= new DownloadPage(startInMarket: true);
        await ShowPanelAsync(_downloadPanel, NavDownload);
        await _downloadPanel.ShowMarketAsync();
    }

    private VersionInfo? FirstVersion()
    {
        var version = _versions.FirstOrDefault()?.Version;
        if (version == null) _ = PlatformServices.Dialogs.InfoAsync("请先添加一个版本。", "DoL 启动器");
        return version;
    }

    private async Task ShowPanelAsync(View panel, IconActionView nav)
    {
        if (ReferenceEquals(_activePanel, panel))
        {
            Highlight(nav);
            return;
        }

        Deactivate(_activePanel);
        _activePanel = panel;
        VersionsPane.IsVisible = false;
        PanelHost.Content = panel;
        PanelHost.IsVisible = true;
        Highlight(nav);
        await ActivateAsync(panel);
    }

    private void ShowVersions()
    {
        if (_activePanel == null) return;
        Deactivate(_activePanel);
        _activePanel = null;
        PanelHost.Content = null;
        PanelHost.IsVisible = false;
        VersionsPane.IsVisible = true;
        Highlight(NavVersions);
    }

    private void Highlight(IconActionView nav)
    {
        NavVersions.Variant = ReferenceEquals(nav, NavVersions) ? "pale" : "plain";
        NavDownload.Variant = ReferenceEquals(nav, NavDownload) ? "pale" : "plain";
        NavBackup.Variant = ReferenceEquals(nav, NavBackup) ? "pale" : "plain";
        NavStory.Variant = ReferenceEquals(nav, NavStory) ? "pale" : "plain";
        NavDiagnostics.Variant = ReferenceEquals(nav, NavDiagnostics) ? "pale" : "plain";
    }

    private static async Task ActivateAsync(View panel)
    {
        switch (panel)
        {
            case DownloadPage download:
                await download.ActivateAsync();
                break;
            case BackupPage backup:
                await backup.ActivateAsync();
                break;
            case StoryPage story:
                story.Activate();
                break;
            case DiagnosticsPage diagnostics:
                diagnostics.Activate();
                break;
        }
    }

    private static void Deactivate(View? panel)
    {
        switch (panel)
        {
            case DownloadPage download:
                download.Deactivate();
                break;
            case StoryPage story:
                story.Deactivate();
                break;
        }
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 删除 / 导入版本后回到列表，重新扫描一次。
        _ = InitializeAsync();
        // 冷启动 / 其它页面期间收到的分享文件，等主界面出现后处理。
        _ = ShareImportHelper.ProcessPendingAsync(this);
        UpdateAvatar();
        // 从详情 / 模组管理等页面返回时，恢复当前面板的轮询与刷新。
        if (_activePanel != null) _ = ActivateAsync(_activePanel);
    }

    protected override void OnDisappearing()
    {
        Deactivate(_activePanel);
        base.OnDisappearing();
    }

    /// <summary>主界面左上角头像跟随设置里的玩家头像。</summary>
    private void UpdateAvatar()
    {
        string? full = null;
        try
        {
            var text = (AppConfig.Load().General.AvatarPath ?? string.Empty).Trim();
            if (text.Length > 0)
                full = Path.IsPathRooted(text) ? text : Path.Combine(Paths.Root, text);
        }
        catch (Exception ex)
        {
            LogService.Error("读取头像设置失败", ex);
        }

        if (full != null && AvatarRules.Validate(full).Ok)
        {
            try
            {
                var bytes = File.ReadAllBytes(full);
                AvatarImage.Source = ImageSource.FromStream(() => new MemoryStream(bytes));
                AvatarImage.IsVisible = true;
                AvatarPlaceholder.IsVisible = false;
                return;
            }
            catch (Exception ex)
            {
                LogService.Error("读取玩家头像失败", ex);
            }
        }

        AvatarImage.Source = null;
        AvatarImage.IsVisible = false;
        AvatarPlaceholder.IsVisible = true;
    }

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

            var versions = _versions.Select(r => r.Version).ToList();
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

/// <summary>主界面版本行：包装 VersionInfo。</summary>
public sealed class MainVersionRow
{
    public MainVersionRow(VersionInfo version) => Version = version;

    public VersionInfo Version { get; }
    public string DisplayName => Version.DisplayName;
    public string Folder => Version.Folder;
}
