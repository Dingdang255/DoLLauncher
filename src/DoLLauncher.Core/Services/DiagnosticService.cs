using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DoLLauncher.Core.Data;
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

        lines.Add("");
        var embedded = LyraPackService.IsLyraPackName(version.Folder)
            ? LyraPackService.ReadEmbeddedMods(version.FullPath)
            : new List<LyraEmbeddedMod>();
        if (embedded.Count > 0)
        {
            lines.Add($"内置模组（整合包 HTML）：{embedded.Count} 个");
            foreach (var mod in embedded)
                lines.Add($"  · [{ (mod.IsBuiltin ? "必需" : "可解压") }] {mod.Name} {mod.Version}");
            lines.Add("");
        }

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
                AddText(zip, "analysis.txt", GameLogAnalysis.Analyze(version));
                AddText(zip, "system.txt", SystemInfo(version));
                AddText(zip, "README.txt", PackageReadme(version));
                AddText(zip, "version.json", $"{{\n  \"folder\": \"{version.Folder}\",\n  \"gameVersion\": \"{version.GameVersion}\",\n  \"path\": \"{version.FullPath.Replace("\\", "\\\\")}\"\n}}");

                AddFileIfExists(zip, "launcher-versions.json", Paths.ConfigFile);
                AddFileIfExists(zip, "mods-state.json", Path.Combine(version.FullPath, "mods-state.json"));
                AddFileIfExists(zip, "modList.json", Path.Combine(version.FullPath, "modList.json"));
                AddFileIfExists(zip, "lyra-mod-cache.json", Path.Combine(version.FullPath, "data", "lyra-mod-cache.json"));
                AddSlotSaves(zip, version.FullPath);
                AddFileIfExists(zip, "saves/idb-mirror.json", Path.Combine(version.FullPath, "data", "idb-mirror.json"));
                AddFileIfExists(zip, "saves/idb-mirror.legacy.json", Path.Combine(version.FullPath, "data", "idb-mirror.legacy.json"));
                AddFileIfExists(zip, "logs/launcher.log", LogService.FilePath);
                AddFileIfExists(zip, "logs/launcher-error.log", LogService.ErrorFilePath);

                foreach (var logFile in RecentGameLogs(version.FullPath, 10))
                {
                    AddFileIfExists(zip, "logs/" + Path.GetFileName(logFile), logFile);
                }

                foreach (var mod in ModService.Load(version.FullPath))
                {
                    var boot = ExtractBootJson(mod.FilePath);
                    if (boot != null) AddText(zip, "mods/" + mod.FileName + ".boot.json", boot);
                }

                log("诊断包已写入：检查报告、报错分析、系统信息、日志与模组元数据。");
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

    /// <summary>版本目录里最近的若干份游戏日志（新的在前）。</summary>
    public static List<string> RecentGameLogs(string versionDir, int max)
    {
        try
        {
            var dir = GameLogService.Dir(versionDir);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "game-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(Math.Max(1, max))
                .Select(f => f.FullName)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string SystemInfo(VersionInfo version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 系统与运行环境 ==");
        sb.AppendLine($"启动器版本：{BuildInfo.Display}");
        sb.AppendLine($"操作系统：{Environment.OSVersion}（64 位系统={Environment.Is64BitOperatingSystem}）");
        sb.AppendLine($"运行时：{Environment.Version}");
        if (!string.IsNullOrWhiteSpace(RuntimeEnvironment.WebViewVersion))
            sb.AppendLine($"WebView 版本：{RuntimeEnvironment.WebViewVersion}");
        sb.AppendLine($"机器名：{Environment.MachineName}，CPU 核数：{Environment.ProcessorCount}");
        sb.AppendLine();
        sb.AppendLine("== 当前版本 ==");
        sb.AppendLine($"目录：{version.FullPath}");
        sb.AppendLine($"显示名：{version.DisplayName}");
        sb.AppendLine($"游戏版本：{version.GameVersion ?? "未知"}");
        sb.AppendLine($"模式：{version.Mode}，端口：{version.Port}");

        try
        {
            var cfg = AppConfig.Load();
            sb.AppendLine();
            sb.AppendLine("== 已登记版本 ==");
            foreach (var v in cfg.Versions)
            {
                sb.AppendLine($"  · {v.Folder}（端口 {v.Port}，{v.Mode}）");
            }
            sb.AppendLine();
            sb.AppendLine("== 日志设置 ==");
            sb.AppendLine($"保留份数={cfg.General.GameLogKeep}，单份上限={cfg.General.GameLogMaxMb} MB（0=无限制），超限处理={cfg.General.GameLogOverflow}");
            sb.AppendLine($"启动器日志详细度={cfg.General.LogVerbosity}（detailed 时错误附完整堆栈与调用链）");
        }
        catch (Exception ex)
        {
            sb.AppendLine("读取配置失败：" + ex.Message);
        }

        sb.AppendLine();
        sb.AppendLine($"启动器日志：{LogService.FilePath}");
        sb.AppendLine($"游戏日志目录：{GameLogService.Dir(version.FullPath)}");
        return sb.ToString();
    }

    private static string PackageReadme(VersionInfo version)
    {
        var sb = new StringBuilder();
        sb.AppendLine("DoL 启动器诊断包");
        sb.AppendLine("=================");
        sb.AppendLine($"版本：{version.DisplayName}（{version.Folder}）");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"启动器：{BuildInfo.Display}");
        sb.AppendLine();
        sb.AppendLine("建议阅读顺序：");
        sb.AppendLine("1. analysis.txt —— 游戏日志里的 warning/error 已分组，并给出次数、时间和可能原因；先看这里。");
        sb.AppendLine("2. report.txt   —— 游戏本体、版本号、模组依赖与完整性的检查结果。");
        sb.AppendLine("3. logs/game-*.log —— 游戏页面控制台原始日志（warning/error 为完整内容，其它级别超长会标注省略字数）。");
        sb.AppendLine("4. logs/launcher.log —— 启动器自身的运行记录。");
        sb.AppendLine("5. logs/launcher-error.log —— 启动器未处理异常的完整堆栈与内部异常链（含 async 任务异常）。");
        sb.AppendLine("6. system.txt   —— 系统、运行时、端口、版本列表与日志设置。");
        sb.AppendLine("7. mods/*.boot.json —— 每个模组的元数据与依赖声明，可用来核对缺失/版本不符的前置。");
        sb.AppendLine("8. modList.json / mods-state.json —— 模组加载顺序与启用状态。");
        sb.AppendLine("9. saves/ —— 槽位存档（manifest.json 与各 slot-*.json、slot-*.details.json；不含可重建的 .read-cache.json）。");
        sb.AppendLine();
        sb.AppendLine("说明：");
        sb.AppendLine("· 游戏日志来自内置服务器注入的采集脚本，只记录浏览器控制台与未捕获异常，不包含存档内容。");
        sb.AppendLine("· 设置页把「日志详细度」调成「详细」后，错误会附带堆栈与调用位置，日志体积也会相应变大。");
        sb.AppendLine("· 排查模组问题时，可先停用可疑模组再启动一次，对比 analysis.txt 的变化。");
        return sb.ToString();
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

    /// <summary>归档槽位存档的清单与各槽位文件；汇总缓存 <c>.read-cache.json</c> 可由槽位重新生成，不归档。</summary>
    private static void AddSlotSaves(ZipArchive zip, string versionDir)
    {
        AddFileIfExists(zip, "saves/manifest.json", SlotSaveStore.ManifestFile(versionDir));
        try
        {
            var root = SlotSaveStore.Root(versionDir);
            if (!Directory.Exists(root)) return;

            var cache = SlotSaveStore.AggregateCacheFile(versionDir);
            foreach (var file in Directory.EnumerateFiles(root, "slot-*.json"))
            {
                if (string.Equals(file, cache, StringComparison.OrdinalIgnoreCase)) continue;
                AddFileIfExists(zip, "saves/" + Path.GetFileName(file), file);
            }
        }
        catch
        {
            // 槽位目录不可读时跳过，不影响诊断包其它内容。
        }
    }
}
