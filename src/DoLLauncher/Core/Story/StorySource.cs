namespace DoLLauncher.Core.Story;

/// <summary>剧情节点的分类，用于思维导图分组与筛选。</summary>
public enum StoryCategory
{
    /// <summary>角色。</summary>
    Character,

    /// <summary>地点。</summary>
    Place,

    /// <summary>事件。</summary>
    Event,

    /// <summary>未分类。</summary>
    Unknown,
}

/// <summary>存档文件的来源类型。</summary>
public enum StorySourceKind
{
    /// <summary>版本目录下的磁盘镜像 <c>data\idb-mirror.json</c>。</summary>
    Mirror,

    /// <summary>备份目录中的存档导出文件。</summary>
    Backup,
}

/// <summary>剧情数据所依赖的存档文件信息。</summary>
public sealed class StorySourceInfo
{
    /// <summary>文件的完整路径。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>来源类型。</summary>
    public StorySourceKind Kind { get; init; }

    /// <summary>所属版本目录。</summary>
    public string VersionFolder { get; init; } = "";

    /// <summary>文件字节长度。</summary>
    public long Length { get; init; }

    /// <summary>文件最后写入时间。</summary>
    public DateTime ModifiedAt { get; init; }

    /// <summary>用于界面展示的简短说明，例如「磁盘镜像」「备份：saves-xxx.json」。</summary>
    public string DisplayNote { get; init; } = "";
}

/// <summary>存档库中的一个槽位摘要。</summary>
public sealed class StorySlotInfo
{
    /// <summary>槽位标识（统一为字符串）。</summary>
    public string SlotId { get; init; } = "";

    /// <summary>存档显示名称；缺失时为「存档 &lt;slot&gt;」。</summary>
    public string SaveName { get; init; } = "";

    /// <summary>存档标题（details.title），可能为空。</summary>
    public string Title { get; init; } = "";

    /// <summary>存档时间（details.date 毫秒时间戳），缺失为 0。</summary>
    public long SavedAt { get; init; }
}

/// <summary>一个存档文件解析出的槽位列表。</summary>
public sealed class StoryArchive
{
    /// <summary>来源文件信息。</summary>
    public StorySourceInfo Source { get; init; } = new();

    /// <summary>槽位列表，按槽位标识排序。</summary>
    public IReadOnlyList<StorySlotInfo> Slots { get; init; } = Array.Empty<StorySlotInfo>();
}

/// <summary>
/// 剧情分支预留信息。当前存档中没有「未选择的选项」，需后续 Mod 提供，
/// 本轮仅定义结构，不产出真实数据。
/// </summary>
public sealed class StoryChoice
{
    /// <summary>选项文本。</summary>
    public string Text { get; init; } = "";

    /// <summary>选项指向的段落标题。</summary>
    public string TargetTitle { get; init; } = "";

    /// <summary>玩家是否选择了该分支。</summary>
    public bool Taken { get; init; }
}

/// <summary>剧情路径中的一个节点（相邻同名步骤已合并）。</summary>
public sealed class StoryStep
{
    /// <summary>节点序号；合并节点取首个原始步序号。</summary>
    public int Index { get; set; }

    /// <summary>段落标题（存档中的 moment.title）。</summary>
    public string Title { get; set; } = "";

    /// <summary>该步的时间戳（variables.timeStamp），缺失为 0。</summary>
    public long Timestamp { get; set; }

    /// <summary>该步的游戏内起始日期（variables.startDate），缺失为 0。</summary>
    public long StartDate { get; set; }

    /// <summary>分类。</summary>
    public StoryCategory Category { get; set; }

    /// <summary>分类下的分组名（如「卧室」「Robin」）。</summary>
    public string GroupName { get; set; } = "";

    /// <summary>相邻同名合并后的步数，未合并为 1。</summary>
    public int RepeatCount { get; set; } = 1;

    /// <summary>被合并的原始步序号列表。</summary>
    public IReadOnlyList<int> StepIndexes { get; set; } = Array.Empty<int>();

