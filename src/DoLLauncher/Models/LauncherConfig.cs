using System.Text.Json.Serialization;

namespace DoLLauncher.Models;

public sealed class VersionEntry
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = string.Empty;
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "http";

    /// <summary>用户已确认「知晓风险、忽略」的冲突对键（双方市场 id 排序后以 | 连接）。</summary>
    [JsonPropertyName("ignoredConflicts")] public List<string> IgnoredConflicts { get; set; } = new();
}

public sealed class BackupSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("saveAuto")] public bool SaveAuto { get; set; }
    [JsonPropertyName("keep")] public int Keep { get; set; } = 5;
    [JsonPropertyName("intervalHours")] public int IntervalHours { get; set; } = 6;
}

public sealed class GeneralSettings
{
    /// <summary>从市场索引补全作者/主页后，把它们写回模组包里的 boot.json。</summary>
    [JsonPropertyName("writeModMetadata")] public bool WriteModMetadata { get; set; } = true;

    /// <summary>启动时预取索引的顺序：parallel / modFirst / gameFirst / none。</summary>
    [JsonPropertyName("prefetch")] public string Prefetch { get; set; } = "parallel";

    /// <summary>旧版 file:// 数据固定打开哪个版本的 html（空表示跟随最新版本）。</summary>
    [JsonPropertyName("legacyFolder")] public string LegacyFolder { get; set; } = string.Empty;

    /// <summary>模组市场索引来源：auto / primary / backup。</summary>
    [JsonPropertyName("marketSource")] public string MarketSource { get; set; } = "auto";

    /// <summary>模组包下载源：auto / ghproxy / llkk / fastgit / ddlc / boki / worker / github。</summary>
    [JsonPropertyName("modDownloadSource")] public string ModDownloadSource { get; set; } = "auto";

    /// <summary>GitHub 接口（版本列表、README 等）访问线路：auto / github / ghproxy。</summary>
    [JsonPropertyName("githubApiSource")] public string GithubApiSource { get; set; } = "auto";

    /// <summary>游戏版本告警阈值：第三段（z）相差达到该值即提示，等于阈值橙色、超过阈值红色。</summary>
    [JsonPropertyName("versionGapThreshold")] public int VersionGapThreshold { get; set; } = 1;

    /// <summary>界面主题：system（跟随系统）/ light（亮色）/ dark（深色）。</summary>
    [JsonPropertyName("themeMode")] public string ThemeMode { get; set; } = "system";

    /// <summary>是否启用界面、按钮、列表与滚动过渡动画。</summary>
    [JsonPropertyName("showAnimations")] public bool ShowAnimations { get; set; } = true;

    /// <summary>用户自定义主色（#AARRGGBB）；为空时使用内置默认主题色。</summary>
    [JsonPropertyName("themeAccentColor")] public string ThemeAccentColor { get; set; } = string.Empty;

    /// <summary>主色饱和度（0-100）：100 为程序推荐的鲜艳基准，0 为灰色。</summary>
    [JsonPropertyName("themeAccentSaturation")] public int ThemeAccentSaturation { get; set; } = 100;

    /// <summary>页面心跳间隔（秒）。</summary>
    [JsonPropertyName("heartbeatSeconds")] public int HeartbeatSeconds { get; set; } = 3;

    /// <summary>页面存在时允许的最大心跳间隔（秒），超过即认为页面已关闭。</summary>
    [JsonPropertyName("heartbeatTimeoutSeconds")] public int HeartbeatTimeoutSeconds { get; set; } = 10;

    /// <summary>页面首次心跳前的宽限时间（秒），用于加载游戏本体。</summary>
    [JsonPropertyName("firstHeartbeatGraceSeconds")] public int FirstHeartbeatGraceSeconds { get; set; } = 30;

    /// <summary>页面主动上报关闭后的防抖时间（秒），期间收到新心跳则视为刷新而非关闭。</summary>
    [JsonPropertyName("closeDebounceSeconds")] public int CloseDebounceSeconds { get; set; } = 3;

    /// <summary>游戏控制台日志（版本目录 logs\game-*.log）最多保留的份数。</summary>
    [JsonPropertyName("gameLogKeep")] public int GameLogKeep { get; set; } = 20;

    /// <summary>单份游戏日志上限（MB），0 表示不限制。</summary>
    [JsonPropertyName("gameLogMaxMb")] public int GameLogMaxMb { get; set; } = 30;

