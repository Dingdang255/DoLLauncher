using System.IO;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Core.Browser;

/// <summary>本次打开页面实际使用的承载方式、能力与是否成功。</summary>
public sealed record BrowserOpenResult(BrowserLaunchMode Mode, BrowserCapabilities Capabilities, bool Opened);

/// <summary>
/// 游戏浏览器承载的应用服务：读取设置、组装平台请求、在不可用时回退系统默认浏览器，
/// 并负责把游戏会话登记到关页检测。不依赖 WPF。
/// </summary>
public static class BrowserService
{
    /// <summary>打开游戏页面；成功后按能力登记关页检测。</summary>
    public static BrowserOpenResult OpenGame(VersionInfo version, string url)
    {
        var result = Open(version, url);
        Track(version, result);
        return result;
    }

    /// <summary>打开存档助手页；同样参与关页检测（页面全部关闭后停服）。</summary>
    public static BrowserOpenResult OpenHelper(VersionInfo version, string url)
    {
        var result = Open(version, url);
        Track(version, result);
        return result;
    }

    /// <summary>
    /// 登记关页监听：心跳与关闭上报由页面脚本完成，与用哪种浏览器无关，
    /// 因此只要页面打开成功、服务器在跑就登记（不再依赖承载方式的能力位）。
    /// </summary>
    private static void Track(VersionInfo version, BrowserOpenResult result)
    {
        if (!result.Opened || !ServerManager.IsRunning(version.Folder)) return;
        BrowserSessionMonitor.Instance.Track(version.Folder, BrowserCapabilities.ReliableCloseDetection,
            () => ServerManager.GetPageState(version.Folder));
        Log($"已登记关页检测（{version.Folder}）。");
    }

    /// <summary>关闭指定版本的页面（同时停止关页监听）。</summary>
    public static void Close(string folder)
    {
        BrowserSessionMonitor.Instance.Forget(folder);
        try
        {
            PlatformServices.Browser.Close(folder);
        }
        catch (Exception ex)
        {
            Log($"关闭浏览器失败（{folder}）：{ex.Message}");
        }
    }

    private static BrowserOpenResult Open(VersionInfo version, string url)
    {
        var general = AppConfig.Load().General;
        var mode = NormalizeMode(general.BrowserMode);
        var executable = (general.BrowserExecutable ?? string.Empty).Trim();

        if (mode == BrowserLaunchMode.WebView2)
        {
            Log("WebView2 承载暂未支持，本次使用系统默认浏览器。");
            mode = BrowserLaunchMode.Default;
        }

        if (mode == BrowserLaunchMode.Custom && (executable.Length == 0 || !File.Exists(executable)))
        {
            Log($"自定义浏览器路径无效（{executable}），本次使用系统默认浏览器。");
            mode = BrowserLaunchMode.Default;
        }

        var profileDir = mode switch
        {
            BrowserLaunchMode.Profile => ResolveProfileDir(general),
            BrowserLaunchMode.Custom when general.BrowserUseProfile => ResolveProfileDir(general),
            _ => null,
        };

        var session = TryOpen(new BrowserLaunchRequest(
            url,
            version.Folder,
            profileDir,
            mode == BrowserLaunchMode.Custom ? executable : null,
            mode == BrowserLaunchMode.Custom ? general.BrowserArguments : null,
            mode));

        if (session == null && mode != BrowserLaunchMode.Default)
        {
            Log($"{Describe(mode)} 无法打开页面，回退到系统默认浏览器。");
            mode = BrowserLaunchMode.Default;
            session = TryOpen(new BrowserLaunchRequest(url, version.Folder, Mode: BrowserLaunchMode.Default));
        }

        if (session == null)
        {
            Log($"打开页面失败（{version.DisplayName}）：{url}");
            return new BrowserOpenResult(mode, BrowserCapabilities.None, false);
        }

        return new BrowserOpenResult(mode, session.Capabilities, true);
    }

    private static BrowserSession? TryOpen(BrowserLaunchRequest request)
    {
        try
        {
            return PlatformServices.Browser.OpenAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"浏览器打开异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 所有版本共用一个启动器专用 Profile 目录：避免每个版本复制一份浏览器缓存导致体积膨胀。
    /// 各版本的数据仍靠不同端口（不同 origin）隔离，与日常浏览器的 Profile 也互不影响。
    /// 删除版本时按端口清理该 origin 的数据，因此不需要为每个版本单独建 Profile。
    /// </summary>
    public static string ResolveProfileDir(GeneralSettings general)
    {
        var baseDir = (general.BrowserProfileRoot ?? string.Empty).Trim();
        if (baseDir.Length == 0) baseDir = PlatformServices.Paths.ProfilesDir;
        return Path.Combine(baseDir, "dol");
    }

    private static BrowserLaunchMode NormalizeMode(string? mode) => (mode ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "default" => BrowserLaunchMode.Default,
        "custom" => BrowserLaunchMode.Custom,
        "webview2" => BrowserLaunchMode.WebView2,
        _ => BrowserLaunchMode.Profile,
    };

    private static string Describe(BrowserLaunchMode mode) => mode switch
    {
        BrowserLaunchMode.Profile => "独立 Profile 浏览器",
        BrowserLaunchMode.Custom => "自定义浏览器",
        BrowserLaunchMode.WebView2 => "内嵌 WebView2",
        _ => "系统默认浏览器",
    };

    private static void Log(string message)
    {
        try { PlatformServices.Log.Write("[浏览器] " + message); } catch { }
    }
}
