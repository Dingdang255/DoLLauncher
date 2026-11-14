namespace DoLLauncher.Core.Story;

/// <summary>
/// 需求 12 预留：剧情数据源。
/// 已确认游戏存档（IndexedDB 库 <c>degrees-of-lewdity</c>，表 <c>saves</c> / <c>details</c>）
/// 中存在 <c>data.history[].title</c> 与 <c>variables</c>，可实现「已发生事件路径」的思维导图；
/// 但未选择的选项、角色 / 地点分类、图标映射需要 Mod 或后续映射表补充。
/// 本轮仅定义接口，不新增未验证的界面。
/// </summary>
public interface IStorySource
{
    /// <summary>实现名称，用于设置页或剧情页说明。</summary>
    string Name { get; }

    /// <summary>当前版本是否具备读取条件。</summary>
    bool IsAvailable { get; }

    /// <summary>不可用时的中文原因。</summary>
    string UnavailableReason { get; }

    /// <summary>读取指定版本最近一次存档中的剧情路径；没有数据时返回 null。</summary>
    Task<StoryPath?> LoadLatestAsync(string versionFolder, CancellationToken cancellationToken = default);
}

/// <summary>一条已发生的剧情路径（按访问顺序排列）。</summary>
public sealed class StoryPath
{
    /// <summary>存档槽位标识（saves.slot）。</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>存档显示名称（details 表补全，可能为空）。</summary>
    public string SaveName { get; set; } = string.Empty;

    /// <summary>存档时间（details.date 或 0）。</summary>
    public long SavedAt { get; set; }

    /// <summary>已访问的章节 / 段落标题序列。</summary>
    public List<StoryStep> Steps { get; set; } = new();
}

/// <summary>剧情路径中的一步：SugarCube history 的 title 与当时的 variables 快照。</summary>
public sealed class StoryStep
{
    public string Title { get; set; } = string.Empty;

    /// <summary>该时刻的变量快照；仅保留实现方关心的键，避免整份存档占用内存。</summary>
    public Dictionary<string, string> Flags { get; set; } = new();
}
