using DoLLauncher.Services;

namespace DoLLauncher;

public partial class MainWindow
{
    /// <summary>启动时在后台检查最新版本；网络异常只写日志，不打扰用户。</summary>
    private async Task AutoCheckUpdateAsync()
    {
        var cfg = AppConfig.Load();
        if (!cfg.General.AutoCheckUpdate) return;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (string.Equals(cfg.General.UpdateSkipDate, today, StringComparison.Ordinal))
        {
            Log("自动检查更新：今日已选择不再提醒，跳过");
            return;
        }

        try
        {
            Log("自动检查更新：开始");
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                Log("自动检查更新：没有可用的 Release 信息");
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            if (!hasUpdate)
            {
                Log($"自动检查更新：已是最新版本（{BuildInfo.Tag}）");
                return;
            }

            Log("自动检查更新：发现新版本 " + release.Tag);
            ShowUpdateDialog(release);
        }
        catch (Exception ex)
        {
            // 网络异常不弹窗，只记录日志
            LogError("自动检查更新失败", ex);
        }
    }

    /// <summary>显示更新弹窗；关闭后按复选框状态决定是否「今日内不再提醒」。</summary>
    private void ShowUpdateDialog(LauncherRelease release)
    {
        var dialog = new UpdateDialog(release) { Owner = this };
        dialog.ShowDialog();

        if (dialog.UpdateCompleted || !dialog.SkipTodayChecked) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.UpdateSkipDate = DateTime.Now.ToString("yyyy-MM-dd");
            AppConfig.Save(cfg);
            Log("更新提示：今日内不再提醒");
        }
        catch (Exception ex)
        {
            LogError("保存设置失败", ex);
        }
    }
}
