using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace DoLLauncher.Models;

/// <summary>GitHub Release 里的一个上传文件。</summary>
public sealed class GameAsset
{
    public string Name { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }

    public string SizeText => FormatSize(Size);

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "";
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F1} MB";
    }
}

/// <summary>汉化仓库的一个 Release。</summary>
public sealed class GameRelease
{
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime? PublishedAt { get; set; }
    public bool Prerelease { get; set; }
    public List<GameAsset> Assets { get; set; } = new();

    public GameAsset? StandardAsset => Find("DoL-ModLoader-", polyfill: false);
    public GameAsset? PolyfillAsset => Find("DoL-ModLoader-", polyfill: true);
    public GameAsset? I18nAsset => Assets.FirstOrDefault(a => a.Name.Contains("ModI18N", StringComparison.OrdinalIgnoreCase));
    public GameAsset? ImagePackAsset => Assets.FirstOrDefault(a => a.Name.Contains("GameOriginalImagePack", StringComparison.OrdinalIgnoreCase));

    public string Title => Tag.TrimStart('v', 'V');
    public string DateText => PublishedAt?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
    public string Summary => $"{DateText}  ·  {Assets.Count} 个文件" + (Prerelease ? "  ·  预览版" : "");
    public string BadgeText => Prerelease ? "预览版" : "";

    public string AssetSummary => string.Join(" · ", new[]
    {
        StandardAsset != null ? "主体 zip" : "",
        PolyfillAsset != null ? "polyfill 可用" : "",
        I18nAsset != null ? "含 ModI18N" : "",
        ImagePackAsset != null ? "含图片包" : "",
    }.Where(s => s.Length > 0));

    public GameAsset? PickAsset(bool polyfill)
    {
        return polyfill ? PolyfillAsset ?? StandardAsset : StandardAsset;
    }

    private GameAsset? Find(string prefix, bool polyfill)
    {
        return Assets.FirstOrDefault(a =>
        {
            if (!a.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var isPolyfill = a.Name.Contains("polyfill", StringComparison.OrdinalIgnoreCase);
            return polyfill ? isPolyfill : !isPolyfill;
        });
    }
}

public sealed class MarketDependency
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>索引里记录的一个已验证资产（只给文件名、地址与版本）。</summary>
public sealed class MarketAssetRef
{
    public string FileName { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>同一模组的不同包型 / 包体系列（例如 RAY 的完整版与轻量版、DoLP 版）。</summary>
public sealed class MarketEdition
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string BootName { get; set; } = "";
    public string AssetSeries { get; set; } = "";
    public List<MarketAssetRef> VerifiedAssets { get; set; } = new();

    public string Display => Label.Length > 0 ? Label : (AssetSeries.Length > 0 ? AssetSeries : Id);
}

/// <summary>ModHub 市场索引里的一个模组条目。</summary>
public sealed class MarketMod : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Category { get; set; } = "";
    public string Tags { get; set; } = "";

