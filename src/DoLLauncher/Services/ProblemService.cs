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

    /// <summary>已启用模组缺少前置（未安装 / 未启用 / 版本不满足）。</summary>
    public static List<ModMissingDependency> MissingDependencies(IEnumerable<ModInfo> mods)
    {
        var all = mods.ToList();
        var result = new List<ModMissingDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in all.Where(m => m.Enabled))
        {
            foreach (var dep in mod.Dependencies)
            {
                if (ModService.IsGameVersion(dep.Name) || ModService.IsBuiltin(dep.Name)) continue;

                var key = mod.FileName + "|" + dep.Name;
                if (!seen.Add(key)) continue;

                var target = all.FirstOrDefault(m =>
                    string.Equals(m.Name, dep.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
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

                if (!target.Enabled)
                {
                    result.Add(new ModMissingDependency
                    {
                        ModFile = mod.FileName,
                        ModName = mod.Name,
                        DependencyName = dep.Name,
                        RequiredVersion = dep.Version,
                        LocalVersion = target.Version,
                        Disabled = true,
                    });
                    continue;
                }

                if (!VersionUtil.Satisfies(dep.Version, target.Version))
                {
                    result.Add(new ModMissingDependency
                    {
                        ModFile = mod.FileName,
                        ModName = mod.Name,
                        DependencyName = dep.Name,
                        RequiredVersion = dep.Version,
                        LocalVersion = target.Version,
                    });
                }
            }
        }

        return result;
    }
}
