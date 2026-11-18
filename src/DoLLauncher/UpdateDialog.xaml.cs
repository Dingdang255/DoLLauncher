using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>发现新版本时的更新弹窗：展示更新日志、下载启动器本体并替换重启。</summary>
public partial class UpdateDialog : Window
{
    private readonly LauncherRelease _release;
    private bool _downloading;
    private bool _completed;

    /// <summary>本次更新是否已经下载并安排好替换（完成后的关闭不再计入「今日不再提醒」）。</summary>
    public bool UpdateCompleted => _completed;

    /// <summary>关闭弹窗时复选框的状态，由调用方决定是否写入跳过日期。</summary>
    public bool SkipTodayChecked => SkipTodayCheck.IsChecked == true;

    public UpdateDialog(LauncherRelease release)
    {
        InitializeComponent();
        SkinService.ApplyDialogBackground(this);
        ThemeService.Attach(this);
        AppDialog.AttachDialogChrome(this, Width);
        UiAnimations.AttachWindow(this);

        _release = release;
        var current = BuildInfo.Tag;
        SubtitleText.Text = current == "dev"
            ? $"最新版本 {release.Tag}（当前为开发版构建）"
            : $"最新版本 {release.Tag}（当前 {current}）";
        ChangelogBox.Document = MarkdownService.Render(
            release.Body.Trim().Length > 0 ? release.Body : "（该版本没有提供更新日志）");

        Closing += OnClosing;
        Loaded += (_, _) => SetCloseEnabled(true);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 已经开始正式下载后不允许关闭；此前的关闭视为取消本次更新
        if (_downloading) e.Cancel = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;

        var package = _release.FindPackage();
        if (package == null)
        {
            ShowFailure("这个发布版本没有提供可下载的启动器包。");
            return;
        }

        _downloading = true;
        SetBusy(true);
        FailurePanel.Visibility = Visibility.Collapsed;
        ChangelogBorder.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "正在下载启动器本体…";
        try
        {
            var zip = await UpdateService.DownloadPackageAsync(package, report =>
                Dispatcher.InvokeAsync(() => SetProgress(report)));

            SetProgress(1);
            ProgressText.Text = "下载完成，正在安装…";
            await Task.Run(() => UpdateService.ScheduleApply(zip));

            _completed = true;
            // 更新已经完成，之后的关闭 / 重启都不再算作「今日不再提醒」
            _downloading = false;
            ProgressText.Text = "更新完成，启动器即将重启…";
            LogService.Append("启动器更新：已下载 " + package.Name + "，准备重启完成替换");
            await Task.Delay(400);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _downloading = false;
            SetBusy(false);
            ProgressPanel.Visibility = Visibility.Collapsed;
            LogService.Error("启动器更新失败", ex);
            ShowFailure("自动更新失败：" + ex.Message);
        }
    }

    private void SetProgress(double value)
    {
        var percent = Math.Clamp(value, 0, 1);
        DownloadProgress.Value = percent;
        ProgressText.Text = $"正在下载启动器本体… {percent * 100:F0}%";
    }

    private void SetBusy(bool busy)
    {
        UpdateBtn.IsEnabled = !busy;
        LaterBtn.IsEnabled = !busy;
        SkipTodayCheck.IsEnabled = !busy;
        SetCloseEnabled(!busy);
    }

    private void ShowFailure(string reason)
    {
        ChangelogBorder.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Visible;
        FailureText.Text = reason + "\n可以前往 GitHub Release 页面手动下载最新的启动器包。";
    }

    private void ManualDownload_Click(object sender, RoutedEventArgs e)
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

    // ---------- 禁用 / 恢复标题栏关闭按钮 ----------

    private const uint ScClose = 0xF060;
    private const uint MfByCommand = 0x00000000;
    private const uint MfEnabled = 0x00000000;
    private const uint MfGrayed = 0x00000001;

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool revert);

    [DllImport("user32.dll")]
    private static extern uint EnableMenuItem(IntPtr hMenu, uint item, uint enable);

    [DllImport("user32.dll")]
    private static extern bool DrawMenuBar(IntPtr hWnd);

    private void SetCloseEnabled(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero) return;
        EnableMenuItem(menu, ScClose, MfByCommand | (enabled ? MfEnabled : MfGrayed));
        DrawMenuBar(hwnd);
    }
}
