using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>日志回调很频繁，界面上的最近一行按时间节流。</summary>
    private long _lastLogTick;
    private string _lastLog = "";

    public DownloadPage()
    {
        InitializeComponent();
        ReleaseList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 只自动加载一次，之后靠下拉刷新 / 工具栏刷新。
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
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
            SourceLabel.Text = $"下载源：{source.Name}";

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

    private async void OnRefreshing(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (_installing) return;
        if (sender is not Button { BindingContext: DownloadRow row }) return;

        var release = row.Release;
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"将下载「{release.DisplayName}」并安装为新版本，是否继续？", "下载游戏", "开始安装"))
            return;

        _installing = true;
        SetBusy(true);
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

            var folder = await DownloadService.InstallGameAsync(release,
                usePolyfill: false, includeI18n: true, includeImagePack: false,
                log: OnInstallLog, progress: progress);

            LogService.Append($"[下载] 安装完成：{folder}");
            await PlatformServices.Dialogs.InfoAsync($"已安装为新版本：{folder}", "下载游戏");

            // 安装完成后刷新列表，让按钮状态与最新数据保持一致。
            await LoadAsync(silent: true);
        }
        catch (Exception ex)
        {
            LogService.Error("安装游戏本体失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("安装失败：" + ex.Message, "下载游戏");
        }
        finally
        {
            _installing = false;
            SetBusy(false);
        }
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

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        RefreshItem.IsEnabled = !busy;
        foreach (var row in _rows)
            row.CanInstall = !busy && row.Release.HasInstallableAsset;
    }
}

/// <summary>下载列表行：包装 GameRelease，安装期间统一禁用「安装」按钮。</summary>
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
