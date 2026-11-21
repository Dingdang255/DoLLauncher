using System.Collections.Concurrent;
using System.IO;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Platform.Abstractions;

namespace DoLLauncher.Services;

/// <summary>某个版本的页面状态：还有哪些页面开着、最后心跳与主动关闭时间。</summary>
public sealed record ServerPageState(DateTime? LastHeartbeatUtc, bool HasPages, DateTime? PageClosedUtc);

/// <summary>正在运行的本地服务器集合。</summary>
public static class ServerManager
{
    // 心跳由服务器线程写入、界面定时器读取，使用并发字典避免跨线程读写冲突
    private static readonly ConcurrentDictionary<string, ILocalServer> Running = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HelperSessions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本地服务器工厂。桌面端默认使用 <see cref="StaticServer"/>；
    /// Android 头工程可在启动时替换为应用内实现。
    /// </summary>
    public static Func<VersionInfo, int, ILocalServer>? ServerFactory { get; set; }

    public static bool IsRunning(string folder) =>
        Running.TryGetValue(folder, out var server) && server.IsListening;

    /// <summary>本次服务器只是为了打开存档助手，游戏本体并没有启动。</summary>
    public static bool IsHelperOnly(string folder) =>
        Running.ContainsKey(folder) && HelperSessions.Contains(folder);

    public static IReadOnlyCollection<string> RunningFolders => Running.Keys.ToList();

    /// <summary>
    /// 该版本服务器的页面状态：未运行返回 null；
    /// <see cref="ServerPageState.HasPages"/> 表示还有游戏页 / 存档助手页开着。
    /// </summary>
    public static ServerPageState? GetPageState(string folder)
    {
        if (!Running.TryGetValue(folder, out var server) || !server.IsListening) return null;
        return new ServerPageState(server.LastHeartbeatUtc, server.HasPages, server.PageClosedUtc);
    }

    /// <summary>该版本是否还有游戏页面在发心跳（不含存档助手页）。</summary>
    public static bool HasGamePage(string folder) =>
        Running.TryGetValue(folder, out var server) && server.IsListening && server.HasGamePage;

    public static ILocalServer EnsureStarted(VersionInfo version) => EnsureStarted(version, false);

    public static ILocalServer EnsureStarted(VersionInfo version, bool saveHelperOnly)
    {
        if (Running.TryGetValue(version.Folder, out var existing) && existing.IsListening)
        {
            if (!saveHelperOnly) HelperSessions.Remove(version.Folder);
            return existing;
        }

        var candidate = version.Port > 0 ? version.Port : FindFreePort(8701);
        Exception? lastError = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var server = ServerFactory?.Invoke(version, candidate)
                             ?? new StaticServer(version.FullPath, candidate, version.HtmlFileName,
                                 version.DisplayName, version.Folder);
                server.Start();
                Running[version.Folder] = server;
                if (saveHelperOnly) HelperSessions.Add(version.Folder);
                else HelperSessions.Remove(version.Folder);
                if (version.Port != candidate)
                {
                    version.Port = candidate;
                    VersionService.UpdatePort(version.Folder, candidate);
                }
                return server;
            }
            catch (Exception ex)
            {
                lastError = ex;
                candidate = FindFreePort(candidate + 1);
            }
        }

        throw new InvalidOperationException($"无法在本地启动服务器：{lastError?.Message}", lastError);
    }

    public static void Stop(string folder)
    {
        HelperSessions.Remove(folder);
        if (Running.TryGetValue(folder, out var server))
        {
            server.Stop();
            Running.TryRemove(folder, out _);
        }
    }

    public static void StopAll()
    {
        foreach (var folder in Running.Keys.ToList())
        {
            Stop(folder);
        }
    }

    public static int FindFreePort(int start)
    {
        for (var port = Math.Max(1024, start); port < 8900; port++)
        {
            if (Running.Values.Any(s => s.Port == port && s.IsListening)) continue;
            if (AppConfig.IsPortFree(port)) return port;
        }
        throw new InvalidOperationException("找不到可用端口。");
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        PlatformServices.Links.OpenPath(path);
    }
}
