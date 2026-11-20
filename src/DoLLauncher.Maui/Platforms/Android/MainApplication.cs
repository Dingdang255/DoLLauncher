using Android.App;
using Android.Content;
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
    /// 在 Activity 绑定基础 Context 时就把密度换成缩放后的配置。
    /// 运行时改 Resources 只在当前 Resources 实例上生效，主题重应用、切前后台、
    /// 启动系统选择器后 Resources 一旦重建就会退回系统密度（表现为「切换几次界面后变大」）；
    /// 从这里派生出的 Resources 都会继承缩放后的密度。
    /// </summary>
    public static Context? CreateScaledContext(Context? baseContext)
    {
        try
        {
            if (baseContext?.Resources?.Configuration is not { } system) return baseContext;

            if (_baseDpi <= 0) _baseDpi = system.DensityDpi > 0 ? system.DensityDpi : 480;
            if (_baseDpi <= 0) return baseContext;

            var targetDpi = Math.Max(72, (int)Math.Round(_baseDpi * UiScale));
            if (system.DensityDpi == targetDpi) return baseContext;

            var config = new Configuration(system) { DensityDpi = targetDpi };
            return baseContext.CreateConfigurationContext(config);
        }
        catch (Exception ex)
        {
            try { LogService.Error("创建缩放后的 Context 失败，使用系统密度", ex); } catch { }
            return baseContext;
        }
    }

    /// <summary>
    /// 修改应用级资源密度：MAUI 的 dp / 字号都按资源密度换算，
    /// 调整后整个界面会按比例缩小，同时同一屏幕能容纳更多内容。
    /// </summary>
    public static void ApplyUiScale() => ApplyUiScale(Android.App.Application.Context.Resources);

    /// <summary>把界面缩放应用到指定 Resources（Activity 的 Resources 与应用级是两份）。</summary>
    public static void ApplyUiScale(Android.Content.Res.Resources? resources) => ApplyUiScale(resources, true);

    /// <summary>
    /// 美化包 / 主题重应用后 Resources 可能已被重建，这里静默补一次缩放，
    /// 避免界面退回系统密度（表现为控件突然变大）。
    /// </summary>
    public static void ReapplyUiScale()
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            ApplyUiScale(activity?.Resources, false);
            ResetMauiDensityCache(activity);
        }
        catch
        {
            // 页面切换瞬间可能拿不到 Activity，忽略即可。
        }
    }

    /// <summary>MAUI 把第一次 dp→px 换算时的 Context 密度缓存进静态字段，之后不再更新。</summary>
    private static readonly System.Reflection.FieldInfo? MauiDensityField =
        typeof(Microsoft.Maui.Platform.ContextExtensions).GetField(
            "s_displayDensity",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    /// <summary>
    /// 让 MAUI 丢掉缓存的系统密度，并立刻用缩放后的 Activity 资源重新初始化。
    /// 不做这一步时，即使资源密度已经改成 0.6x，所有布局尺寸仍按系统密度换算（界面看上去没有缩放）。
    /// </summary>
    public static void ResetMauiDensityCache(Context? context)
    {
        try
        {
            if (context == null || MauiDensityField == null) return;
            MauiDensityField.SetValue(null, float.MinValue);
            // 抢先用自己的资源初始化缓存，避免下一次换算落在未缩放的 Context 上。
            Microsoft.Maui.Platform.ContextExtensions.ToPixels(context, 1d);
        }
        catch (Exception ex)
        {
            try { LogService.Error("重置 MAUI 密度缓存失败", ex); } catch { }
        }
    }

    private static void ApplyUiScale(Android.Content.Res.Resources? resources, bool log)
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

            if (!log) return;
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
