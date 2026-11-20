using Android.Content.PM;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 启动器锁定横屏；进入游戏页时按设置切到横屏或竖屏，并允许在该方向内翻转 180°；
/// 返回启动器后恢复横屏锁定。
/// </summary>
internal static class AppOrientation
{
    public static void EnterGame()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity == null) return;

        var landscape = string.Equals(AppConfig.Load().General.GameOrientation, "landscape",
            StringComparison.OrdinalIgnoreCase);

        // 用 SensorLandscape / SensorPortrait：既保持设置的方向，又允许该方向上的左右/上下翻转。
        // 不能解除到 FullSensor，否则设备（或模拟器）传感器朝上时会立刻转回竖屏，设置形同虚设。
        activity.RequestedOrientation = landscape
            ? ScreenOrientation.SensorLandscape
            : ScreenOrientation.SensorPortrait;
    }

    public static void ExitGame()
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity == null) return;

        try
        {
            activity.RequestedOrientation = ScreenOrientation.Landscape;
        }
        catch
        {
            // Activity 已销毁时忽略
        }
    }
}
