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
    private IconActionView? _activeNav;

    // 启动时的自动备份检查只执行一次（返回列表不再重复）。
    private static bool _startupBackupChecked;
    private static bool _startupHelperOpened;

    public MainPage()
    {
        InitializeComponent();
        ModalShield.BlockInput(BusyOverlay);
        Instance = this;
        // 主界面不需要再叠一条「DoL 启动器」应用栏，内容直接从屏幕顶部开始。
        NavigationPage.SetHasNavigationBar(this, false);
        VersionList.ItemsSource = _versions;
        RootLabel.Text = Paths.Root;
        Highlight(NavVersions);
    }

    /// <summary>当前主界面实例，供美化包脚本读取版本 / 模组数据与切换页面。</summary>
    internal static MainPage? Instance { get; private set; }

    private async Task ReloadAsync()
    {
        // 自动刷新与下拉刷新可能重叠，直接结束下拉动画避免一直转圈。
        if (_loading)
        {
            Refresher.IsRefreshing = false;
            return;
        }
        _loading = true;
        SetRefreshBusy(true);
        var started = DateTime.UtcNow;
        try
        {
            var list = await Task.Run(VersionService.Scan);
            _versions.Clear();
            // 与 WPF 一致：file:// 伪版本也常驻列表，方便找回旧数据
            foreach (var item in list)
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
            // 扫描太快时给个最短可见时间，否则用户会觉得按钮没反应。
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            if (elapsed < 450) await Task.Delay((int)(450 - elapsed));
            _loading = false;
            SetRefreshBusy(false);
            Refresher.IsRefreshing = false;
        }
    }

    private void SetRefreshBusy(bool busy)
    {
        RefreshButton.IsVisible = !busy;
        RefreshSpinner.IsVisible = busy;
        RefreshSpinner.IsRunning = busy;
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    /// <summary>设置里的启动器副标题：为空时保持原样，有内容时显示在标题下方。</summary>
    private void ApplySubtitle()
    {
        var text = (AppConfig.Load().General.Subtitle ?? string.Empty).Trim();
        SubtitleLabel.Text = text;
        SubtitleLabel.IsVisible = text.Length > 0;
    }

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

        var activation = ActivateAsync(panel);
        await PlayEnterAsync(PanelHost, 18);
        await activation;
    }

    private void ShowVersions()
    {
        if (_activePanel == null)
        {
            Highlight(NavVersions);
            return;
        }
        Deactivate(_activePanel);
        _activePanel = null;
        PanelHost.Content = null;
        PanelHost.IsVisible = false;
        VersionsPane.IsVisible = true;
        Highlight(NavVersions);
        _ = PlayEnterAsync(VersionsPane, -14);
    }

    /// <summary>面板切换时淡入并轻微横移，减少「瞬间换屏」的突兀感。</summary>
    private static async Task PlayEnterAsync(VisualElement view, double fromX)
    {
        if (!UiMotion.Enabled)
        {
            view.Opacity = 1;
            view.TranslationX = 0;
            return;
        }

        view.Opacity = 0;
        view.TranslationX = fromX;
        await Task.WhenAll(
            view.FadeToAsync(1, 150, Easing.CubicOut),
            view.TranslateToAsync(0, 0, 180, Easing.CubicOut));
        view.Opacity = 1;
        view.TranslationX = 0;
    }

    private void Highlight(IconActionView nav)
    {
        _activeNav = nav;
        NavVersions.SetSelected(ReferenceEquals(nav, NavVersions));
        NavDownload.SetSelected(ReferenceEquals(nav, NavDownload));
        NavBackup.SetSelected(ReferenceEquals(nav, NavBackup));
        NavStory.SetSelected(ReferenceEquals(nav, NavStory));
        NavDiagnostics.SetSelected(ReferenceEquals(nav, NavDiagnostics));
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
            case DiagnosticsPage diagnostics:
                diagnostics.Deactivate();
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
        BackRouter.Handler = HandleBack;
        // 删除 / 导入版本后回到列表，重新扫描一次。
        _ = InitializeAsync();
        // 冷启动 / 其它页面期间收到的分享文件，等主界面出现后处理。
        _ = ShareImportHelper.ProcessPendingAsync(this);
        UpdateAvatar();
        ApplySubtitle();
        // 主题色可能在设置页被改过，回来时重新取一次选中色。
        Highlight(_activeNav ?? NavVersions);
        // 从详情 / 模组管理等页面返回时，恢复当前面板的轮询与刷新。
        if (_activePanel != null) _ = ActivateAsync(_activePanel);
    }

    protected override void OnDisappearing()
    {
        if (BackRouter.Handler == HandleBack) BackRouter.Handler = null;
        Deactivate(_activePanel);
        base.OnDisappearing();
    }

    /// <summary>
    /// 主界面返回键：有版本还在运行时先确认退出（对应 WPF 关闭窗口时的提示）；
    /// 没有运行中的版本或上面还有二级页时交回默认处理。
    /// </summary>
    private bool HandleBack()
    {
        if (Navigation.NavigationStack.Count > 1) return false;

        var running = ServerManager.RunningFolders;
        if (running.Count == 0) return false;

        _ = ConfirmExitAsync(running);
        return true;
    }

    private async Task ConfirmExitAsync(IReadOnlyCollection<string> folders)
    {
        var names = folders.Select(folder =>
        {
            var row = _versions.FirstOrDefault(v => v.Version.Folder == folder);
            if (row == null) return Path.GetFileName(folder.TrimEnd('\\', '/'));
            return row.Version.GameVersion != null
                ? $"DoL {row.Version.GameVersion}"
                : row.Version.DisplayName;
        }).ToList();

        var detail = string.Join("、", names.Take(5)) +
                     (names.Count > 5 ? $"，另有 {names.Count - 5} 个" : string.Empty);
        var ok = await PlatformServices.Dialogs.ConfirmAsync(
            $"{names.Count} 个游戏版本还在运行：{detail}\n\n" +
            "退出会一并停止本地服务器，这些游戏的页面将无法继续使用。",
            "退出 DoL 启动器", "仍然退出", danger: true);
        if (!ok) return;

        GameSurface.DisposeSession();
        ServerManager.StopAll();
        LauncherStatusService.Sync();
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Finish();
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
        // 按「启动时预取索引」设置预热下载中心的三份索引，不阻塞界面。
        _ = IndexPrefetchService.StartAsync();
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

    // ---------- 美化包轻量脚本入口 ----------

    internal int SkinVersionCount => _versions.Count;

    internal string SkinVersionName => _versions.FirstOrDefault()?.Version.DisplayName ?? string.Empty;

    internal string SkinGameVersion => _versions.FirstOrDefault()?.Version.GameVersion ?? string.Empty;

    internal bool SkinVersionRunning =>
        _versions.Any(row => ServerManager.IsRunning(row.Version.Folder));

    internal string SkinVersionFolder => _versions.FirstOrDefault()?.Version.FullPath ?? string.Empty;

    internal int SkinModTotal => LoadMods().Count;

    internal int SkinModEnabled => LoadMods().Count(mod => mod.Enabled);

    private List<ModInfo> LoadMods()
    {
        var folder = SkinVersionFolder;
        if (folder.Length == 0) return new List<ModInfo>();
        try
        {
            return ModService.Load(folder);
        }
        catch (Exception ex)
        {
            LogService.Error("美化包读取模组列表失败", ex);
            return new List<ModInfo>();
        }
    }

    /// <summary>按名字切换主界面；未知页面返回 false。</summary>
    internal bool SkinOpenPage(string name)
    {
        switch ((name ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "version":
                ShowVersions();
                return true;
            case "download":
                OnDownloadNavClicked(null, EventArgs.Empty);
                return true;
            case "backup":
                OnBackupNavClicked(null, EventArgs.Empty);
                return true;
            case "story":
                OnStoryNavClicked(null, EventArgs.Empty);
                return true;
            case "diagnostic":
            case "diagnostics":
                OnDiagnosticNavClicked(null, EventArgs.Empty);
                return true;
            case "settings":
                SkinOpenSettings();
                return true;
            default:
                return false;
        }
    }

    /// <summary>打开设置首页。</summary>
    internal void SkinOpenSettings()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await Navigation.PushAsync(new SettingsPage());
            }
            catch (Exception ex)
            {
                LogService.Error("打开设置页失败", ex);
            }
        });
    }

    /// <summary>启动第一个版本（与详情页「启动游戏」一致）。</summary>
    internal void SkinLaunch()
    {
        if (_versions.FirstOrDefault()?.Version is not { } version) return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                var page = new VersionDetailPage(version);
                await Navigation.PushAsync(page);
                await page.LaunchAsync();
            }
            catch (Exception ex)
            {
                LogService.Error("美化包启动版本失败", ex);
            }
        });
    }
}

