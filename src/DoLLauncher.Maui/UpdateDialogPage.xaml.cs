using DoLLauncher.Maui.Platform;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

/// <summary>
/// 更新弹窗（Android）：展示更新日志，下载 Release 里的 APK 并交给系统安装。
/// 关闭时按「今日内不再提醒」复选框决定是否写入 updateSkipDate。
/// </summary>
public partial class UpdateDialogPage : ContentPage
{
    private static bool _showing;
    private static bool _autoCheckStarted;

    private readonly LauncherRelease _release;
    private readonly Func<bool> _backHandler;
    private readonly TaskCompletionSource<(bool Completed, bool Skip)> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Func<bool>? _previousBackHandler;
    private bool _downloading;
    private bool _completed;
    private bool _closed;

    private UpdateDialogPage(LauncherRelease release)
    {
        InitializeComponent();
        _release = release;
        _backHandler = HandleBack;

        var current = BuildInfo.Tag;
        SubtitleLabel.Text = current == "dev"
            ? $"最新版本 {release.Tag}（当前为开发版构建）"
            : $"最新版本 {release.Tag}（当前 {current}）";
        ChangelogLabel.Text = release.Body.Trim().Length > 0
            ? release.Body.Trim()
            : "（该版本没有提供更新日志）";
    }

    /// <summary>以模态方式展示更新弹窗，关闭后返回；勾选「今日不再提醒」时写入配置。</summary>
    public static async Task ShowAsync(Page host, LauncherRelease release)
    {
        if (_showing) return;
        _showing = true;
        try
        {
            if (host.Navigation.ModalStack.Count > 0) return;

            var page = new UpdateDialogPage(release);
            await host.Navigation.PushModalAsync(page);

            var (completed, skip) = await page._completion.Task;
            if (!completed && skip)
            {
                try
                {
                    var cfg = AppConfig.Load();
                    cfg.General.UpdateSkipDate = DateTime.Now.ToString("yyyy-MM-dd");
                    AppConfig.Save(cfg);
                    LogService.Append("更新提示：今日内不再提醒");
                }
                catch (Exception ex)
                {
                    LogService.Error("保存更新跳过日期失败", ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("显示更新弹窗失败", ex);
        }
        finally
        {
            _showing = false;
        }
    }

    /// <summary>启动后按配置在后台检查更新；网络异常只写日志。</summary>
    public static void StartAutoCheck(Window window)
    {
        if (_autoCheckStarted) return;
        _autoCheckStarted = true;
        _ = RunAutoCheckAsync(window);
    }

    private static async Task RunAutoCheckAsync(Window window)
    {
        try
        {
            var cfg = AppConfig.Load();
            if (!cfg.General.AutoCheckUpdate)
            {
                LogService.Append("自动检查更新：未开启，跳过");
                return;
            }

            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.Equals(cfg.General.UpdateSkipDate, today, StringComparison.Ordinal))
            {
                LogService.Append("自动检查更新：今日已选择不再提醒，跳过");
                return;
            }

            // 等主界面稳定后再检查，避免影响启动
            await Task.Delay(1500);
            LogService.Append("自动检查更新：开始");
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                LogService.Append("自动检查更新：没有可用的 Release 信息");
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            if (!hasUpdate)
            {
                LogService.Append($"自动检查更新：已是最新版本（{BuildInfo.Tag}）");
                return;
            }

            LogService.Append("自动检查更新：发现新版本 " + release.Tag);
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var host = window.Page;
                if (host == null) return;
                await ShowAsync(host, release);
            });
        }
        catch (Exception ex)
        {
            // 网络异常不弹窗，只记录日志
            LogService.Error("自动检查更新失败", ex);
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _previousBackHandler = BackRouter.Handler;
        BackRouter.Handler = _backHandler;
    }

    protected override void OnDisappearing()
    {
        if (ReferenceEquals(BackRouter.Handler, _backHandler)) BackRouter.Handler = _previousBackHandler;
        Complete();
        base.OnDisappearing();
    }

    private bool HandleBack()
    {
        if (_downloading) return true;
        _ = CloseAsync();
        return true;
    }

    private async void OnLaterClicked(object? sender, EventArgs e) => await CloseAsync();

    private async Task CloseAsync()
    {
        if (_downloading) return;
        try
        {
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("关闭更新弹窗失败", ex);
        }
        Complete();
    }

    private void Complete()
    {
        if (_closed) return;
        _closed = true;
        _completion.TrySetResult((_completed, SkipTodayCheck.IsChecked == true));
    }

    private async void OnUpdateClicked(object? sender, EventArgs e)
    {
        if (_downloading || _completed) return;

        _downloading = true;
        SetBusy(true);
        FailurePanel.IsVisible = false;
        StatusLabel.IsVisible = false;
        ProgressPanel.IsVisible = true;
        DownloadProgress.Progress = 0;
        ProgressText.Text = "正在下载安装包…";

        try
        {
            var path = await AndroidUpdateInstaller.DownloadApkAsync(_release, value =>
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DownloadProgress.Progress = value;
                    ProgressText.Text = $"正在下载安装包… {value * 100:F0}%";
                }));

            ProgressText.Text = "下载完成，正在启动系统安装程序…";
            AndroidUpdateInstaller.Install(path);

            _completed = true;
            ProgressPanel.IsVisible = false;
            StatusLabel.IsVisible = true;
            StatusLabel.Text = "安装包已下载，请在系统安装界面完成安装；安装前请允许本应用安装未知应用。";
            UpdateButton.Text = "已下载";
            UpdateButton.IsEnabled = false;
            LaterButton.Text = "关闭";
            SkipTodayCheck.IsEnabled = false;
            LogService.Append("启动器更新：已下载 " + Path.GetFileName(path) + "，等待系统安装");
        }
        catch (Exception ex)
        {
            LogService.Error("启动器更新失败", ex);
            ProgressPanel.IsVisible = false;
            ShowFailure(ex.Message);
        }
        finally
        {
            _downloading = false;
            if (!_completed) SetBusy(false);
        }
    }

    private void ShowFailure(string reason)
    {
        FailurePanel.IsVisible = true;
        FailureText.Text = "自动更新失败：" + reason +
                           "\n可以点击下面的按钮前往 GitHub Release 页面手动下载最新安装包。";
        UpdateButton.IsEnabled = true;
    }

    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        SkipTodayCheck.IsEnabled = !busy;
    }

    private void OnManualDownload(object? sender, EventArgs e)
    {
        if (_release.Url.Length == 0) return;
        try
        {
            PlatformServices.Links.OpenUrl(_release.Url);
        }
        catch (Exception ex)
        {
            LogService.Error("打开更新发布页失败", ex);
        }
    }
}
