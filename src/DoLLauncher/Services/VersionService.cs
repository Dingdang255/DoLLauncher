using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>扫描 versions 目录，维护「版本 ↔ 端口」映射，并提供游戏版本号识别。</summary>
public static class VersionService
{
    /// <summary>旧的 file:// 直开数据在启动器里的伪版本标识。</summary>
    public const string LegacyFileFolder = "__legacy_file__";

    private static readonly Dictionary<string, (DateTime WriteTime, string? Version)> VersionCache = new();

    public static List<VersionInfo> Scan()
    {
        var cfg = AppConfig.Load();
        var list = new List<VersionInfo>();

        var dirs = Directory.Exists(Paths.VersionsDir)
            ? Directory.GetDirectories(Paths.VersionsDir).ToList()
            : new List<string>();

        var changed = false;

        var candidates = new List<(string Dir, string Html, string Folder, string? Version)>();
        foreach (var dir in dirs)
        {
            var html = FindHtml(dir);
            if (html == null) continue;
            var folder = Path.GetFileName(dir);
            candidates.Add((dir, html, folder, DetectGameVersion(dir, folder)));
        }

        // 游戏版本号从大到小
        candidates.Sort((a, b) =>
        {
            var cmp = VersionUtil.Compare(b.Version ?? "0", a.Version ?? "0");
            return cmp != 0 ? cmp : string.Compare(b.Folder, a.Folder, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var candidate in candidates)
        {
            var entry = AppConfig.GetOrAdd(cfg, candidate.Folder);
            if (entry.Port <= 0)
            {
                entry.Port = AppConfig.AllocatePort(cfg.Versions);
                changed = true;
            }
            entry.Mode = "http";

            list.Add(new VersionInfo
            {
                Folder = candidate.Folder,
                FullPath = candidate.Dir,
                HtmlPath = candidate.Html,
                Mode = "http",
                Port = entry.Port,
                GameVersion = candidate.Version,
                DisplayName = Prettify(candidate.Folder, candidate.Version),
            });
        }

        // 旧的 file:// 数据条目：共享浏览器 origin，单独列出以便管理
        if (list.Count > 0)
        {
            var chosen = list.FirstOrDefault(v => v.Folder == cfg.General.LegacyFolder) ?? list[0];
            list.Add(new VersionInfo
            {
                Folder = LegacyFileFolder,
                FullPath = string.Empty,
                HtmlPath = chosen.HtmlPath,
                Mode = "file",
                Port = 0,
                GameVersion = chosen.GameVersion,
                DisplayName = "旧版 file:// 数据",
            });
        }

        // 清理配置里已经不存在的目录
        var alive = list.Where(v => v.Mode == "http").Select(v => v.Folder).ToHashSet();
        var before = cfg.Versions.Count;
        cfg.Versions.RemoveAll(v => !alive.Contains(v.Folder));
        if (cfg.Versions.Count != before) changed = true;

        if (changed) AppConfig.Save(cfg);
        return list;
    }

    public static void RemoveEntry(string folder)
    {
        var cfg = AppConfig.Load();
        cfg.Versions.RemoveAll(v => v.Folder == folder);
        AppConfig.Save(cfg);
    }

    public static void UpdatePort(string folder, int port)
    {
        var cfg = AppConfig.Load();
        var entry = AppConfig.GetOrAdd(cfg, folder);
        entry.Port = port;
        AppConfig.Save(cfg);
    }

    public static string? FindHtml(string versionDir)
    {
        var standard = Path.Combine(versionDir, Paths.HtmlName);
        if (File.Exists(standard)) return standard;
        var any = Directory.GetFiles(versionDir, "*.html").OrderBy(f => f).FirstOrDefault();
        return any;
    }

    /// <summary>优先从目录名取版本号；目录名不含版本时扫描 HTML 内的 version: "x.y.z"。</summary>
    public static string? DetectGameVersion(string versionDir, string? folderName = null)
    {
        var fromFolder = VersionUtil.FromFolderName(folderName ?? Path.GetFileName(versionDir));
        if (fromFolder != null) return fromFolder;

        var html = FindHtml(versionDir);
        if (html == null) return null;

        try
        {
            var info = new FileInfo(html);
            var cached = VersionCache.GetValueOrDefault(html);
            if (cached.WriteTime == info.LastWriteTimeUtc) return cached.Version;

            var version = ScanHtmlVersion(html);
            VersionCache[html] = (info.LastWriteTimeUtc, version);
            return version;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Regex VersionRegex =
        new(@"version\s*:\s*""([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)""", RegexOptions.Compiled);

    private static string? ScanHtmlVersion(string html)
    {
        using var stream = new FileStream(html, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var read = 0L;
        while (read < 40L * 1024 * 1024)
        {
            var line = reader.ReadLine();
            if (line == null) break;
            read += line.Length + 1;
            var m = VersionRegex.Match(line);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    public static string Prettify(string folder, string? gameVersion)
    {
        var m = Regex.Match(folder, @"^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)-v([0-9]+\.[0-9]+(?:\.[0-9]+)?)");
        if (m.Success) return $"DoL {m.Groups[1].Value} · ModLoader {m.Groups[2].Value}";
        if (gameVersion != null) return $"DoL {gameVersion}";
        return folder;
    }
}
