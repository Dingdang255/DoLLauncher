using System.ComponentModel;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

/// <summary>版本卡片的状态，由界面层决定具体配色。</summary>
public enum VersionStatus
{
    /// <summary>未启动。</summary>
    Idle,

    /// <summary>游戏本体正在运行。</summary>
    Running,

    /// <summary>只有存档助手在运行。</summary>
    Helper,

    /// <summary>正在做剧情分支模拟。</summary>
    Simulating,

    /// <summary>file:// 旧数据，没有可启动的本体。</summary>
    Legacy,
}

public sealed class VersionVm : INotifyPropertyChanged
{
    public VersionInfo Info { get; }

    public VersionVm(VersionInfo info) => Info = info;

    public string DisplayName => Info.DisplayName;

    /// <summary>版本使用的 ModLoader 类型：Neo 单文件源安装的是 Neo，其余按 JML 计。</summary>
    public string LoaderType => Info.IsFileMode
        ? ""
        : Info.Folder.StartsWith("Neo-", StringComparison.OrdinalIgnoreCase) ? "Neo" : "JML";

    /// <summary>下拉框里用的短名称，避免在窄列里被截断。</summary>
    public string ComboName => Info.IsFileMode
        ? "旧数据（file://）"
        : Info.GameVersion != null
            ? $"DoL {Info.GameVersion} · {LoaderType} · {Info.Port}"
            : $"{Info.DisplayName} · {LoaderType} · {Info.Port}";

    public string Subtitle => Info.IsFileMode
        ? "file:// 模式 · 与所有本地网页共享浏览器数据"
        : $"端口 {Info.Port} · {Info.GameVersion ?? "未知版本"}";

    public string StatusText
    {
        get
        {
            if (Info.IsFileMode) return "旧数据";
            if (SimulationTracker.IsSimulating(Info.Folder)) return "模拟中";
            if (!ServerManager.IsRunning(Info.Folder)) return "未启动";
            return ServerManager.IsHelperOnly(Info.Folder) ? "存档助手" : "运行中";
        }
    }

    public bool IsRunning => !Info.IsFileMode && ServerManager.IsRunning(Info.Folder);

    public VersionStatus Status
    {
        get
        {
            if (Info.IsFileMode) return VersionStatus.Legacy;
            if (SimulationTracker.IsSimulating(Info.Folder)) return VersionStatus.Simulating;
            if (!ServerManager.IsRunning(Info.Folder)) return VersionStatus.Idle;
            return ServerManager.IsHelperOnly(Info.Folder) ? VersionStatus.Helper : VersionStatus.Running;
        }
    }

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ModVm : INotifyPropertyChanged
{
    private string _issueText = string.Empty;
    private string _conflictText = string.Empty;

    public ModInfo Info { get; }

    public ModVm(ModInfo info) => Info = info;

    /// <summary>重复、缺少前置等提示；空串表示没有发现问题。</summary>
    public string IssueText
    {
        get => _issueText;
        set
        {
            if (_issueText == value) return;
            _issueText = value;
            Raise(nameof(IssueText));
        }
    }

    /// <summary>简介 / README 里声明的「可能与某某冲突」；空串表示没有冲突。</summary>
    public string ConflictText
    {
        get => _conflictText;
        set
        {
            if (_conflictText == value) return;
            _conflictText = value;
            Raise(nameof(ConflictText));
        }
    }

    public bool Enabled
    {
        get => Info.Enabled;
        set
        {
            if (Info.Enabled == value) return;
            Info.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(StateText));
        }
    }

    /// <summary>勾选框旁的状态文字，只表示启用/停用，和列表选中状态无关。</summary>
    public string StateText => Enabled ? "已启用" : "已停用";

    public string Title => Info.Title;

    public string Subtitle => Info.BootParsed
        ? Info.FileName
        : Info.FileName + " · 无法解析 boot.json";

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>模组市场列表行：多选勾选状态 + 原始市场条目。</summary>
public sealed class MarketItemVm : INotifyPropertyChanged
{
    private bool _isChecked;

    public MarketItemVm(MarketMod mod, bool isChecked)
    {
        Mod = mod;
        _isChecked = isChecked;
    }

    public MarketMod Mod { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
