using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.Content;
using DoLLauncher.Services;
using Microsoft.Maui.Storage;
using JavaFile = Java.IO.File;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// Android 端的启动器更新：下载 Release 里的 APK，再通过 FileProvider + ACTION_VIEW
/// 交给系统安装程序。任何一步失败都抛中文异常，由页面回退到浏览器下载。
/// </summary>
public static class AndroidUpdateInstaller
{
    private const string ProviderSuffix = ".fileprovider";
    private const string ApkMime = "application/vnd.android.package-archive";

    private static readonly HttpClient Http = CreateClient();

    public static string UpdateCacheDir => Path.Combine(FileSystem.CacheDirectory, "updates");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DoLLauncher-Android");
        return client;
    }

    /// <summary>优先选名字里带 launcher 的 APK，其次任意 APK。</summary>
    public static LauncherAsset? FindApk(LauncherRelease release) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("launcher", StringComparison.OrdinalIgnoreCase))
        ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

    /// <summary>把 APK 下载到缓存目录（FileProvider 已声明 updates 子目录），返回本地路径。</summary>
    public static async Task<string> DownloadApkAsync(LauncherRelease release, Action<double> progress,
        CancellationToken cancellationToken = default)
    {
        var asset = FindApk(release)
                    ?? throw new InvalidOperationException("该发布版本没有提供 APK 安装包。");

        Directory.CreateDirectory(UpdateCacheDir);
        var dest = Path.Combine(UpdateCacheDir, SafeName(asset.Name));
        var temp = dest + ".part";

        try
        {
            if (File.Exists(temp)) File.Delete(temp);

            using var response = await Http.GetAsync(asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    read += count;
                    if (total > 0) progress(Math.Clamp((double)read / total, 0, 1));
                }
            }

            File.Move(temp, dest, overwrite: true);
            progress(1);
            return dest;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>用系统安装程序打开已下载的 APK。失败时抛中文异常。</summary>
    public static void Install(string apkPath)
    {
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
                       ?? throw new InvalidOperationException("无法获取当前活动页面。");
        if (!File.Exists(apkPath)) throw new InvalidOperationException("安装包不存在，请重新下载。");

#pragma warning disable CA1416
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = activity.PackageManager;
            if (manager != null && !manager.CanRequestPackageInstalls())
            {
                try
                {
                    var settings = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources);
                    settings.SetData(Android.Net.Uri.Parse("package:" + activity.PackageName));
                    settings.AddFlags(ActivityFlags.NewTask);
                    activity.StartActivity(settings);
                }
                catch (Exception ex)
                {
                    LogService.Error("打开未知来源安装设置失败", ex);
                }

                throw new InvalidOperationException(
                    "请先在系统设置中允许本应用「安装未知应用」，然后重新点击「立即更新」。");
            }
        }
#pragma warning restore CA1416

        var packageName = activity.PackageName
                          ?? throw new InvalidOperationException("无法获取应用包名。");
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
            activity, packageName + ProviderSuffix, new JavaFile(apkPath));

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, ApkMime);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(intent);
    }

    private static string SafeName(string name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? "update.apk" : name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "update.apk" : result;
    }
}
