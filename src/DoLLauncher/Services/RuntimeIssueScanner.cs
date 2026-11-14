using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 从最近的游戏日志里识别「模组补丁没匹配上当前游戏版本」这类运行期不兼容，
/// 与 boot.json 声明的版本差异一起展示在「模组版本不匹配」里。
/// 只扫描最近几份日志，并按当前已启用的模组过滤，避免把早已停用模组的旧报错翻出来。
/// </summary>
public static class RuntimeIssueScanner
{
    private const int MaxFiles = 3;
    private const int MaxBytesPerFile = 2_000_000;
    private const int HeadLength = 400;

    private static readonly Regex PatchName = new("\"name\"\\s*:\\s*\"([^\"]{1,80})\"", RegexOptions.Compiled);
    private static readonly Regex ZoneName = new(@"区域\s+([^\s]{1,40})\s+不存在", RegexOptions.Compiled);
    private static readonly Regex Timestamp = new(@"^(\d{4}-\d{2}-\d{2}T[0-9:.]+Z?)", RegexOptions.Compiled);

    private sealed class Aggregate
    {
        public string Kind = "";
        public string ModName = "";
        public string ModFile = "";
        public int Count;
        public DateTime? Last;
        public readonly List<string> Samples = new();
    }

    private static readonly object CacheSync = new();
    private static readonly Dictionary<string, (long Stamp, List<ModVersionMismatch> Result)> Cache
        = new(StringComparer.Ordinal);

