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

public partial class MainWindow : Window
{
    private readonly ObservableCollection<VersionVm> _versions = new();
    private readonly ObservableCollection<ModVm> _mods = new();
    private readonly ObservableCollection<GameRelease> _gameReleases = new();
    private readonly ObservableCollection<MarketMod> _marketAll = new();
    private readonly ObservableCollection<MarketMod> _marketView = new();
    private readonly ObservableCollection<VersionVm> _targets = new();
    private readonly ObservableCollection<string> _categories = new();
    private readonly ObservableCollection<GameRelease> _marketReleases = new();
    private readonly ObservableCollection<BackupEntry> _backupEntries = new();
    private readonly Dictionary<string, List<GameRelease>> _releaseCache = new();
    private readonly Dictionary<string, string> _readmeCache = new();
    private VersionVm? _selected;
    private bool _loadingMods;
    private bool _gameLoaded;
    private bool _marketLoaded;
    private bool _downloadBusy;
    private bool _loadingBackupSettings;
    private bool _loadingGeneralSettings;
    private bool _loadingLegacyTarget;
    private bool _marketLoading;
    private bool _loadingMarketPrefs = true;
    private string _projectUrl = "";
    private bool _gameLoading;
    private MarketCatalog? _marketCatalog;
    private List<ModConflict> _conflicts = new();
    private List<ModMissingDependency> _missingDeps = new();
    private List<ModVersionMismatch> _versionMismatches = new();
    private List<ModDuplicate> _duplicates = new();
    private int _conflictCheckToken;
    private bool _conflictDialogOpen;

    // mods 目录的外部改动监听：FileSystemWatcher + 防抖
    private FileSystemWatcher? _modsWatcher;
    private FileSystemWatcher? _versionWatcher;
    private DispatcherTimer? _modsWatchTimer;
    private string? _watchedVersionDir;
    private DateTime _ignoreModsWatchUntil = DateTime.MinValue;

    // 设置页当前所在分类路径（空表示分类首页）
    private readonly List<string> _settingsPath = new();

