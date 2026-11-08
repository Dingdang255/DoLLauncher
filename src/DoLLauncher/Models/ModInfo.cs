namespace DoLLauncher.Models;

public sealed class ModDependency
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class ModInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public List<ModDependency> Dependencies { get; set; } = new();

    /// <summary>boot.json 能否正常解析；为 false 时说明这个 zip 不是合法模组。</summary>
    public bool BootParsed { get; set; }

    public string Title => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name}  {Version}";
}

/// <summary>模组详情（从 zip 里的 boot.json 与 README 读取）。</summary>
public sealed class ModDetail
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Homepage { get; set; } = string.Empty;
    public string Readme { get; set; } = string.Empty;
    public bool BootParsed { get; set; }
    public List<ModDependency> Dependencies { get; set; } = new();
}
