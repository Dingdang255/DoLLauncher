namespace DoLLauncher.Core.Story;

/// <summary>模拟节点的状态。</summary>
public enum StoryBranchStatus
{
    /// <summary>根节点（当前段落）。</summary>
    Root,

    /// <summary>试走成功。</summary>
    Success,

    /// <summary>试走失败。</summary>
    Failed,

    /// <summary>与剧情无关、模拟时跳过的界面分支。</summary>
    Skipped,

    /// <summary>与同层其它节点状态相同，已合并。</summary>
    Merged,
}

/// <summary>节点实际使用的随机种子。</summary>
public sealed class StoryBranchSeed
{
    /// <summary>用户输入的原始种子文本（纯数字）。</summary>
    public string Raw { get; init; } = "";

    /// <summary>种子的 32 位无符号数值。</summary>
    public uint Hash { get; init; }

    /// <summary>种子来源：<c>global</c> 或 <c>node</c>。</summary>
    public string Source { get; init; } = "global";

    /// <summary>是否为节点级覆盖。</summary>
    public bool IsNodeOverride => string.Equals(Source, "node", StringComparison.OrdinalIgnoreCase);
}

/// <summary>单个变量的前后变化。</summary>
public sealed class StoryBranchVarChange
{
    /// <summary>变量名。</summary>
    public string Key { get; init; } = "";

    /// <summary>变化类型：<c>added</c> / <c>removed</c> / <c>changed</c>。</summary>
    public string Kind { get; init; } = "changed";

    /// <summary>变化前的值（JSON 文本）；新增时为 null。</summary>
    public string? Before { get; init; }

    /// <summary>变化后的值（JSON 文本）；删除时为 null。</summary>
    public string? After { get; init; }
}

/// <summary>模拟树中的一个节点。</summary>
public sealed class StoryBranchNode
{
    /// <summary>节点标识。</summary>
    public string Id { get; init; } = "";

    /// <summary>父节点标识；根节点为空。</summary>
    public string ParentId { get; init; } = "";

    /// <summary>深度，根节点为 0。</summary>
    public int Depth { get; init; }

    /// <summary>选项在父节点中的序号。</summary>
    public int OptionIndex { get; init; }

    /// <summary>选项文本。</summary>
    public string OptionText { get; init; } = "";

    /// <summary>出发段落。</summary>
    public string FromPassage { get; init; } = "";

    /// <summary>选项直接指向的段落。</summary>
    public string ToPassage { get; init; } = "";

    /// <summary>实际到达的段落（可能经过重定向）。</summary>
    public string ResultPassage { get; init; } = "";

    /// <summary>节点状态。</summary>
    public StoryBranchStatus Status { get; init; }

    /// <summary>失败原因。</summary>
    public string Error { get; init; } = "";

    /// <summary>副作用告警。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>变量差异。</summary>
    public IReadOnlyList<StoryBranchVarChange> Diff { get; init; } = Array.Empty<StoryBranchVarChange>();

    /// <summary>未能列出的变量差异数量。</summary>
    public int DiffOmitted { get; init; }

    /// <summary>实际使用的种子。</summary>
    public StoryBranchSeed Seed { get; init; } = new();

    /// <summary>本次试走使用随机数的次数。</summary>
    public int RandomCalls { get; init; }

    /// <summary>试走耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>是否因深度上限被截断。</summary>
    public bool CutByDepth { get; init; }

    /// <summary>被合并到的节点标识。</summary>
    public string MergedInto { get; init; } = "";

    /// <summary>子节点。</summary>
    public List<StoryBranchNode> Children { get; } = new();

    /// <summary>界面展示用标题。</summary>
    public string DisplayTitle => OptionText.Length > 0 ? OptionText : "(未命名选项)";
}

/// <summary>一份完整的模拟结果。</summary>
public sealed class StoryBranchTree
{
    /// <summary>模拟起点段落。</summary>
    public string RootPassage { get; init; } = "";

    /// <summary>起始存档槽（为空表示从当前状态开始）。</summary>
    public string SaveSlot { get; init; } = "";

    /// <summary>生成结果时的游戏版本。</summary>
    public string GameVersion { get; init; } = "";

    /// <summary>生成时间（ISO 8601）。</summary>
    public string GeneratedAt { get; init; } = "";

    /// <summary>模拟深度设置。</summary>
    public int Depth { get; init; }

    /// <summary>节点预算设置。</summary>
    public int Budget { get; init; }

    /// <summary>全局默认种子。</summary>
    public StoryBranchSeed GlobalSeed { get; init; } = new();

    /// <summary>是否因预算截断。</summary>
    public bool Truncated { get; init; }

    /// <summary>模拟结束后玩家状态是否与开始前一致。</summary>
    public bool StateIntact { get; init; } = true;

    /// <summary>结果是否来自其它游戏版本。</summary>
    public bool VersionMismatch { get; init; }

    /// <summary>版本不一致时的提示。</summary>
    public string VersionWarning { get; init; } = "";

    /// <summary>根节点。</summary>
    public StoryBranchNode? Root { get; init; }

    /// <summary>全部节点（含根）。</summary>
    public IReadOnlyList<StoryBranchNode> Nodes { get; init; } = Array.Empty<StoryBranchNode>();

    /// <summary>结果文件最后写入时间。</summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>统计各状态节点数量。</summary>
    public (int Success, int Failed, int Merged, int Skipped) CountByStatus()
    {
        var success = 0;
        var failed = 0;
        var merged = 0;
        var skipped = 0;
        foreach (var node in Nodes)
        {
            switch (node.Status)
            {
                case StoryBranchStatus.Failed:
                    failed++;
                    break;
                case StoryBranchStatus.Merged:
                    merged++;
                    break;
                case StoryBranchStatus.Skipped:
                    skipped++;
                    break;
                default:
                    success++;
                    break;
            }
        }
        return (success, failed, merged, skipped);
    }
}