    /// <summary>单份日志达到上限后的处理：newfile（写 -2、-3… 新文件）/ stop（停止写入）。</summary>
    [JsonPropertyName("gameLogOverflow")] public string GameLogOverflow { get; set; } = "newfile";

    /// <summary>页面日志上报队列上限（KB），超出后丢弃普通级别日志。</summary>
    [JsonPropertyName("logQueueKb")] public int LogQueueKb { get; set; } = 4;

    /// <summary>页面日志上报的合并间隔（毫秒）。</summary>
    [JsonPropertyName("logFlushMs")] public int LogFlushMs { get; set; } = 1000;

    /// <summary>游戏浏览器承载方式：profile（独立 Profile）/ default（系统默认）/ custom（自定义）/ webview2。</summary>
    [JsonPropertyName("browserMode")] public string BrowserMode { get; set; } = "profile";

    /// <summary>自定义浏览器的可执行文件路径（browserMode = custom 时生效）。</summary>
    [JsonPropertyName("browserExecutable")] public string BrowserExecutable { get; set; } = string.Empty;

    /// <summary>自定义浏览器的启动参数，可用 {url} 占位。</summary>
    [JsonPropertyName("browserArguments")] public string BrowserArguments { get; set; } = string.Empty;

    /// <summary>自定义浏览器是否使用独立 Profile。</summary>
    [JsonPropertyName("browserUseProfile")] public bool BrowserUseProfile { get; set; } = true;

    /// <summary>独立 Profile 根目录；留空时使用启动器目录下的 profiles。</summary>
    [JsonPropertyName("browserProfileRoot")] public string BrowserProfileRoot { get; set; } = string.Empty;

    /// <summary>关闭游戏页面后的处理：close（直接停服）/ ask（询问）。</summary>
    [JsonPropertyName("closePageBehavior")] public string ClosePageBehavior { get; set; } = "close";

    /// <summary>左上角启动器名称下方的副标题；空字符串表示使用默认文案「版本 · 模组 · 存档」。</summary>
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = string.Empty;

    /// <summary>自动调整布局：开启后各分区按设计比例随窗口等比缩放；关闭时沿用拖动出的像素尺寸。</summary>
    [JsonPropertyName("autoLayout")] public bool AutoLayout { get; set; }

    /// <summary>模组市场高级搜索：匹配模式（contains / word / anytag / alltags）。</summary>
    [JsonPropertyName("marketMatch")] public string MarketMatchMode { get; set; } = "contains";

    /// <summary>模组市场高级搜索：范围（all / title / author / description）。</summary>
    [JsonPropertyName("marketScope")] public string MarketSearchScope { get; set; } = "all";

    /// <summary>模组市场排序方式（default / updated / name / stars）。</summary>
    [JsonPropertyName("marketSort")] public string MarketSortMode { get; set; } = "default";

    /// <summary>模组市场排序是否倒序；默认正序（配合默认排序保持 mods.json 原始顺序）。</summary>
    [JsonPropertyName("marketSortDesc")] public bool MarketSortDesc { get; set; } = false;
}

/// <summary>界面上可拖动分栏的尺寸，键是各分割条的 SettingKey。</summary>
public sealed class LayoutSettings
{
    [JsonPropertyName("sizes")] public Dictionary<string, double> Sizes { get; set; } = new();

    /// <summary>自动布局使用的比例（0-1），与 Sizes 分开存，切换开关时互不覆盖。</summary>
    [JsonPropertyName("ratios")] public Dictionary<string, double> Ratios { get; set; } = new();
}

public sealed class LauncherConfig
{
    [JsonPropertyName("versions")] public List<VersionEntry> Versions { get; set; } = new();
    [JsonPropertyName("backup")] public BackupSettings Backup { get; set; } = new();
    [JsonPropertyName("general")] public GeneralSettings General { get; set; } = new();
    [JsonPropertyName("layout")] public LayoutSettings Layout { get; set; } = new();
}

public sealed class BackupEntry
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Time { get; set; }

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
    public string SizeText => Size <= 0 ? "" : Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:F1} MB"
        : $"{Size / 1024.0:F0} KB";

    public bool IsSnapshot => FileName.EndsWith(".dolpack", StringComparison.OrdinalIgnoreCase);
    public bool IsModsConfig => FileName.StartsWith("mods-config-", StringComparison.OrdinalIgnoreCase);
    public bool IsSaves => FileName.StartsWith("saves-", StringComparison.OrdinalIgnoreCase) ||
                           FileName.StartsWith("auto-saves-", StringComparison.OrdinalIgnoreCase);
}
