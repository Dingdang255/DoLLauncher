using Android.App;
using Android.Content.Res;
using Android.Runtime;
using Android.Util;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

[Application]
public class MainApplication : MauiApplication
{
    /// <summary>整体界面缩放：系统密度的 0.6 倍。</summary>
    private const float UiScale = 0.6f;

    // 记录系统原始密度，避免多次应用缩放时重复相乘。
    private static int _baseDpi;

    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        ApplyUiScale();
        base.OnCreate();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// 修改应用级资源密度：MAUI 的 dp / 字号都按资源密度换算，
    /// 调整后整个界面会按比例缩小，同时同一屏幕能容纳更多内容。
    /// </summary>
    public static void ApplyUiScale() => ApplyUiScale(Android.App.Application.Context.Resources);

    /// <summary>把界面缩放应用到指定 Resources（Activity 的 Resources 与应用级是两份）。</summary>
    public static void ApplyUiScale(Android.Content.Res.Resources? resources)
    {
        try
        {
            if (resources?.Configuration is not { } current) return;

            if (_baseDpi <= 0) _baseDpi = current.DensityDpi;
            if (_baseDpi <= 0) return;

            var targetDpi = Math.Max(72, (int)Math.Round(_baseDpi * UiScale));

            var config = new Configuration(current) { DensityDpi = targetDpi };
            var metrics = resources.DisplayMetrics;
            if (metrics != null)
            {
                var density = targetDpi / 160f;
                var fontScale = config.FontScale > 0 ? config.FontScale : 1f;
#pragma warning disable CA1422
                // DensityDpi 是 @Density 枚举，Density / ScaledDensity 仍是浮点。
                metrics.DensityDpi = (DisplayMetricsDensity)targetDpi;
                metrics.Density = density;
                metrics.ScaledDensity = density * fontScale;
#pragma warning restore CA1422
            }
#pragma warning disable CA1422
            resources.UpdateConfiguration(config, metrics);
#pragma warning restore CA1422

            try
            {
                LogService.Append($"界面缩放已启用：密度 {_baseDpi} → {targetDpi}（{UiScale:0.0}x）");
            }
            catch
            {
                // 启动初期日志模块可能尚未初始化。
            }
        }
        catch (Exception ex)
        {
            try { LogService.Error("应用界面缩放失败，使用系统默认尺寸", ex); } catch { }
        }
    }
}
