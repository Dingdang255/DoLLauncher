using System.ComponentModel;
using System.Windows.Media;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

public sealed class VersionVm : INotifyPropertyChanged
{
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(0x6E, 0xCB, 0x8F));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x98, 0xA6));
    private static readonly Brush HelperBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0x9C, 0xD9));
    private static readonly Brush LegacyBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA6, 0x5C));

    public VersionInfo Info { get; }

    public VersionVm(VersionInfo info) => Info = info;

    public string DisplayName => Info.DisplayName;

    /// <summary>下拉框里用的短名称，避免在窄列里被截断。</summary>
    public string ComboName => Info.IsFileMode
        ? "旧数据（file://）"
        : Info.GameVersion != null ? $"DoL {Info.GameVersion} · {Info.Port}" : Info.DisplayName;

    public string Subtitle => Info.IsFileMode
        ? "file:// 模式 · 与所有本地网页共享浏览器数据"
        : $"端口 {Info.Port} · {Info.GameVersion ?? "未知版本"}";

    public string StatusText
    {
        get
        {
            if (Info.IsFileMode) return "旧数据";
            if (!ServerManager.IsRunning(Info.Folder)) return "未启动";
            return ServerManager.IsHelperOnly(Info.Folder) ? "存档助手" : "运行中";
        }
    }

    public bool IsRunning => !Info.IsFileMode && ServerManager.IsRunning(Info.Folder);

    public Brush StatusBrush
    {
        get
        {
            if (Info.IsFileMode) return LegacyBrush;
            if (!ServerManager.IsRunning(Info.Folder)) return IdleBrush;
            return ServerManager.IsHelperOnly(Info.Folder) ? HelperBrush : RunningBrush;
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
