using System.Diagnostics;
using System.IO;
using System.Text;
using DoLLauncher.Core.Browser;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Platform.Windows;

/// <summary>
/// Windows 下的浏览器承载：用 Edge / Chrome 的 --app 模式打开独立窗口，
/// 或退回系统默认浏览器 / 用户自选的可执行文件。
/// </summary>
public sealed class WindowsBrowserHost : IBrowserHost
{
    private static readonly object Sync = new();
    private static readonly BrowserCapabilities ProfileCapabilities =
        BrowserCapabilities.SilentCacheClear |
        BrowserCapabilities.ReliableCloseDetection |
        BrowserCapabilities.DiskDataStore |
        BrowserCapabilities.ProcessTracking;

    /// <summary>
    /// 独立 Profile 固定追加的硬化参数：不加载扩展、不参与账号同步，
    /// 避免 Edge 跟随 Windows 账户自动登录后把日常扩展同步进来并弹出窗口。
    /// </summary>
    private static readonly string[] ProfileHardeningArgs =
    {
        "--disable-extensions",
        "--disable-sync",
        "--disable-component-extensions-with-background-pages",
        "--no-service-autorun",
    };

    private readonly Dictionary<string, TrackedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被跟踪的浏览器进程自行退出时触发，参数为版本目录名。</summary>
    public event Action<string>? SessionClosed;

    public BrowserCapabilities Capabilities => ProfileCapabilities;

    /// <summary>指定承载方式是否可用；WebView2 暂未实现。</summary>
    public bool CanUse(BrowserLaunchMode mode) => mode != BrowserLaunchMode.WebView2;

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

    private BrowserSession? OpenCore(BrowserLaunchRequest request)
    {
        if (!CanUse(request.Mode))
        {
            Log("WebView2 承载尚未实现，已跳过。");
            return null;
        }

        switch (request.Mode)
        {
            case BrowserLaunchMode.Default:
                return LaunchDefault(request);

            case BrowserLaunchMode.Custom:
                return LaunchCustom(request);

            case BrowserLaunchMode.Profile:
                return LaunchProfile(request);
        }

        return null;
    }

    private BrowserSession? LaunchProfile(BrowserLaunchRequest request)
    {
        var executable = FindProfileBrowser();
        if (executable == null)
        {
            Log("未找到 Edge 或 Chrome，无法使用独立 Profile 承载。");
            return null;
        }

        string profileDir;
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
        foreach (var flag in ProfileHardeningArgs) args.Append(' ').Append(flag);

        BrowserService.HardenProfile(profileDir);
        Log($"独立 Profile 已禁用浏览器扩展与账号同步（{profileDir}）。");

        return StartTracked(request.VersionFolder, executable, args.ToString(), profileDir, ProfileCapabilities);
    }