    /// <summary>可选项（预留，本轮为空）。</summary>
    public List<StoryChoice> Choices { get; set; } = new();

    /// <summary>该 moment 在文件中的字节偏移；<c>delta</c> 存档无意义，为 0。</summary>
    internal long ContentOffset { get; set; }
}

/// <summary>一个槽位解析出的完整剧情路径。</summary>
public sealed class StoryPath
{
    /// <summary>槽位标识。</summary>
    public string SlotId { get; set; } = "";

    /// <summary>存档显示名称。</summary>
    public string SaveName { get; set; } = "";

    /// <summary>存档时间（details.date），缺失为 0。</summary>
    public long SavedAt { get; set; }

    /// <summary>来源说明，例如「磁盘镜像」「备份：saves-xxx.json」。</summary>
    public string SourceNote { get; set; } = "";

    /// <summary>是否为增量（delta）存档。</summary>
    public bool IsDelta { get; set; }

    /// <summary>按访问顺序排列的剧情节点。</summary>
    public List<StoryStep> Steps { get; set; } = new();
}

/// <summary>
/// 剧情数据源接口。已确认存档（IndexedDB 库 <c>degrees-of-lewdity</c>，表 <c>saves</c> / <c>details</c>）
/// 中存在 <c>data.history[].title</c> 与 <c>variables</c>，可还原「已发生事件路径」；
/// 未选择的选项与图标映射需 Mod 或后续映射表补充。
/// </summary>
public interface IStorySource
{
    /// <summary>实现名称，用于剧情页说明。</summary>
    string Name { get; }

    /// <summary>当前版本是否具备读取条件。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的中文原因。</summary>
    string UnavailableReason { get; }

    /// <summary>读取指定版本最近一次存档中的剧情路径；没有数据时返回 null。</summary>
    Task<StoryPath?> LoadLatestAsync(string versionFolder, CancellationToken cancellationToken = default);
}

/// <summary>基于存档镜像 / 备份文件的剧情数据源。</summary>
public sealed class MirrorStorySource : IStorySource
{
    private readonly string _versionDir;
    private readonly StorySourceInfo? _source;

    /// <summary>用版本目录创建数据源，构造时即定位一次存档文件。</summary>
    /// <param name="versionDir">版本目录的完整路径（<c>VersionInfo.FullPath</c>）；省略时需在读取时传入。</param>
    public MirrorStorySource(string versionDir = "")
    {
        _versionDir = versionDir ?? "";
        _source = string.IsNullOrWhiteSpace(_versionDir) ? null : SaveMirrorReader.FindSource(_versionDir);
    }

    /// <inheritdoc />
    public string Name => "存档镜像";

    /// <inheritdoc />
    public bool IsAvailable => _source != null;

    /// <inheritdoc />
    public string UnavailableReason => _source != null
        ? ""
        : "没有找到该版本的存档文件（磁盘镜像或备份导出）";

    /// <inheritdoc />
    public Task<StoryPath?> LoadLatestAsync(string versionFolder, CancellationToken cancellationToken = default)
    {
        var folder = string.IsNullOrWhiteSpace(versionFolder) ? _versionDir : versionFolder;
        return Task.Run<StoryPath?>(() => LoadLatest(folder, cancellationToken), cancellationToken);
    }

    private static StoryPath? LoadLatest(string versionFolder, CancellationToken ct)
    {
        var source = SaveMirrorReader.FindSource(versionFolder);
        if (source == null) return null;

        var archive = SaveMirrorReader.LoadArchive(source, ct, out _);
        if (archive == null || archive.Slots.Count == 0) return null;

        StorySlotInfo? latest = null;
        foreach (var slot in archive.Slots)
        {
            if (latest == null || slot.SavedAt > latest.SavedAt) latest = slot;
        }

        if (latest == null) return null;
        var path = SaveMirrorReader.LoadSlot(archive, latest.SlotId, ct, out _);
        if (path != null) path.SourceNote = archive.Source.DisplayNote;
        return path;
    }
}
