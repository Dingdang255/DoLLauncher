using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoLLauncher.Core.Data;

namespace DoLLauncher.Core.Story;

/// <summary>
/// 存档镜像 / 备份文件的只读访问入口：定位文件、汇总槽位、解析单个槽位的剧情路径，
/// 并按需回读某一步的完整变量。所有文件均以只读共享方式打开，不修改存档。
/// </summary>
public static class SaveMirrorReader
{
    private const int CacheLimit = 4;

    private static readonly object Sync = new();
    private static readonly List<ArchiveEntry> ArchiveCache = new();
    private static readonly List<PathEntry> PathCache = new();
    private static DeltaEntry? _deltaCache;

    private sealed class ArchiveEntry
    {
        public string Signature = "";
        public StoryArchive Archive = new();
    }

    private sealed class PathEntry
    {
        public string Signature = "";
        public string SlotId = "";
        public string FilePath = "";
        public StoryPath Path = new();
        public List<RawMoment> Moments = new();
        public bool IsDelta;
        public long DeltaStart = -1;
        public long DeltaEnd = -1;
    }

    private sealed class DeltaEntry
    {
        public string Signature = "";
        public string SlotId = "";
        public List<JsonNode> Nodes = new();
    }

    /// <summary>定位该版本可用的存档文件（磁盘镜像优先，其次最新备份）；没有时返回 null。</summary>
    /// <param name="versionDir">版本目录的完整路径。</param>
    public static StorySourceInfo? FindSource(string versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(versionDir);
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(full)) return null;

        // 槽位存档优先：把所有槽位汇总成 dol-saves/1 缓存，供解析器复用。
        if (SlotSaveStore.HasStore(full))
        {
            try
            {
                var m = SlotSaveStore.ReadManifest(full);
                var db = string.IsNullOrWhiteSpace(m.SaveDb) ? "degrees-of-lewdity" : m.SaveDb;
                var cache = SlotSaveStore.BuildAggregateCache(full, db);
                if (!string.IsNullOrWhiteSpace(cache) && File.Exists(cache))
                {
                    var info = new FileInfo(cache);
                    return new StorySourceInfo
                    {
                        FilePath = info.FullName,
                        Kind = StorySourceKind.Mirror,
                        VersionFolder = full,
                        Length = info.Length,
                        ModifiedAt = info.LastWriteTime,
                        DisplayNote = "磁盘槽位",
                    };
                }
            }
            catch
            {
                // 槽位汇总失败时继续尝试旧镜像与备份。
            }
        }

        var mirror = Path.Combine(full, "data", "idb-mirror.json");
        if (File.Exists(mirror))
        {
            try
            {
                var info = new FileInfo(mirror);
                return new StorySourceInfo
                {
                    FilePath = info.FullName,
                    Kind = StorySourceKind.Mirror,
                    VersionFolder = full,
                    Length = info.Length,
                    ModifiedAt = info.LastWriteTime,
                    DisplayNote = "磁盘镜像",
                };
            }
            catch
            {
                // 镜像不可读时继续尝试备份。
            }
        }

        var versionName = new DirectoryInfo(full).Name;
        string backupsRoot;
        try
        {
            backupsRoot = Path.GetFullPath(Path.Combine(full, "..", "..", "backups", versionName));
        }
        catch
        {
            return null;
        }

