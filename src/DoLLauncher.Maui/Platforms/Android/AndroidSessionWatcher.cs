using DoLLauncher.Core.Browser;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 关页检测（Android）：订阅 <see cref="BrowserSessionMonitor.SessionLost"/>，
/// 按 closePageBehavior 决定停服或先询问；应用退到后台时暂停检测，回前台重新登记。
/// 事件在线程池触发，所有界面与停服动作都切回主线程执行。
/// </summary>
internal static class AndroidSessionWatcher
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Desired = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> LastHandled = new(StringComparer.OrdinalIgnoreCase);
    private static bool _subscribed;

    /// <summary>应用是否在后台；后台时不响应关页事件，避免误杀正在运行的服务器。</summary>
    public static bool IsBackground { get; private set; }

    /// <summary>在应用启动时调用一次：订阅会话丢失事件。</summary>
    public static void Initialize()
    {
        lock (Sync)
        {
            if (_subscribed) return;
            _subscribed = true;
        }

        try
        {
            BrowserSessionMonitor.Instance.SessionLost += OnSessionLost;
            LogService.Append("已启用关页检测（Android WebView 心跳）。");
        }
        catch (Exception ex)
        {
            LogService.Error("订阅关页检测失败", ex);
        }
    }

    /// <summary>登记某个版本的关页检测；页面关闭后由监听器决定是否停服。</summary>
    public static void Track(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        lock (Sync) Desired.Add(folder);
        if (!IsBackground) TrackCore(folder);
    }

    /// <summary>页面自己关闭时取消登记；之后手动停服不会再触发关页事件。</summary>
    public static void Untrack(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        lock (Sync) Desired.Remove(folder);
        BrowserSessionMonitor.Instance.Forget(folder);
    }

    /// <summary>应用进入后台：先撤销会话，避免后台 WebView 被暂停导致心跳停止造成误判。</summary>
    public static void OnAppBackgrounded()
    {
        IsBackground = true;
        foreach (var folder in SnapshotDesired())
            BrowserSessionMonitor.Instance.Forget(folder);
        LogService.Append("应用进入后台，暂停关页检测（本地服务器保持运行）。");
    }

    /// <summary>应用回到前台：为仍在运行的版本重新登记会话。</summary>
    public static void OnAppForegrounded()
    {
        IsBackground = false;
        foreach (var folder in SnapshotDesired())
        {
            if (ServerManager.IsRunning(folder)) TrackCore(folder);
        }
        LogService.Append("应用回到前台，恢复关页检测。");
    }

    private static string[] SnapshotDesired()
    {
        lock (Sync) return Desired.ToArray();
    }

    private static void TrackCore(string folder)
    {
        try
        {
            BrowserSessionMonitor.Instance.Track(folder, BrowserCapabilities.ReliableCloseDetection,
                () => ServerManager.GetPageState(folder));
        }
        catch (Exception ex)
        {
            LogService.Error($"登记关页检测失败（{folder}）", ex);
        }
    }

    private static void OnSessionLost(string folder) =>
        MainThread.BeginInvokeOnMainThread(() => _ = HandleSessionLostAsync(folder));

    private static async Task HandleSessionLostAsync(string folder)
    {
        try
        {
            // 后台期间心跳会被系统暂停，不能据此停服；回前台后会重新登记。
            if (IsBackground)
            {
                LogService.Append($"应用在后台，忽略关页事件：{folder}");
                return;
            }

            if (!ServerManager.IsRunning(folder))
            {
                LogService.Append($"关页事件对应服务器已停止，忽略：{folder}");
                return;
            }

            // 同一版本短时间可能收到重复事件（刷新 / 页面重建），做时间窗防抖。
            lock (Sync)
            {
                if (LastHandled.TryGetValue(folder, out var last) &&
                    DateTime.UtcNow - last < TimeSpan.FromSeconds(10))
                    return;
                LastHandled[folder] = DateTime.UtcNow;
            }

            var name = await ResolveNameAsync(folder);
            var behavior = (AppConfig.Load().General.ClosePageBehavior ?? "close").Trim().ToLowerInvariant();
            if (behavior == "ask")
            {
                var stop = await PlatformServices.Dialogs.ConfirmAsync(
                    $"「{name}」的游戏页面已关闭。\n\n是否停止运行该版本？\n选择「取消」会保留本地服务器。",
                    "页面已关闭", "停止服务器");
                if (!stop)
                {
                    LogService.Append($"页面已关闭，按设置保留本地服务器：{name}（{folder}）");
                    return;
                }
            }

            ServerManager.Stop(folder);
            LauncherStatusService.Sync();
            RefreshVersionState(folder);
            LogService.Append($"页面已关闭，已停止本地服务器：{name}（{folder}）");
        }
        catch (Exception ex)
        {
            LogService.Error($"处理关页事件失败（{folder}）", ex);
        }
    }

    /// <summary>用版本显示名做提示文案；扫描失败时退回目录名。</summary>
    private static async Task<string> ResolveNameAsync(string folder)
    {
        try
        {
            var versions = await Task.Run(VersionService.Scan);
            var match = versions.FirstOrDefault(v =>
                string.Equals(v.Folder, folder, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.IsNullOrWhiteSpace(match.DisplayName)) return match.DisplayName;
        }
        catch (Exception ex)
        {
            LogService.Error($"读取版本名称失败（{folder}）", ex);
        }
        return Path.GetFileName(folder.TrimEnd('\\', '/'));
    }

    /// <summary>停服后刷新正打开的版本详情页，避免界面仍显示「运行中」。</summary>
    private static void RefreshVersionState(string folder)
    {
        try
        {
            var windows = Application.Current?.Windows;
            if (windows == null) return;
            foreach (var window in windows)
            {
                if (window.Page is not NavigationPage nav) continue;
                foreach (var page in nav.Navigation.NavigationStack)
                {
                    if (page is VersionDetailPage detail && detail.MatchesFolder(folder))
                        detail.RefreshStatus();
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Error("刷新版本运行状态失败", ex);
        }
    }
}
