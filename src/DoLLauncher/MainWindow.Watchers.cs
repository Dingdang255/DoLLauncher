using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
using DoLLauncher.Controls;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Search;
using DoLLauncher.Core.Theme;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Win32;
using SubtitleTextHelper = DoLLauncher.Core.Ui.SubtitleText;

namespace DoLLauncher;

public partial class MainWindow
{
    // ---------- mods 目录外部改动监听 ----------

    private void EnsureModsWatcher(VersionInfo version)
    {
        if (_modsWatcher != null && _watchedVersionDir != null &&
            string.Equals(_watchedVersionDir, version.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        StopModsWatcher();
        try
        {
            var modsDir = ModService.ModsDir(version.FullPath);
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);
            _modsWatcher = CreateModsWatcher(modsDir);
            // 版本根目录只关心启动器自己写的两个清单文件；logs 等其它子目录变动一律忽略
            _versionWatcher = CreateVersionWatcher(version.FullPath);
            _watchedVersionDir = version.FullPath;
        }
        catch (Exception ex)
        {
            Log("监听 mods 目录失败：" + ex.Message);
        }
    }

    private FileSystemWatcher CreateModsWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        watcher.Created += OnModsFolderChanged;
        watcher.Deleted += OnModsFolderChanged;
        watcher.Changed += OnModsFolderChanged;
        watcher.Renamed += OnModsFolderRenamed;
        watcher.Error += (_, args) => Log("mods 目录监听出错：" + args.GetException().Message);
        return watcher;
    }

    /// <summary>版本根目录的监听：只响应 modList.json / mods-state.json。</summary>
    private FileSystemWatcher CreateVersionWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Created += OnVersionManifestChanged;
        watcher.Deleted += OnVersionManifestChanged;
        watcher.Changed += OnVersionManifestChanged;
        watcher.Renamed += (s, e) => OnVersionManifestChanged(s, e);
        watcher.Error += (_, args) => Log("版本目录监听出错：" + args.GetException().Message);
        return watcher;
    }

    private void OnVersionManifestChanged(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        if (!name.Equals("modList.json", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("mods-state.json", StringComparison.OrdinalIgnoreCase))
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ScheduleModsReload));
    }

    private void OnModsFolderChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsModsWatchRelevant(e.Name)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ScheduleModsReload));
    }

    private void OnModsFolderRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsModsWatchRelevant(e.Name)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ScheduleModsReload));
    }

    private static bool IsModsWatchRelevant(string? name)
    {
        // 目录级事件（例如游戏往 logs 里写日志、新建子目录）可能拿到空名字，
        // 这些都不是模组变化，忽略，避免写日志时误刷新模组列表
        if (string.IsNullOrWhiteSpace(name)) return false;
        var file = Path.GetFileName(name);
        if (file.Length == 0) return false;
        if (file.Equals("mods-state.json", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("modList.json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("~", StringComparison.Ordinal))
            return false;
        return ModService.IsModPackage(file);
    }

    private void ScheduleModsReload()
    {
        if (DateTime.Now < _ignoreModsWatchUntil) return;
        if (_modsWatchTimer == null)
        {
            _modsWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _modsWatchTimer.Tick += (_, _) => RunModsReload();
        }
        _modsWatchTimer.Stop();
        _modsWatchTimer.Start();
    }

    private void RunModsReload()
    {
        _modsWatchTimer?.Stop();
        if (DateTime.Now < _ignoreModsWatchUntil) return;

        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;
        if (_watchedVersionDir != null &&
            !string.Equals(_watchedVersionDir, version.FullPath, StringComparison.OrdinalIgnoreCase))
            return;

        // 刷新本身可能触发 modList.json 的补建，短暂抑制监听避免自触发循环
        MarkInternalModsChange();
        LoadMods(version);
        Log("检测到 mods 目录被启动器以外的操作修改，已自动刷新模组列表。");
    }

    private void MarkInternalModsChange()
    {
        _ignoreModsWatchUntil = DateTime.Now.AddMilliseconds(1600);
    }

    private void StopModsWatcher()
    {
        _modsWatchTimer?.Stop();
        _modsWatcher?.Dispose();
        _modsWatcher = null;
        _versionWatcher?.Dispose();
        _versionWatcher = null;
        _watchedVersionDir = null;
    }

}