    private BrowserSession? LaunchDefault(BrowserLaunchRequest request)
    {
        var psi = new ProcessStartInfo(request.Url) { UseShellExecute = true };
        Process.Start(psi);
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
                profileDir = null;
            }
        }

        var args = request.Arguments?.Trim() ?? string.Empty;
        if (profileDir != null)
        {
            args = AppendArg(args, "--user-data-dir=" + Quote(profileDir));
            args = AppendHardening(args);
            BrowserService.HardenProfile(profileDir);
        }
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
            // Chrome / Edge 同一 Profile 的第二条命令会转发给已有实例后立即退出，
            // 因此不替换跟踪对象，仍以首个进程作为会话。
            StartRelay(executable, arguments);
            return new BrowserSession(existing.Process.Id, existing.ProfileDir, existing.Capabilities);
        }

        Process? process;
        try
        {
            process = StartProcess(executable, arguments);
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

    /// <summary>把该版本已打开的浏览器窗口切到前台；没有存活窗口时返回 false。</summary>
    public bool TryFocus(string versionFolder)
    {
        TrackedSession? tracked;
        lock (Sync) _sessions.TryGetValue(versionFolder, out tracked);
        if (tracked == null) return false;

        try
        {
            if (tracked.Process.HasExited) return false;
            var handle = FindWindowHandle(tracked.Process);
            if (handle == IntPtr.Zero) return false;

            // 最小化的窗口先还原，再激活；系统的前台锁偶尔会拒绝第一次调用，重试一次。
            ShowWindow(handle, IsIconic(handle) ? SwRestore : SwShow);
            SetForegroundWindow(handle);
            if (GetForegroundWindow() == handle) return true;

            ShowWindow(handle, SwRestore);
            SetForegroundWindow(handle);
            return GetForegroundWindow() == handle;
        }
        catch (Exception ex)
        {
            LogError($"聚焦浏览器窗口失败（{versionFolder}）", ex);
            return false;
        }
    }

    public void Close(string versionFolder)
    {
        TrackedSession? tracked = null;
        lock (Sync)
        {
            if (_sessions.TryGetValue(versionFolder, out tracked)) _sessions.Remove(versionFolder);
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
        var reliable = false;
        lock (Sync)
        {
            if (_sessions.TryGetValue(versionFolder, out var tracked) &&
                ReferenceEquals(tracked.Process, process))
            {
                _sessions.Remove(versionFolder);
                removed = true;
                // 只有带独立 Profile 的浏览器才保证「进程退出 = 这个版本的页面全关了」；
                // 共享实例的浏览器进程退出可能只是把 URL 转交给了别的实例，不能据此停服。
                reliable = tracked.Capabilities.HasFlag(BrowserCapabilities.ReliableCloseDetection);
            }
        }

        if (!removed) return;

        try { process.Dispose(); } catch { }
        if (!reliable) return;
        try { SessionClosed?.Invoke(versionFolder); }
        catch (Exception ex) { LogError($"SessionClosed 处理失败（{versionFolder}）", ex); }
    }

    private string ResolveProfileDir(BrowserLaunchRequest request)
    {
        var dir = string.IsNullOrWhiteSpace(request.ProfileDir)
            ? Path.Combine(PlatformServices.Paths.ProfilesDir, "dol")
            : request.ProfileDir!;
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>按常见安装位置探测 Edge 与 Chrome，找不到返回 null。</summary>
    private static string? FindProfileBrowser()
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
        };

        foreach (var candidate in candidates)
        {
            if (candidate != null && File.Exists(candidate)) return candidate;
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

    /// <summary>
    /// 在浏览器进程里找游戏页窗口：跳过存档助手这类页面窗口，
    /// 找不到可见且有标题的窗口时返回 0（调用方会改为打开新页面）。
    /// </summary>
    private static IntPtr FindWindowHandle(Process process)
    {
        int pid;
        try { pid = process.Id; }
        catch { return IntPtr.Zero; }

        var found = IntPtr.Zero;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var windowPid);
                if (windowPid != pid || !IsWindowVisible(hWnd)) return true;
                var title = GetWindowTitle(hWnd);
                if (title.Length == 0) return true;
                // 存档助手页由同一个浏览器打开，标题不同，不能当作游戏页面聚焦。
                if (title.Contains("存档助手") || title.Contains("正在备份存档")) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogError("枚举浏览器窗口失败", ex);
        }
        return found;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static Process? StartProcess(string fileName, string arguments) =>
        Process.Start(new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

    private static void StartRelay(string fileName, string arguments)
    {
        try
        {
            using var relay = StartProcess(fileName, arguments);
        }
        catch (Exception ex)
        {
            LogError("转发打开请求失败", ex);
        }
    }

    private static string AppendArg(string arguments, string extra) =>
        arguments.Length == 0 ? extra : arguments + " " + extra;

    /// <summary>补齐硬化参数；用户自定义参数里已有的不重复追加。</summary>
    private static string AppendHardening(string arguments)
    {
        foreach (var flag in ProfileHardeningArgs)
        {
            if (arguments.Contains(flag, StringComparison.OrdinalIgnoreCase)) continue;
            arguments = AppendArg(arguments, flag);
        }
        return arguments;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void Log(string message)
    {
        try { PlatformServices.Log.Write("[浏览器] " + message); } catch { }
    }

    private static void LogError(string context, Exception ex)
    {
        try { PlatformServices.Log.WriteError("[浏览器] " + context, ex); } catch { }
    }

    private sealed record TrackedSession(Process Process, string? ProfileDir, BrowserCapabilities Capabilities);
}
