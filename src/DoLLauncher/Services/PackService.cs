using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

public sealed class ImportResult
{
    public string? NewVersionFolder { get; set; }
    public bool HasGame { get; set; }
    public int ModCount { get; set; }
}

/// <summary>.dolpack 的导出与导入。格式是 zip：manifest.json + game/ + mods/。</summary>
public static class PackService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Export(VersionInfo version, bool includeGame, string outputPath, string note, Action<string> log)
    {
        ModService.Apply(version.FullPath);
        var mods = ModService.Load(version.FullPath);

        var manifest = new PackManifest
        {
            Format = "dolpack/1",
            Id = version.Folder,
            Name = version.DisplayName,
            GameVersion = version.GameVersion,
            HasGame = includeGame,
            Mods = mods.Select(m => new PackMod { File = m.FileName, Enabled = m.Enabled }).ToList(),
            CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Note = note,
        };

        var temp = outputPath + ".tmp";
        try
        {
            using (var fs = File.Create(temp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "manifest.json", JsonSerializer.Serialize(manifest, JsonOpts));

                if (includeGame)
                {
                    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "modList.json", "mods-state.json", "manifest.json",
                    };
                    foreach (var file in Directory.GetFiles(version.FullPath, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(version.FullPath, file).Replace('\\', '/');
                        if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (skip.Contains(rel)) continue;
                        AddFile(zip, "game/" + rel, file);
                    }
                    log("已写入游戏本体与版本文件。");
                }

                foreach (var mod in mods)
                {
                    AddFile(zip, "mods/" + mod.FileName, mod.FilePath);
                }
                log($"已写入 {mods.Count} 个模组。");
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(temp, outputPath);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    /// <summary>预览包内是否含游戏本体（不需要解压）。</summary>
    public static bool PeekHasGame(string packPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packPath);
            var manifestEntry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry != null)
            {
                var manifest = JsonSerializer.Deserialize<PackManifest>(ReadText(manifestEntry), JsonOpts);
                if (manifest != null) return manifest.HasGame;
            }
            return zip.Entries.Any(e => e.FullName.Replace('\\', '/').EndsWith(Paths.HtmlName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static ImportResult Import(string packPath, string? targetVersionDir, Action<string> log)
    {
        using var zip = ZipFile.OpenRead(packPath);

        PackManifest? manifest = null;
        var manifestEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (manifestEntry != null)
        {
            manifest = JsonSerializer.Deserialize<PackManifest>(ReadText(manifestEntry), JsonOpts);
        }

        var hasGame = manifest?.HasGame ?? zip.Entries.Any(e =>
            e.FullName.Replace('\\', '/').EndsWith(Paths.HtmlName, StringComparison.OrdinalIgnoreCase));

        string dest;
        string? newFolder = null;

        if (hasGame)
        {
            var baseId = string.IsNullOrWhiteSpace(manifest?.Id)
                ? Path.GetFileNameWithoutExtension(packPath)
                : manifest!.Id;
            var id = Sanitize(baseId);
            dest = UniqueDir(Paths.VersionsDir, id);
            newFolder = Path.GetFileName(dest);
        }
        else
        {
            if (targetVersionDir == null || !Directory.Exists(targetVersionDir))
            {
                throw new InvalidOperationException("这个包不含游戏本体，请先在左侧选中一个要导入模组的版本。");
            }
            dest = targetVersionDir;
        }

        Directory.CreateDirectory(dest);
        var destFull = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar);

        var extracted = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) continue;

            string rel;
            if (hasGame)
            {
                if (name.StartsWith("game/", StringComparison.OrdinalIgnoreCase)) rel = name[5..];
                else if (name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) rel = name;
                else if (name.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)) rel = name;
                else if (manifest != null) continue;
                else rel = name;
            }
            else
            {
                if (name.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) rel = name;
                else if (manifest != null) continue;
                else rel = name;
            }

            var target = Path.GetFullPath(Path.Combine(destFull, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                log($"跳过越界条目：{rel}");
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (parent != null) Directory.CreateDirectory(parent);
            entry.ExtractToFile(target, overwrite: true);
            extracted++;
        }

        log($"解压完成：{extracted} 个文件");

        if (hasGame)
        {
            FlattenSingleFolder(dest, log);
            if (VersionService.FindHtml(dest) == null)
            {
                log("警告：这个包里没有找到 Degrees of Lewdity.html，导入的版本可能无法启动。");
            }
        }

        // 套用包内记录的模组顺序与启停状态
        var mods = ModService.Load(dest);
        if (manifest is { Mods.Count: > 0 })
        {
            foreach (var mod in mods)
            {
                var packed = manifest.Mods.FirstOrDefault(p =>
                    string.Equals(p.File, mod.FileName, StringComparison.OrdinalIgnoreCase));
                if (packed != null) mod.Enabled = packed.Enabled;
            }

            var order = manifest.Mods.Select(m => m.File).ToList();
            mods = mods
                .OrderBy(m =>
                {
                    var idx = order.FindIndex(f => string.Equals(f, m.FileName, StringComparison.OrdinalIgnoreCase));
                    return idx < 0 ? int.MaxValue : idx;
                })
                .ToList();
        }
        ModService.Save(dest, mods);

        return new ImportResult { NewVersionFolder = newFolder, HasGame = hasGame, ModCount = mods.Count };
    }

    private static void FlattenSingleFolder(string dest, Action<string> log)
    {
        if (VersionService.FindHtml(dest) != null) return;

        var subDirs = Directory.GetDirectories(dest);
        if (subDirs.Length != 1) return;

        var inner = VersionService.FindHtml(subDirs[0]);
        if (inner == null) return;

        log("包内多套了一层目录，正在自动上移…");
        foreach (var item in Directory.GetFileSystemEntries(subDirs[0]))
        {
            var name = Path.GetFileName(item);
            var target = Path.Combine(dest, name);
            if (File.Exists(item))
            {
                File.Move(item, target, overwrite: true);
            }
            else
            {
                CopyDirectory(item, target);
                Directory.Delete(item, recursive: true);
            }
        }
        Directory.Delete(subDirs[0], recursive: true);
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = text.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "imported-version" : result;
    }

    private static string UniqueDir(string root, string id)
    {
        var path = Path.Combine(root, id);
        var i = 2;
        while (Directory.Exists(path))
        {
            path = Path.Combine(root, $"{id}-{i}");
            i++;
        }
        return path;
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void WriteText(ZipArchive archive, string entryName, string text)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFile(ZipArchive archive, string entryName, string sourcePath)
    {
        archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Optimal);
    }
}
