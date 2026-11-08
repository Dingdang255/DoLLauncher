using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>兼容性检查、日志查看与诊断包导出。</summary>
public static class DiagnosticService
{
    public static List<string> Compatibility(VersionInfo version)
    {
        var lines = new List<string>
        {
            "== 兼容性检查 ==",
            $"版本：{version.DisplayName}（{version.Folder}）",
            $"游戏版本：{version.GameVersion ?? "未知"}",
        };

        var modLoader = DetectModLoaderVersion(version.Folder);
        lines.Add($"ModLoader 版本：{modLoader ?? "未知"}");
        lines.Add("");

        var mods = ModService.Load(version.FullPath);
        lines.Add($"磁盘模组：{mods.Count} 个（启用 {mods.Count(m => m.Enabled)} 个）");
        foreach (var mod in mods)
        {
            var deps = mod.Dependencies.Count == 0
                ? ""
                : "  依赖：" + string.Join("、", mod.Dependencies.Select(d =>
                    d.Version.Length > 0 ? $"{d.Name} {d.Version}" : d.Name));
            lines.Add($"  · {mod.Name} {mod.Version}" +
                      (mod.BootParsed ? "" : "（boot.json 无法解析）") +
                      (mod.Enabled ? "" : "（已停用）") + deps);
        }
        lines.Add("");

        lines.AddRange(ModService.Check(version));

        var invalid = mods.Count(m => !m.BootParsed);
        if (invalid > 0) lines.Add($"[警告] 有 {invalid} 个 zip 不是有效模组，加载时会被跳过。");

        return lines;
    }

    /// <summary>从 "0.5.12.13-v2.101.1" 这样的目录名里取 ModLoader 版本。</summary>
    public static string? DetectModLoaderVersion(string folder)
    {
        var match = Regex.Match(folder, @"-v([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>版本目录里能找到的最新日志文件（ModLoader 导出的日志一般也在这里）。</summary>
    public static string? FindLatestLog(string versionDir)
    {
        if (!Directory.Exists(versionDir)) return null;
        try
        {
            return Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f).ToLowerInvariant();
                    if (!name.EndsWith(".log") && !name.EndsWith(".txt")) return false;
                    return name.Contains("log");
                })
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public static string ReadTail(string path, int lines = 400)
    {
        try
        {
            var all = File.ReadAllLines(path, Encoding.UTF8);
            var start = Math.Max(0, all.Length - lines);
            return string.Join(Environment.NewLine, all[start..]);
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }

    public static string ExportPackage(VersionInfo version, string outputPath, Action<string> log)
    {
        var temp = outputPath + ".tmp";
        try
        {
            using (var fs = File.Create(temp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var report = string.Join(Environment.NewLine, Compatibility(version));
                AddText(zip, "report.txt", report);
                AddText(zip, "version.json", $"{{\n  \"folder\": \"{version.Folder}\",\n  \"gameVersion\": \"{version.GameVersion}\",\n  \"path\": \"{version.FullPath.Replace("\\", "\\\\")}\"\n}}");

                AddFileIfExists(zip, "launcher-versions.json", Paths.ConfigFile);
                AddFileIfExists(zip, "mods-state.json", Path.Combine(version.FullPath, "mods-state.json"));
                AddFileIfExists(zip, "modList.json", Path.Combine(version.FullPath, "modList.json"));
                AddFileIfExists(zip, "logs/launcher.log", LogService.FilePath);

                var gameLog = FindLatestLog(version.FullPath);
                if (gameLog != null) AddFileIfExists(zip, "logs/" + Path.GetFileName(gameLog), gameLog);

                foreach (var mod in ModService.Load(version.FullPath))
                {
                    var boot = ExtractBootJson(mod.FilePath);
                    if (boot != null) AddText(zip, "mods/" + mod.FileName + ".boot.json", boot);
                }

                log("诊断包已写入：兼容性报告、模组清单、启动器日志。");
            }

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(temp, outputPath);
            return outputPath;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    private static string? ExtractBootJson(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Equals("boot.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static void AddText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void AddFileIfExists(ZipArchive zip, string entryName, string path)
    {
        if (File.Exists(path)) zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }
}
