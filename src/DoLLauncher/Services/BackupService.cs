using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 存档 / 模组配置 / 整版快照的本地备份。所有内容都放在启动器根目录的 backups 下，
/// 按版本目录名分文件夹，不污染游戏目录。
/// </summary>
public static class BackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Root => Path.Combine(Paths.Root, "backups");

    public static string VersionDir(string folder) => Path.Combine(Root, folder);

    public static string SavesDir(string folder) => Path.Combine(VersionDir(folder), "saves");

    public static List<BackupEntry> List(string folder, bool includeSaves = true)
    {
        var result = new List<BackupEntry>();
        var dir = VersionDir(folder);
        if (!Directory.Exists(dir)) return result;

        foreach (var file in Directory.GetFiles(dir))
        {
            if (!IsBackupFile(Path.GetFileName(file))) continue;
            result.Add(MakeEntry(file, "快照"));
        }

        if (includeSaves && Directory.Exists(SavesDir(folder)))
        {
            foreach (var file in Directory.GetFiles(SavesDir(folder), "*.json"))
            {
                result.Add(MakeEntry(file, "存档"));
            }
        }

        return result.OrderByDescending(e => e.Time).ToList();
    }

    private static bool IsBackupFile(string name) =>
        name.EndsWith(".dolpack", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("mods-config-", StringComparison.OrdinalIgnoreCase);

    private static BackupEntry MakeEntry(string fullPath, string kind)
    {
        var info = new FileInfo(fullPath);
        var name = Path.GetFileName(fullPath);
        if (name.StartsWith("mods-config-", StringComparison.OrdinalIgnoreCase)) kind = "模组配置";
        return new BackupEntry
        {
            FileName = name,
            FullPath = fullPath,
            Kind = kind,
            Size = info.Length,
            Time = info.LastWriteTime,
        };
    }

    // ---------- 模组配置 ----------

    public static string ExportModsConfig(VersionInfo version)
    {
        var folder = VersionDir(version.Folder);
        Directory.CreateDirectory(folder);
        var mods = ModService.Load(version.FullPath);
        var payload = new
        {
            format = "dol-mods-config/1",
            version = version.Folder,
            gameVersion = version.GameVersion,
            createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            order = mods.Select(m => m.FileName).ToList(),
            disabled = mods.Where(m => !m.Enabled).Select(m => m.FileName).ToList(),
            mods = mods.Select(m => new
            {
                file = m.FileName,
                name = m.Name,
                modVersion = m.Version,
                enabled = m.Enabled,
            }).ToList(),
        };

        var path = Path.Combine(folder, "mods-config-" + Stamp() + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts), new UTF8Encoding(false));
        return path;
    }

    /// <summary>还原模组顺序与启停。支持启动器导出的配置和 mods-state.json 两种格式。</summary>
    public static int RestoreModsConfig(string versionDir, string jsonPath, Action<string> log)
    {
        var text = File.ReadAllText(jsonPath, Encoding.UTF8);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        var order = ReadStrings(root, "order");
        var disabled = ReadStrings(root, "disabled").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mods = ModService.Load(versionDir);
        if (order.Count == 0 && root.TryGetProperty("mods", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var file = item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
                if (file.Length > 0) order.Add(file);
                var enabled = !(item.TryGetProperty("enabled", out var en) &&
                                en.ValueKind == JsonValueKind.False);
                if (!enabled && file.Length > 0) disabled.Add(file);
            }
        }

        if (order.Count == 0) throw new InvalidOperationException("这个文件里没有可用的模组顺序。");

        foreach (var mod in mods) mod.Enabled = !disabled.Contains(mod.FileName);
        var ordered = order
            .Select(file => mods.FirstOrDefault(m =>
                string.Equals(m.FileName, file, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m != null)
            .Select(m => m!)
            .ToList();
        foreach (var mod in mods)
        {
            if (!ordered.Contains(mod)) ordered.Add(mod);
        }

        ModService.Save(versionDir, ordered);
        log($"已还原 {ordered.Count} 个模组的顺序与启停。");
        return ordered.Count;
    }

    // ---------- 整版快照 ----------

    public static string CreateSnapshot(VersionInfo version, bool includeGame, Action<string> log, string prefix = "snapshot")
    {
        var folder = VersionDir(version.Folder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{prefix}-{Stamp()}.dolpack");
        PackService.Export(version, includeGame, path, "启动器备份", log);
        return path;
    }

    /// <summary>启动时按设置自动建快照，并清理超出保留份数的旧自动快照。</summary>
    public static string? AutoBackup(VersionInfo version, Action<string> log)
    {
        var cfg = AppConfig.Load();
        if (!cfg.Backup.Enabled) return null;

        var folder = VersionDir(version.Folder);
        var interval = Math.Max(1, cfg.Backup.IntervalHours);
        var newest = Directory.Exists(folder)
            ? Directory.GetFiles(folder, "auto-*.dolpack")
                .Select(f => new FileInfo(f).LastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max()
            : DateTime.MinValue;

        if (DateTime.Now - newest < TimeSpan.FromHours(interval)) return null;

        var path = CreateSnapshot(version, includeGame: false, log, prefix: "auto");
        PruneAuto(version.Folder, Math.Max(1, cfg.Backup.Keep));
        log("自动备份完成：" + Path.GetFileName(path));
        return path;
    }

    public static void PruneAuto(string folder, int keep)
    {
        var dir = VersionDir(folder);
        if (!Directory.Exists(dir)) return;
        var files = Directory.GetFiles(dir, "auto-*.dolpack")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Skip(keep)
            .ToList();
        foreach (var file in files)
        {
            try { File.Delete(file.FullName); } catch { }
        }
    }

    // ---------- 浏览器存档（由存档助手页 POST 上来） ----------

    public static string SaveBrowserBackup(string folder, string name, string text, bool auto)
    {
        var dir = SavesDir(folder);
        Directory.CreateDirectory(dir);

        var safe = Sanitize(name);
        if (!safe.StartsWith("saves-", StringComparison.OrdinalIgnoreCase) &&
            !safe.StartsWith("auto-saves-", StringComparison.OrdinalIgnoreCase))
        {
            safe = (auto ? "auto-saves-" : "saves-") + safe;
        }
        if (!safe.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) safe += ".json";

        var path = UniquePath(dir, safe);
        File.WriteAllText(path, text, new UTF8Encoding(false));

        if (auto)
        {
            var keep = Math.Max(1, AppConfig.Load().Backup.Keep);
            foreach (var file in Directory.GetFiles(dir, "auto-saves-*.json")
                         .Select(f => new FileInfo(f))
                         .OrderByDescending(f => f.LastWriteTime)
                         .Skip(keep))
            {
                try { File.Delete(file.FullName); } catch { }
            }
        }
        return Path.GetFileName(path);
    }

    public static string? ReadText(string folder, string name, bool saves)
    {
        var dir = saves ? SavesDir(folder) : VersionDir(folder);
        var path = Resolve(dir, name);
        if (path == null || !File.Exists(path)) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public static bool Delete(string folder, string name, bool saves)
    {
        var dir = saves ? SavesDir(folder) : VersionDir(folder);
        var path = Resolve(dir, name);
        if (path == null || !File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>把备份列表写成存档助手页可读的 JSON。</summary>
    public static string ListJson(string folder)
    {
        var saves = List(folder, includeSaves: true)
            .Where(e => e.IsSaves)
            .Select(e => new
            {
                name = e.FileName,
                time = e.TimeText,
                size = e.SizeText,
            })
            .ToList();
        return JsonSerializer.Serialize(new { saves }, JsonOpts);
    }

    private static string? Resolve(string dir, string name)
    {
        var safeName = Path.GetFileName(name);
        if (safeName.Length == 0) return null;
        var full = Path.GetFullPath(Path.Combine(dir, safeName));
        var baseFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    private static string Sanitize(string name)
    {
        var safe = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
        return safe.Trim();
    }

    private static string UniquePath(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        var baseName = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 999; i++)
        {
            path = Path.Combine(dir, $"{baseName}-{i}{ext}");
            if (!File.Exists(path)) return path;
        }
        return path;
    }

    private static List<string> ReadStrings(JsonElement root, string name)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
                list.Add(text);
        }
        return list;
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
}