    /// <summary>索引里的标签原始列表，供标签匹配使用；为空时回退到 <see cref="Tags"/> 字符串。</summary>
    public List<string> TagList { get; set; } = new();
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Repo { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string UpdateDate { get; set; } = "";
    public bool Prerelease { get; set; }
    public List<string> BootNames { get; set; } = new();
    public List<string> Aliases { get; set; } = new();
    public List<MarketDependency> Dependencies { get; set; } = new();

    /// <summary>索引推荐的资产文件名；空串表示没有验证过的资产。</summary>
    public string VerifiedAssetFileName { get; set; } = "";

    /// <summary>可选的包型 / 包体系列；只有多项时才需要在界面上让用户选择。</summary>
    public List<MarketEdition> Editions { get; set; } = new();

    /// <summary>目标版本里已安装的版本号；空串表示未安装。</summary>
    public string InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (_installedVersion == value) return;
            _installedVersion = value;
            OnPropertyChanged(nameof(InstalledVersion));
            OnPropertyChanged(nameof(Installed));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    private string _installedVersion = "";
    private bool _updateAvailable;

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set
        {
            if (_updateAvailable == value) return;
            _updateAvailable = value;
            OnPropertyChanged(nameof(UpdateAvailable));
            OnPropertyChanged(nameof(BadgeText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool Installed => InstalledVersion.Length > 0;

    public string Title => Version.Length > 0 ? $"{Name}  {Version}" : Name;
    public string Subtitle => string.Join("  ·  ", new[] { Category, Author, UpdateDate }.Where(s => s.Length > 0));

    public string BadgeText => !Installed ? "" : UpdateAvailable ? "可更新 → " + Version : "已安装 " + InstalledVersion;

    public string SearchText =>
        (Name + " " + Id + " " + Category + " " + Tags + " " + Author + " " + Description +
         " " + string.Join(" ", BootNames)).ToLowerInvariant();
}

/// <summary>ModHub 市场索引。</summary>
public sealed class MarketCatalog
{
    public List<MarketMod> Mods { get; } = new();
    public List<string> Categories { get; } = new();
    public string GeneratedAt { get; set; } = "";
}

/// <summary>
/// 两个已启用模组之间由简介 / README 声明的「可能冲突」。
/// 只做警告，不阻止安装；停用任意一方或标记忽略后消失。
/// </summary>
public sealed class ModConflict
{
    /// <summary>稳定键：双方市场 id 排序后以 | 连接，用于按版本记录忽略。</summary>
    public string PairKey { get; set; } = "";

    /// <summary>声明冲突的一方。</summary>
    public string LeftName { get; set; } = "";
    public string LeftFile { get; set; } = "";

    /// <summary>被声明冲突的一方。</summary>
    public string RightName { get; set; } = "";
    public string RightFile { get; set; } = "";

    /// <summary>声明所在的原文句子。</summary>
    public string Reason { get; set; } = "";

    /// <summary>声明来源：index（市场简介）/ readme。</summary>
    public string Source { get; set; } = "index";
    public string SourceText => Source == "readme" ? "README" : "市场简介";

    /// <summary>用户已确认忽略；用户界面里不再显示为未解决。</summary>
    public bool Ignored { get; set; }

    public string Title => $"「{LeftName}」与「{RightName}」可能存在冲突";
    public string Detail => $"{SourceText}里写明：{Reason}";
    public string LeftDisableText => $"停用「{LeftName}」";
    public string RightDisableText => $"停用「{RightName}」";
}

/// <summary>问题严重程度：橙色只作提醒，红色表示很可能影响游戏运行。</summary>
public enum ProblemSeverity
{
    Orange,
    Red,
}

/// <summary>已启用模组缺少前置（未安装 / 未启用 / 版本不满足）。</summary>
public sealed class ModMissingDependency
{
    public string ModFile { get; set; } = "";
    public string ModName { get; set; } = "";
    public string DependencyName { get; set; } = "";
    public string RequiredVersion { get; set; } = "";

    /// <summary>本地已找到的前置版本；为空表示完全没装。</summary>
    public string LocalVersion { get; set; } = "";

    /// <summary>前置已安装但处于停用状态。</summary>
    public bool Disabled { get; set; }

    public string Title => LocalVersion.Length == 0
        ? $"「{ModName}」缺少前置「{DependencyName}」"
        : Disabled
            ? $"「{ModName}」的前置「{DependencyName}」未启用"
            : $"「{ModName}」的前置「{DependencyName}」版本不满足";

    public string Detail => LocalVersion.Length == 0
        ? (RequiredVersion.Length > 0
            ? $"需要 {DependencyName} {RequiredVersion}，本地没有安装。可以到下载中心补装。"
            : $"需要 {DependencyName}，本地没有安装。可以到下载中心补装。")
        : Disabled
            ? $"前置已安装（{LocalVersion}），但当前是停用状态，需要重新启用。"
            : $"前置已安装（{LocalVersion}），但不满足版本要求 {RequiredVersion}。";

    public string DisableText => $"停用「{ModName}」";

    /// <summary>前置已安装但被停用时，可以直接重新启用。</summary>
    public string EnableText => $"启用前置「{DependencyName}」";
}

/// <summary>
/// 模组与当前游戏版本不匹配：一种来自 boot.json 声明的目标版本差异，
/// 另一种是运行日志里实际出现的补丁失败（Runtime = true）。
/// </summary>
public sealed class ModVersionMismatch
{
    public string ModFile { get; set; } = "";
    public string ModName { get; set; } = "";
    public string TargetGameVersion { get; set; } = "";
    public string CurrentGameVersion { get; set; } = "";
    public int Gap { get; set; }
    public ProblemSeverity Severity { get; set; } = ProblemSeverity.Orange;

    /// <summary>是否由游戏运行日志推断出来（而不是 boot.json 声明）。</summary>
    public bool Runtime { get; set; }

    /// <summary>运行日志问题的简短原因，用于模组列表行提示。</summary>
    public string RuntimeReason { get; set; } = "";

    public string RuntimeTitle { get; set; } = "";
    public string RuntimeDetail { get; set; } = "";

    public string Title => Runtime && RuntimeTitle.Length > 0
        ? RuntimeTitle
        : $"「{ModName}」目标游戏版本 {TargetGameVersion}，当前 {CurrentGameVersion}（相差 {Gap}）";

    public string Detail => Runtime && RuntimeDetail.Length > 0
        ? RuntimeDetail
        : Severity == ProblemSeverity.Red
            ? "版本差距较大，脚本或资源接口可能已经变化，游戏很可能无法正常运行。"
            : "版本存在差异，部分脚本或资源可能无法正常生效，建议留意运行情况。";

    /// <summary>日志里只认出名字、本地找不到对应模组时没有可停用的对象。</summary>
    public bool CanDisable => ModFile.Length > 0;

    public string DisableText => $"停用「{ModName}」";
}

/// <summary>同名模组组里的一个文件。</summary>
public sealed class ModDuplicateMember
{
    public string FileName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>组内版本最新的一个（只在这个成员上为 true）。</summary>
    public bool IsNewest { get; set; }

    public string Title => Version.Length > 0 ? $"「{Name}」 {Version}" : $"「{Name}」";

    public string BadgeText => IsNewest ? "最新" : "";

    public string DisableText => "停用此文件";
}

/// <summary>同时启用的多个同名模组（忽略大小写与符号后同名）。</summary>
public sealed class ModDuplicate
{
    public string Name { get; set; } = "";
    public string NewestFileName { get; set; } = "";
    public List<ModDuplicateMember> Members { get; set; } = new();

    public string Title => $"「{Name}」有 {Members.Count} 个同名模组同时启用";

    public string Detail =>
        "同名模组会被 ModLoader 重复加载，脚本和事件可能被覆盖或触发多次，游戏可能无法正常运行。建议只保留一个。";

    public string KeepNewestText => "保留最新一个，停用其余";
}

/// <summary>安装检查里一条需要补装的前置。</summary>
public sealed class InstallDependency
{
    public MarketMod Mod { get; set; } = new();

    /// <summary>索引 / boot.json 里要求的版本；README 识别出来的没有版本要求。</summary>
    public string RequiredVersion { get; set; } = "";
    public string InstalledVersion { get; set; } = "";

    /// <summary>是否来自 README 文本识别，而不是索引里的 dependencies 字段。</summary>
    public bool FromReadme { get; set; }

    /// <summary>默认勾选，用户可以在对话框里取消。</summary>
    public bool Selected { get; set; } = true;

    public string Title => Mod.Version.Length > 0 ? $"{Mod.Name}  {Mod.Version}" : Mod.Name;

    public string SourceText => FromReadme ? "README" : "索引";

    public string Detail
    {
        get
        {
            var requirement = RequiredVersion.Length > 0 ? $"要求 {RequiredVersion}" : "无版本要求";
            return InstalledVersion.Length == 0
                ? $"本地未安装 · {requirement} → 将安装"
                : $"本地 {InstalledVersion} · {requirement} → 将更新";
        }
    }
}

/// <summary>安装前检查发现的本地冲突。</summary>
public sealed class InstallConflict
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>一次安装前检查的结果。</summary>
public sealed class InstallCheckResult
{
    /// <summary>需要补装的前置，按「先前置后本体」的顺序排列。</summary>
    public List<InstallDependency> Dependencies { get; } = new();

    /// <summary>已经满足、不需要补装的前置说明。</summary>
    public List<string> Satisfied { get; } = new();

    /// <summary>与本地已装模组的冲突。</summary>
    public List<InstallConflict> Conflicts { get; } = new();

    /// <summary>游戏版本、市场缺前置之类的提示。</summary>
    public List<string> Notes { get; } = new();

    public bool HasFindings => Dependencies.Count > 0 || Conflicts.Count > 0 || Notes.Count > 0;
}
