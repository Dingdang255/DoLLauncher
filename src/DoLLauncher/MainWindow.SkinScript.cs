using System.Windows.Controls;

namespace DoLLauncher;

/// <summary>美化包轻量脚本 / 声明式组件的界面入口：页面跳转、启动与只读数据。</summary>
public partial class MainWindow
{
    /// <summary>按名字切换主页面；未知页面返回 false。</summary>
    internal bool SkinOpenPage(string name)
    {
        RadioButton? target = (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "version" => NavVersions,
            "download" => NavDownload,
            "backup" => NavBackup,
            "story" => NavStory,
            "diagnostic" => NavDiagnostic,
            "settings" => NavSettings,
            _ => null,
        };
        if (target == null) return false;

        target.IsChecked = true;
        return true;
    }

    /// <summary>启动当前选中的版本（与「启动游戏」按钮一致）。</summary>
    internal void SkinLaunch() => LaunchSelected();

    /// <summary>当前选中版本的目录；未选中时为空串。</summary>
    internal string SkinVersionFolder => _selected?.Info.FullPath ?? string.Empty;

    internal int SkinVersionCount => _versions.Count;

    internal string SkinVersionName => _selected?.DisplayName ?? string.Empty;

    internal string SkinGameVersion => _selected?.Info.GameVersion ?? string.Empty;

    internal bool SkinVersionRunning => _selected?.IsRunning ?? false;

    internal int SkinModTotal => _mods.Count;

    internal int SkinModEnabled => _mods.Count(m => m.Enabled);
}
