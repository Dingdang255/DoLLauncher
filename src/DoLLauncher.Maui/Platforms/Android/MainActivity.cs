using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
// 从文件管理器「分享」或直接打开 zip / 版本包时进入导入流程
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/zip")]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/octet-stream")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/zip")]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "application/octet-stream")]
public class MainActivity : MauiAppCompatActivity
{
    private const int NotificationPermissionCode = 47002;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 启动即请求通知权限（Android 13+）；被拒绝时只影响通知展示，前台服务仍可运行。
        RequestNotificationPermission();

        // 冷启动时由系统直接打开 / 分享进来的文件（重建 Activity 时不重复导入）
        if (savedInstanceState == null) ShareImportHelper.AcceptIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent == null) return;
        ShareImportHelper.AcceptIntent(intent);
    }

    // OnBackPressed 在新 API 上标记过时，但 MauiAppCompatActivity 仍会走这里，作为统一返回入口
#pragma warning disable CS0612
    public override void OnBackPressed()
    {
        // 页面注册了返回处理（例如游戏页先回退网页历史、再按策略关页）时由页面接管
        if (BackRouter.Handler?.Invoke() == true) return;
        base.OnBackPressed();
    }
#pragma warning restore CS0612

    /// <summary>外接键盘：先交给当前页面（Ctrl+A / Delete），输入框获得焦点时由路由放行。</summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e != null && AndroidKeyRouter.Dispatch(e)) return true;
        return base.DispatchKeyEvent(e);
    }

    // MAUI 的组件（文件选择等）依赖基类处理结果，先交给基类，再处理本工程自己的 SAF 保存请求
#pragma warning disable CS0618, CS0672
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        SaveFileHelper.HandleResult(requestCode, resultCode, data);
    }
#pragma warning restore CS0618, CS0672

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions,
        Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationPermissionCode) return;

        var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
        if (granted) LogService.Append("已获得通知权限，运行状态通知可以正常显示。");
        else LogService.Append("通知权限被拒绝：前台服务仍会运行，但状态通知可能不显示。");
    }

    private void RequestNotificationPermission()
    {
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;
            if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) == Permission.Granted)
                return;

            // 系统只会弹一次授权框；避免每次启动都重复请求。
            const string key = "notification_permission_requested";
            if (Preferences.Default.Get(key, false)) return;
            Preferences.Default.Set(key, true);

            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications },
                NotificationPermissionCode);
        }
        catch (Exception ex)
        {
            LogService.Error("请求通知权限失败", ex);
        }
    }
}
