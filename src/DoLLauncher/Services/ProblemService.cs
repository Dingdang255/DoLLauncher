using DoLLauncher.Core.Versions;
using DoLLauncher.Models;

namespace DoLLauncher.Services;

/// <summary>
/// 汇总「可能存在问题」三类检查里与冲突无关的两类：
/// 启用模组缺少前置、模组目标游戏版本的 z 段与当前版本相差过大。
/// </summary>
public static class ProblemService
{
    public static int NormalizeThreshold(int value) => value < 1 ? 1 : value;

    /// <summary>去掉 "&gt;=0.5.12.13" 这类约束前缀，只保留版本号本身。</summary>
    public static string ConstraintVersion(string? constraint)
    {
        var text = (constraint ?? string.Empty).Trim();
        while (text.Length > 0 && !char.IsDigit(text[0])) text = text[1..];
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return text[..end].TrimEnd('.');
    }

    /// <summary>取版本第 index 段（0 起）的整数，缺失时按 0。</summary>
    public static int Segment(string? version, int index)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var parts = version.Trim().Split('.');
        if (index >= parts.Length) return 0;
        var digits = new string(parts[index].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    /// <summary>目标版本与当前版本第三段（z）相差的绝对值；任一版本无法解析时返回 -1。</summary>
    public static int SegmentGap(string? target, string? current)
    {
        var targetVersion = ConstraintVersion(target);
        if (targetVersion.Length == 0 || string.IsNullOrWhiteSpace(current)) return -1;
        return Math.Abs(Segment(targetVersion, 2) - Segment(current, 2));
    }

    /// <summary>相差达到阈值即橙色，超过阈值升为红色。</summary>
    public static ProblemSeverity GapSeverity(int gap, int threshold) =>
        gap > NormalizeThreshold(threshold) ? ProblemSeverity.Red : ProblemSeverity.Orange;

    /// <summary>已启用模组里「游戏版本不满足，且 z 段相差达到阈值」的条目。</summary>
    public static List<ModVersionMismatch> VersionMismatches(
        IEnumerable<ModInfo> mods, string? gameVersion, int threshold)
    {
        var result = new List<ModVersionMismatch>();
        if (string.IsNullOrWhiteSpace(gameVersion)) return result;

        var limit = NormalizeThreshold(threshold);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(m => m.Enabled))
        {
            foreach (var dep in mod.Dependencies)
            {
                if (!ModService.IsGameVersion(dep.Name)) continue;
                if (VersionUtil.Satisfies(dep.Version, gameVersion)) continue;

                var target = ConstraintVersion(dep.Version);
                var gap = SegmentGap(target, gameVersion);
                if (gap < limit) continue;

                var key = mod.FileName + "|" + target;
                if (!seen.Add(key)) continue;

                result.Add(new ModVersionMismatch
                {
                    ModFile = mod.FileName,
                    ModName = mod.Name,
                    TargetGameVersion = target,
                    CurrentGameVersion = gameVersion,
                    Gap = gap,
                    Severity = GapSeverity(gap, limit),
                });
            }
        }

        return result;
    }

