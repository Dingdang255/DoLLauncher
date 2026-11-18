using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DoLLauncher.Core.Browser;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.ViewModels;

/// <summary>备份页视图模型：存档备份、模组配置导入导出、整版快照与自动备份设置。</summary>
public sealed class BackupViewModel : INotifyPropertyChanged
{
    private bool _loading = true;
    private VersionItemVm? _selectedTarget;
    private BackupEntry? _selectedBackup;
    private string _mirrorText = string.Empty;
    private string _countText = string.Empty;
    private bool _snapshotIncludeGame;
    private bool _autoBackup;
    private bool _autoSave;
    private int _keep = 5;
    private bool _busy;
    private string _logText = string.Empty;

    public BackupViewModel()
    {
        OpenSavesHelperCommand = new RelayCommand(_ => _ = OpenSavesHelperAsync(), _ => SelectedTarget != null);
        BackupSavesNowCommand = new RelayCommand(_ => _ = BackupSavesNowAsync(), _ => SelectedTarget != null);
        ExportModsConfigCommand = new RelayCommand(_ => _ = ExportModsConfigAsync(), _ => SelectedTarget != null);
        RestoreModsConfigCommand = new RelayCommand(_ => _ = RestoreModsConfigAsync(), _ => SelectedTarget != null);
        CreateSnapshotCommand = new RelayCommand(_ => _ = CreateSnapshotAsync(), _ => SelectedTarget != null && !_busy);
        RestoreSelectedCommand = new RelayCommand(_ => _ = RestoreSelectedAsync(), _ => SelectedBackup != null && !_busy);
        DeleteSelectedCommand = new RelayCommand(_ => _ = DeleteSelectedAsync(), _ => SelectedBackup != null && !_busy);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedTarget != null);
        RefreshCommand = new RelayCommand(_ => RefreshBackups(), _ => SelectedTarget != null);

        var backup = AppConfig.Load().Backup;
        _autoBackup = backup.Enabled;
        _autoSave = backup.SaveAuto;
        _keep = backup.Keep is 3 or 5 or 10 or 20 ? backup.Keep : 5;
        _loading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>快照导入成了新版本时通知主窗口刷新版本列表。</summary>
    public event Action<string>? VersionImported;

    /// <summary>模组列表被还原后通知主窗口重新读取。</summary>
    public event Action? ModsChanged;

    public ObservableCollection<VersionItemVm> Targets { get; } = new();

    public ObservableCollection<BackupEntry> Backups { get; } = new();

    public List<int> KeepOptions { get; } = new() { 3, 5, 10, 20 };

    public ICommand OpenSavesHelperCommand { get; }

    public ICommand BackupSavesNowCommand { get; }

    public ICommand ExportModsConfigCommand { get; }

    public ICommand RestoreModsConfigCommand { get; }

    public ICommand CreateSnapshotCommand { get; }

    public ICommand RestoreSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand RefreshCommand { get; }

