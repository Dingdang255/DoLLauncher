using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
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

    // 主题 / 深色模式等配置变化后重新套用界面缩放，避免系统重置资源密度。
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        MainApplication.ApplyUiScale(Resources);
        ApplySystemBars();
    }

    /// <summary>状态栏 / 导航栏跟随应用深浅色，避免留在默认紫色或纯黑。</summary>
    internal static void ApplySystemBars()
    {
        try
        {
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not Activity activity) return;
            var window = activity.Window;
            if (window?.DecorView == null) return;

            var dark = (activity.Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;
            var background = Android.Graphics.Color.ParseColor(dark ? "#1C1C1E" : "#F5F5F5");
#pragma warning disable CA1422 // 新系统上会被边缘化处理，老系统仍需要显式着色
            window.SetStatusBarColor(background);
            window.SetNavigationBarColor(background);
#pragma warning restore CA1422

            var controller = new AndroidX.Core.View.WindowInsetsControllerCompat(window, window.DecorView);
            controller.AppearanceLightStatusBars = !dark;
            controller.AppearanceLightNavigationBars = !dark;
        }
        catch (Exception ex)
        {
            LogService.Error("设置系统栏配色失败", ex);
        }
    }

    /// <summary>
    /// 在 Activity 生成 Resources 之前就套用缩放：之后无论资源被重建多少次
    /// （主题重应用、前后台切换、系统文件选择器返回等）都会继承缩放后的密度。
    /// </summary>
    protected override void AttachBaseContext(Context? @base)
    {
        base.AttachBaseContext(MainApplication.CreateScaledContext(@base) ?? @base);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Activity 的 Resources 与应用级是两份，必须在 MAUI 创建窗口前单独套用缩放。
        MainApplication.ApplyUiScale(Resources);

        base.OnCreate(savedInstanceState);

        // MAUI 创建窗口时可能重置 Activity 资源密度，首次布局前再套用一次。
        MainApplication.ApplyUiScale(Resources);
        ApplySystemBars();

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
