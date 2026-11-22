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

    /// <summary>
    /// 把该版本上一次打开的游戏页面窗口切到前台；游戏页已经关闭（只剩存档助手页或其它页面）
    /// 或找不到窗口时返回 false，调用方据此重新打开游戏页面。
    /// </summary>
    public static bool TryFocus(string folder)
    {
        try
        {
            // 必须确认是这个版本端口上的游戏页还活着，不能聚焦存档助手或别的窗口。
            if (!ServerManager.HasGamePage(folder)) return false;
            return PlatformServices.Browser.TryFocus(folder);
        }
        catch (Exception ex)
        {
            LogError($"聚焦页面失败（{folder}）", ex);
            return false;
        }
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
            LogError($"关闭浏览器失败（{folder}）", ex);
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
            LogError("浏览器打开异常", ex);
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

    /// <summary>
    /// 独立 Profile 下需要清理的扩展与同步残留（相对 Default 目录）。
    /// 启动参数已经禁用扩展加载与同步，这里只清残留数据，
    /// 绝不触碰 IndexedDB / Local Storage 等游戏存档与模组缓存。
    /// </summary>
    private static readonly string[] ProfileHardeningTargets =
    {
        "Extensions",
        "Extension Rules",
        "Extension Scripts",
        "Extension State",
        "Local Extension Settings",
        "Managed Extension Settings",
        "Sync Extension Settings",
        "Sync Data",
        "ExtensionActivityEdge",
        "ExtensionActivityEdge-journal",
    };

    /// <summary>
    /// 启动器启动时清理一次独立 Profile 的扩展与同步残留。
    /// 已清理过（存在标记）或 Profile 不存在时直接跳过。
    /// </summary>
    public static void HardenProfileOnStartup()
    {
        try
        {
            var dir = ResolveProfileDir(AppConfig.Load().General);
            if (Directory.Exists(dir)) HardenProfile(dir);
        }
        catch (Exception ex)
        {
            LogError("启动时清理浏览器 Profile 失败", ex);
        }
    }

    /// <summary>清理指定 Profile 的扩展与同步残留；成功后写标记，之后不再重复清理。</summary>
    public static void HardenProfile(string profileDir)
    {
        try
        {
            var marker = Path.Combine(profileDir, ".dol-hardened");
            if (File.Exists(marker)) return;

            var defaultDir = Path.Combine(profileDir, "Default");
            foreach (var name in ProfileHardeningTargets)
            {
                var path = Path.Combine(defaultDir, name);
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex)
                {
                    // 目录可能被正在运行的浏览器占用，保留标记不写，下次启动继续尝试。
                    LogError($"清理 Profile 残留失败（{path}）", ex);
                    return;
                }
            }

            File.WriteAllText(marker, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            LogError("清理浏览器 Profile 失败", ex);
        }
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

    private static void LogError(string context, Exception ex)
    {
        try { PlatformServices.Log.WriteError("[浏览器] " + context, ex); } catch { }
    }
}