    /// <summary>扫描最近日志；同一批日志与同一组启用模组的结果会缓存，避免每次刷新都重读。</summary>
    public static List<ModVersionMismatch> Scan(string versionDir, IEnumerable<ModInfo> mods, string? gameVersion)
    {
        var enabled = mods.Where(m => m.Enabled).ToList();
        if (enabled.Count == 0 || string.IsNullOrWhiteSpace(versionDir))
            return new List<ModVersionMismatch>();

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(GameLogService.Dir(versionDir))
                .GetFiles("game-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFiles)
                .ToList();
        }
        catch
        {
            return new List<ModVersionMismatch>();
        }
        if (files.Count == 0) return new List<ModVersionMismatch>();

        var signature = string.Join("|", enabled
            .Select(m => m.FileName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        var key = versionDir + "|" + (gameVersion ?? "") + "|" + signature;
        var stamp = files.Max(f => f.LastWriteTimeUtc.Ticks);

        lock (CacheSync)
        {
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Result;
        }

        var result = ScanCore(files, enabled, gameVersion);

        lock (CacheSync)
        {
            if (Cache.Count > 32) Cache.Clear();
            Cache[key] = (stamp, result);
        }
        return result;
    }

    private static List<ModVersionMismatch> ScanCore(List<FileInfo> files, List<ModInfo> enabled, string? gameVersion)
    {
        var groups = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
        var framework = FindByName(enabled, "simpleframeworks")
                        ?? FindByName(enabled, "simpleframework")
                        ?? FindByName(enabled, "简易框架");
        var patchOwner = framework ?? enabled.FirstOrDefault(m => m.Dependencies.Any(d =>
            d.Name.Contains("TweeReplacer", StringComparison.OrdinalIgnoreCase)));

        foreach (var file in files)
        {
            foreach (var line in ReadTailLines(file.FullName, MaxBytesPerFile))
            {
                if (line.Length < 12) continue;
                var head = line.Length > HeadLength ? line[..HeadLength] : line;

                // TweeReplacer 找不到补丁目标：日志里只有这一种会带出模组自己的 name。
                if (head.Contains("do_patch() (pp.content.search", StringComparison.Ordinal))
                {
                    var name = MatchPatchName(line);
                    if (name == null) continue;
                    var mod = FindByName(enabled, name);
                    Add(groups, "patch", mod?.Name ?? name, mod?.FileName ?? "", line, null);
                    continue;
                }

                // match failed 不带模组名，按「谁在用 TweeReplacer 打文本补丁」归属。
                if (head.Contains("match failed:", StringComparison.Ordinal) && patchOwner != null)
                {
                    Add(groups, "match", patchOwner.Name, patchOwner.FileName, line, ExtractMatchSample(line));
                    continue;
                }

                // maplebirch 之类的框架注入界面区域失败。
                if (head.Contains("[zone]", StringComparison.OrdinalIgnoreCase) &&
                    head.Contains("不存在", StringComparison.Ordinal))
                {
                    var mod = FindByText(enabled, head);
                    if (mod == null) continue;
                    var zone = ZoneName.Match(line);
                    Add(groups, "zone", mod.Name, mod.FileName, line,
                        zone.Success ? zone.Groups[1].Value : null);
                }
            }
        }

        return Build(groups, gameVersion);
    }

    private static List<ModVersionMismatch> Build(Dictionary<string, Aggregate> groups, string? gameVersion)
    {
        var result = new List<ModVersionMismatch>();
        var version = string.IsNullOrWhiteSpace(gameVersion) ? "当前版本" : gameVersion;

        foreach (var agg in groups.Values)
        {
            var time = agg.Last.HasValue ? "最近一次 " + agg.Last.Value.ToString("MM-dd HH:mm") : "";
            switch (agg.Kind)
            {
                case "patch":
                    result.Add(new ModVersionMismatch
                    {
                        Runtime = true,
                        RuntimeReason = "补丁应用失败",
                        ModFile = agg.ModFile,
                        ModName = agg.ModName,
                        CurrentGameVersion = gameVersion ?? "",
                        Severity = ProblemSeverity.Red,
                        RuntimeTitle = $"「{agg.ModName}」补丁应用失败（与当前游戏版本可能不兼容）",
                        RuntimeDetail =
                            $"运行日志里出现 {agg.Count} 次 TweeReplacer 找不到替换目标的错误，" +
                            $"说明这个模组的补丁没有匹配上当前 {version} 的游戏文本。" +
                            "通常是模组针对的游戏版本与当前版本不一致，相关剧情或界面可能缺失、报错。" +
                            "可先停用该模组，或换用支持当前版本的新版本。" + time,
                    });
                    break;

                case "match":
                    var samples = agg.Samples.Count > 0 ? "，例如：" + string.Join("、", agg.Samples) : "";
                    result.Add(new ModVersionMismatch
                    {
                        Runtime = true,
                        RuntimeReason = "游戏文本补丁未匹配",
                        ModFile = agg.ModFile,
                        ModName = agg.ModName,
                        CurrentGameVersion = gameVersion ?? "",
                        Severity = ProblemSeverity.Orange,
                        RuntimeTitle = $"「{agg.ModName}」有 {agg.Count} 处游戏文本补丁未匹配（可能不兼容当前版本）",
                        RuntimeDetail =
                            $"运行日志里出现 {agg.Count} 处 match failed{samples}。" +
                            "补丁要找的原文在当前游戏版本里已经改动，做侧边栏或状态栏改造的模组在漏匹配时可能出现文字重叠、错位。" +
                            "建议更新模组或先停用。" + time,
                    });
                    break;

                case "zone":
                    var zones = agg.Samples.Count > 0 ? "，涉及：" + string.Join("、", agg.Samples) : "";
                    result.Add(new ModVersionMismatch
                    {
                        Runtime = true,
                        RuntimeReason = "界面区域不存在",
                        ModFile = agg.ModFile,
                        ModName = agg.ModName,
                        CurrentGameVersion = gameVersion ?? "",
                        Severity = ProblemSeverity.Orange,
                        RuntimeTitle = $"「{agg.ModName}」引用了当前游戏中不存在的界面区域",
                        RuntimeDetail =
                            $"运行日志里出现 {agg.Count} 次「区域不存在」的错误{zones}。" +
                            "说明模组向游戏界面注入内容时找不到目标区域，常见于模组与游戏版本不匹配，相关界面可能显示异常。" +
                            "建议更新模组或先停用。" + time,
                    });
                    break;
            }
        }

        return result;
    }

    private static void Add(Dictionary<string, Aggregate> groups, string kind,
        string modName, string modFile, string line, string? sample)
    {
        var key = kind + "|" + (modFile.Length > 0 ? modFile : modName);
        if (!groups.TryGetValue(key, out var agg))
        {
            agg = new Aggregate { Kind = kind, ModName = modName, ModFile = modFile };
            groups[key] = agg;
        }

        agg.Count++;
        var time = ParseTime(line);
        if (time.HasValue && (agg.Last == null || time > agg.Last)) agg.Last = time;
        if (!string.IsNullOrWhiteSpace(sample) && agg.Samples.Count < 3 &&
            !agg.Samples.Contains(sample!, StringComparer.Ordinal))
            agg.Samples.Add(sample!);
    }

    /// <summary>do_patch 报错行后面的 JSON 里第一个 name 就是出问题的模组。</summary>
    private static string? MatchPatchName(string line)
    {
        var start = line.IndexOf("{\"name\"", StringComparison.Ordinal);
        if (start < 0) return null;
        var window = line.Substring(start, Math.Min(300, line.Length - start));
        var match = PatchName.Match(window);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>match failed 后面的原文片段，去掉控制台样式尾巴。</summary>
    private static string ExtractMatchSample(string line)
    {
        var idx = line.IndexOf("match failed:", StringComparison.Ordinal);
        if (idx < 0) return "";
        var text = line[(idx + 13)..].Trim();
        if (text.StartsWith("%c", StringComparison.Ordinal)) text = text[2..].Trim();
        var color = text.IndexOf(" color:", StringComparison.Ordinal);
        if (color > 0) text = text[..color];
        return text.Length > 60 ? text[..60] + "…" : text;
    }

    private static DateTime? ParseTime(string line)
    {
        var match = Timestamp.Match(line);
        if (!match.Success) return null;
        return DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value)
            ? value.LocalDateTime
            : null;
    }

    private static ModInfo? FindByName(List<ModInfo> mods, string? name)
    {
        var key = DownloadService.NormalizeName(name ?? string.Empty);
        if (key.Length == 0) return null;

        var exact = mods.FirstOrDefault(m =>
            DownloadService.NormalizeName(m.Name) == key ||
            DownloadService.NormalizeName(Stem(m.FileName)) == key);
        if (exact != null) return exact;

        if (key.Length < 4) return null;
        return mods.FirstOrDefault(m =>
        {
            var normalized = DownloadService.NormalizeName(m.Name);
            return normalized.Length >= 4 &&
                   (normalized.Contains(key, StringComparison.Ordinal) ||
                    key.Contains(normalized, StringComparison.Ordinal));
        });
    }

    /// <summary>按日志行里出现的模组名 / 包名主干反查已启用模组（如 [maplebirch]）。</summary>
    private static ModInfo? FindByText(List<ModInfo> mods, string head)
    {
        var text = DownloadService.NormalizeName(head);
        if (text.Length == 0) return null;

        foreach (var mod in mods)
        {
            var name = DownloadService.NormalizeName(mod.Name);
            if (name.Length >= 4 && text.Contains(name, StringComparison.Ordinal)) return mod;

            var stem = DownloadService.NormalizeName(Stem(Stem(mod.FileName)));
            if (stem.Length >= 6 && text.Contains(stem, StringComparison.Ordinal)) return mod;
        }
        return null;
    }

    private static string Stem(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    /// <summary>只读文件尾部，避免几十 MB 的旧日志全量读入。</summary>
    private static IEnumerable<string> ReadTailLines(string path, int maxBytes)
    {
        FileStream? stream = null;
        StreamReader? reader = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            reader = new StreamReader(stream, Encoding.UTF8, true);
            if (start > 0) reader.ReadLine(); // 丢弃被截断的首行
            while (reader.ReadLine() is { } line)
                yield return line;
        }
        finally
        {
            reader?.Dispose();
            stream?.Dispose();
        }
    }
}