        foreach (var dir in new[] { Path.Combine(backupsRoot, "saves"), backupsRoot })
        {
            if (!Directory.Exists(dir)) continue;
            var found = FindLatestBackup(dir, full);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// 读取文件里的槽位列表（details + saves 的 slot 汇总）。
    /// 失败时返回 null，并通过 <paramref name="error"/> 给出中文原因。
    /// </summary>
    public static StoryArchive? LoadArchive(StorySourceInfo source, CancellationToken ct, out string? error)
    {
        error = null;
        if (source == null || string.IsNullOrWhiteSpace(source.FilePath))
        {
            error = "未指定存档文件";
            return null;
        }

        if (!File.Exists(source.FilePath))
        {
            error = "存档文件不存在";
            return null;
        }

        string signature;
        try
        {
            signature = BuildSignature(source.FilePath);
        }
        catch (Exception ex)
        {
            error = "读取存档文件信息失败：" + ex.Message;
            return null;
        }

        lock (Sync)
        {
            var hit = ArchiveCache.FirstOrDefault(e => e.Signature == signature);
            if (hit != null)
            {
                ArchiveCache.Remove(hit);
                ArchiveCache.Add(hit);
                return hit.Archive;
            }
        }

        try
        {
            var parsed = StoryFileParser.ParseArchive(source.FilePath, ct);
            if (parsed.Error != null)
            {
                error = parsed.Error;
                return null;
            }

            var archive = new StoryArchive { Source = source, Slots = parsed.Slots };
            lock (Sync)
            {
                ArchiveCache.RemoveAll(e => e.Signature == signature);
                ArchiveCache.Add(new ArchiveEntry { Signature = signature, Archive = archive });
                while (ArchiveCache.Count > CacheLimit) ArchiveCache.RemoveAt(0);
            }

            return archive;
        }
        catch (OperationCanceledException)
        {
            error = "操作已取消";
            return null;
        }
        catch (JsonException)
        {
            error = "存档文件格式损坏或不完整";
            return null;
        }
        catch (IOException ex)
        {
            error = "读取存档文件失败：" + ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有权限读取存档文件";
            return null;
        }
    }

    /// <summary>
    /// 解析指定槽位的完整剧情路径。
    /// 失败时返回 null，并通过 <paramref name="error"/> 给出中文原因。
    /// </summary>
    public static StoryPath? LoadSlot(StoryArchive archive, string slotId, CancellationToken ct, out string? error)
    {
        error = null;
        if (archive == null || string.IsNullOrWhiteSpace(archive.Source.FilePath))
        {
            error = "未指定存档文件";
            return null;
        }

        var normalizedSlot = (slotId ?? "").Trim();
        if (normalizedSlot.Length == 0)
        {
            error = "未指定存档槽位";
            return null;
        }

        string signature;
        try
        {
            signature = BuildSignature(archive.Source.FilePath);
        }
        catch (Exception ex)
        {
            error = "读取存档文件信息失败：" + ex.Message;
            return null;
        }

        lock (Sync)
        {
            var hit = PathCache.FirstOrDefault(e => e.Signature == signature && e.SlotId == normalizedSlot);
            if (hit != null)
            {
                PathCache.Remove(hit);
                PathCache.Add(hit);
                return hit.Path;
            }
        }

        StorySlotInfo? slot = null;
        foreach (var item in archive.Slots)
        {
            if (string.Equals(item.SlotId, normalizedSlot, StringComparison.Ordinal))
            {
                slot = item;
                break;
            }
        }

        try
        {
            var parsed = StoryFileParser.ParseSlot(archive.Source.FilePath, normalizedSlot, ct, out var parseError);
            if (parseError != null)
            {
                error = parseError;
                return null;
            }

            var isDelta = false;
            var moments = parsed.Moments;

            if ((!parsed.HadHistory || moments.Count == 0) && parsed.IsDelta)
            {
                var nodes = StoryFileParser.RestoreDeltaMoments(
                    archive.Source.FilePath, parsed.DeltaStart, parsed.DeltaEnd, ct, out var deltaError);
                if (nodes == null)
                {
                    error = deltaError ?? "增量存档还原失败";
                    return null;
                }

                isDelta = true;
                moments = new List<RawMoment>(nodes.Count);
                foreach (var node in nodes) moments.Add(StoryFileParser.ToRawMoment(node));
            }
            else if (!parsed.HadHistory && !parsed.IsDelta)
            {
                error = "存档里没有剧情历史";
                return null;
            }

            var path = new StoryPath
            {
                SlotId = normalizedSlot,
                SaveName = slot == null
                    ? (string.IsNullOrWhiteSpace(normalizedSlot) ? "" : "存档 " + normalizedSlot)
                    : slot.SaveName,
                SavedAt = slot?.SavedAt ?? 0,
                SourceNote = archive.Source.DisplayNote,
                IsDelta = isDelta,
                Steps = BuildSteps(moments),
            };

            lock (Sync)
            {
                PathCache.RemoveAll(e => e.Signature == signature && e.SlotId == normalizedSlot);
                PathCache.Add(new PathEntry
                {
                    Signature = signature,
                    SlotId = normalizedSlot,
                    FilePath = archive.Source.FilePath,
                    Path = path,
                    Moments = moments,
                    IsDelta = isDelta,
                    DeltaStart = parsed.DeltaStart,
                    DeltaEnd = parsed.DeltaEnd,
                });
                while (PathCache.Count > CacheLimit) PathCache.RemoveAt(0);
            }

            return path;
        }
        catch (OperationCanceledException)
        {
            error = "操作已取消";
            return null;
        }
        catch (JsonException)
        {
            error = "存档文件格式损坏或不完整";
            return null;
        }
        catch (IOException ex)
        {
            error = "读取存档文件失败：" + ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有权限读取存档文件";
            return null;
        }
    }

    /// <summary>
    /// 按需读取某一步的完整 variables（键 → 原始 JSON 文本）。失败时返回 null 并给出中文原因。
    /// <paramref name="stepIndex"/> 优先按原始步序号解释，越界时回退为节点列表下标。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? LoadStepVariables(
        StoryArchive archive, StoryPath path, int stepIndex, CancellationToken ct, out string? error)
    {
        error = null;
        if (archive == null || path == null)
        {
            error = "尚未加载存档数据";
            return null;
        }

        PathEntry? entry;
        lock (Sync)
        {
            entry = PathCache.FirstOrDefault(e => ReferenceEquals(e.Path, path));
            if (entry == null && !string.IsNullOrWhiteSpace(path.SlotId))
            {
                var signature = BuildSignatureSafe(archive.Source.FilePath);
                entry = PathCache.FirstOrDefault(e => e.Signature == signature && e.SlotId == path.SlotId);
            }
        }

        if (entry == null)
        {
            error = "剧情数据已过期，请重新加载存档";
            return null;
        }

        var rawIndex = stepIndex;
        if (rawIndex < 0 || rawIndex >= entry.Moments.Count)
        {
            if (stepIndex >= 0 && stepIndex < path.Steps.Count) rawIndex = path.Steps[stepIndex].Index;
            else if (path.Steps.Count > 0) rawIndex = path.Steps[path.Steps.Count - 1].Index;
        }

        if (rawIndex < 0 || rawIndex >= entry.Moments.Count)
        {
            error = "该步骤不存在";
            return null;
        }

        try
        {
            if (!entry.IsDelta)
            {
                var moment = entry.Moments[rawIndex];
                if (moment.StartOffset <= 0 || moment.EndOffset <= moment.StartOffset)
                {
                    error = "该步骤缺少定位信息";
                    return null;
                }

                return StoryFileParser.LoadMomentVariables(
                    entry.FilePath, moment.StartOffset, moment.EndOffset, ct, out error);
            }

            var nodes = GetDeltaNodes(entry, ct, out var deltaError);
            if (nodes == null)
            {
                error = deltaError ?? "增量存档还原失败";
                return null;
            }

            if (rawIndex >= nodes.Count)
            {
                error = "该步骤不存在";
                return null;
            }

            return StoryFileParser.ExtractVariables(nodes[rawIndex]);
        }
        catch (OperationCanceledException)
        {
            error = "操作已取消";
            return null;
        }
        catch (JsonException)
        {
            error = "该步骤的数据格式无法识别";
            return null;
        }
        catch (IOException ex)
        {
            error = "读取该步骤变量失败：" + ex.Message;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            error = "没有权限读取存档文件";
            return null;
        }
    }

