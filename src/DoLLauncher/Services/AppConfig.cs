using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>launcher-versions.json 的读写（与 launcher.ps1 共用同一个文件）。</summary>
public static class AppConfig
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static LauncherConfig Load()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return new LauncherConfig();
            var text = File.ReadAllText(Paths.ConfigFile, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new LauncherConfig();
            var cfg = JsonSerializer.Deserialize<LauncherConfig>(text);
            return cfg ?? new LauncherConfig();
        }
        catch
        {
            return new LauncherConfig();
        }
    }

    public static void Save(LauncherConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, WriteOptions);
        File.WriteAllText(Paths.ConfigFile, json, new UTF8Encoding(false));
    }

    public static VersionEntry GetOrAdd(LauncherConfig cfg, string folder, string mode = "http")
    {
        var hit = cfg.Versions.FirstOrDefault(v => v.Folder == folder);
        if (hit != null) return hit;
        hit = new VersionEntry { Folder = folder, Mode = mode };
        cfg.Versions.Add(hit);
        return hit;
    }

    /// <summary>在 8701 起找第一个既没被配置文件占用、也没被系统占用的端口。</summary>
    public static int AllocatePort(IEnumerable<VersionEntry> entries, string? excludeFolder = null)
    {
        var used = entries
            .Where(e => excludeFolder == null || e.Folder != excludeFolder)
            .Select(e => e.Port)
            .Where(p => p > 0)
            .ToHashSet();

        for (var port = 8701; port < 8900; port++)
        {
            if (used.Contains(port)) continue;
            if (IsPortFree(port)) return port;
        }
        throw new InvalidOperationException("8701-8900 之间没有可用端口。");
    }

    public static bool IsPortFree(int port)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }
}