    public VersionItemVm? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            RaiseCommands();
            RefreshBackups();
        }
    }

    public BackupEntry? SelectedBackup
    {
        get => _selectedBackup;
        set
        {
            if (!Set(ref _selectedBackup, value)) return;
            RaiseCommands();
        }
    }

    public string MirrorText
    {
        get => _mirrorText;
        private set => Set(ref _mirrorText, value);
    }

    public string CountText
    {
        get => _countText;
        private set => Set(ref _countText, value);
    }

    public bool SnapshotIncludeGame
    {
        get => _snapshotIncludeGame;
        set => Set(ref _snapshotIncludeGame, value);
    }

    public bool AutoBackup
    {
        get => _autoBackup;
        set
        {
            if (!Set(ref _autoBackup, value) || _loading) return;
            SaveBackupSettings(b => b.Enabled = value, "设置：启动时自动备份 = " + (value ? "开" : "关"));
        }
    }

    public bool AutoSave
    {
        get => _autoSave;
        set
        {
            if (!Set(ref _autoSave, value) || _loading) return;
            SaveBackupSettings(b => b.SaveAuto = value, "设置：启动时自动备份存档 = " + (value ? "开" : "关"));
        }
    }

    public int Keep
    {
        get => _keep;
        set
        {
            if (!Set(ref _keep, value) || _loading) return;
            SaveBackupSettings(b => b.Keep = value, "设置：备份保留份数 = " + value);
        }
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    /// <summary>主窗口的版本列表刷新后同步目标版本下拉框。</summary>
    public void SyncTargets(IEnumerable<VersionItemVm> versions, string? preferredFolder)
    {
        var disk = versions.Where(v => !v.Info.IsFileMode).ToList();
        var current = preferredFolder ?? SelectedTarget?.Info.Folder;
        var same = disk.Count == Targets.Count &&
                   disk.Select((v, i) => ReferenceEquals(v, Targets[i])).All(x => x);
        if (!same)
        {
            Targets.Clear();
            foreach (var vm in disk) Targets.Add(vm);
        }
        SelectedTarget = Targets.FirstOrDefault(v => v.Info.Folder == current) ?? Targets.FirstOrDefault();
    }

    /// <summary>首次进入备份页时刷新列表。</summary>
    public void EnsureLoaded() => RefreshBackups();

    private void RefreshBackups()
    {
        Backups.Clear();
        SelectedBackup = null;
        var version = SelectedTarget?.Info;
        if (version == null)
        {
            CountText = string.Empty;
            MirrorText = "磁盘镜像：等待读取…";
            return;
        }

        try
        {
            foreach (var entry in BackupService.List(version.Folder)) Backups.Add(entry);
            CountText = $"{Backups.Count} 个备份";
            MirrorText = "磁盘镜像：" + BackupService.DescribeMirror(version.Folder);
        }
        catch (Exception ex)
        {
            LogError("读取备份列表失败", ex);
            MirrorText = "磁盘镜像：读取失败";
        }
    }

    private async Task OpenSavesHelperAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var server = ServerManager.EnsureStarted(version, saveHelperOnly: true);
            var result = BrowserService.OpenHelper(version, server.SaveHelperUrl);
            Log(result.Opened ? "已打开存档助手。" : "打开存档助手失败：没有可用的浏览器。");
            if (!result.Opened)
                await PlatformServices.Dialogs.ErrorAsync("没有可用的浏览器，无法打开存档助手。", "打开存档助手");
        }
        catch (Exception ex)
        {
            LogError("打开存档助手失败", ex);
            await PlatformServices.Dialogs.ErrorAsync(ex.Message, "打开存档助手");
        }
    }

    private async Task BackupSavesNowAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var name = BackupService.BackupSavesFromMirror(version.Folder, out var reason);
            if (name == null)
            {
                await PlatformServices.Dialogs.InfoAsync(reason, "立即备份存档");
                return;
            }
            Log("已静默备份存档：" + name);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            LogError("静默备份存档失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("备份失败：" + ex.Message, "立即备份存档");
        }
    }

    private async Task ExportModsConfigAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var path = BackupService.ExportModsConfig(version);
            Log("已导出模组配置：" + path);
            RefreshBackups();
            await PlatformServices.Dialogs.InfoAsync("模组顺序与启停已保存：\n" + path, "导出模组配置");
        }
        catch (Exception ex)
        {
            LogError("导出模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出模组配置");
        }
    }

    private async Task RestoreModsConfigAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;

        var path = PlatformServices.FilePicker.PickFile(
            "选择模组配置", "模组配置 (*.json)|*.json|所有文件 (*.*)|*.*");
        if (path == null) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                "将用这个文件里的顺序与启停覆盖当前模组列表，继续吗？", "还原模组配置", "覆盖"))
            return;

        try
        {
            BackupService.RestoreModsConfig(version.FullPath, path, Log);
            ModsChanged?.Invoke();
            RefreshBackups();
        }
        catch (Exception ex)
        {
            LogError("还原模组配置失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原模组配置");
        }
    }

    private async Task CreateSnapshotAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null || _busy) return;

        var includeGame = SnapshotIncludeGame;
        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"为版本「{version.DisplayName}」创建快照？\n" +
                (includeGame ? "包含游戏本体，体积较大。\n" : "只包含模组与配置，体积较小。\n") +
                "快照保存在启动器的 backups 目录。", "创建快照", "创建"))
            return;

        Busy = true;
        try
        {
            Log("正在创建快照…");
            var path = await Task.Run(() => BackupService.CreateSnapshot(version, includeGame, Log));
            Log("快照完成：" + path);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            LogError("创建快照失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("创建快照失败：" + ex.Message, "快照");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RestoreSelectedAsync()
    {
        var version = SelectedTarget?.Info;
        var entry = SelectedBackup;
        if (version == null || entry == null || _busy) return;

        if (entry.IsSaves)
        {
            if (!await PlatformServices.Dialogs.ConfirmAsync(
                    $"会用「{entry.FileName}」覆盖该版本下次打开游戏时的存档库。\n\n" +
                    "不会立即打开浏览器；下次启动游戏页时自动导入。继续吗？", "还原存档", "还原"))
                return;
            try
            {
                BackupService.QueueSavesRestore(version.Folder, entry.FullPath);
                Log("已排队还原存档：" + entry.FileName);
                await PlatformServices.Dialogs.InfoAsync("已加入还原队列：下次启动这个版本时会自动导入存档。", "还原存档");
            }
            catch (Exception ex)
            {
                LogError("还原存档失败", ex);
                await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原存档");
            }
            return;
        }

        if (entry.IsModsConfig)
        {
            if (!await PlatformServices.Dialogs.ConfirmAsync(
                    $"将按「{entry.FileName}」还原模组顺序与启停，继续吗？", "还原模组配置", "覆盖"))
                return;
            try
            {
                BackupService.RestoreModsConfig(version.FullPath, entry.FullPath, Log);
                ModsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LogError("还原模组配置失败", ex);
                await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原模组配置");
            }
            return;
        }

        if (!entry.IsSnapshot) return;

        var inspection = PackService.Inspect(entry.FullPath);
        if (inspection.IsUnknown)
        {
            await PlatformServices.Dialogs.WarnAsync(
                $"「{entry.FileName}」不是可识别的版本包或模组包，无法还原。", "还原快照");
            return;
        }

        var hasGame = inspection.IsGame;
        var message = hasGame
            ? inspection.Game.Kind == PackGameKind.SingleHtml
                ? $"「{entry.FileName}」里没有找到 Degrees of Lewdity.html，只找到一个 HTML。\n" +
                  "该包可能不是可用的 DoL 版本，确定导入吗？"
                : $"「{entry.FileName}」含游戏本体，将作为新版本导入到 versions 目录。继续吗？"
            : $"「{entry.FileName}」只含模组，将导入到版本「{version.DisplayName}」，同名模组会被覆盖。继续吗？";
        if (!await PlatformServices.Dialogs.ConfirmAsync(message, "还原快照", "还原")) return;

        Busy = true;
        try
        {
            Log("正在还原快照：" + entry.FileName);
            var targetPath = hasGame ? null : version.FullPath;
            var result = await Task.Run(() => PackService.Import(entry.FullPath, inspection, targetPath, Log));
            if (result.NewVersionFolder != null)
            {
                Log($"快照已导入为新版本：{result.NewVersionFolder}");
                VersionImported?.Invoke(result.NewVersionFolder);
            }
            else
            {
                ModsChanged?.Invoke();
                Log($"快照已还原到版本「{version.DisplayName}」。");
            }
            RefreshBackups();
        }
        catch (Exception ex)
        {
            LogError("还原快照失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("还原失败：" + ex.Message, "还原快照");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var version = SelectedTarget?.Info;
        var entry = SelectedBackup;
        if (version == null || entry == null) return;

        if (!await PlatformServices.Dialogs.ConfirmAsync(
                $"确定删除备份「{entry.FileName}」吗？", "删除备份", "删除"))
            return;

        try
        {
            BackupService.Delete(version.Folder, entry.FileName, entry.IsSaves);
            Log("已删除备份：" + entry.FileName);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            LogError("删除备份失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("删除失败：" + ex.Message, "删除备份");
        }
    }

    private void OpenFolder()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var dir = BackupService.VersionDir(version.Folder);
            Directory.CreateDirectory(dir);
            PlatformServices.Links.OpenPath(dir);
        }
        catch (Exception ex)
        {
            LogError("打开备份目录失败", ex);
        }
    }

    private void SaveBackupSettings(Action<BackupSettings> mutate, string message)
    {
        try
        {
            var cfg = AppConfig.Load();
            mutate(cfg.Backup);
            AppConfig.Save(cfg);
            Log(message);
        }
        catch (Exception ex)
        {
            LogService.Error("保存备份设置失败", ex);
        }
    }

    private bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        (OpenSavesHelperCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BackupSavesNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportModsConfigCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreModsConfigCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CreateSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Log(string message)
    {
        try { LogService.Append(message); } catch { }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        void Append()
        {
            var text = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
            var lines = text.Split(Environment.NewLine);
            if (lines.Length > 300) text = string.Join(Environment.NewLine, lines[^300..]);
            LogText = text;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append();
        else Dispatcher.UIThread.Post(Append);
    }

    private void LogError(string context, Exception ex)
    {
        Log(context + "：" + ex.Message);
        try { LogService.Error(context, ex); } catch { }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
