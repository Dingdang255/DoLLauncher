using System.IO;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>正在运行的本地服务器集合。</summary>
public static class ServerManager
{
    private static readonly Dictionary<string, StaticServer> Running = new();
    private static readonly HashSet<string> HelperSessions = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRunning(string folder) =>
        Running.TryGetValue(folder, out var server) && server.IsListening;

    /// <summary>本次服务器只是为了打开存档助手，游戏本体并没有启动。</summary>
    public static bool IsHelperOnly(string folder) =>
        Running.ContainsKey(folder) && HelperSessions.Contains(folder);

    public static IReadOnlyCollection<string> RunningFolders => Running.Keys;

    public static StaticServer EnsureStarted(VersionInfo version) => EnsureStarted(version, false);

    public static StaticServer EnsureStarted(VersionInfo version, bool saveHelperOnly)
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
                var server = new StaticServer(version.FullPath, candidate, version.HtmlFileName,
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
            Running.Remove(folder);
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

    public static void OpenInBrowser(string url)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true,
        });
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }
}
