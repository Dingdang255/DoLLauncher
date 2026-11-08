namespace DoLLauncher.Models;

/// <summary>.dolpack 的 manifest.json。导出/导入双方都靠它识别内容。</summary>
public sealed class PackManifest
{
    public string Format { get; set; } = "dolpack/1";
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? GameVersion { get; set; }
    /// <summary>是否包含游戏本体（Degrees of Lewdity.html）。</summary>
    public bool HasGame { get; set; }
    public List<PackMod> Mods { get; set; } = new();
    public string CreatedAt { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class PackMod
{
    public string File { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
