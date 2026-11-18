using System.Diagnostics;
using System.IO;
using System.Text;
using DoLLauncher.Platform.Abstractions;
using DoLLauncher.Services;

namespace DoLLauncher.Platform.Desktop;

/// <summary>
/// 桌面浏览器承载：Windows / Linux / macOS 优先用 Edge / Chrome / Chromium / Brave 的
/// --app 模式打开独立窗口；找不到时退回系统默认浏览器或用户自选的可执行文件。
/// </summary>
public sealed class DesktopBrowserHost : IBrowserHost
{
    private static readonly object Sync = new();

    private static readonly BrowserCapabilities ProfileCapabilities =
        BrowserCapabilities.SilentCacheClear |
        BrowserCapabilities.ReliableCloseDetection |
        BrowserCapabilities.DiskDataStore |
        BrowserCapabilities.ProcessTracking;

    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public BrowserCapabilities Capabilities => ProfileCapabilities;

    public event Action<string>? SessionClosed;

    public Task<BrowserSession?> OpenAsync(BrowserLaunchRequest request, CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(OpenCore(request));
        }
        catch (Exception ex)
        {
            LogError($"打开页面失败（{request.VersionFolder}）", ex);
            return Task.FromResult<BrowserSession?>(null);
        }
    }

    private BrowserSession? OpenCore(BrowserLaunchRequest request) => request.Mode switch
    {
        BrowserLaunchMode.Default => LaunchDefault(request),
        BrowserLaunchMode.Custom => LaunchCustom(request),
        BrowserLaunchMode.Profile => LaunchProfile(request),
        _ => null,
    };

    private BrowserSession? LaunchProfile(BrowserLaunchRequest request)
    {
        string? profileDir;
        try
        {
            profileDir = ResolveProfileDir(request);
        }
        catch (Exception ex)
        {
            LogError("创建 Profile 目录失败", ex);
            return null;
        }

        var args = new StringBuilder();
        args.Append("--user-data-dir=").Append(Quote(profileDir));
        args.Append(" --app=").Append(Quote(request.Url));
        args.Append(" --no-first-run");
        args.Append(" --no-default-browser-check");
        args.Append(" --disable-features=Translate");

        string? executable;
        string arguments;
        if (OperatingSystem.IsMacOS())
        {
            var app = FindMacApp();
            if (app == null)
            {
                Log("未找到 Chrome / Chromium / Brave / Edge，无法使用独立 Profile 承载。");
                return null;
            }
            executable = "/usr/bin/open";
            arguments = "-na " + Quote(app) + " --args " + args;
        }
        else
        {
            executable = FindProfileBrowser();
            if (executable == null)
            {
                Log("未找到 Edge / Chrome / Chromium / Brave，无法使用独立 Profile 承载。");
                return null;
            }
            arguments = args.ToString();
        }

        return StartTracked(request.VersionFolder, executable, arguments, profileDir, ProfileCapabilities);
    }

    private BrowserSession? LaunchDefault(BrowserLaunchRequest request)
    {
        Process.Start(new ProcessStartInfo(request.Url) { UseShellExecute = true });
        return new BrowserSession(0, null, BrowserCapabilities.None);
    }

    private BrowserSession? LaunchCustom(BrowserLaunchRequest request)
    {
        var executable = request.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Log($"自定义浏览器路径无效：{executable}");
            return null;
        }

        string? profileDir = null;
        if (!string.IsNullOrWhiteSpace(request.ProfileDir))
        {
            try
            {
                Directory.CreateDirectory(request.ProfileDir);
                profileDir = request.ProfileDir;
            }
            catch (Exception ex)
            {
                LogError("创建 Profile 目录失败", ex);
            }
        }

        var args = request.Arguments?.Trim() ?? string.Empty;
        if (profileDir != null) args = AppendArg(args, "--user-data-dir=" + Quote(profileDir));
        args = args.Contains("{url}", StringComparison.Ordinal)
            ? args.Replace("{url}", request.Url, StringComparison.Ordinal)
            : AppendArg(args, Quote(request.Url));

        var capabilities = BrowserCapabilities.ProcessTracking;
        if (profileDir != null)
        {
            capabilities |= BrowserCapabilities.SilentCacheClear |
                            BrowserCapabilities.DiskDataStore |
                            BrowserCapabilities.ReliableCloseDetection;
        }

        return StartTracked(request.VersionFolder, executable, args, profileDir, capabilities);
    }

    /// <summary>启动进程并跟踪；同一版本已有存活进程时只把新窗口交给它，避免误判为关闭。</summary>
    private BrowserSession? StartTracked(string versionFolder, string executable, string arguments,
        string? profileDir, BrowserCapabilities capabilities)
    {
        TrackedSession? existing = null;
        lock (Sync) _sessions.TryGetValue(versionFolder, out existing);

        if (existing != null && IsAlive(existing.Process))
        {
            StartRelay(executable, arguments);
            return new BrowserSession(existing.Process.Id, existing.ProfileDir, existing.Capabilities);
        }

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(executable)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogError($"启动浏览器失败（{executable}）", ex);
            return null;
        }

        if (process == null)
        {
            Log($"启动浏览器失败（{executable}）：没有返回进程。");
            return null;
        }

        var tracked = new TrackedSession(process, profileDir, capabilities);
        lock (Sync) _sessions[versionFolder] = tracked;

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnProcessExited(versionFolder, process);
        }
        catch (Exception ex)
        {
            LogError($"跟踪浏览器进程失败（{versionFolder}）", ex);
        }

        return new BrowserSession(process.Id, profileDir, capabilities);
    }

    public void Close(string versionFolder)
    {
        TrackedSession? tracked;
        lock (Sync)
        {
            _sessions.TryGetValue(versionFolder, out tracked);
            _sessions.Remove(versionFolder);
        }
        if (tracked == null) return;

        var process = tracked.Process;
        _ = Task.Run(() =>
        {
            try
            {
                if (process.HasExited) return;
                process.CloseMainWindow();
                if (process.WaitForExit(3000)) return;
                Log($"关闭浏览器超时，强制结束进程树（{versionFolder}）。");
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                LogError($"关闭浏览器失败（{versionFolder}）", ex);
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        });
    }

    private void OnProcessExited(string versionFolder, Process process)
    {
        var removed = false;
        lock (Sync)
        {
            if (_sessions.TryGetValue(versionFolder, out var tracked) &&
                ReferenceEquals(tracked.Process, process))
            {
                _sessions.Remove(versionFolder);
                removed = true;
            }
        }

        if (!removed) return;

        try { process.Dispose(); } catch { }
        try { SessionClosed?.Invoke(versionFolder); }
        catch (Exception ex) { LogError($"SessionClosed 处理失败（{versionFolder}）", ex); }
    }

    private static string ResolveProfileDir(BrowserLaunchRequest request)
    {
        var dir = string.IsNullOrWhiteSpace(request.ProfileDir)
            ? Path.Combine(PlatformServices.Paths.ProfilesDir, "dol")
            : request.ProfileDir!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Windows / Linux 下按常见安装位置探测浏览器，找不到返回 null。</summary>
    private static string? FindProfileBrowser()
    {
        if (OperatingSystem.IsWindows()) return FindWindowsBrowser();
        if (OperatingSystem.IsLinux()) return FindLinuxBrowser();
        return null;
    }

    private static string? FindWindowsBrowser()
    {
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        var localAppData = Environment.GetEnvironmentVariable("LocalAppData");

        var candidates = new[]
        {
            Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"),
            Combine(programFiles, @"Microsoft\Edge\Application\msedge.exe"),
            Combine(programFiles, @"Google\Chrome\Application\chrome.exe"),
            Combine(programFilesX86, @"Google\Chrome\Application\chrome.exe"),
            Combine(localAppData, @"Google\Chrome\Application\chrome.exe"),
            Combine(programFiles, @"Chromium\Application\chrome.exe"),
            Combine(localAppData, @"Chromium\Application\chrome.exe"),
            Combine(programFiles, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            Combine(programFilesX86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (candidate != null && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindLinuxBrowser() =>
        FindOnPath("google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "brave-browser", "brave");

    private static string? FindOnPath(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(new[] { "/usr/bin", "/usr/local/bin", "/snap/bin", "/opt/google/chrome" })
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static string? FindMacApp()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            "/Applications/Google Chrome.app",
            "/Applications/Chromium.app",
            "/Applications/Brave Browser.app",
            "/Applications/Microsoft Edge.app",
            Path.Combine(home, "Applications/Google Chrome.app"),
            Path.Combine(home, "Applications/Chromium.app"),
            Path.Combine(home, "Applications/Brave Browser.app"),
            Path.Combine(home, "Applications/Microsoft Edge.app"),
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? Combine(string? root, string relative)
        => string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, relative);

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }

    private static void StartRelay(string fileName, string arguments)
    {
        try
        {
            using var relay = Process.Start(new ProcessStartInfo(fileName)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogError("转发打开请求失败", ex);
        }
    }

    private static string AppendArg(string arguments, string extra) =>
        arguments.Length == 0 ? extra : arguments + " " + extra;

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void Log(string message)
    {
        try { LogService.Append("[浏览器] " + message); } catch { }
    }

    private static void LogError(string context, Exception ex)
    {
        try { LogService.Error("[浏览器] " + context, ex); } catch { }
    }

    private sealed record TrackedSession(Process Process, string? ProfileDir, BrowserCapabilities Capabilities);
}
