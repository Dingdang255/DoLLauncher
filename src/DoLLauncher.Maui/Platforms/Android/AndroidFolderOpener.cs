using Android.Content;
using Android.OS;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 用文件管理器打开一个目录。优先直接唤起 MT 管理器的目录浏览（MT 的 OpenFileActivity 在收到
/// file:// + inode/directory 时会直接定位到该目录，不再弹「打开方式」列表）；
/// MT 不可用或失败时退回系统的「选择打开方式」选择器。都失败返回 false。
/// </summary>
internal static class AndroidFolderOpener
{
    private const string MtPackage = "bin.mt.plus";
    private const string MtActivity = "bin.mt.plus.OpenFileActivity";

    public static bool TryOpen(string path)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity == null) return false;

            var dir = new Java.IO.File(path);
            if (!dir.IsDirectory) return false;

            if (TryOpenWithMt(activity, dir)) return true;
            return TryOpenWithChooser(activity, dir);
        }
        catch (Exception ex)
        {
            LogService.Append($"打开文件夹失败：{path} · {ex.Message}");
            return false;
        }
    }

    /// <summary>直接让 MT 打开并定位到该目录。</summary>
    private static bool TryOpenWithMt(Android.App.Activity activity, Java.IO.File dir)
    {
        if (!IsInstalled(activity, MtPackage)) return false;

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(Android.Net.Uri.FromFile(dir), "inode/directory");
        intent.SetComponent(new ComponentName(MtPackage, MtActivity));
        intent.AddFlags(ActivityFlags.NewTask);
        return Launch(activity, intent);
    }

    /// <summary>退回系统「选择打开方式」：用 */* 让 MT、系统文件管理器等都能出现在列表里。</summary>
    private static bool TryOpenWithChooser(Android.App.Activity activity, Java.IO.File dir)
    {
        var target = new Intent(Intent.ActionView);
        target.SetDataAndType(Android.Net.Uri.FromFile(dir), "*/*");

        var matches = activity.PackageManager?.QueryIntentActivities(target,
            Android.Content.PM.PackageInfoFlags.MatchDefaultOnly);
        if (matches is not { Count: > 0 }) return false;

        var chooser = Intent.CreateChooser(target, "选择打开方式");
        if (chooser == null) return false;

        // 别让启动器自己出现在列表里（它注册了包导入的 VIEW）。
        try
        {
            var bundle = new Bundle();
            bundle.PutParcelableArray(Intent.ExtraExcludeComponents,
                new Android.OS.IParcelable[]
                {
                    new ComponentName(activity, activity.Class),
                });
            chooser.PutExtras(bundle);
        }
        catch
        {
            // 个别 ROM 不支持排除项时忽略
        }
        return Launch(activity, chooser);
    }

    private static bool Launch(Android.App.Activity activity, Intent intent)
    {
        // 把 file:// 交给其它应用会触发 StrictMode 的 FileUriExposedException；
        // 目标应用本来就要自己申请存储权限才能读取，这里只在本方法内放宽。
        var previous = StrictMode.GetVmPolicy();
        try
        {
            StrictMode.SetVmPolicy(new StrictMode.VmPolicy.Builder().PenaltyLog().Build());
            activity.StartActivity(intent);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Append("打开文件夹失败：" + ex.Message);
            return false;
        }
        finally
        {
            if (previous != null) StrictMode.SetVmPolicy(previous);
        }
    }

    private static bool IsInstalled(Android.App.Activity activity, string packageName)
    {
        try
        {
            activity.PackageManager?.GetPackageInfo(packageName, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
