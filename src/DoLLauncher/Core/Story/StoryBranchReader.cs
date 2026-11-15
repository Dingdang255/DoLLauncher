using System.IO;
using System.Text.Json;

namespace DoLLauncher.Core.Story;

/// <summary>
/// 剧情分支模拟结果的读写。结果由游戏内模组导出，启动器只读展示。
/// 文件位置：<c>&lt;版本目录&gt;\data\story-sim\latest.json</c>。
/// </summary>
public static class StoryBranchReader
{
    /// <summary>结果格式标识。</summary>
    public const string Format = "dol-story-sim/1";

    /// <summary>单份结果文件的字节上限。</summary>
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>结果目录。</summary>
    public static string FolderOf(string versionFolder) => Path.Combine(versionFolder, "data", "story-sim");

    /// <summary>结果文件路径。</summary>
    public static string PathOf(string versionFolder) => Path.Combine(FolderOf(versionFolder), "latest.json");

    /// <summary>
    /// 保存游戏内上报的模拟结果，返回可展示的相对路径。
    /// </summary>
    /// <exception cref="InvalidOperationException">内容为空、过大或格式不符。</exception>
    public static string SaveLatest(string versionFolder, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("模拟结果为空");
        var bytes = System.Text.Encoding.UTF8.GetByteCount(body);
        if (bytes > MaxBytes) throw new InvalidOperationException("模拟结果超过 64MB 上限");

        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("模拟结果不是 JSON 对象");
            var format = ReadString(doc.RootElement, "format");
            if (!string.Equals(format, Format, StringComparison.Ordinal))
                throw new InvalidOperationException("不支持的模拟结果格式：" + format);
        }

        var dir = FolderOf(versionFolder);
        Directory.CreateDirectory(dir);
        var path = PathOf(versionFolder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, body, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
        return "data/story-sim/latest.json";
    }

    /// <summary>删除上次结果。</summary>
    public static bool DeleteLatest(string versionFolder)
    {
        var path = PathOf(versionFolder);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>读取模拟结果；没有结果或格式损坏时返回 null，并给出中文原因。</summary>
    public static StoryBranchTree? Load(string versionFolder, out string? error)
    {
        error = null;
        var path = PathOf(versionFolder);
        if (!File.Exists(path))
        {
            error = "该版本还没有模拟结果。请先在游戏内导出。";
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                error = "模拟结果文件过大，已跳过。";
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "模拟结果格式无效。";
                return null;
            }

            var format = ReadString(root, "format");
            if (!string.Equals(format, Format, StringComparison.Ordinal))
            {
                error = "模拟结果版本不兼容：" + format;
                return null;
            }

            var all = new List<StoryBranchNode>();
            var byId = new Dictionary<string, StoryBranchNode>(StringComparer.Ordinal);
            if (root.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in nodesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var node = ParseNode(item);
                    if (node.Id.Length == 0) continue;
                    all.Add(node);
                    byId[node.Id] = node;
                }
            }

            if (all.Count == 0)
            {
                error = "模拟结果里没有任何节点。";
                return null;
            }

            foreach (var node in all)
            {
                if (node.ParentId.Length > 0 && byId.TryGetValue(node.ParentId, out var parent))
                {
                    parent.Children.Add(node);
                }
            }

            StoryBranchNode? rootNode = null;
            foreach (var node in all)
            {
                if (node.ParentId.Length == 0)
                {
                    rootNode = node;
                    break;
                }
            }
            rootNode ??= all[0];

            var gameVersion = ReadString(root, "gameVersion");
            var mismatch = gameVersion.Length > 0 &&
                           !Path.GetFileName(versionFolder.TrimEnd('\\', '/'))
                               .Contains(gameVersion, StringComparison.OrdinalIgnoreCase);

            var settings = root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            var depth = ReadInt(settings, "depth");
            var budget = ReadInt(settings, "budget");
            var seedEl = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("globalSeed", out var gs)
                ? gs
                : default;

            return new StoryBranchTree
            {
                RootPassage = ReadString(root, "rootPassage"),
                SaveSlot = ReadString(root, "saveSlot"),
                GameVersion = gameVersion,
                GeneratedAt = ReadString(root, "generatedAt"),
                Depth = depth,
                Budget = budget,
                GlobalSeed = ParseSeed(seedEl),
                Truncated = ReadBool(root, "truncated"),
                StateIntact = !root.TryGetProperty("stateIntact", out var si) || si.ValueKind != JsonValueKind.False,
                VersionMismatch = mismatch,
                VersionWarning = mismatch ? $"结果来自游戏版本 {gameVersion}，当前版本可能不适用。" : "",
                Root = rootNode,
                Nodes = all,
                ModifiedAt = info.LastWriteTime,
            };
        }
        catch (Exception ex)
        {
            error = "读取模拟结果失败：" + ex.Message;
            return null;
        }
    }

    private static StoryBranchNode ParseNode(JsonElement item)
    {
        var status = ReadString(item, "status") switch
        {
            "root" => StoryBranchStatus.Root,
            "failed" => StoryBranchStatus.Failed,
            "skipped" => StoryBranchStatus.Skipped,
            "merged" => StoryBranchStatus.Merged,
            _ => StoryBranchStatus.Success,
        };

        var warnings = new List<string>();
        if (item.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in w.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var text = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) warnings.Add(text!);
                }
            }
        }

        var diff = new List<StoryBranchVarChange>();
        if (item.TryGetProperty("diff", out var d) && d.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in d.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                diff.Add(new StoryBranchVarChange
                {
                    Key = ReadString(entry, "key"),
                    Kind = ReadString(entry, "kind") is { Length: > 0 } kind ? kind : "changed",
                    Before = ReadNullableString(entry, "before"),
                    After = ReadNullableString(entry, "after"),
                });
            }
        }

        var seedEl = item.TryGetProperty("seed", out var se) ? se : default;

        return new StoryBranchNode
        {
            Id = ReadString(item, "id"),
            ParentId = ReadString(item, "parentId"),
            Depth = ReadInt(item, "depth"),
            OptionIndex = ReadInt(item, "optionIndex"),
            OptionText = ReadString(item, "optionText"),
            FromPassage = ReadString(item, "fromPassage"),
            ToPassage = ReadString(item, "toPassage"),
            ResultPassage = ReadString(item, "resultPassage"),
            Status = status,
            Error = ReadString(item, "error"),
            Warnings = warnings,
            Diff = diff,
            DiffOmitted = ReadInt(item, "diffOmitted"),
            Seed = ParseSeed(seedEl),
            RandomCalls = ReadInt(item, "randomCalls"),
            DurationMs = ReadLong(item, "durationMs"),
            CutByDepth = ReadBool(item, "cutByDepth"),
            MergedInto = ReadString(item, "mergedInto"),
        };
    }

    private static StoryBranchSeed ParseSeed(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return new StoryBranchSeed();
        var hash = 0u;
        if (el.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetUInt32(out var parsed))
        {
            hash = parsed;
        }
        var source = ReadString(el, "source");
        return new StoryBranchSeed
        {
            Raw = ReadString(el, "raw"),
            Hash = hash,
            Source = source.Length > 0 ? source : "global",
        };
    }

    private static string ReadString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return "";
        if (!el.TryGetProperty(name, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string? ReadNullableString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int ReadInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        return v.TryGetInt32(out var n) ? n : 0;
    }

    private static long ReadLong(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        return v.TryGetInt64(out var n) ? n : 0;
    }

    private static bool ReadBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(name, out var v)) return false;
        return v.ValueKind == JsonValueKind.True;
    }
}
