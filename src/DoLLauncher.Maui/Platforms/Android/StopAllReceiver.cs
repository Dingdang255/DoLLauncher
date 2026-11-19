using Android.App;
using Android.Content;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>通知栏「停止全部」：停止所有运行中的本地服务器，并让前台服务自行退出。</summary>
[BroadcastReceiver(Exported = false)]
public sealed class StopAllReceiver : BroadcastReceiver
{
    public const string ActionStopAll = "com.dol.launcher.action.STOP_ALL";

    public override void OnReceive(Context? context, Intent? intent)
    {
        try
        {
            var folders = ServerManager.RunningFolders;
            var count = folders.Count(folder => ServerManager.IsRunning(folder));
            ServerManager.StopAll();
            LogService.Append($"通知栏「停止全部」：已停止 {count} 个版本。");
        }
        catch (Exception ex)
        {
            LogService.Error("通知栏停止全部失败", ex);
        }
        finally
        {
            LauncherStatusService.Sync();
        }
    }
}
