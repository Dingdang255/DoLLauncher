using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class DownloadPage : ContentPage
{
    private readonly ObservableCollection<DownloadRow> _rows = new();
    private bool _loading;
    private bool _installing;
    private bool _loaded;
    /// <summary>本轮加载是否失败：失败时只显示错误面板，避免空态文案与错误文案叠在一起。</summary>
    private bool _failed;

    /// <summary>程序化设置下载源时置位，避免触发保存与重复加载。</summary>
    private bool _loadingSource;

    /// <summary>日志回调很频繁，界面上的最近一行按时间节流。</summary>
    private long _lastLogTick;
    private string _lastLog = "";

    /// <summary>最近一次安装的参数，失败后供「重试」按钮使用。</summary>
    private GameRelease? _retryRelease;
    private bool _retryPolyfill;
    private bool _retryI18n;
    private bool _retryImagePack;

    /// <summary>任务完成后的延迟移除，页面离开时取消，避免泄漏。</summary>
    private CancellationTokenSource? _retireCts;
    private bool _taskEventsAttached;

    public DownloadPage()
    {
        InitializeComponent();
        ReleaseList.ItemsSource = _rows;
        TaskList.ItemsSource = DownloadService.DownloadTasks;

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
        ShowSegment(true);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AttachTaskEvents();

        // 只自动加载一次，之后靠下拉刷新 / 工具栏刷新。
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        DetachTaskEvents();
        base.OnDisappearing();
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

    // ---------- 分段与游戏源 ----------

    private void OnGameSegmentClicked(object? sender, EventArgs e) => ShowSegment(true);

    private void OnMarketSegmentClicked(object? sender, EventArgs e) => ShowSegment(false);

    private void ShowSegment(bool game)
    {
        GamePane.IsVisible = game;
        GameContent.IsVisible = game;
        MarketPlaceholder.IsVisible = !game;
        StyleSegmentButton(GameSegmentButton, game);
        StyleSegmentButton(MarketSegmentButton, !game);
    }

    private static void StyleSegmentButton(Button button, bool active)
    {
        if (active)
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb("#512BD4"), Color.FromArgb("#7B5BD6"));
            button.TextColor = Colors.White;
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
            button.SetAppThemeColor(Button.TextColorProperty,
                Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        }
    }

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

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private void OnToggleBodyClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: DownloadRow row })
            row.IsExpanded = !row.IsExpanded;
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
        InstallProgress.Progress = 0;
        PercentLabel.Text = "0%";
        LogLabel.Text = "";

        try
        {
            Action<double> progress = value => MainThread.BeginInvokeOnMainThread(() =>
            {
                var ratio = Math.Clamp(value, 0, 1);
                InstallProgress.Progress = ratio;
                PercentLabel.Text = ratio.ToString("P0");
            });

            Task<string> RunInstall() => DownloadService.InstallGameAsync(release,
                usePolyfill: usePolyfill, includeI18n: includeI18n, includeImagePack: includeImagePack,
                log: OnInstallLog, progress: progress);

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

            InstallProgress.Progress = 1;
            PercentLabel.Text = "100%";
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

    /// <summary>完整日志写入日志文件，界面只节流显示最近一行。</summary>
    private void OnInstallLog(string message)
    {
        LogService.Append("[下载] " + message);

        var now = Environment.TickCount64;
        if (message == _lastLog || now - _lastLogTick < 300) return;
        _lastLog = message;
        _lastLogTick = now;
        MainThread.BeginInvokeOnMainThread(() => LogLabel.Text = message);
    }

    // ---------- 下载任务 ----------

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
    }

    private void OnTaskFinished(object? sender, EventArgs e)
    {
        if (sender is not DownloadTaskVm task) return;
        _ = RetireTaskAsync(task, _retireCts?.Token ?? CancellationToken.None);
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
        if (sender is Button { BindingContext: DownloadTaskVm task })
            task.Cancel();
    }

    /// <summary>遮罩上的取消：取消当前所有可取消的下载任务。</summary>
    private void OnCancelInstallClicked(object? sender, EventArgs e)
    {
        var active = DownloadService.DownloadTasks.Where(t => t.CanCancel).ToList();
        foreach (var task in active) task.Cancel();
        if (active.Count == 0) LogService.Append("[下载] 当前没有可取消的任务。");
    }

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        RefreshItem.IsEnabled = !busy;
        GameSourcePicker.IsEnabled = !busy;
        GameSegmentButton.IsEnabled = !busy;
        MarketSegmentButton.IsEnabled = !busy;
        foreach (var row in _rows)
            row.CanInstall = !busy && row.Release.HasInstallableAsset;
    }
}

/// <summary>下载列表行：包装 GameRelease，并保存更新日志的展开状态。</summary>
public sealed class DownloadRow : INotifyPropertyChanged
{
    /// <summary>更新日志纯文本上限，避免超长 Release 正文撑爆列表。</summary>
    private const int BodyLimit = 4000;

    private bool _canInstall;
    private bool _expanded;

    public DownloadRow(GameRelease release)
    {
        Release = release;
        _canInstall = release.HasInstallableAsset;
        BodyPreview = release.Body.Length > BodyLimit
            ? release.Body[..BodyLimit] + "\n…（已截断，完整内容见 Release 页面）"
            : release.Body;
    }

    public GameRelease Release { get; }
    public string Title => Release.Title;
    public string MetaText => Release.ReleaseTip;
    public string AssetSummary => Release.AssetSummary;
    public bool Prerelease => Release.Prerelease;
    public string BodyPreview { get; }
    public bool HasBody => BodyPreview.Length > 0;

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

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            OnPropertyChanged(nameof(IsExpanded));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