    public MainWindow()
    {
        // 防止 XAML 初始化时 ComboBox 的 SelectionChanged 提前把设置写回文件
        _loadingBackupSettings = true;
        InitializeComponent();
        _loadingBackupSettings = false;
        ThemeService.Attach(this);
        UiAnimations.AttachWindow(this);
        BrowserSessionMonitor.Instance.SessionLost += OnBrowserSessionLost;
        GameLogService.FatalDetected += OnGameFatal;
        ModService.InternalWriteStarted += MarkInternalModsChange;
        // 不再订阅浏览器进程退出：共用 Profile 时新命令会转发给已有实例并立刻退出，
        // 进程退出并不代表页面关闭，关页统一以页面心跳为准。
        Closed += (_, _) =>
        {
            StopModsWatcher();
            BrowserSessionMonitor.Instance.SessionLost -= OnBrowserSessionLost;
            GameLogService.FatalDetected -= OnGameFatal;
            ModService.InternalWriteStarted -= MarkInternalModsChange;
        };
        VersionList.ItemsSource = _versions;
        ModList.ItemsSource = _mods;
        GameReleaseList.ItemsSource = _gameReleases;
        MarketList.ItemsSource = _marketView;
        MarketTargetBox.ItemsSource = _targets;
        MarketCategoryBox.ItemsSource = _categories;
        MarketVersionBox.ItemsSource = _marketReleases;
        BackupTargetBox.ItemsSource = _targets;
        DiagTargetBox.ItemsSource = _targets;
        LegacyTargetBox.ItemsSource = _targets;
        BackupList.ItemsSource = _backupEntries;
        // 选中项左侧的小三角：随选中项滑动，滚动/尺寸变化时跟随，不做动画
        VersionList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => UpdateVersionIndicator(false)));
        VersionList.SizeChanged += (_, _) => UpdateVersionIndicator(false);
        ApplySubtitle(AppConfig.Load().General.Subtitle);
        Loaded += (_, _) =>
        {
            ReloadVersions();
            LoadBackupSettings();
            LoadGeneralSettings();
            LoadMarketSearchPrefs();
            LoadCredits();
            LoadPlayerAvatar();
            LoadSkinSettings();
            NavigateSettings();
            // 启动时按设置里的时序在后台预取索引，不阻塞界面
            _ = PrefetchIndexesAsync();
        };
    }

    // ---------- 版本列表 ----------

    private void ReloadVersions(string? selectFolder = null)
    {
        try
        {
            var keep = selectFolder ?? _selected?.Info.Folder;
            var scanned = VersionService.Scan();

            _selected = null;
            _versions.Clear();
            foreach (var info in scanned) _versions.Add(new VersionVm(info));

            VersionCountText.Text = $"{_versions.Count} 个";

            _targets.Clear();
            foreach (var vm in _versions.Where(v => !v.Info.IsFileMode)) _targets.Add(vm);

            VersionVm? target = null;
            if (keep != null) target = _versions.FirstOrDefault(v => v.Info.Folder == keep);
            target ??= _versions.FirstOrDefault(v => !v.Info.IsFileMode);
            target ??= _versions.FirstOrDefault();

            VersionList.SelectedItem = target;
            RefreshDetail();
            SyncMarketTarget();
            SyncBackupTarget();
            SyncDiagTarget();
        }
        catch (Exception ex)
        {
            Log("扫描版本失败：" + ex.Message);
        }
    }

    private void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshDetail();
        // 等布局完成后按选中项定位左侧小三角
        Dispatcher.BeginInvoke(new Action(() => UpdateVersionIndicator(true)), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 把选中版本左侧的小三角移到该条目中部；滚动或窗口尺寸变化时只跟随不播放动画。
    /// 颜色用两个重叠三角交叉淡入淡出：未运行是主题蓝色，运行中是绿色。
    /// </summary>
    private void UpdateVersionIndicator(bool animate)
    {
        try
        {
            UpdateVersionIndicatorCore(animate);
        }
        catch
        {
            // 条目还没进入可视树（刚刷新列表）时取相对坐标会失败，先隐藏，等滚动/布局回调再定位
            VersionIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateVersionIndicatorCore(bool animate)
    {
        var container = VersionList.SelectedItem != null
            ? VersionList.ItemContainerGenerator.ContainerFromItem(VersionList.SelectedItem) as ListBoxItem
            : null;
        if (container == null || container.ActualHeight <= 0 || VersionListHost.ActualHeight <= 0)
        {
            VersionIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var top = container.TransformToAncestor(VersionListHost).Transform(new Point(0, 0)).Y;
        var targetY = top + (container.ActualHeight - 12) / 2;
        if (targetY < -2 || targetY > VersionListHost.ActualHeight - 10)
        {
            VersionIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        VersionIndicator.Visibility = Visibility.Visible;
        var transform = VersionIndicatorMove;
        var running = VersionList.SelectedItem is VersionVm vm && vm.IsRunning;
        var targetOpacity = running ? 1.0 : 0.0;

        if (animate && UiAnimations.Active)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(220);
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(transform.Y, targetY, duration) { EasingFunction = ease });
            IndicatorRunning.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(IndicatorRunning.Opacity, targetOpacity, duration) { EasingFunction = ease });
            return;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.Y = targetY;
        IndicatorRunning.BeginAnimation(UIElement.OpacityProperty, null);
        IndicatorRunning.Opacity = targetOpacity;
    }

    private void VersionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VersionList.SelectedItem is VersionVm) LaunchSelected();
    }

    /// <summary>旧版 file:// 数据改指向哪个版本的 html。</summary>
    private void LegacyTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLegacyTarget) return;
        var vm = LegacyTargetBox.SelectedItem as VersionVm;
        if (vm == null) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.LegacyFolder = vm.Info.Folder;
            AppConfig.Save(cfg);
            Log("旧版 file:// 数据改为打开：" + vm.Info.Folder);
            ReloadVersions(VersionService.LegacyFileFolder);
        }
        catch (Exception ex)
        {
            Log("保存旧版 file:// 目标失败：" + ex.Message);
        }
    }

    private void RefreshDetail()
    {
        _selected = VersionList.SelectedItem as VersionVm;
        DetailRoot.IsEnabled = _selected != null;
        _mods.Clear();
        _conflicts.Clear();
        _missingDeps.Clear();
        _versionMismatches.Clear();

        if (_selected == null)
        {
            TitleText.Text = "未选择版本";
            SubText.Text = "在左侧选择一个版本，或点「添加版本」把已有的游戏目录加进来。";
            BadgeMode.Text = string.Empty;
            PathText.Text = string.Empty;
            ModCountText.Text = string.Empty;
            LegacyTargetPanel.Visibility = Visibility.Collapsed;
            UpdateButtons();
            return;
        }

        var version = _selected.Info;
        _selected.Refresh();

        TitleText.Text = version.DisplayName;
        SubText.Text = version.IsFileMode
            ? "用 file:// 直接打开：浏览器会把所有本地网页算作同一个来源，存档与模组开关无法和其它版本隔离，仅用于找回旧数据。"
            : $"游戏版本 {version.GameVersion ?? "未知"} · 本地端口 {version.Port}";
        BadgeMode.Text = version.IsFileMode ? "file:// 共享" : "127.0.0.1:" + version.Port;
        PathText.Text = version.IsFileMode ? version.HtmlPath : version.FullPath;

        // file:// 伪版本可以改指向哪个版本的 html
        _loadingLegacyTarget = true;
        LegacyTargetPanel.Visibility = version.IsFileMode ? Visibility.Visible : Visibility.Collapsed;
        LegacyTargetBox.SelectedItem = version.IsFileMode
            ? _targets.FirstOrDefault(v => v.Info.HtmlPath == version.HtmlPath)
            : null;
        _loadingLegacyTarget = false;

        if (version.IsFileMode)
        {
            ModCountText.Text = "file:// 模式下不支持磁盘模组管理";
        }
        else
        {
            LoadMods(version);
        }

        UpdateButtons();
    }

    private void LoadMods(VersionInfo version)
    {
        var before = _mods.Count;
        _loadingMods = true;
        _mods.Clear();
        try
        {
            ModService.EnsureModList(version.FullPath);
            foreach (var mod in ModService.Load(version.FullPath)) _mods.Add(new ModVm(mod));
            ApplyModIssues();
        }
        catch (Exception ex)
        {
            Log("读取模组失败：" + ex.Message);
        }
        finally
        {
            _loadingMods = false;
        }
        UpdateModCount();
        UpdateModButtons();
        EnsureModsWatcher(version);
        FixModListScroll();
        if (_mods.Count != before) UiAnimations.Fade(ModList, 0.55, 1, 160);
        _ = CheckConflictsAsync();
    }

    /// <summary>
    /// 删除 / 刷新后 ListBox 的滚动范围偶尔不会立即收缩，这里强制重新测量并把偏移夹回有效范围，
    /// 避免「删掉一个模组后还能滚到已经不存在的底部」。
    /// </summary>
    private void FixModListScroll()
    {
        ModList.UpdateLayout();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ModList.UpdateLayout();
            var viewer = FindModListScrollViewer();
            if (viewer == null) return;
            viewer.InvalidateMeasure();
            viewer.UpdateLayout();
            if (viewer.VerticalOffset > viewer.ScrollableHeight)
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
        }));
    }

    private ScrollViewer? FindModListScrollViewer()
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(ModList);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is ScrollViewer viewer) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }

    /// <summary>给列表补上重复 / 缺少前置之类的提示。</summary>
    private void ApplyModIssues()
    {
        var infos = _mods.Select(m => m.Info).ToList();
        var threshold = ProblemService.NormalizeThreshold(AppConfig.Load().General.VersionGapThreshold);
        // 运行日志里实际出现的补丁失败，按当前启用模组过滤后并入版本不匹配列表
        var runtime = _selected == null
            ? new List<ModVersionMismatch>()
            : RuntimeIssueScanner.Scan(_selected.Info.FullPath, infos, _selected.Info.GameVersion);
        var runtimeByFile = runtime
            .Where(r => r.ModFile.Length > 0)
            .GroupBy(r => r.ModFile, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byName = _mods
            .Where(m => m.Info.Name.Length > 0)
            .GroupBy(m => DownloadService.NormalizeName(m.Info.Name), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var vm in _mods)
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
                    var gameVersion = _selected?.Info.GameVersion;
                    if (gameVersion != null && gameVersion.Length > 0 &&
                        !VersionUtil.Satisfies(dep.Version, gameVersion))
                    {
                        issues.Add($"游戏版本不满足（需要 {dep.Version}，当前 {gameVersion}）");
                    }
                    continue;
                }

                if (ModService.IsBuiltin(dep.Name)) continue;
                var target = _mods.FirstOrDefault(m => string.Equals(m.Info.Name, dep.Name,
                    StringComparison.OrdinalIgnoreCase));
                if (target == null || !target.Enabled)
                {
                    issues.Add($"缺少前置「{dep.Name}」");
                }
                else if (!VersionUtil.Satisfies(dep.Version, target.Info.Version))
                {
                    issues.Add($"前置「{dep.Name}」版本不满足（需要 {dep.Version}）");
                }
            }

            if (runtimeByFile.TryGetValue(vm.Info.FileName, out var runtimeIssue))
                issues.Add($"运行日志显示{runtimeIssue.RuntimeReason}");

            vm.IssueText = string.Join("；", issues);
        }

        _missingDeps = ProblemService.MissingDependencies(infos);
        _versionMismatches = ProblemService.VersionMismatches(infos, _selected?.Info.GameVersion, threshold);
        foreach (var issue in runtime)
        {
            // boot.json 已经报过的模组不再重复列一条
            var duplicate = issue.ModFile.Length > 0
                ? _versionMismatches.Any(m => string.Equals(m.ModFile, issue.ModFile,
                    StringComparison.OrdinalIgnoreCase))
                : _versionMismatches.Any(m => m.ModFile.Length == 0 &&
                    string.Equals(m.ModName, issue.ModName, StringComparison.OrdinalIgnoreCase));
            if (duplicate) continue;
            _versionMismatches.Add(issue);
        }
        _duplicates = ProblemService.Duplicates(infos);
        UpdateProblemButton();
    }

    // ---------- 可能存在的问题 ----------

    private List<ModConflict> ActiveConflicts => _conflicts.Where(c => !c.Ignored).ToList();

    private int TotalProblems =>
        ActiveConflicts.Count + _missingDeps.Count + _versionMismatches.Count + _duplicates.Count;

    private int RedProblems =>
        ActiveConflicts.Count + _missingDeps.Count + _duplicates.Count +
        _versionMismatches.Count(m => m.Severity == ProblemSeverity.Red);

    private bool HasProblems => TotalProblems > 0;

    /// <summary>重新扫描已启用模组的简介 / README 里的冲突声明；只警告不阻止。</summary>
    private async Task CheckConflictsAsync()
    {
        var version = _selected?.Info;
        var token = ++_conflictCheckToken;

        if (version == null || version.IsFileMode || _marketCatalog == null || _marketCatalog.Mods.Count == 0)
        {
            _conflicts = new List<ModConflict>();
            ApplyConflictText();
            UpdateProblemButton();
            return;
        }

        var mods = _mods.Select(m => m.Info).ToList();
        List<ModConflict> found;
        try
        {
            found = await ModConflictService.CheckAsync(mods, _marketCatalog, Log);
        }
        catch (Exception ex)
        {
            Log("检查模组冲突失败：" + ex.Message);
            found = new List<ModConflict>();
        }

        if (token != _conflictCheckToken) return;
        if (_selected?.Info.Folder != version.Folder) return;

        var ignored = LoadIgnoredConflicts(version.Folder);
        foreach (var conflict in found) conflict.Ignored = ignored.Contains(conflict.PairKey);
        _conflicts = found;
        ApplyConflictText();
        UpdateProblemButton();
    }

    /// <summary>把冲突写到模组列表的红色提示行；停用的一方不在启用集合里，自然消失。</summary>
    private void ApplyConflictText()
    {
        foreach (var vm in _mods)
        {
            var others = new List<string>();
            foreach (var conflict in _conflicts)
            {
                if (conflict.Ignored) continue;
                if (conflict.LeftFile.Length > 0 &&
                    string.Equals(conflict.LeftFile, vm.Info.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    others.Add(conflict.RightName);
                }
                else if (conflict.RightFile.Length > 0 &&
                         string.Equals(conflict.RightFile, vm.Info.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    others.Add(conflict.LeftName);
                }
            }

            vm.ConflictText = others.Count == 0
                ? ""
                : "可能与「" + string.Join("」「", others.Distinct(StringComparer.OrdinalIgnoreCase)) + "」冲突";
        }
    }

    private void UpdateProblemButton()
    {
        if (ConflictBtn == null) return;
        var total = TotalProblems;
        var red = RedProblems;

        if (total == 0)
        {
            ConflictBtn.Style = (Style)FindResource("IconMutedButton");
            ConflictBtn.Content = "暂未检测到问题";
            ConflictBtn.ToolTip = "当前未检测到冲突、重复、版本不匹配或缺少前置，点此查看检测结果";
            return;
        }

        ConflictBtn.Style = (Style)FindResource(red > 0 ? "IconDangerAlertButton" : "IconWarningButton");
        ConflictBtn.Content = $"可能存在问题 {total}";
        ConflictBtn.ToolTip = red > 0
            ? $"{red} 个问题可能导致游戏无法正常运行，点此查看处理建议"
            : $"{total} 个提醒项可能影响模组运行，点此查看详情";
    }

    private void Conflict_Click(object sender, RoutedEventArgs e) => ShowConflictDialog(launchMode: false);

    /// <summary>打开问题窗口；返回是否继续启动（仅「仍然启动」为 true）。</summary>
    private bool ShowConflictDialog(bool launchMode)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return true;

        var proceed = false;
        _conflictDialogOpen = true;
        try
        {
            var dialog = new ConflictDialog(
                _conflicts, _missingDeps, _versionMismatches, _duplicates, launchMode,
                DisableConflictSide, EnableMissingDependencyAsync,
                SetConflictIgnored, LocateDependencyInMarket, CurrentProblemSet)
            {
                Owner = this,
            };
            // 取消也要保持取消：不能因为问题已被停用就当成允许启动
            proceed = dialog.ShowDialog() == true;
        }
        finally
        {
            _conflictDialogOpen = false;
        }

        ApplyConflictText();
        UpdateProblemButton();
        _ = CheckConflictsAsync();
        return proceed;
    }

    /// <summary>在问题窗口里点了「停用某某」：直接改启用状态并落盘，然后移除相关条目。</summary>
    private bool DisableConflictSide(string fileName)
    {
        var vm = _mods.FirstOrDefault(m =>
            string.Equals(m.Info.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (vm == null) return false;

        vm.Enabled = false;
        PersistMods();
        _conflicts.RemoveAll(c =>
            string.Equals(c.LeftFile, fileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.RightFile, fileName, StringComparison.OrdinalIgnoreCase));
        _missingDeps.RemoveAll(m =>
            string.Equals(m.ModFile, fileName, StringComparison.OrdinalIgnoreCase));
        _versionMismatches.RemoveAll(m =>
            string.Equals(m.ModFile, fileName, StringComparison.OrdinalIgnoreCase));
        ApplyConflictText();
        UpdateProblemButton();
        return true;
    }

    /// <summary>问题窗口里点了「启用前置」：启停状态落盘后按最新检查结果刷新窗口。</summary>
    private async Task<bool> EnableMissingDependencyAsync(string name)
    {
        var vm = _mods.FirstOrDefault(m =>
            string.Equals(m.Info.Name, name, StringComparison.OrdinalIgnoreCase));
        if (vm == null || vm.Enabled) return false;

        vm.Enabled = true;
        PersistMods();
        Log($"已在问题窗口启用前置「{vm.Info.Name}」。");

        // 启用模组可能引入新的冲突，重新核对一次再让窗口重建内容
        if (_selected != null && !_selected.Info.IsFileMode &&
            _marketCatalog != null && _marketCatalog.Mods.Count > 0)
        {
            await CheckConflictsAsync();
        }
        return true;
    }

    /// <summary>给问题窗口提供最新一批检查结果，用于停用 / 启用后重建分页内容。</summary>
    private (IEnumerable<ModConflict>, IEnumerable<ModMissingDependency>,
        IEnumerable<ModVersionMismatch>, IEnumerable<ModDuplicate>)
        CurrentProblemSet() => (_conflicts, _missingDeps, _versionMismatches, _duplicates);

    /// <summary>从问题窗口跳到下载中心的模组市场，并按名字搜索缺的前置。</summary>
    private void LocateDependencyInMarket(string name)
    {
        NavDownload.IsChecked = true;
        DownloadNavMod.IsChecked = true;
        SyncMarketTarget();
        MarketSearchBox.Text = name;
        if (!_marketLoaded) _ = RefreshMarketAsync();
        Log("已定位到下载中心的模组市场：" + name);
    }

    private static HashSet<string> LoadIgnoredConflicts(string folder)
    {
        var entry = AppConfig.Load().Versions.FirstOrDefault(v => v.Folder == folder);
        return new HashSet<string>(entry?.IgnoredConflicts ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>忽略记录按版本保存在 launcher-versions.json 的 ignoredConflicts 里。</summary>
    private void SetConflictIgnored(ModConflict conflict, bool ignored)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;

        var cfg = AppConfig.Load();
        var entry = AppConfig.GetOrAdd(cfg, version.Folder);
        entry.IgnoredConflicts ??= new List<string>();
        entry.IgnoredConflicts.RemoveAll(k =>
            string.Equals(k, conflict.PairKey, StringComparison.OrdinalIgnoreCase));
        if (ignored) entry.IgnoredConflicts.Add(conflict.PairKey);
        AppConfig.Save(cfg);

        conflict.Ignored = ignored;
        ApplyConflictText();
        UpdateProblemButton();
    }

    private void UpdateModButtons()
    {
        var selected = ModList.SelectedItems.Cast<ModVm>().ToList();
        var usable = _selected != null && !_selected.Info.IsFileMode;
        ImportModBtn.IsEnabled = usable;
        ModDetailBtn.IsEnabled = usable && selected.Count == 1;
        RenameModBtn.IsEnabled = usable && selected.Count == 1;
        DeleteModBtn.IsEnabled = usable && selected.Count > 0;
        BatchEnableBtn.IsEnabled = usable && selected.Count > 0;
        BatchDisableBtn.IsEnabled = usable && selected.Count > 0;
    }

    private void UpdateModCount()
    {
        var enabled = _mods.Count(m => m.Enabled);
        ModCountText.Text = _mods.Count == 0
            ? "（mods 目录为空）"
            : $"共 {_mods.Count} 个 · 启用 {enabled} 个";
    }

    private void UpdateButtons()
    {
        var version = _selected?.Info;
        var running = version != null && !version.IsFileMode && ServerManager.IsRunning(version.Folder);
        var helperOnly = version != null && running && ServerManager.IsHelperOnly(version.Folder);

        LaunchBtn.IsEnabled = version != null;
        LaunchBtn.Content = version == null
            ? "启动游戏"
            : version.IsFileMode
                ? "用 file:// 打开"
                : running && !helperOnly ? "打开游戏页面" : "启动游戏";
        StopBtn.IsEnabled = running;
        SavesBtn.IsEnabled = version != null && !version.IsFileMode;
        OpenFolderBtn.IsEnabled = version != null && !version.IsFileMode;
        ExportBtn.IsEnabled = version != null;
        CheckBtn.IsEnabled = version != null && !version.IsFileMode;
        DeleteBtn.IsEnabled = version != null && !version.IsFileMode;
        RefreshModsBtn.IsEnabled = version != null && !version.IsFileMode;
        ConflictBtn.IsEnabled = version != null && !version.IsFileMode;
        UpdateModButtons();
        UpdateProblemButton();
    }

    private void RefreshListStates()
    {
        foreach (var vm in _versions) vm.Refresh();
        if (_selected != null)
        {
            _selected.Refresh();
            SubText.Text = _selected.Info.IsFileMode
                ? SubText.Text
                : $"游戏版本 {_selected.Info.GameVersion ?? "未知"} · 本地端口 {_selected.Info.Port}";
            BadgeMode.Text = _selected.Info.IsFileMode ? "file:// 共享" : "127.0.0.1:" + _selected.Info.Port;
        }
        UpdateButtons();
        // 启动/停止后三角颜色跟着变（蓝 ↔ 绿），等布局完成再定位
        Dispatcher.BeginInvoke(new Action(() => UpdateVersionIndicator(true)), DispatcherPriority.Loaded);
    }

    // ---------- 启动 / 停止 ----------

    private void Launch_Click(object sender, RoutedEventArgs e) => LaunchSelected();

    private async void LaunchSelected()
    {
        var vm = VersionList.SelectedItem as VersionVm;
        if (vm == null) return;
        var version = vm.Info;

        if (version.IsFileMode)
        {
            if (!AppDialog.Confirm(
                    "file:// 模式下，浏览器会把本机所有 file:// 页面当成同一个来源，\n" +
                    "存档和模组开关会和别的本地网页互相干扰。\n\n只有需要找回旧数据时才建议这样做。继续打开吗？",
                    "file:// 共享提示", "继续打开")) return;

            try
            {
                var opened = BrowserService.OpenGame(version, new Uri(version.HtmlPath).AbsoluteUri);
                if (!opened.Opened)
                {
                    AppDialog.Warn("没有可用的浏览器，无法打开页面。");
                    return;
                }
                Log("已用 file:// 打开：" + version.HtmlPath);
            }
            catch (Exception ex)
            {
                AppDialog.Error("打开失败：" + ex.Message);
            }
            return;
        }

        try
        {
            if (!File.Exists(version.HtmlPath))
            {
                AppDialog.Warn("这个版本目录里找不到游戏本体 HTML，无法启动。");
                return;
            }

            // 每次启动前重新核对；存在冲突 / 版本不匹配 / 缺前置就弹窗提示
            if (_marketCatalog != null) await CheckConflictsAsync();
            if (HasProblems && !ShowConflictDialog(launchMode: true)) return;

            ModService.Apply(version.FullPath);
            var server = ServerManager.EnsureStarted(version);
            BrowserService.OpenGame(version, server.GameUrl);

            Log($"已启动 {version.DisplayName}：{server.GameUrl}");
            LoadMods(version);
            RefreshListStates();

            var cfg = AppConfig.Load();
            if (cfg.Backup.Enabled)
            {
                // 自动备份是刻意不阻塞启动的 fire-and-forget，用弃元显式忽略返回值
                _ = Task.Run(() =>
                {
                    try
                    {
                        BackupService.AutoBackup(version, Log);
                        Dispatcher.BeginInvoke(new Action(RefreshBackups));
                    }
                    catch (Exception ex)
                    {
                        Log("自动备份失败：" + ex.Message);
                    }
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
            Log("启动失败：" + ex.Message);
            AppDialog.Error("启动失败：" + ex.Message);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var vm = VersionList.SelectedItem as VersionVm;
        if (vm == null || vm.Info.IsFileMode) return;
        BrowserService.Close(vm.Info.Folder);
        ServerManager.Stop(vm.Info.Folder);
        Log("已停止：" + vm.Info.DisplayName);
        RefreshListStates();
    }

    // ---------- 模组 ----------

    private void ModCheck_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox box) return;
        // 勾选框只负责启用/停用，不参与列表选中
        e.Handled = true;
        if (_loadingMods) return;
        box.IsChecked = !(box.IsChecked ?? false);
        PersistMods();
    }

    private void ModList_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (ModList.SelectedItems.Count == 0) return;
        var source = e.OriginalSource as DependencyObject;
        if (source == null) return;
        // 点在模组行或滚动条上时保持选区，点空白处才清空
        if (ItemsControl.ContainerFromElement(ModList, source) is ListBoxItem) return;
        if (IsScrollBarPart(source)) return;
        ModList.SelectedItems.Clear();
    }

    private static bool IsScrollBarPart(DependencyObject node)
    {
        while (node != null)
        {
            if (node is System.Windows.Controls.Primitives.ScrollBar ||
                node is System.Windows.Controls.Primitives.RepeatButton ||
                node is System.Windows.Controls.Primitives.Thumb)
                return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void MarketList_PreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (MarketList.SelectedItem == null) return;
        var source = e.OriginalSource as DependencyObject;
        if (source == null) return;
        // 点在模组行或滚动条上时保持选中，点空白处才取消选中
        if (ItemsControl.ContainerFromElement(MarketList, source) is ListBoxItem) return;
        if (IsScrollBarPart(source)) return;
        MarketList.UnselectAll();
    }

    private void ModList_PreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(ModList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (item?.DataContext is not ModVm vm) return;
        // 右键点到已选中的行时保留多选，点到未选中的行则只选中它
        if (!ModList.SelectedItems.Contains(vm))
        {
            ModList.SelectedItems.Clear();
            ModList.SelectedItem = vm;
        }
    }

    private void ModList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ModList.SelectAll();
            e.Handled = true;
        }
    }

    private void BatchEnable_Click(object sender, RoutedEventArgs e) => SetSelectedEnabled(true);

    private void BatchDisable_Click(object sender, RoutedEventArgs e) => SetSelectedEnabled(false);

    private void SetSelectedEnabled(bool enabled)
    {
        if (_loadingMods) return;
        var targets = ModList.SelectedItems.Cast<ModVm>().ToList();
        if (targets.Count == 0) return;

        var changed = 0;
        foreach (var vm in targets)
        {
            if (vm.Enabled == enabled) continue;
            vm.Enabled = enabled;
            changed++;
        }

        if (changed == 0) return;
        PersistMods();
        Log($"已将 {changed} 个模组{(enabled ? "启用" : "停用")}。");
    }

    private void ModMenuUp_Click(object sender, RoutedEventArgs e) => MoveSelectedMod(-1);

    private void ModMenuDown_Click(object sender, RoutedEventArgs e) => MoveSelectedMod(1);

    private void MoveSelectedMod(int delta)
    {
        if (ModList.SelectedItems.Count != 1) return;
        if (ModList.SelectedItem is ModVm vm) MoveModVm(vm, delta);
    }

    private void ModMenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Info.IsFileMode) return;
        ServerManager.OpenFolder(ModService.ModsDir(_selected.Info.FullPath));
    }

    private void ModUp_Click(object sender, RoutedEventArgs e) => MoveMod(sender, -1);

    private void ModDown_Click(object sender, RoutedEventArgs e) => MoveMod(sender, 1);

    private void MoveMod(object sender, int delta)
    {
        var vm = (sender as FrameworkElement)?.DataContext as ModVm;
        if (vm == null) return;
        MoveModVm(vm, delta);
    }

    private void MoveModVm(ModVm vm, int delta)
    {
        var index = _mods.IndexOf(vm);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _mods.Count) return;
        _mods.Move(index, target);
        ModList.SelectedItem = vm;
        PersistMods();
    }

    private void PersistMods()
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;
        MarkInternalModsChange();
        try
        {
            ModService.Save(version.FullPath, _mods.Select(m => m.Info));
            UpdateModCount();
            // 启用/停用、排序、导入、删除后立刻重算前置提示，不必等重新读取列表
            ApplyModIssues();
            Log($"已更新 modList.json（启用 {_mods.Count(m => m.Enabled)} 个）");
            // mods 发生变动（启用/停用、排序、导入、删除）后重新核对可能冲突
            if (!_conflictDialogOpen) _ = CheckConflictsAsync();
        }
        catch (Exception ex)
        {
            Log("保存模组状态失败：" + ex.Message);
        }
    }

    private void RefreshMods_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Info.IsFileMode) return;
        LoadMods(_selected.Info);
        Log("已重新读取磁盘模组。");
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateModButtons();

    private void ModList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ModDetail_Click(sender, e);

    private void ModList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    _selected != null && !_selected.Info.IsFileMode
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ModList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        ImportModPaths(paths);
        e.Handled = true;
    }

    private void ImportMod_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入模组",
            Filter = "模组包 (*.zip;*.modpack)|*.zip;*.modpack|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        ImportModPaths(dialog.FileNames);
    }

    private void ImportModPaths(IEnumerable<string> paths)
    {
        if (_selected == null || _selected.Info.IsFileMode) return;
        var version = _selected.Info;
        var zips = paths.Where(ModService.IsModPackage).ToList();
        if (zips.Count == 0)
        {
            AppDialog.Info("只支持 .zip / .modpack 模组包。", "导入模组");
            return;
        }

        Log($"开始导入 {zips.Count} 个模组…");
        MarkInternalModsChange();
        var count = ModService.ImportFiles(version.FullPath, zips, Log);
        Log($"导入完成：成功 {count} 个。");
        LoadMods(version);
        RefreshMarketStatus();
    }

    private async void ModDetail_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItems.Count != 1) return;
        if (ModList.SelectedItem is not ModVm vm) return;
        var detail = ModService.ReadDetail(vm.Info);
        var needAuthor = detail.Author.Length == 0;
        var needHomepage = detail.Homepage.Length == 0;

        // boot.json 里没有作者/主页时，用市场索引补全；市场还没加载就先拉一次索引，
        // 这样不必先去下载中心点一遍模组市场，详情窗也能显示「打开主页」。
        if (_marketAll.Count == 0 && (needHomepage || needAuthor))
        {
            await RefreshMarketAsync();
        }

        var normalized = DownloadService.NormalizeName(vm.Info.Name);
        var market = _marketAll.FirstOrDefault(m =>
            DownloadService.NormalizeName(m.Name) == normalized ||
            m.BootNames.Any(b => DownloadService.NormalizeName(b) == normalized));
        if (market != null)
        {
            if (needAuthor) detail.Author = market.Author;
            if (detail.Description.Length == 0) detail.Description = market.Description;
            if (needHomepage && market.Repo.Length > 0)
                detail.Homepage = "https://github.com/" + market.Repo;
        }

        // 只补写原本缺失的字段；开关在「设置」页
        if ((needAuthor || needHomepage) && AppConfig.Load().General.WriteModMetadata)
        {
            var writeAuthor = needAuthor ? detail.Author : "";
            var writeHomepage = needHomepage ? detail.Homepage : "";
            if (ModService.WriteBackMetadata(vm.Info, writeAuthor, writeHomepage))
                Log($"已把市场信息写回模组包：{vm.Info.FileName}");
        }

        ShowModDetail(detail);
    }

    private void RenameMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Info.IsFileMode) return;
        if (ModList.SelectedItems.Count != 1) return;
        if (ModList.SelectedItem is not ModVm vm) return;

        var input = PromptText("重命名模组", "新的文件名（不含 .zip 也可以）：",
            Path.GetFileNameWithoutExtension(vm.Info.FileName));
        if (input == null) return;

        try
        {
            MarkInternalModsChange();
            ModService.Rename(_selected.Info.FullPath, vm.Info.FileName, input);
            Log($"已重命名：{vm.Info.FileName} → {input}");
            LoadMods(_selected.Info);
        }
        catch (Exception ex)
        {
            AppDialog.Error("重命名失败：" + ex.Message);
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _selected.Info.IsFileMode) return;
        var targets = ModList.SelectedItems.Cast<ModVm>().ToList();
        if (targets.Count == 0) return;

        var fileNameList = string.Join("\n", targets.Select(m => "· " + m.Info.FileName));
        var message = targets.Count == 1
            ? $"确定删除模组「{targets[0].Info.Name}」吗？\n\n文件：{targets[0].Info.FileName}\n\n" +
              "只删除磁盘上的 zip，不会影响浏览器里的存档。"
            : $"确定删除选中的 {targets.Count} 个模组吗？\n\n{fileNameList}\n\n" +
              "只删除磁盘上的 zip，不会影响浏览器里的存档。";
        if (!AppDialog.Confirm(message, "删除模组", "删除", danger: true)) return;

        var deleted = 0;
        try
        {
            MarkInternalModsChange();
            foreach (var vm in targets)
            {
                ModService.Delete(_selected.Info.FullPath, vm.Info.FileName);
                deleted++;
            }
            Log($"已删除 {deleted} 个模组：{string.Join("、", targets.Select(m => m.Info.FileName))}");
            LoadMods(_selected.Info);
            RefreshMarketStatus();
        }
        catch (Exception ex)
        {
            AppDialog.Error("删除失败：" + ex.Message);
            LoadMods(_selected.Info);
        }
    }

    private void ShowModDetail(ModDetail detail)
    {
        var win = new Window
        {
            Title = "模组详情",
            Width = 760,
            Height = 640,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
            Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
        };
        ThemeService.Attach(win);
        AppDialog.AttachDialogChrome(win, 760);
        UiAnimations.AttachWindow(win);

        var root = new DockPanel { Margin = new Thickness(18) };
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(bottom, Dock.Bottom);

        if (!string.IsNullOrWhiteSpace(detail.Homepage))
        {
            var open = new Button
            {
                Content = "打开主页 / WIKI",
                Style = TryFindResource("FlatButton") as Style,
                MinWidth = 120,
                Margin = new Thickness(0, 0, 8, 0),
            };
            open.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(detail.Homepage) { UseShellExecute = true }); }
                catch { }
            };
            bottom.Children.Add(open);
        }

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(detail.Name) ? detail.FileName : detail.Name,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = $"版本 {detail.Version}　·　作者 {(detail.Author.Length > 0 ? detail.Author : "未知")}　·　{detail.FileName}",
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.7,
        });
        if (detail.Description.Length > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = detail.Description,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        header.Children.Add(new TextBlock
        {
            Text = ModService.IsNativePack(detail.FilePath)
                ? "前置：未知（.modpack 是 ModLoader 私有格式，启动器无法解析包内信息）"
                : detail.Dependencies.Count == 0
                    ? "前置：无"
                    : "前置：" + string.Join("、", detail.Dependencies.Select(d =>
                        d.Version.Length > 0 ? $"{d.Name} {d.Version}" : d.Name)),
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        DockPanel.SetDock(header, Dock.Top);

        var readme = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 6, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = MarkdownService.Render(detail.Readme.Length > 0
                ? detail.Readme
                : "（这个模组没有附带 README 文件）"),
        };

        if (bottom.Children.Count > 0) root.Children.Add(bottom);
        root.Children.Add(header);
        root.Children.Add(readme);
        win.Content = root;
        // 去掉「关闭」按钮后，Esc 直接关窗
        win.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) win.Close();
        };
        win.ShowDialog();
    }

    // ---------- 其他操作 ----------

    private void Saves_Click(object sender, RoutedEventArgs e)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;
        try
        {
            var server = ServerManager.EnsureStarted(version, saveHelperOnly: true);
            RefreshListStates();
            BrowserService.OpenHelper(version, server.SaveHelperUrl);
            Log("已打开存档助手：" + server.SaveHelperUrl);
        }
        catch (Exception ex)
        {
            AppDialog.Error("打开存档助手失败：" + ex.Message);
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;
        Log("开始完整性检查…");
        try
        {
            var serverState = ServerManager.IsRunning(version.Folder) ? "运行中" : "未运行";
            Log($"本地服务器：{serverState}（端口 {version.Port}）");
            foreach (var line in ModService.Check(version)) Log(line);
            ModService.Apply(version.FullPath);
            Log("已重新生成 modList.json。");
            LoadMods(version);
        }
        catch (Exception ex)
        {
            Log("检查失败：" + ex.Message);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;
        ServerManager.OpenFolder(version.FullPath);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var version = _selected?.Info;
        if (version == null) return;

        if (version.IsFileMode)
        {
            AppDialog.Info("file:// 伪版本只是用于找回旧浏览器数据，没有可导出的磁盘内容。");
            return;
        }

        var dialog = new ExportDialog(version) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var save = new SaveFileDialog
        {
            Title = "保存版本包",
            Filter = "DoL 版本包 (*.dolpack)|*.dolpack|Zip 压缩包 (*.zip)|*.zip",
            DefaultExt = ".dolpack",
            AddExtension = true,
            FileName = version.Folder + ".dolpack",
        };
        if (save.ShowDialog(this) != true) return;

        ExportBtn.IsEnabled = false;
        DeleteBtn.IsEnabled = false;
        Log($"开始导出到 {save.FileName} …");
        try
        {
            var includeGame = dialog.IncludeGame;
            var note = dialog.Note;
            var path = save.FileName;
            var info = version;

            await Task.Run(() => PackService.Export(info, includeGame, path, note, Log));

            Log("导出完成：" + path);
            AppDialog.Info("导出完成：\n" + path);
        }
        catch (Exception ex)
        {
            Log("导出失败：" + ex.Message);
            AppDialog.Error("导出失败：" + ex.Message);
        }
        finally
        {
            UpdateButtons();
        }
    }

    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择版本包",
            Filter = "DoL 版本包 (*.dolpack;*.zip)|*.dolpack;*.zip|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var hasGame = PackService.PeekHasGame(path);
        var target = _selected?.Info;

        if (!hasGame)
        {
            if (target == null || target.IsFileMode)
            {
                AppDialog.Info("这个包不含游戏本体，请先在左侧选中一个要接收模组的版本。");
                return;
            }

            if (!AppDialog.Confirm(
                    $"这个包不含游戏本体，将把包内模组导入到版本「{target.DisplayName}」。\n" +
                    "包内同名模组会被覆盖，是否继续？",
                    "导入模组包", "继续导入")) return;
        }

        ImportPackBtn.IsEnabled = false;
        AddVersionBtn.IsEnabled = false;
        Log("开始导入：" + path);
        try
        {
            var targetPath = hasGame ? null : target!.FullPath;
            var result = await Task.Run(() => PackService.Import(path, targetPath, Log));

            if (result.NewVersionFolder != null)
            {
                Log($"导入完成，新版本目录：{result.NewVersionFolder}（模组 {result.ModCount} 个）");
                ReloadVersions(result.NewVersionFolder);
            }
            else
            {
                Log($"导入完成，已写入 {result.ModCount} 个模组到当前版本。");
                ReloadVersions(target!.Folder);
            }
        }
        catch (Exception ex)
        {
            Log("导入失败：" + ex.Message);
            AppDialog.Error("导入失败：" + ex.Message);
        }
        finally
        {
            ImportPackBtn.IsEnabled = true;
            AddVersionBtn.IsEnabled = true;
        }
    }

    private async void AddVersion_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 Degrees of Lewdity.html 的版本目录",
        };
        if (dialog.ShowDialog(this) != true) return;

        var source = Path.GetFullPath(dialog.FolderName).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(source)) return;

        if (VersionService.FindHtml(source) == null)
        {
            AppDialog.Warn("这个目录里没有 .html 游戏本体，请选择包含 Degrees of Lewdity.html 的目录。");
            return;
        }

        var versionsRoot = Path.GetFullPath(Paths.VersionsDir).TrimEnd(Path.DirectorySeparatorChar);
        if (source.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            ReloadVersions(Path.GetFileName(source));
            return;
        }

        var name = Path.GetFileName(source);
        var dest = Path.Combine(Paths.VersionsDir, name);
        if (Directory.Exists(dest))
        {
            AppDialog.Warn($"versions 目录下已经有同名版本「{name}」，请先改名或删除旧版本。");
            return;
        }

        if (!AppDialog.Confirm(
                $"将把目录\n{source}\n\n复制到\n{dest}\n\n文件较多（含 60 MB 以上的游戏本体），可能需要一会儿。继续吗？",
                "添加版本", "开始复制")) return;

        AddVersionBtn.IsEnabled = false;
        ImportPackBtn.IsEnabled = false;
        Log("正在复制版本目录…");
        try
        {
            await Task.Run(() => PackService.CopyDirectory(source, dest));
            Log("复制完成：" + dest);
            ReloadVersions(name);
        }
        catch (Exception ex)
        {
            Log("复制失败：" + ex.Message);
            AppDialog.Error("复制失败：" + ex.Message);
            try { if (Directory.Exists(dest)) Directory.Delete(dest, true); } catch { }
        }
        finally
        {
            AddVersionBtn.IsEnabled = true;
            ImportPackBtn.IsEnabled = true;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var version = _selected?.Info;
        if (version == null || version.IsFileMode) return;

        if (!AppDialog.Confirm(
                $"确定删除版本「{version.DisplayName}」吗？\n\n目录：{version.FullPath}\n\n" +
                "该目录下的游戏本体和全部模组都会被永久删除；\n" +
                "该版本在浏览器里的缓存与存档数据也会被静默清理，不会弹出浏览器窗口。",
                "删除版本", "删除", danger: true)) return;

        try
        {
            BrowserService.Close(version.Folder);
            ServerManager.Stop(version.Folder);
            BrowserDataCleaner.CleanVersion(version, Log);
            Directory.Delete(version.FullPath, recursive: true);
            VersionService.RemoveEntry(version.Folder);
            Log("已删除：" + version.FullPath);
            ReloadVersions();
        }
        catch (Exception ex)
        {
            Log("删除失败：" + ex.Message);
            AppDialog.Error("删除失败：" + ex.Message);
        }
    }

    // ---------- 关页停服 ----------

    /// <summary>游戏页上报 Fatal 级日志：在启动器里弹出独立提示窗，确保用户不会错过。</summary>
    private void OnGameFatal(string versionName, string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Log($"游戏致命错误（{versionName}）：{message}");
            AppDialog.Warn(
                $"「{versionName}」出现致命错误：\n\n{message}\n\n" +
                "点开游戏页右侧的悬浮球可以查看完整日志与中文原因分析。",
                "游戏致命错误");
        }), DispatcherPriority.Normal);
    }

    private void OnBrowserSessionLost(string folder)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ServerManager.IsRunning(folder)) return;

            var vm = _versions.FirstOrDefault(v => v.Info.Folder == folder);
            var name = vm?.Info.DisplayName ?? Path.GetFileName(folder.TrimEnd('\\', '/'));

            var behavior = (AppConfig.Load().General.ClosePageBehavior ?? "close").Trim().ToLowerInvariant();
            if (behavior == "ask" &&
                !AppDialog.Confirm(
                    $"「{name}」的页面已全部关闭。\n\n要停止这个版本的本地服务器吗？\n选择「取消」会保留服务器，直到手动停止。",
                    "页面已关闭", "停止服务器"))
            {
                Log("页面已全部关闭，按设置保留本地服务器：" + name);
                return;
            }

            BrowserService.Close(folder);
            ServerManager.Stop(folder);
            Log("页面已全部关闭，已停止本地服务器：" + name);
            RefreshListStates();
        }), DispatcherPriority.Normal);
    }

    // ---------- 日志 / 关闭 ----------

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<string>(Log), message);
            return;
        }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        DownloadLogBox.AppendText(line);
        DownloadLogBox.ScrollToEnd();
        LogService.Append(message);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        var running = ServerManager.RunningFolders;
        if (running.Count == 0) return;

        if (!ConfirmCloseRunning(running))
        {
            e.Cancel = true;
            return;
        }
        ServerManager.StopAll();
    }

    /// <summary>关闭前确认：列出仍在运行的版本（最多 5 行，其余折叠成「另有 N 个」）。</summary>
    private bool ConfirmCloseRunning(IReadOnlyCollection<string> folders)
    {
        var names = folders.Select(folder =>
        {
            var vm = _versions.FirstOrDefault(v => v.Info.Folder == folder);
            if (vm == null) return Path.GetFileName(folder.TrimEnd('\\', '/'));
            return vm.Info.GameVersion != null ? $"DoL {vm.Info.GameVersion}" : vm.Info.DisplayName;
        }).ToList();

        var win = new Window
        {
            Title = "退出 DoL 启动器",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            // 保留系统标题栏，但不要 thick frame 的可调整边框
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as Brush ?? Brushes.White,
            Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.Black,
        };
        AppDialog.AttachDialogChrome(win, 470);
        ThemeService.Attach(win);
        UiAnimations.AttachWindow(win);

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };

        var title = new TextBlock
        {
            Text = $"{names.Count} 个游戏版本还在运行",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindResource("TextBrush") as Brush,
        };
        root.Children.Add(title);
        root.Children.Add(new TextBlock
        {
            Text = "关闭启动器会一并停止本地服务器，这些游戏的页面将无法继续使用。",
            Margin = new Thickness(0, 6, 0, 10),
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("MutedBrush") as Brush,
        });

        var card = new Border
        {
            Background = TryFindResource("PanelBrush") as Brush,
            BorderBrush = TryFindResource("RunningBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
        };
        var list = new StackPanel();
        const int maxRows = 5;
        foreach (var name in names.Take(maxRows))
        {
            list.Children.Add(new TextBlock
            {
                Text = "● " + name,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = TryFindResource("RunningBrush") as Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (names.Count > maxRows)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"…以及另外 {names.Count - maxRows} 个版本",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = TryFindResource("MutedBrush") as Brush,
            });
        }
        card.Child = list;
        root.Children.Add(card);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button
        {
            Content = "取消",
            Style = TryFindResource("FlatButton") as Style,
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) => win.DialogResult = false;
        var confirm = new Button
        {
            Content = "仍然退出",
            Style = TryFindResource("FlatButton") as Style,
            MinWidth = 96,
            Foreground = TryFindResource("DangerBrush") as Brush,
            BorderBrush = TryFindResource("DangerBorderBrush") as Brush,
        };
        confirm.Click += (_, _) => win.DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        win.Content = root;
        UiAnimations.ShowPage(root);
        return win.ShowDialog() == true;
    }
}
