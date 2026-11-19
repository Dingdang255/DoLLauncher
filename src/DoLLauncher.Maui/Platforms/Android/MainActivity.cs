using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using DoLLauncher.Maui.Platform;

namespace DoLLauncher.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    // OnBackPressed 在新 API 上标记过时，但 MauiAppCompatActivity 仍会走这里，作为统一返回入口
#pragma warning disable CS0612
    public override void OnBackPressed()
    {
        // 页面注册了返回处理（例如游戏页先回退网页历史、再按策略关页）时由页面接管
        if (BackRouter.Handler?.Invoke() == true) return;
        base.OnBackPressed();
    }
#pragma warning restore CS0612

    // MAUI 的组件（文件选择等）依赖基类处理结果，先交给基类，再处理本工程自己的 SAF 保存请求
#pragma warning disable CS0618, CS0672
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        SaveFileHelper.HandleResult(requestCode, resultCode, data);
    }
#pragma warning restore CS0618, CS0672
}
