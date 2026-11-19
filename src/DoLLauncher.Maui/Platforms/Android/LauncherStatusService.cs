using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using DoLLauncher.Services;

// 运行期已按 Build.VERSION.SdkInt 分支调用新 API，构建期分析器识别不了这层判断
#pragma warning disable CA1416

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 运行状态前台服务：有版本运行时常驻一条通知（「N 个版本运行中」），
/// 通知上的「停止全部」由 <see cref="StopAllReceiver"/> 处理；没有版本运行时自动停止服务并撤销通知。
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class LauncherStatusService : Service
{
    private const string ChannelId = "dol_running_status";
    private const int NotificationId = 47010;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private static bool _serviceRequested;

    private Timer? _timer;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            var running = CountRunning();
            if (running == 0)
            {
                // 没有版本运行时不显示空通知；Sync 也会避免启动本服务。
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            var notification = BuildNotification(running);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, notification);

            _timer?.Dispose();
            _timer = new Timer(_ => Refresh(), null, RefreshInterval, RefreshInterval);
            _serviceRequested = true;
            LogService.Append("运行状态通知服务已启动。");
            return StartCommandResult.Sticky;
        }
        catch (Exception ex)
        {
            // Android 12+ 后台启动前台服务受限、Android 14 类型不匹配等都在这里兜底，界面不影响。
            LogService.Error("启动运行状态通知服务失败", ex);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
    }

    public override void OnDestroy()
    {
        _timer?.Dispose();
        _timer = null;
        _serviceRequested = false;
        try
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Cancel(NotificationId);
        }
        catch (Exception ex)
        {
            LogService.Error("撤销运行状态通知失败", ex);
        }
        LogService.Append("运行状态通知服务已停止。");
        base.OnDestroy();
    }

    /// <summary>
    /// 按当前运行数量同步服务状态：有运行就确保服务启动，没有就停止服务。
    /// 可在任意线程调用；Android 12+ 的后台启动限制由异常兜底。
    /// </summary>
    public static void Sync()
    {
        try
        {
            var running = CountRunning();
            if (running == 0)
            {
                if (!_serviceRequested) return;
                _serviceRequested = false;
                Microsoft.Maui.ApplicationModel.Platform.AppContext.StopService(
                    new Intent(Microsoft.Maui.ApplicationModel.Platform.AppContext, typeof(LauncherStatusService)));
                return;
            }

            if (_serviceRequested) return;
            var context = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            var intent = new Intent(context, typeof(LauncherStatusService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
            _serviceRequested = true;
        }
        catch (Exception ex)
        {
            LogService.Error("更新运行状态通知失败", ex);
        }
    }

    private void Refresh()
    {
        try
        {
            var running = CountRunning();
            if (running == 0)
            {
                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.Cancel(NotificationId);
                StopSelf();
                return;
            }

            var manager2 = (NotificationManager?)GetSystemService(NotificationService);
            manager2?.Notify(NotificationId, BuildNotification(running));
        }
        catch (Exception ex)
        {
            LogService.Error("刷新运行状态通知失败", ex);
        }
    }

    private Notification BuildNotification(int running)
    {
        EnsureChannel();

        var openIntent = new Intent(this, typeof(MainActivity));
        openIntent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var openPending = PendingIntent.GetActivity(this, 0, openIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var stopIntent = new Intent(this, typeof(StopAllReceiver));
        stopIntent.SetAction(StopAllReceiver.ActionStopAll);
        var stopPending = PendingIntent.GetBroadcast(this, 1, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new NotificationCompat.Builder(this, ChannelId)!;
        builder.SetContentTitle("DoL 启动器");
        builder.SetContentText($"{running} 个版本运行中");
        builder.SetSmallIcon(Android.Resource.Drawable.StatSysDownload);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetContentIntent(openPending);
        builder.AddAction(0, "停止全部", stopPending);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetCategory(NotificationCompat.CategoryService);
        return builder.Build()!;
    }

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager == null) return;

        var channel = new NotificationChannel(ChannelId, "运行状态", NotificationImportance.Low)
        {
            Description = "显示正在运行的版本数量，并提供「停止全部」操作。",
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private static int CountRunning() =>
        ServerManager.RunningFolders.Count(folder => ServerManager.IsRunning(folder));
}
