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
}

/// <summary>界面上可拖动分栏的尺寸，键是各分割条的 SettingKey。</summary>
public sealed class LayoutSettings
{
    [JsonPropertyName("sizes")] public Dictionary<string, double> Sizes { get; set; } = new();
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
