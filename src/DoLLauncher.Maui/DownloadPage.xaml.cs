using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DoLLauncher.Core.Import;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class DownloadPage : ContentView
{
    private readonly ObservableCollection<DownloadRow> _rows = new();
    private bool _loading;
    private bool _installing;
    private bool _loaded;
    /// <summary>本轮加载是否失败：失败时只显示错误面板，避免空态文案与错误文案叠在一起。</summary>
    private bool _failed;

    /// <summary>程序化设置下载源时置位，避免触发保存与重复加载。</summary>
    private bool _loadingSource;
    /// <summary>最近一次安装的参数，失败后供「重试」按钮使用。</summary>
    private GameRelease? _retryRelease;
    private bool _retryPolyfill;
    private bool _retryI18n;
    private bool _retryImagePack;

    /// <summary>任务完成后的延迟移除，页面离开时取消，避免泄漏。</summary>
    private CancellationTokenSource? _retireCts;
    private bool _taskEventsAttached;

    private readonly VersionInfo? _preferredTarget;
    private readonly List<VersionInfo> _versions = new();
    private bool _marketActive;
    private bool _packActive;
    private readonly ObservableCollection<PackOption> _packOptions = new();
    private bool _packLoaded;
    private bool _packLoading;
    private bool _packInstalling;
    private bool _optionsExpanded = true;
    private bool _togglingOptions;
    private bool _activatingMarket;
    private bool _fillingTargets;

    public DownloadPage() : this(false, null) { }

    public DownloadPage(bool startInMarket, VersionInfo? targetVersion = null)
    {
        InitializeComponent();
        _preferredTarget = targetVersion;

        ReleaseList.ItemsSource = _rows;
        TaskList.ItemsSource = DownloadService.DownloadTasks;
        PackList.ItemsSource = _packOptions;
        PackTypePicker.ItemsSource = new[] { DownloadService.LyraSource };
        PackTypePicker.SelectedIndex = 0;

        GameSourcePicker.ItemsSource = DownloadService.GameSources.ToList();
        GameSourcePicker.ItemDisplayBinding = new Binding(nameof(GameSource.Name));

        _loadingSource = true;
        try
        {
            GameSourcePicker.SelectedItem = DownloadService.CurrentGameSource();
        }
        finally
        {
            _loadingSource = false;
        }

        ApplySourceUi(DownloadService.CurrentGameSource());
        ShowSegment(!startInMarket);
    }

    /// <summary>主界面切换到本面板时调用。</summary>
    public async Task ActivateAsync()
    {
        AttachTaskEvents();

        // 只自动加载一次，之后靠下拉刷新 / 侧栏刷新。
        if (!_loaded && !_marketActive)
        {
            _loaded = true;
            await LoadAsync();
        }

        if (_marketActive) await ActivateMarketAsync();
        if (_packActive) await LoadPacksAsync();
    }

    /// <summary>切走或离开主界面时调用。</summary>
    public void Deactivate()
    {
        MarketContent.Deactivate();
        DetachTaskEvents();
    }

    private async Task LoadAsync(bool silent = false)
    {
        if (_loading) return;
        _loading = true;

        // 下拉刷新时系统已经有转圈，不再叠一层加载面板。
        var showLoading = !silent && !Refresher.IsRefreshing;
        if (!silent)
        {
            _failed = false;
            ErrorPanel.IsVisible = false;
        }
        if (showLoading)
        {
            ReleaseList.IsVisible = false;
            LoadingPanel.IsVisible = true;
            LoadingIndicator.IsRunning = true;
        }

        try
        {
            var source = DownloadService.CurrentGameSource();
            ApplySourceUi(source);

            var releases = await DownloadService.FetchGameReleasesAsync(source);
            _rows.Clear();
            foreach (var release in releases.OrderByDescending(r => r.PublishedAt))
                _rows.Add(new DownloadRow(release));
        }
        catch (Exception ex)
        {
            LogService.Error("获取游戏版本列表失败", ex);
            if (!silent)
            {
                _failed = true;
                ErrorLabel.Text = "获取版本列表失败：" + ex.Message;
                ErrorPanel.IsVisible = true;
            }
        }
        finally
        {
            _loading = false;
            if (showLoading)
            {
                LoadingIndicator.IsRunning = false;
                LoadingPanel.IsVisible = false;
            }
            if (!silent) ReleaseList.IsVisible = !_failed;
            Refresher.IsRefreshing = false;
        }
    }

    // ---------- 子面板 / 侧栏 / 折叠 ----------

    /// <summary>宿主（剧情页等）切到模组市场面板。</summary>
    public async Task ShowMarketAsync()
    {
        ShowSegment(false);
        await ActivateMarketAsync();
    }

    private async void OnGameSegmentClicked(object? sender, EventArgs e) => ShowSegment(true);

    private async void OnMarketSegmentClicked(object? sender, EventArgs e)
    {
        ShowSegment(false);
        await ActivateMarketAsync();
    }

    private async void OnPackSegmentClicked(object? sender, EventArgs e)
    {
        ShowPackSegment();
        await LoadPacksAsync();
    }

    private void ShowSegment(bool game)
    {
        if (!_packActive && _marketActive == !game) return;
        _packActive = false;
        _marketActive = !game;
        PackContent.IsVisible = false;

        var show = game ? (View)GameContent : MarketContent;
        var hide = game ? (View)MarketContent : GameContent;
        hide.IsVisible = false;
        show.IsVisible = true;
        if (UiMotion.Enabled)
        {
            show.Opacity = 0;
            _ = show.FadeToAsync(1, 150, Easing.CubicOut);
        }
        else
        {
            show.Opacity = 1;
        }

        TargetPanel.IsVisible = !game;
        StyleNav(NavGame, game);
        StyleNav(NavMarket, !game);
        StyleNav(NavPack, false);
    }

    private void ShowPackSegment()
    {
        if (_packActive) return;
        _packActive = true;
        _marketActive = false;

        GameContent.IsVisible = false;
        MarketContent.IsVisible = false;
        PackContent.IsVisible = true;
        if (UiMotion.Enabled)
        {
            PackContent.Opacity = 0;
            _ = PackContent.FadeToAsync(1, 150, Easing.CubicOut);
        }
        else
        {
            PackContent.Opacity = 1;
        }

        TargetPanel.IsVisible = false;
        StyleNav(NavGame, false);
        StyleNav(NavMarket, false);
        StyleNav(NavPack, true);
    }

    private static void StyleNav(IconActionView nav, bool active)
        => nav.Variant = active ? "pale" : "muted";

    /// <summary>首次切入市场面板：先补版本列表，再让面板拉索引。</summary>
    private async Task ActivateMarketAsync()
    {
        if (_activatingMarket) return;
        _activatingMarket = true;
        try
        {
            await LoadTargetVersionsAsync();
            await MarketContent.ActivateAsync();
        }
        finally
        {
            _activatingMarket = false;
        }
    }

    private async Task LoadTargetVersionsAsync()
    {
        List<VersionInfo> list;
        try
        {
            list = await Task.Run(VersionService.Scan);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            return;
        }

        list = list.Where(v => v.Mode == "http").ToList();
        var same = list.Count == _versions.Count &&
                   list.Select(v => v.Folder).SequenceEqual(_versions.Select(v => v.Folder),
                       StringComparer.OrdinalIgnoreCase);
        if (same && TargetVersionPicker.ItemsSource != null) return;

        _versions.Clear();
        _versions.AddRange(list);

        _fillingTargets = true;
        try
        {
            var keep = (TargetVersionPicker.SelectedItem as VersionInfo)?.Folder;
            TargetVersionPicker.ItemsSource = _versions;
            var index = keep == null ? -1 : _versions.FindIndex(v =>
                string.Equals(v.Folder, keep, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && _preferredTarget != null)
                index = _versions.FindIndex(v =>
                    string.Equals(v.Folder, _preferredTarget.Folder, StringComparison.OrdinalIgnoreCase));
            TargetVersionPicker.SelectedIndex = index >= 0 ? index : (_versions.Count > 0 ? 0 : -1);
        }
        finally
        {
            _fillingTargets = false;
        }

        await MarketContent.SetTargetAsync(TargetVersionPicker.SelectedItem as VersionInfo);
    }

    private async void OnTargetVersionChanged(object? sender, EventArgs e)
    {
        if (_fillingTargets) return;
        await MarketContent.SetTargetAsync(TargetVersionPicker.SelectedItem as VersionInfo);
    }

    private async void OnToggleOptionsClicked(object? sender, TappedEventArgs e)
    {
        if (_togglingOptions) return;
        _togglingOptions = true;
        _optionsExpanded = !_optionsExpanded;
        try
        {
            await Task.WhenAll(
                UiMotion.TogglePanelAsync(OptionsBody, _optionsExpanded),
                UiMotion.RotateAsync(OptionsChevron, _optionsExpanded ? 90 : 0));
        }
        finally { _togglingOptions = false; }
    }

    // ---------- 游戏源 ----------

    /// <summary>按游戏源形态调整说明与可选安装项：单 HTML 源没有 polyfill。</summary>
    private void ApplySourceUi(GameSource source)
    {
        SourceHintLabel.Text = source.Hint;
        PolyfillOption.IsVisible = source.IsZip;
        if (!source.IsZip) PolyfillCheck.IsChecked = false;
    }

    private async void OnGameSourceChanged(object? sender, EventArgs e)
    {
        if (_loadingSource || GameSourcePicker.SelectedItem is not GameSource source) return;
        if (source.Id == DownloadService.CurrentGameSource().Id)
        {
            ApplySourceUi(source);
            return;
        }

        try
        {
            var config = AppConfig.Load();
            config.General.GameSource = source.Id;
            await Task.Run(() => AppConfig.Save(config));
        }
        catch (Exception ex)
        {
            LogService.Error("保存游戏下载源失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存下载源失败：" + ex.Message, "下载中心");
            return;
        }

        ApplySourceUi(source);
        LogService.Append($"[下载] 切换游戏源：{source.Name}");
        await LoadAsync();
    }

    // ---------- Release 列表 ----------

    private async void OnRefreshing(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (_marketActive) await MarketContent.RefreshAsync();
        else if (_packActive) await LoadPacksAsync(force: true);
        else await LoadAsync();
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnOpenBodyClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: DownloadRow row }) return;
        if (string.IsNullOrWhiteSpace(row.Release.Body)) return;
        await Navigation.PushAsync(new MarkdownPage(row.Title, row.Release.Body));
    }

    // ---------- 整合包 ----------

    private async Task LoadPacksAsync(bool force = false)
    {
        if (_packLoading || (_packLoaded && !force)) return;
        _packLoading = true;
        PackStatusLabel.Text = "正在获取 Lyra 整合包列表…";
        try
        {
            var releases = await DownloadService.FetchLyraReleasesAsync();
            var release = releases.FirstOrDefault(r => r.Assets.Count > 0);
            _packOptions.Clear();

            if (release != null)
            {
                foreach (var asset in release.Assets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var label = asset.Name;
                    var detail = $"{asset.SizeText} · {release.Tag}";
                    if (LyraPackName.TryParse(asset.Name, out var info))
                    {
                        label = $"Lyra {info.ComboLabel} · 游戏 {info.GameVersion} · 汉化 {info.ChsVersion}";
                        detail = $"{asset.SizeText} · {asset.Name}";
                    }

                    _packOptions.Add(new PackOption
                    {
                        Label = label,
                        Detail = detail,
                        Asset = asset,
                        Release = release,
                    });
                }
            }

            _packLoaded = true;
            PackStatusLabel.Text = _packOptions.Count > 0
                ? $"共 {_packOptions.Count} 个整合包"
                : "没有找到 Lyra 整合包";
            LogService.Append($"已获取 {_packOptions.Count} 个 Lyra 整合包（{release?.Tag ?? "无 Release"}）");
        }
        catch (Exception ex)
        {
            PackStatusLabel.Text = "获取失败：" + ex.Message;
            LogService.Error("获取 Lyra 整合包列表失败", ex);
        }
        finally
        {
            _packLoading = false;
        }
    }

    private async void OnInstallPackClicked(object? sender, EventArgs e)
    {
        if (_packInstalling) return;
        if (sender is not Button { BindingContext: PackOption option }) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将下载「{option.Label}」（{option.Asset.SizeText}）并安装为独立版本。\n\n" +
                "安装后可以选择把普通模组解压到 mods/，ModLoader 必需组件会保留在内置。继续吗？",
                "下载整合包")) return;

        _packInstalling = true;
        PackBusyPanel.IsVisible = true;
        PackBusyIndicator.IsRunning = true;
        PackBusyLabel.Text = "0%";
        try
        {
            var folder = await DownloadService.InstallLyraAsync(option.Asset,
                msg => LogService.Append(msg),
                p => MainThread.BeginInvokeOnMainThread(() => PackBusyLabel.Text = $"{p:P0}"));
            PackBusyLabel.Text = "安装完成";

            var versionDir = Path.Combine(Paths.VersionsDir, folder);
            var embedded = await Task.Run(() => LyraPackService.ReadEmbeddedMods(versionDir));
            var extractable = embedded.Count(m => !m.IsBuiltin);

            if (extractable > 0)
            {
                var choice = await DolDialog.ChooseAsync("解压内置模组",
                    $"整合包已安装，其中 {extractable} 个普通模组可以解压到 mods/ 以便启停和排序。\n" +
                    $"ModLoader 必需组件（{embedded.Count - extractable} 个）会保留在内置。现在解压吗？",
                    new[] { "解压到 mods", "稍后" }, DolDialogKind.Info, primaryIndex: 0);
                if (choice == 0)
                {
                    var result = await Task.Run(() => LyraPackService.ExtractToMods(versionDir, LogService.Append));
                    LogService.Append($"内置模组已解压：{result.Extracted} 个；{result.Skipped} 个必需组件保留内置。");
                }
            }

            await PlatformServices.Dialogs.InfoAsync(
                $"整合包已安装为版本「{folder}」。" +
                (extractable > 0 ? "" : "\n没有可解压的普通模组。") +
                "\n请到版本页启动或管理它。",
                "安装完成");
        }
        catch (Exception ex)
        {
            LogService.Error("整合包下载/安装失败", ex);
            await PlatformServices.Dialogs.ErrorAsync(ex.Message, "下载失败");
        }
        finally
        {
            _packInstalling = false;
            PackBusyIndicator.IsRunning = false;
            PackBusyPanel.IsVisible = false;
        }
    }

    // ---------- 安装 ----------

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (_installing) return;
        if (sender is not Button { BindingContext: DownloadRow row }) return;

        var release = row.Release;
        var usePolyfill = release.Source.IsZip && PolyfillCheck.IsChecked;
        if (release.PickAsset(usePolyfill) == null)
        {
            await PlatformServices.Dialogs.InfoAsync(
                release.Source.IsZip ? "这个 Release 没有可用的游戏压缩包。" : "这个 Release 没有可用的 HTML 文件。",
                "下载中心");
            return;
        }

        var includeI18n = I18nCheck.IsChecked;
        var includeImagePack = ImagePackCheck.IsChecked;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                BuildConfirmMessage(release, usePolyfill, includeI18n, includeImagePack),
                "下载游戏本体", "开始安装"))
            return;

        await RunInstallAsync(release, usePolyfill, includeI18n, includeImagePack);
    }

    private static string BuildConfirmMessage(GameRelease release, bool usePolyfill,
        bool includeI18n, bool includeImagePack)
    {
        var parts = new List<string> { "游戏本体" };
        if (includeI18n) parts.Add("ModI18N 汉化模组");
        if (includeImagePack) parts.Add("原版图片包");

        var message = $"将下载：{string.Join(" + ", parts)}";
        if (usePolyfill) message += "（polyfill 版本）";
        message += $"\n\n{release.Title} · {release.DateText}" +
                   "\n下载体积较大，安装完成后会出现在版本列表。继续吗？";
        return message;
    }

    private async void OnRetryInstallClicked(object? sender, EventArgs e)
    {
        if (_installing || _retryRelease == null) return;
        var release = _retryRelease;
        await RunInstallAsync(release, _retryPolyfill, _retryI18n, _retryImagePack);
    }

    private async Task RunInstallAsync(GameRelease release, bool usePolyfill,
        bool includeI18n, bool includeImagePack)
    {
        _installing = true;
        _retryRelease = release;
        _retryPolyfill = usePolyfill;
        _retryI18n = includeI18n;
        _retryImagePack = includeImagePack;

        SetBusy(true);
        RetryPanel.IsVisible = false;

        try
        {
            Task<string> RunInstall() => DownloadService.InstallGameAsync(release,
                usePolyfill: usePolyfill, includeI18n: includeI18n, includeImagePack: includeImagePack,
                log: OnInstallLog, progress: _ => { });

            string folder;
            try
            {
                folder = await RunInstall();
            }
            catch (Exception ex) when (ex.Message.Contains("版本目录已存在"))
            {
                // 重复安装同一版本：询问是否清掉旧目录后重来，避免每次都卡在「已存在」。
                var name = ex.Message[(ex.Message.LastIndexOfAny(new[] { '\\', '/' }) + 1)..].Trim();
                var dir = Path.Combine(PlatformServices.Paths.VersionsDir, name);
                var overwrite = await PlatformServices.Dialogs.ConfirmAsync(
                    $"版本目录已存在：{name}\n\n是否删除旧目录并重新安装？", "下载游戏",
                    "覆盖安装", danger: true);
                if (!overwrite)
                {
                    LogService.Append($"[下载] 用户取消覆盖安装：{name}");
                    return;
                }
                await Task.Run(() => Directory.Delete(dir, true));
                folder = await RunInstall();
            }

            LogService.Append($"[下载] 安装完成：{folder}");
            _retryRelease = null;

            await LoadAsync(silent: true);

            // 成功后询问是否直接进入新版本详情页。
            if (await PlatformServices.Dialogs.ConfirmAsync(
                    $"安装完成：{folder}", "下载游戏", "查看版本"))
                await OpenVersionAsync(folder);
        }
        catch (OperationCanceledException)
        {
            LogService.Append("[下载] 安装已取消。");
            await PlatformServices.Dialogs.InfoAsync("安装已取消。", "下载游戏");
        }
        catch (Exception ex)
        {
            LogService.Error("安装游戏本体失败", ex);
            RetryLabel.Text = "上次安装失败：" + ex.Message;
            RetryPanel.IsVisible = true;
            await PlatformServices.Dialogs.ErrorAsync("安装失败：" + ex.Message, "下载游戏");
        }
        finally
        {
            _installing = false;
            SetBusy(false);
        }
    }

    /// <summary>按目录重新取版本信息；取不到就回到版本列表。</summary>
    private async Task OpenVersionAsync(string folder)
    {
        var list = await Task.Run(VersionService.Scan);
        var info = list.FirstOrDefault(v => v.Mode == "http" &&
            string.Equals(v.Folder, folder, StringComparison.OrdinalIgnoreCase));
        if (info == null)
        {
            await Navigation.PopToRootAsync();
            return;
        }
        await Navigation.PushAsync(new VersionDetailPage(info));
    }

    /// <summary>安装日志写入日志文件；进度由左侧下载列表的任务条目实时展示。</summary>
    private static void OnInstallLog(string message) => LogService.Append("[下载] " + message);

    // ---------- 下载任务 ----------

    private DownloadTaskVm? SelectedTask => TaskList.SelectedItem as DownloadTaskVm;

    private void OnTaskSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 只有仍在进行、可以取消的任务才显示取消按钮（与 WPF 端一致）。
        CancelTaskButton.IsVisible = SelectedTask?.CanCancel == true;
    }

    private void AttachTaskEvents()
    {
        if (_taskEventsAttached) return;
        _taskEventsAttached = true;
        _retireCts = new CancellationTokenSource();
        DownloadService.DownloadTasks.CollectionChanged += OnTasksChanged;

        foreach (var task in DownloadService.DownloadTasks)
        {
            task.Finished += OnTaskFinished;
            // 页面不在前台期间已完成 / 取消的任务，回来时补一次延迟移除。
            if (task.State is "完成" or "已取消")
                OnTaskFinished(task, EventArgs.Empty);
        }
    }

    private void DetachTaskEvents()
    {
        if (!_taskEventsAttached) return;
        _taskEventsAttached = false;
        DownloadService.DownloadTasks.CollectionChanged -= OnTasksChanged;

        foreach (var task in DownloadService.DownloadTasks)
            task.Finished -= OnTaskFinished;

        _retireCts?.Cancel();
        _retireCts?.Dispose();
        _retireCts = null;
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (var task in e.OldItems.OfType<DownloadTaskVm>())
                task.Finished -= OnTaskFinished;

        if (e.NewItems != null)
            foreach (var task in e.NewItems.OfType<DownloadTaskVm>())
                task.Finished += OnTaskFinished;

        RefreshCancelButton();
    }

    private void OnTaskFinished(object? sender, EventArgs e)
    {
        if (sender is not DownloadTaskVm task) return;
        RefreshCancelButton();
        _ = RetireTaskAsync(task, _retireCts?.Token ?? CancellationToken.None);
    }

    /// <summary>取消按钮只在选中「仍可取消」的任务时出现（完成 / 已取消的任务不显示）。</summary>
    private void RefreshCancelButton()
    {
        if (SelectedTask == null || !DownloadService.DownloadTasks.Contains(SelectedTask))
            TaskList.SelectedItem = null;
        CancelTaskButton.IsVisible = SelectedTask?.CanCancel == true;
    }

    /// <summary>完成或被取消的任务先留 3 秒，再从共享任务列表移除。</summary>
    private static async Task RetireTaskAsync(DownloadTaskVm task, CancellationToken token)
    {
        try
        {
            await Task.Delay(3000, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (DownloadService.DownloadTasks.Contains(task))
                DownloadService.DownloadTasks.Remove(task);
        });
    }

    private void OnCancelTaskClicked(object? sender, EventArgs e)
    {
        var task = SelectedTask;
        if (task is null || !task.CanCancel) return;
        task.Cancel();
        TaskList.SelectedItem = null;
        CancelTaskButton.IsVisible = false;
    }

    /// <summary>遮罩上的取消已移除：下载进度改由左侧列表展示，取消在列表里选中任务后操作。</summary>
    private void SetBusy(bool busy)
    {
        Refresher.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        GameSourcePicker.IsEnabled = !busy;
        NavGame.IsEnabled = !busy;
        NavMarket.IsEnabled = !busy;
        foreach (var row in _rows)
            row.CanInstall = !busy && row.Release.HasInstallableAsset;
    }
}

/// <summary>下载列表行：包装 GameRelease，并保存更新日志的展开状态。</summary>
public sealed class DownloadRow : INotifyPropertyChanged
{
    private bool _canInstall;

    public DownloadRow(GameRelease release)
    {
        Release = release;
        _canInstall = release.HasInstallableAsset;
    }

    public GameRelease Release { get; }
    public string Title => Release.Title;
    public string MetaText => Release.ReleaseTip;
    public string AssetSummary => Release.AssetSummary;
    public bool Prerelease => Release.Prerelease;
    public bool HasBody => !string.IsNullOrWhiteSpace(Release.Body);

    public bool CanInstall
    {
        get => _canInstall;
        set
        {
            if (_canInstall == value) return;
            _canInstall = value;
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