    private static List<StoryStep> BuildSteps(List<RawMoment> moments)
    {
        var steps = new List<StoryStep>();
        StoryStep? current = null;
        List<int>? indexes = null;

        for (var i = 0; i < moments.Count; i++)
        {
            var moment = moments[i];
            if (current != null && string.Equals(current.Title, moment.Title, StringComparison.Ordinal))
            {
                indexes!.Add(i);
                current.RepeatCount = indexes.Count;
                continue;
            }

            indexes = new List<int> { i };
            current = new StoryStep
            {
                Index = i,
                Title = moment.Title,
                Timestamp = moment.Timestamp,
                StartDate = moment.StartDate,
                RepeatCount = 1,
                StepIndexes = indexes,
                ContentOffset = moment.StartOffset,
            };
            steps.Add(current);
        }

        foreach (var step in steps)
        {
            if (StoryRules.Default.TryClassify(step.Title, out var category, out var group))
            {
                step.Category = category;
                step.GroupName = group;
            }
        }

        return steps;
    }

    private static List<JsonNode>? GetDeltaNodes(PathEntry entry, CancellationToken ct, out string? error)
    {
        lock (Sync)
        {
            if (_deltaCache != null && _deltaCache.Signature == entry.Signature &&
                string.Equals(_deltaCache.SlotId, entry.SlotId, StringComparison.Ordinal))
            {
                error = null;
                return _deltaCache.Nodes;
            }
        }

        var nodes = StoryFileParser.RestoreDeltaMoments(
            entry.FilePath, entry.DeltaStart, entry.DeltaEnd, ct, out error);
        if (nodes == null) return null;

        lock (Sync)
        {
            _deltaCache = new DeltaEntry { Signature = entry.Signature, SlotId = entry.SlotId, Nodes = nodes };
        }

        return nodes;
    }

    private static StorySourceInfo? FindLatestBackup(string dir, string versionDir)
    {
        FileInfo? best = null;
        try
        {
            foreach (var pattern in new[] { "saves-*.json", "auto-saves-*.json" })
            {
                foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    if (!info.Exists) continue;
                    if (best == null || info.LastWriteTimeUtc > best.LastWriteTimeUtc) best = info;
                }
            }
        }
        catch
        {
            return null;
        }

        if (best == null) return null;

        return new StorySourceInfo
        {
            FilePath = best.FullName,
            Kind = StorySourceKind.Backup,
            VersionFolder = versionDir,
            Length = best.Length,
            ModifiedAt = best.LastWriteTime,
            DisplayNote = "备份：" + best.Name,
        };
    }

    private static string BuildSignature(string filePath)
    {
        var info = new FileInfo(filePath);
        return info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
    }

    private static string BuildSignatureSafe(string filePath)
    {
        try
        {
            return BuildSignature(filePath);
        }
        catch
        {
            return filePath + "|0|0";
        }
    }
}