/// <summary>主界面版本行：包装 VersionInfo，带 WPF 版本卡片的副标题与运行状态。</summary>
public sealed class MainVersionRow
{
    public MainVersionRow(VersionInfo version) => Version = version;

    public VersionInfo Version { get; }
    public string DisplayName => Version.DisplayName;
    public bool CanManageMods => !Version.IsFileMode;

    public string Subtitle => Version.IsFileMode
        ? "file:// 模式 · 与所有本地网页共享浏览器数据"
        : $"端口 {Version.Port} · {(string.IsNullOrWhiteSpace(Version.GameVersion) ? "未知版本" : Version.GameVersion)}";

    public string StatusText
    {
        get
        {
            if (Version.IsFileMode) return "旧数据";
            if (SimulationTracker.IsSimulating(Version.Folder)) return "模拟中";
            if (!ServerManager.IsRunning(Version.Folder)) return "未启动";
            return ServerManager.IsHelperOnly(Version.Folder) ? "存档助手" : "运行中";
        }
    }

    public Color StatusColor => Version.IsFileMode
        ? Color.FromArgb("#E0A65C")
        : SimulationTracker.IsSimulating(Version.Folder)
            ? Color.FromArgb("#B39DDB")
            : !ServerManager.IsRunning(Version.Folder)
                ? Color.FromArgb("#9098A6")
                : ServerManager.IsHelperOnly(Version.Folder)
                    ? Color.FromArgb("#6C9CD9")
                    : Color.FromArgb("#6ECB8F");
}