    /// <summary>已启用模组里名字相同（忽略大小写与符号）的多个文件；每个文件都会被 ModLoader 加载，属于红色问题。</summary>
    public static List<ModDuplicate> Duplicates(IEnumerable<ModInfo> mods)
    {
        var groups = mods
            .Where(m => m.Enabled && m.Name.Length > 0)
            .GroupBy(m => DownloadService.NormalizeName(m.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1);

        var result = new List<ModDuplicate>();
        foreach (var group in groups)
        {
            var ordered = group.ToList();
            ordered.Sort((a, b) =>
            {
                var byVersion = VersionUtil.Compare(b.Version, a.Version);
                return byVersion != 0
                    ? byVersion
                    : string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            var duplicate = new ModDuplicate
            {
                Name = ordered[0].Name,
                NewestFileName = ordered[0].FileName,
            };
            for (var i = 0; i < ordered.Count; i++)
            {
                duplicate.Members.Add(new ModDuplicateMember
                {
                    FileName = ordered[i].FileName,
                    Name = ordered[i].Name,
                    Version = ordered[i].Version,
                    IsNewest = i == 0,
                });
            }
            result.Add(duplicate);
        }

        return result;
    }

    /// <summary>
    /// 已启用模组缺少前置（未安装 / 未启用 / 版本不满足）。
    /// 调查结论（两套实际版本目录 + launcher.log，模组包只读未改）：
    /// · 已装的「简易框架」boot name 是 Simple Frameworks、包名是 Simple.Framework.ver2.0.5.build_2.zip，
    ///   市场与本机日志里还出现过 Simple Framework / SimpleFrameworks 的写法，直接按 Name 精确比较会误报缺失；
    /// · maplebirch / ModI18N 等的依赖写成 ModLoaderGui、ModLoader DoL ImageLoaderHook、GameVersion，
    ///   这些是 ModLoader 内置或虚拟前置，不应按磁盘模组查找；写成 Mod Loader、ModLoader GUI、
    ///   Check Game Version 等变体时同样要识别；
    /// · 包名普遍带版本尾巴（maplebirch-0.5.12.13-v5.6.2.mod.zip），按文件名兜底前必须先去掉；
    /// · 少数 boot.json 的 modName 带首尾空格或把版本约束拼进名字（"ModLoader &gt;=2.0.0"），
    ///   以及把游戏本体写成 Degrees of Lewdity / DoL，这些都不是磁盘前置；
    /// · 自依赖、空名字、只剩版本号的条目直接跳过。
    /// 未安装 / 已停用 / 版本不满足三种真实问题仍按原字段输出，窗口继续按红色问题展示。
    /// </summary>
    public static List<ModMissingDependency> MissingDependencies(IEnumerable<ModInfo> mods)
    {
        var all = mods.ToList();
        var result = new List<ModMissingDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 归一化名 → 本地模组。NameKeys 同时覆盖 boot name、包名主干、版本尾巴与英文单复数写法。
        var byKey = new Dictionary<string, List<ModInfo>>(StringComparer.Ordinal);
        foreach (var local in all)
        {
            foreach (var key in ModService.NameKeys(local))
            {
                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = new List<ModInfo>();
                if (!list.Contains(local)) list.Add(local);
            }
        }

        foreach (var mod in all.Where(m => m.Enabled))
        {
            var selfKeys = new[]
            {
                ModService.NormalizeModName(mod.Name),
                ModService.NormalizeModName(ModService.FileNameStem(mod.FileName)),
            };

            foreach (var dep in mod.Dependencies)
            {
                var dependencyName = CleanDependencyName(dep.Name);
                var key = ModService.NormalizeModName(dependencyName);
                if (key.Length == 0 || IsNonPrerequisiteName(dependencyName, key)) continue;
                if (ModService.IsGameVersion(dependencyName) || ModService.IsBuiltin(dependencyName)) continue;
                if (selfKeys.Contains(key)) continue;

                // 同一模组用不同写法重复声明同一个前置时只报一次。
                if (!seen.Add(mod.FileName + "|" + key)) continue;

                var candidates = byKey.TryGetValue(key, out var matched)
                    ? matched.Where(m => !ReferenceEquals(m, mod)).ToList()
                    : null;
                if (candidates == null || candidates.Count == 0)
                {
                    result.Add(new ModMissingDependency
                    {
                        ModFile = mod.FileName,
                        ModName = mod.Name,
                        DependencyName = dep.Name,
                        RequiredVersion = dep.Version,
                    });
                    continue;
                }

                var enabledTargets = candidates.Where(m => m.Enabled).ToList();
                if (enabledTargets.Count == 0)
                {
                    var disabled = candidates[0];
                    result.Add(new ModMissingDependency
                    {
                        ModFile = mod.FileName,
                        ModName = mod.Name,
                        DependencyName = dep.Name,
                        RequiredVersion = dep.Version,
                        LocalVersion = disabled.Version,
                        Disabled = true,
                    });
                    continue;
                }

                // 已启用的同名模组里只要有一个满足版本约束就不再报；都不满足才按版本问题输出。
                if (enabledTargets.Any(m => VersionUtil.Satisfies(dep.Version, m.Version)))
                    continue;

                result.Add(new ModMissingDependency
                {
                    ModFile = mod.FileName,
                    ModName = mod.Name,
                    DependencyName = dep.Name,
                    RequiredVersion = dep.Version,
                    LocalVersion = enabledTargets[0].Version,
                });
            }
        }

        return result;
    }

    /// <summary>modName 里误抄了版本约束（"ModLoader &gt;=2.0.0"）时，只保留约束前的名字。</summary>
    private static string CleanDependencyName(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var cut = text.IndexOfAny(new[] { '<', '>', '=', '^', '~' });
        return cut > 0 ? text[..cut].Trim() : text;
    }

    private static readonly HashSet<string> NonPrerequisiteNames = new(StringComparer.Ordinal)
    {
        "none", "null", "na", "readme", "无", "无前置", "无依赖",
        "游戏本体", "degreesoflewdity", "dol", "dolgame",
        "loadafter", "loadbefore", "loadorder", "加载顺序",
    };

    /// <summary>明显不是磁盘前置的条目：占位词、游戏本体、加载顺序声明、网址、整段说明文字。</summary>
    private static bool IsNonPrerequisiteName(string name, string key)
    {
        if (key.Length == 0) return true;
        if (!key.Any(char.IsLetter)) return true; // 只剩版本号或符号
        if (key.Length > 80) return true; // 整段说明被误写进 modName
        if (name.Contains("://", StringComparison.Ordinal)) return true;
        if (NonPrerequisiteNames.Contains(key)) return true;
        return key.StartsWith("loadorder", StringComparison.Ordinal) ||
               key.Contains("加载顺序", StringComparison.Ordinal);
    }
}
