using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Core.Browser;

/// <summary>
/// 关页检测：每秒检查已登记的会话，没有任何页面在发心跳（或页面主动上报关闭）时判定页面已关闭。
/// 游戏页与存档助手页都会参与登记，多个页面同时开时只要还有一个活着就不算关闭。
/// </summary>
public sealed class BrowserSessionMonitor
{
    private static readonly Lazy<BrowserSessionMonitor> LazyInstance = new(() => new BrowserSessionMonitor());

    /// <summary>全局单例：由界面在启动时订阅事件，服务层负责登记会话。</summary>
    public static BrowserSessionMonitor Instance => LazyInstance.Value;

    /// <summary>页面还在时允许的心跳间隔上限（加载卡住主线程时心跳会变慢）。</summary>
    private static TimeSpan AliveTimeout => TimeSpan.FromSeconds(Math.Clamp(Timing().TimeoutSeconds, 5, 600));

    /// <summary>页面主动上报关闭后的防抖时间，刷新页面时新心跳会取消关闭。</summary>
    private static TimeSpan CloseDebounce => TimeSpan.FromSeconds(Math.Clamp(Timing().DebounceSeconds, 0, 120));

    /// <summary>页面首次发心跳前的宽限期：游戏 HTML 很大，加载 + 初始化可能远超几秒。</summary>
    private static TimeSpan FirstHeartbeatGrace => TimeSpan.FromSeconds(Math.Clamp(Timing().GraceSeconds, 5, 900));

    private static (int TimeoutSeconds, int DebounceSeconds, int GraceSeconds) _timingCache;
    private static DateTime _timingLoadedAt;

    /// <summary>从设置读取时间参数，3 秒内复用缓存，避免每秒读一次配置文件。</summary>
    private static (int TimeoutSeconds, int DebounceSeconds, int GraceSeconds) Timing()
    {
        if (_timingLoadedAt != default && (DateTime.UtcNow - _timingLoadedAt).TotalSeconds < 3)
            return _timingCache;

        var g = AppConfig.Load().General;
        _timingCache = (g.HeartbeatTimeoutSeconds, g.CloseDebounceSeconds, g.FirstHeartbeatGraceSeconds);
        _timingLoadedAt = DateTime.UtcNow;
        return _timingCache;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, WatchedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _timer;
    private bool _running;

    /// <summary>会话丢失（页面关闭）时触发，参数为版本目录名；在线程池线程上触发。</summary>
    public event Action<string>? SessionLost;

    private BrowserSessionMonitor()
    {
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>登记会话；state 返回 null 表示服务器已停止。</summary>
    public void Track(string folder, BrowserCapabilities capabilities, Func<ServerPageState?> state)
    {
        if (!capabilities.HasFlag(BrowserCapabilities.ReliableCloseDetection)) return;

        lock (_sync)
        {
            _sessions[folder] = new WatchedSession(DateTime.UtcNow, state);
        }
        EnsureRunning();
    }

    /// <summary>停止监听某个版本（手动停服或页面关闭后调用）。</summary>
    public void Forget(string folder)
    {
        lock (_sync) _sessions.Remove(folder);
    }

    private void EnsureRunning()
    {
        lock (_sync)
        {
            if (_running) return;
            _running = true;
        }
        _timer.Change(1000, 1000);
    }

    private void Check(object? timerState)
    {
        List<KeyValuePair<string, WatchedSession>> snapshot;
        lock (_sync)
        {
            if (_sessions.Count == 0) return;
            snapshot = _sessions.ToList();
        }

        var now = DateTime.UtcNow;
        foreach (var pair in snapshot)
        {
            var folder = pair.Key;
            var session = pair.Value;

            ServerPageState? state;
            try { state = session.State(); }
            catch { state = null; }

            // 服务器已经不在运行（手动停服、删除版本等），不需要再判断
            if (state == null)
            {
                Forget(folder);
                continue;
            }

            if (state.HasPages)
            {
                // 还有页面开着：主线程被加载卡住时心跳会变慢，给 20 秒容错
                var last = state.LastHeartbeatUtc ?? session.OpenedAtUtc;
                if (now - last <= AliveTimeout) continue;
            }
            else if (state.PageClosedUtc is { } closedAt)
            {
                // 页面主动上报关闭：给 6 秒防抖，刷新页面时新心跳会把它取消
                if (now - closedAt <= CloseDebounce) continue;
            }
            else
            {
                // 从没开过页面：按登记时间给足加载宽限
                if (now - session.OpenedAtUtc <= FirstHeartbeatGrace) continue;
            }

            var removed = false;
            lock (_sync) removed = _sessions.Remove(folder);
            if (!removed) continue;

            Log($"页面已全部关闭（{folder}）：hasPages={state.HasPages}, " +
                $"lastHeartbeat={state.LastHeartbeatUtc:HH:mm:ss}, closedAt={state.PageClosedUtc:HH:mm:ss}");

            try
            {
                SessionLost?.Invoke(folder);
            }
            catch (Exception ex)
            {
                Log($"会话丢失事件处理失败（{folder}）：{ex.Message}");
            }
        }
    }

    private static void Log(string message)
    {
        try { DoLLauncher.Platform.PlatformServices.Log.Write("[浏览器] " + message); } catch { }
    }

    private sealed record WatchedSession(DateTime OpenedAtUtc, Func<ServerPageState?> State);
}
