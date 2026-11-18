using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DoLLauncher.Core.Browser;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.ViewModels;

/// <summary>版本页视图模型：版本列表、模组管理、启动 / 停止与问题检查。</summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const string ModFilter = "模组包|*.zip;*.modpack|所有文件|*.*";

    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _watchTimer;

    private VersionItemVm? _selectedVersion;
    private bool _loadingMods;
    private bool _isVersionsSelected = true;
    private bool _isSettingsSelected;
    private bool _isDownloadSelected;
    private bool _isBackupSelected;
    private bool _isDiagnosticSelected;
    private bool _isStorySelected;
    private string _placeholderText = string.Empty;
    private string _titleText = "未选择版本";
    private string _subText = "在左侧选择一个版本，或把已有游戏目录放进 versions 后刷新。";
    private string _badgeMode = string.Empty;
    private string _pathText = string.Empty;
    private string _modCountText = string.Empty;
    private string _versionCountText = string.Empty;
    private string _problemText = "暂未检测到问题";
    private string _logText = string.Empty;
    private string _launchText = "启动游戏";
    private bool _hasProblems;
    private bool _hasDanger;
    private bool _canLaunch;
    private bool _canStop;
    private bool _canManageMods;

    private List<ModConflict> _conflicts = new();
    private List<ModMissingDependency> _missingDeps = new();
    private List<ModVersionMismatch> _versionMismatches = new();
    private List<ModDuplicate> _duplicates = new();
    private int _conflictToken;

    private MarketCatalog? _catalog;
    private bool _catalogRequested;

    private FileSystemWatcher? _modsWatcher;
    private FileSystemWatcher? _versionWatcher;
    private string? _watchedVersionDir;
    private DateTime _ignoreWatchUntil = DateTime.MinValue;

    public MainWindowViewModel()
    {
        NavigateCommand = new RelayCommand(Navigate);
        LaunchCommand = new RelayCommand(_ => _ = LaunchAsync(), _ => _canLaunch);
        StopCommand = new RelayCommand(_ => Stop(), _ => _canStop);
        ImportModsCommand = new RelayCommand(_ => _ = ImportModsAsync(), _ => _canManageMods);
        RefreshModsCommand = new RelayCommand(_ => RefreshMods(), _ => _canManageMods);
        DeleteModsCommand = new RelayCommand(_ => _ = DeleteModsAsync(), _ => _canManageMods && SelectedMods.Count > 0);
        MoveUpCommand = new RelayCommand(p => MoveMod(p, -1), _ => CanMove());
        MoveDownCommand = new RelayCommand(p => MoveMod(p, 1), _ => CanMove());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => _canManageMods);
        ShowProblemsCommand = new RelayCommand(_ => _ = ShowProblemsAsync(), _ => _canManageMods);

        Settings = new SettingsViewModel();
        Settings.VersionGapChanged += () =>
        {
            if (_selectedVersion != null) ApplyIssues();
        };

        Download = new DownloadViewModel();
        Download.VersionInstalled += folder =>
        {
            Reload();
            SelectedVersion = Versions.FirstOrDefault(v => v.Info.Folder == folder) ?? SelectedVersion;
        };

        Backup = new BackupViewModel();
        Backup.VersionImported += folder =>
        {
            Reload();
            SelectedVersion = Versions.FirstOrDefault(v => v.Info.Folder == folder) ?? SelectedVersion;
        };
        Backup.ModsChanged += () =>
        {
            if (_selectedVersion != null) RefreshDetail();
        };

        Diagnostic = new DiagnosticViewModel();

        Story = new StoryViewModel();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _statusTimer.Start();

        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _watchTimer.Tick += (_, _) => RunModsReload();

        ModService.InternalWriteStarted += MarkInternalModsChange;
        BrowserSessionMonitor.Instance.SessionLost += OnSessionLost;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>导航切换完成后触发，供视图播放页面过渡动画。</summary>
    public event Action? Navigated;

    public ObservableCollection<VersionItemVm> Versions { get; } = new();

    public ObservableCollection<ModItemVm> Mods { get; } = new();

    public List<ModItemVm> SelectedMods { get; private set; } = new();

    public ICommand NavigateCommand { get; }

    public ICommand LaunchCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ImportModsCommand { get; }

    public ICommand RefreshModsCommand { get; }

    public ICommand DeleteModsCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand ShowProblemsCommand { get; }

    public SettingsViewModel Settings { get; }

    public DownloadViewModel Download { get; }

    public BackupViewModel Backup { get; }

    public DiagnosticViewModel Diagnostic { get; }

    public StoryViewModel Story { get; }

    public bool IsVersionsSelected
    {
        get => _isVersionsSelected;
        private set
        {
            if (_isVersionsSelected == value) return;
            _isVersionsSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsSettingsSelected
    {
        get => _isSettingsSelected;
        private set
        {
            if (_isSettingsSelected == value) return;
            _isSettingsSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsDownloadSelected
    {
        get => _isDownloadSelected;
        private set
        {
            if (_isDownloadSelected == value) return;
            _isDownloadSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsBackupSelected
    {
        get => _isBackupSelected;
        private set
        {
            if (_isBackupSelected == value) return;
            _isBackupSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsDiagnosticSelected
    {
        get => _isDiagnosticSelected;
        private set
        {
            if (_isDiagnosticSelected == value) return;
            _isDiagnosticSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsStorySelected
    {
        get => _isStorySelected;
        private set
        {
            if (_isStorySelected == value) return;
            _isStorySelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlaceholderVisible));
        }
    }

    public bool IsPlaceholderVisible =>
        !_isVersionsSelected && !_isSettingsSelected && !_isDownloadSelected &&
        !_isBackupSelected && !_isDiagnosticSelected && !_isStorySelected;

    public string PlaceholderText
    {
        get => _placeholderText;
        private set => Set(ref _placeholderText, value);
    }

    public string TitleText
    {
        get => _titleText;
        private set => Set(ref _titleText, value);
    }

    public string SubText
    {
        get => _subText;
        private set => Set(ref _subText, value);
    }

    public string BadgeMode
    {
        get => _badgeMode;
        private set => Set(ref _badgeMode, value);
    }

    public string PathText
    {
        get => _pathText;
        private set => Set(ref _pathText, value);
    }

    public string ModCountText
    {
        get => _modCountText;
        private set => Set(ref _modCountText, value);
    }

    public string VersionCountText
    {
        get => _versionCountText;
        private set => Set(ref _versionCountText, value);
    }

    public string ProblemText
    {
        get => _problemText;
        private set => Set(ref _problemText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    public string LaunchText
    {
        get => _launchText;
        private set => Set(ref _launchText, value);
    }

    public bool HasProblems
    {
        get => _hasProblems;
        private set => Set(ref _hasProblems, value);
    }

    public bool HasDanger
    {
        get => _hasDanger;
        private set => Set(ref _hasDanger, value);
    }

    public VersionItemVm? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (ReferenceEquals(_selectedVersion, value)) return;
            _selectedVersion = value;
            OnPropertyChanged();
            RefreshDetail();
            Download.SyncTargets(Versions, value?.Info.Folder);
            Backup.SyncTargets(Versions, value?.Info.Folder);
            Diagnostic.SyncTargets(Versions, value?.Info.Folder);
            Story.SyncTargets(Versions, value?.Info.Folder);
        }
    }

    /// <summary>扫描版本目录并刷新列表。</summary>
    public void Reload()
    {
        try
        {
            var keep = _selectedVersion?.Info.Folder;
            var scanned = VersionService.Scan();
            Versions.Clear();
            foreach (var info in scanned) Versions.Add(new VersionItemVm(info));
            VersionCountText = $"{Versions.Count} 个";

            SelectedVersion = Versions.FirstOrDefault(v => v.Info.Folder == keep)
                              ?? Versions.FirstOrDefault(v => !v.Info.IsFileMode)
                              ?? Versions.FirstOrDefault();
            Log($"扫描到 {Versions.Count} 个版本");
        }
        catch (Exception ex)
        {
            LogError("扫描版本失败", ex);
        }
    }

    /// <summary>列表选中项变化时由界面写回，供删除 / 启停 / 排序使用。</summary>
    public void SetSelectedMods(IEnumerable<ModItemVm> mods)
    {
        SelectedMods = mods.ToList();
        UpdateCommands();
    }

    /// <summary>拖放或文件选择得到的路径，统一在这里过滤并导入。</summary>
    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;

        var zips = paths.Where(ModService.IsModPackage).ToList();
        if (zips.Count == 0)
        {
            await PlatformServices.Dialogs.InfoAsync("只支持 .zip / .modpack 模组包。", "导入模组");
            return;
        }

        Log($"开始导入 {zips.Count} 个模组…");
        MarkInternalModsChange();
        try
        {
            var count = ModService.ImportFiles(version.FullPath, zips, Log);
            Log($"导入完成：成功 {count} 个。");
            LoadMods(version);
        }
        catch (Exception ex)
        {
            LogError("导入模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入失败：" + ex.Message, "导入模组");
            LoadMods(version);
        }
    }

    /// <summary>仍有服务器在运行时，窗口关闭前需要确认。</summary>
    public bool HasRunningServers => ServerManager.RunningFolders.Count > 0;

    /// <summary>关闭窗口前询问并停止所有服务器；返回 true 表示可以退出。</summary>
    public async Task<bool> ConfirmStopAllAsync()
    {
        var running = ServerManager.RunningFolders;
        if (running.Count == 0) return true;

        var names = running.Select(folder =>
        {
            var vm = Versions.FirstOrDefault(v => v.Info.Folder == folder);
            return vm?.Info.GameVersion != null ? $"DoL {vm.Info.GameVersion}" : folder;
        }).ToList();
        var more = names.Count > 5 ? $"\n…另有 {names.Count - 5} 个" : string.Empty;
        var list = string.Join("\n", names.Take(5).Select(n => "· " + n)) + more;

        var confirmed = await PlatformServices.Dialogs.ConfirmAsync(
            $"{names.Count} 个游戏版本还在运行：\n\n{list}\n\n退出会停止这些版本的本地服务器。",
            "退出 DoL 启动器", "停止并退出");
        if (!confirmed) return false;

        ServerManager.StopAll();
        return true;
    }

    private void RefreshDetail()
    {
        var vm = _selectedVersion;
        _modsWatcher?.Dispose();
        _modsWatcher = null;
        _versionWatcher?.Dispose();
        _versionWatcher = null;
        _watchedVersionDir = null;

        Mods.Clear();
        SetSelectedMods(Array.Empty<ModItemVm>());
        _conflicts.Clear();
        _missingDeps.Clear();
        _versionMismatches.Clear();
        _duplicates.Clear();

        if (vm == null)
        {
            TitleText = "未选择版本";
            SubText = "在左侧选择一个版本，或把已有游戏目录放进 versions 后刷新。";
            BadgeMode = string.Empty;
            PathText = string.Empty;
            ModCountText = string.Empty;
            ProblemText = "暂未检测到问题";
            HasProblems = false;
            HasDanger = false;
            UpdateButtons();
            return;
        }

        var version = vm.Info;
        vm.Refresh();

        TitleText = version.DisplayName;
        SubText = version.IsFileMode
            ? "用 file:// 直接打开：浏览器会把所有本地网页算作同一个来源，存档与模组开关无法和其它版本隔离，仅用于找回旧数据。"
            : $"游戏版本 {version.GameVersion ?? "未知"} · 加载器 {vm.LoaderType}";
        BadgeMode = version.IsFileMode ? "file:// 共享" : "127.0.0.1:" + version.Port;
        PathText = version.IsFileMode ? version.HtmlPath : version.FullPath;

        if (version.IsFileMode)
        {
            ModCountText = "file:// 模式下不支持磁盘模组管理";
        }
        else
        {
            LoadMods(version);
            EnsureWatcher(version);
            RequestCatalog();
        }

        UpdateButtons();
    }

    private void LoadMods(VersionInfo version)
    {
        _loadingMods = true;
        try
        {
            Mods.Clear();
            ModService.EnsureModList(version.FullPath);
            foreach (var info in ModService.Load(version.FullPath))
            {
                var vm = new ModItemVm(info) { EnabledChanged = OnModEnabledChanged };
                Mods.Add(vm);
            }
            ApplyIssues();
        }
        catch (Exception ex)
        {
            LogError("读取模组失败", ex);
        }
        finally
        {
            _loadingMods = false;
        }

        UpdateModCount();
        UpdateButtons();
    }

    private void OnModEnabledChanged(ModItemVm mod)
    {
        if (_loadingMods) return;
        PersistMods();
    }

    private void PersistMods()
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;

        MarkInternalModsChange();
        try
        {
            ModService.Save(version.FullPath, Mods.Select(m => m.Info));
            UpdateModCount();
            ApplyIssues();
            Log($"已更新 modList.json（启用 {Mods.Count(m => m.Enabled)} 个）");
        }
        catch (Exception ex)
        {
            LogError("保存模组状态失败", ex);
        }
    }

    private void ApplyIssues()
    {
        var version = _selectedVersion?.Info;
        var infos = Mods.Select(m => m.Info).ToList();
        var threshold = ProblemService.NormalizeThreshold(AppConfig.Load().General.VersionGapThreshold);
        var runtime = version == null || version.IsFileMode
            ? new List<ModVersionMismatch>()
            : RuntimeIssueScanner.Scan(version.FullPath, infos, version.GameVersion);

        var ignoredVersion = LoadIgnoredVersionIssues(version?.Folder);
        foreach (var issue in runtime) issue.Ignored = ignoredVersion.Contains(issue.Key);

        var runtimeByFile = runtime
            .Where(r => !r.Ignored && r.ModFile.Length > 0)
            .GroupBy(r => r.ModFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byName = Mods
            .Where(m => m.Info.Name.Length > 0)
            .GroupBy(m => DownloadService.NormalizeName(m.Info.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var vm in Mods)
        {
            var issues = new List<string>();
            if (!vm.Info.BootParsed) issues.Add("boot.json 无法解析");

            var normalized = DownloadService.NormalizeName(vm.Info.Name);
            if (normalized.Length > 0 && byName.TryGetValue(normalized, out var count) && count > 1)
                issues.Add($"与其它 {count - 1} 个模组同名");

            foreach (var dep in vm.Info.Dependencies)
            {
                if (ModService.IsGameVersion(dep.Name))
                {
                    var gameVersion = version?.GameVersion;
                    if (!string.IsNullOrWhiteSpace(gameVersion) &&
                        !VersionUtil.Satisfies(dep.Version, gameVersion))
                        issues.Add($"游戏版本不满足（需要 {dep.Version}，当前 {gameVersion}）");
                    continue;
                }

                if (ModService.IsBuiltin(dep.Name)) continue;
                var target = Mods.FirstOrDefault(m => string.Equals(m.Info.Name, dep.Name,
                    StringComparison.OrdinalIgnoreCase));
                if (target == null || !target.Enabled)
                    issues.Add($"缺少前置「{dep.Name}」");
                else if (!VersionUtil.Satisfies(dep.Version, target.Info.Version))
                    issues.Add($"前置「{dep.Name}」版本不满足（需要 {dep.Version}）");
            }

            if (runtimeByFile.TryGetValue(vm.Info.FileName, out var runtimeIssue))
                issues.Add($"运行日志显示{runtimeIssue.RuntimeReason}");

            vm.IssueText = string.Join("；", issues);
        }

        _missingDeps = ProblemService.MissingDependencies(infos);
        _versionMismatches = ProblemService.VersionMismatches(infos, version?.GameVersion, threshold);
        foreach (var issue in runtime)
        {
            var duplicate = issue.ModFile.Length > 0
                ? _versionMismatches.Any(m => string.Equals(m.ModFile, issue.ModFile,
                    StringComparison.OrdinalIgnoreCase))
                : _versionMismatches.Any(m => m.ModFile.Length == 0 &&
                    string.Equals(m.ModName, issue.ModName, StringComparison.OrdinalIgnoreCase));
            if (!duplicate) _versionMismatches.Add(issue);
        }
        foreach (var issue in _versionMismatches)
            issue.Ignored = ignoredVersion.Contains(issue.Key);

        _duplicates = ProblemService.Duplicates(infos);
        ApplyConflictText();
        UpdateProblemButton();
    }

    private List<ModConflict> ActiveConflicts => _conflicts.Where(c => !c.Ignored).ToList();

    private int TotalProblems =>
        ActiveConflicts.Count + _missingDeps.Count +
        _versionMismatches.Count(m => !m.Ignored) + _duplicates.Count;

    private int RedProblems =>
        ActiveConflicts.Count + _missingDeps.Count + _duplicates.Count +
        _versionMismatches.Count(m => !m.Ignored && m.Severity == ProblemSeverity.Red);

    private void UpdateProblemButton()
    {
        var total = TotalProblems;
        var red = RedProblems;
        HasProblems = total > 0;
        HasDanger = red > 0;
        ProblemText = total == 0 ? "暂未检测到问题" : $"可能存在问题 {total}";
    }

    private void ApplyConflictText()
    {
        foreach (var vm in Mods)
        {
            var others = new List<string>();
            foreach (var conflict in _conflicts)
            {
                if (conflict.Ignored) continue;
                if (conflict.LeftFile.Length > 0 &&
                    string.Equals(conflict.LeftFile, vm.Info.FileName, StringComparison.OrdinalIgnoreCase))
                    others.Add(conflict.RightName);
                else if (conflict.RightFile.Length > 0 &&
                         string.Equals(conflict.RightFile, vm.Info.FileName, StringComparison.OrdinalIgnoreCase))
                    others.Add(conflict.LeftName);
            }

            vm.ConflictText = others.Count == 0
                ? string.Empty
                : "可能与「" + string.Join("」「", others.Distinct(StringComparer.OrdinalIgnoreCase)) + "」冲突";
        }
    }

    /// <summary>首次进入版本页时后台加载市场索引，用于简介 / README 的冲突声明检查。</summary>
    private void RequestCatalog()
    {
        if (_catalogRequested) return;
        _catalogRequested = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _catalog = await DownloadService.FetchMarketAsync(Log);
                await CheckConflictsAsync();
            }
            catch (Exception ex)
            {
                Log("加载市场索引失败，本次跳过冲突检测：" + ex.Message);
            }
        });
    }

    private async Task CheckConflictsAsync()
    {
        var version = _selectedVersion?.Info;
        var token = ++_conflictToken;

        if (version == null || version.IsFileMode || _catalog == null || _catalog.Mods.Count == 0)
        {
            _conflicts = new List<ModConflict>();
            ApplyConflictText();
            UpdateProblemButton();
            return;
        }

        var mods = Mods.Select(m => m.Info).ToList();
        List<ModConflict> found;
        try
        {
            found = await ModConflictService.CheckAsync(mods, _catalog, Log);
        }
        catch (Exception ex)
        {
            Log("检查模组冲突失败：" + ex.Message);
            found = new List<ModConflict>();
        }

        if (token != _conflictToken || _selectedVersion?.Info.Folder != version.Folder) return;

        var ignored = LoadIgnoredConflicts(version.Folder);
        foreach (var conflict in found) conflict.Ignored = ignored.Contains(conflict.PairKey);
        _conflicts = found;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyConflictText();
            UpdateProblemButton();
        });
    }

    private async Task ShowProblemsAsync()
    {
        if (!HasProblems)
        {
            await PlatformServices.Dialogs.InfoAsync("当前未检测到冲突、重复、版本不匹配或缺少前置。", "模组问题");
            return;
        }

        var lines = new List<string>();
        lines.AddRange(_conflicts.Where(c => !c.Ignored).Select(c => "· " + c.Title + "\n  " + c.Detail));
        lines.AddRange(_missingDeps.Select(m => "· " + m.Title + "\n  " + m.Detail));
        lines.AddRange(_versionMismatches.Where(m => !m.Ignored)
            .Select(m => "· " + m.Title + "\n  " + m.Detail));
        lines.AddRange(_duplicates.Select(d => "· " + d.Title));

        var text = string.Join("\n\n", lines.Take(12));
        if (lines.Count > 12) text += $"\n\n…另有 {lines.Count - 12} 项";
        await PlatformServices.Dialogs.InfoAsync(text, "模组问题");
    }

    private async Task LaunchAsync()
    {
        var vm = _selectedVersion;
        if (vm == null) return;
        var version = vm.Info;

        if (version.IsFileMode)
        {
            if (!await PlatformServices.Dialogs.ConfirmAsync(
                    "file:// 模式下，浏览器会把本机所有 file:// 页面当成同一个来源，\n" +
                    "存档和模组开关会和别的本地网页互相干扰。\n\n只有需要找回旧数据时才建议这样做。继续打开吗？",
                    "file:// 共享提示", "继续打开")) return;

            try
            {
                var opened = BrowserService.OpenGame(version, new Uri(version.HtmlPath).AbsoluteUri);
                if (!opened.Opened)
                {
                    await PlatformServices.Dialogs.WarnAsync("没有可用的浏览器，无法打开页面。");
                    return;
                }
                Log("已用 file:// 打开：" + version.HtmlPath);
            }
            catch (Exception ex)
            {
                LogError("打开失败", ex);
                await PlatformServices.Dialogs.ErrorAsync("打开失败：" + ex.Message);
            }
            return;
        }

        try
        {
            if (!File.Exists(version.HtmlPath))
            {
                await PlatformServices.Dialogs.WarnAsync("这个版本目录里找不到游戏本体 HTML，无法启动。");
                return;
            }

            ApplyIssues();
            if (HasProblems && !await ConfirmLaunchAsync()) return;

            ModService.Apply(version.FullPath);
            var server = ServerManager.EnsureStarted(version);
            var opened = BrowserService.OpenGame(version, server.GameUrl);
            if (!opened.Opened)
                await PlatformServices.Dialogs.WarnAsync("没有可用的浏览器，无法打开游戏页面。");
            else if (opened.Mode == DoLLauncher.Platform.Abstractions.BrowserLaunchMode.Default)
                Log("当前使用系统默认浏览器承载，无法自动关闭页面窗口；关页后服务器按心跳判定。");

            Log($"已启动 {version.DisplayName}：{server.GameUrl}");
            LoadMods(version);
            RefreshStatuses();

            var cfg = AppConfig.Load();
            if (cfg.Backup.Enabled)
            {
                _ = Task.Run(() =>
                {
                    try { BackupService.AutoBackup(version, Log); }
                    catch (Exception ex) { LogError("自动备份失败", ex); }
                });
            }
            if (cfg.Backup.SaveAuto)
            {
                BrowserService.OpenHelper(version, server.SaveHelperUrl + "?auto=1");
                Log("已打开自动存档备份页（完成后可关闭该页面）。");
            }
        }
        catch (Exception ex)
        {
            LogError("启动失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("启动失败：" + ex.Message);
        }
    }

    private async Task<bool> ConfirmLaunchAsync()
    {
        var lines = new List<string>();
        lines.AddRange(ActiveConflicts.Select(c => "· " + c.Title));
        lines.AddRange(_missingDeps.Select(m => "· " + m.Title));
        lines.AddRange(_versionMismatches.Where(m => !m.Ignored).Select(m => "· " + m.Title));
        lines.AddRange(_duplicates.Select(d => "· " + d.Title));

        var text = string.Join("\n", lines.Take(8));
        if (lines.Count > 8) text += $"\n…另有 {lines.Count - 8} 项";
        return await PlatformServices.Dialogs.ConfirmAsync(
            $"检测到 {lines.Count} 个可能的问题：\n\n{text}\n\n仍然要启动吗？",
            "启动前检查", "仍然启动");
    }

    private void Stop()
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;
        BrowserService.Close(version.Folder);
        ServerManager.Stop(version.Folder);
        Log("已停止：" + version.DisplayName);
        RefreshStatuses();
    }

    private void RefreshMods()
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;
        LoadMods(version);
        Log("已重新读取磁盘模组。");
    }

    private async Task ImportModsAsync()
    {
        if (_selectedVersion?.Info is not { IsFileMode: false }) return;
        var paths = PlatformServices.FilePicker.PickFiles("导入模组", ModFilter, false);
        if (paths.Count == 0) return;
        await ImportPathsAsync(paths);
    }

    private async Task DeleteModsAsync()
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;
        var targets = SelectedMods.ToList();
        if (targets.Count == 0) return;

        var names = string.Join("\n", targets.Select(m => "· " + m.Info.FileName));
        var message = targets.Count == 1
            ? $"确定删除模组「{targets[0].Info.Name}」吗？\n\n文件：{targets[0].Info.FileName}\n\n" +
              "只删除磁盘上的 zip，不会影响浏览器里的存档。"
            : $"确定删除选中的 {targets.Count} 个模组吗？\n\n{names}\n\n" +
              "只删除磁盘上的 zip，不会影响浏览器里的存档。";
        if (!await PlatformServices.Dialogs.ConfirmAsync(message, "删除模组", "删除", danger: true)) return;

        MarkInternalModsChange();
        try
        {
            foreach (var target in targets)
                ModService.Delete(version.FullPath, target.Info.FileName);
            Log($"已删除 {targets.Count} 个模组：{string.Join("、", targets.Select(m => m.Info.FileName))}");
        }
        catch (Exception ex)
        {
            LogError("删除模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("删除失败：" + ex.Message);
        }
        LoadMods(version);
    }

    private bool CanMove() =>
        _canManageMods && SelectedMods.Count == 1 && SelectedVersion != null;

    private void MoveMod(object? parameter, int delta)
    {
        if (parameter is not ModItemVm vm) return;
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;

        var index = Mods.IndexOf(vm);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Mods.Count) return;
        Mods.Move(index, target);
        PersistMods();
    }

    private void OpenFolder()
    {
        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;
        ServerManager.OpenFolder(ModService.ModsDir(version.FullPath));
    }

    private void RefreshStatuses()
    {
        foreach (var vm in Versions) vm.Refresh();
        if (_selectedVersion is { Info.IsFileMode: false } current)
        {
            SubText = $"游戏版本 {current.Info.GameVersion ?? "未知"} · 加载器 {current.LoaderType}";
            BadgeMode = "127.0.0.1:" + current.Info.Port;
        }
        UpdateButtons();
    }

    private void UpdateModCount()
    {
        var enabled = Mods.Count(m => m.Enabled);
        ModCountText = Mods.Count == 0
            ? "（mods 目录为空）"
            : $"共 {Mods.Count} 个 · 启用 {enabled} 个";
    }

    private void UpdateButtons()
    {
        var version = _selectedVersion?.Info;
        var running = version != null && !version.IsFileMode && ServerManager.IsRunning(version.Folder);
        var helperOnly = version != null && running && ServerManager.IsHelperOnly(version.Folder);

        _canLaunch = version != null;
        _canStop = running;
        _canManageMods = version != null && !version.IsFileMode;
        LaunchText = version == null
            ? "启动游戏"
            : version.IsFileMode
                ? "用 file:// 打开"
                : running && !helperOnly ? "打开游戏页面" : "启动游戏";
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        (LaunchCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ImportModsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RefreshModsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteModsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ShowProblemsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Navigate(object? parameter)
    {
        var key = parameter as string ?? "versions";
        IsVersionsSelected = key == "versions";
        IsSettingsSelected = key == "settings";
        IsDownloadSelected = key == "download";
        IsBackupSelected = key == "backup";
        IsDiagnosticSelected = key == "diagnostic";
        IsStorySelected = key == "story";
        if (IsDownloadSelected) Download.EnsureLoaded();
        if (IsBackupSelected) Backup.EnsureLoaded();
        if (IsStorySelected) Story.EnsureLoaded();
        if (IsDiagnosticSelected)
        {
            Diagnostic.EnsureLoaded();
            Diagnostic.Activate();
        }
        else
        {
            Diagnostic.Deactivate();
        }
        PlaceholderText = string.Empty;
        Navigated?.Invoke();
    }

    // ---------- 目录监听 ----------

    private void EnsureWatcher(VersionInfo version)
    {
        if (_watchedVersionDir != null &&
            string.Equals(_watchedVersionDir, version.FullPath, StringComparison.OrdinalIgnoreCase)) return;

        _modsWatcher?.Dispose();
        _versionWatcher?.Dispose();

        try
        {
            var modsDir = ModService.ModsDir(version.FullPath);
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

            _modsWatcher = new FileSystemWatcher(modsDir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            _modsWatcher.Created += OnModsFolderChanged;
            _modsWatcher.Deleted += OnModsFolderChanged;
            _modsWatcher.Changed += OnModsFolderChanged;
            _modsWatcher.Renamed += (_, e) => OnModsFolderChanged(null, e);

            _versionWatcher = new FileSystemWatcher(version.FullPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _versionWatcher.Created += OnVersionManifestChanged;
            _versionWatcher.Deleted += OnVersionManifestChanged;
            _versionWatcher.Changed += OnVersionManifestChanged;

            _watchedVersionDir = version.FullPath;
        }
        catch (Exception ex)
        {
            LogError("监听 mods 目录失败", ex);
        }
    }

    private void OnModsFolderChanged(object? sender, FileSystemEventArgs e)
    {
        if (!IsModsWatchRelevant(e.Name)) return;
        ScheduleModsReload();
    }

    private void OnVersionManifestChanged(object? sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        if (!name.Equals("modList.json", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("mods-state.json", StringComparison.OrdinalIgnoreCase)) return;
        ScheduleModsReload();
    }

    private static bool IsModsWatchRelevant(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var file = Path.GetFileName(name);
        if (file.Length == 0) return false;
        if (file.Equals("mods-state.json", StringComparison.OrdinalIgnoreCase) ||
            file.Equals("modList.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("~", StringComparison.Ordinal)) return false;
        return ModService.IsModPackage(file);
    }

    private void ScheduleModsReload()
    {
        if (DateTime.Now < _ignoreWatchUntil) return;
        _watchTimer.Stop();
        _watchTimer.Start();
    }

    private void RunModsReload()
    {
        _watchTimer.Stop();
        if (DateTime.Now < _ignoreWatchUntil) return;

        var version = _selectedVersion?.Info;
        if (version == null || version.IsFileMode) return;
        if (_watchedVersionDir != null &&
            !string.Equals(_watchedVersionDir, version.FullPath, StringComparison.OrdinalIgnoreCase)) return;

        MarkInternalModsChange();
        LoadMods(version);
        Log("检测到 mods 目录被启动器以外的操作修改，已自动刷新模组列表。");
    }

    private void MarkInternalModsChange() => _ignoreWatchUntil = DateTime.Now.AddMilliseconds(1600);

    // ---------- 关页检测 ----------

    private void OnSessionLost(string folder) =>
        Dispatcher.UIThread.Post(() => _ = HandleSessionLostAsync(folder));

    private async Task HandleSessionLostAsync(string folder)
    {
        if (!ServerManager.IsRunning(folder)) return;

        var name = Versions.FirstOrDefault(v => v.Info.Folder == folder)?.Info.DisplayName
                   ?? Path.GetFileName(folder.TrimEnd('\\', '/'));
        var behavior = (AppConfig.Load().General.ClosePageBehavior ?? "close").Trim().ToLowerInvariant();
        if (behavior == "ask" &&
            !await PlatformServices.Dialogs.ConfirmAsync(
                $"「{name}」的页面已全部关闭。\n\n要停止这个版本的本地服务器吗？\n选择「取消」会保留服务器，直到手动停止。",
                "页面已关闭", "停止服务器"))
        {
            Log("页面已全部关闭，按设置保留本地服务器：" + name);
            return;
        }

        BrowserService.Close(folder);
        ServerManager.Stop(folder);
        Log("页面已全部关闭，已停止本地服务器：" + name);
        RefreshStatuses();
    }

    // ---------- 配置 / 日志 ----------

    private static HashSet<string> LoadIgnoredConflicts(string folder)
    {
        var entry = AppConfig.Load().Versions.FirstOrDefault(v => v.Folder == folder);
        return new HashSet<string>(entry?.IgnoredConflicts ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> LoadIgnoredVersionIssues(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entry = AppConfig.Load().Versions.FirstOrDefault(v => v.Folder == folder);
        return new HashSet<string>(entry?.IgnoredVersionIssues ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private void Log(string message)
    {
        try { LogService.Append(message); } catch { }
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLogLine(message));
            return;
        }
        AppendLogLine(message);
    }

    private void AppendLogLine(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        var text = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
        var lines = text.Split(Environment.NewLine);
        if (lines.Length > 300) text = string.Join(Environment.NewLine, lines[^300..]);
        LogText = text;
    }

    private void LogError(string context, Exception ex)
    {
        Log(context + "：" + ex.Message);
        try { LogService.Error(context, ex); } catch { }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _watchTimer.Stop();
        _modsWatcher?.Dispose();
        _versionWatcher?.Dispose();
        ModService.InternalWriteStarted -= MarkInternalModsChange;
        BrowserSessionMonitor.Instance.SessionLost -= OnSessionLost;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>版本列表项：把 Core 的版本信息转成界面可绑定的只读视图。</summary>
public sealed class VersionItemVm : INotifyPropertyChanged
{
    public VersionItemVm(VersionInfo info) => Info = info;

    public event PropertyChangedEventHandler? PropertyChanged;

    public VersionInfo Info { get; }

    public string DisplayName => Info.DisplayName;

    public string LoaderType => Info.IsFileMode
        ? string.Empty
        : Info.Folder.StartsWith("Neo-", StringComparison.OrdinalIgnoreCase) ? "Neo" : "JML";

    public bool IsRunning => !Info.IsFileMode && ServerManager.IsRunning(Info.Folder);

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

    public string Subtitle => Info.IsFileMode
        ? "file:// 模式 · 与所有本地网页共享浏览器数据"
        : $"端口 {Info.Port} · {LoaderType} · {Info.GameVersion ?? "未知版本"}";

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>模组列表项：承接启用状态与两类问题提示。</summary>
public sealed class ModItemVm : INotifyPropertyChanged
{
    private string _issueText = string.Empty;
    private string _conflictText = string.Empty;

    public ModItemVm(ModInfo info) => Info = info;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>启用状态由用户改动后通知视图模型落盘；加载期间由视图模型忽略。</summary>
    public Action<ModItemVm>? EnabledChanged { get; set; }

    public ModInfo Info { get; }

    public bool Enabled
    {
        get => Info.Enabled;
        set
        {
            if (Info.Enabled == value) return;
            Info.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(StateText));
            EnabledChanged?.Invoke(this);
        }
    }

    public string StateText => Enabled ? "已启用" : "已停用";

    public string Title => Info.Title;

    public string Subtitle => Info.BootParsed
        ? Info.FileName
        : Info.FileName + " · 无法解析 boot.json";

    public string IssueText
    {
        get => _issueText;
        set
        {
            if (_issueText == value) return;
            _issueText = value;
            Raise(nameof(IssueText));
            Raise(nameof(HasIssue));
        }
    }

    public bool HasIssue => _issueText.Length > 0;

    public string ConflictText
    {
        get => _conflictText;
        set
        {
            if (_conflictText == value) return;
            _conflictText = value;
            Raise(nameof(ConflictText));
            Raise(nameof(HasConflict));
        }
    }

    public bool HasConflict => _conflictText.Length > 0;

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
