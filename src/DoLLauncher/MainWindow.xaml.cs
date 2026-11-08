using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DoLLauncher.Controls;
using DoLLauncher.Models;
using DoLLauncher.Services;
using Microsoft.Win32;

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
        Closed += (_, _) => StopModsWatcher();
        VersionList.ItemsSource = _versions;
        ModList.ItemsSource = _mods;
        GameReleaseList.ItemsSource = _gameReleases;
        MarketList.ItemsSource = _marketView;
        MarketTargetBox.ItemsSource = _targets;
        MarketCategoryBox.ItemsSource = _categories;
        MarketVersionBox.ItemsSource = _marketReleases;
        BackupTargetBox.ItemsSource = _targets;
        LegacyTargetBox.ItemsSource = _targets;
        BackupList.ItemsSource = _backupEntries;
        // 选中项左侧的小三角：随选中项滑动，滚动/尺寸变化时跟随，不做动画
        VersionList.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => UpdateVersionIndicator(false)));
        VersionList.SizeChanged += (_, _) => UpdateVersionIndicator(false);
        Loaded += (_, _) =>
        {
            ReloadVersions();
            LoadBackupSettings();
            LoadGeneralSettings();
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

            vm.IssueText = string.Join("；", issues);
        }

        var infos = _mods.Select(m => m.Info).ToList();
        var threshold = ProblemService.NormalizeThreshold(AppConfig.Load().General.VersionGapThreshold);
        _missingDeps = ProblemService.MissingDependencies(infos);
        _versionMismatches = ProblemService.VersionMismatches(infos, _selected?.Info.GameVersion, threshold);
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
                ServerManager.OpenInBrowser(new Uri(version.HtmlPath).AbsoluteUri);
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
            ServerManager.OpenInBrowser(server.GameUrl);

            Log($"已启动 {version.DisplayName}：{server.GameUrl}");
            LoadMods(version);
            RefreshListStates();

            var cfg = AppConfig.Load();
            if (cfg.Backup.Enabled)
            {
                Task.Run(() =>
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
                ServerManager.OpenInBrowser(server.SaveHelperUrl + "?auto=1");
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
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
            Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
        };
        ThemeService.Attach(win);
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

    // ---------- 模组市场详情 ----------

    private void MarketList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarketList.SelectedItem is MarketMod mod) ShowMarketDetail(mod);
    }

    private void ShowMarketDetail(MarketMod mod)
    {
        var win = new Window
        {
            Title = "模组详情",
            Width = 820,
            Height = 680,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White,
            Foreground = TryFindResource("TextBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black,
        };
        ThemeService.Attach(win);
        UiAnimations.AttachWindow(win);

        var root = new DockPanel { Margin = new Thickness(18) };

        // 底部：安装目标版本 / 历史版本 / 下载 / 关闭
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);

        var actions = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var status = new TextBlock
        {
            Style = TryFindResource("MutedText") as Style,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var install = new Button
        {
            Content = "下载并安装",
            Style = TryFindResource("PrimaryButton") as Style,
            MinWidth = 110,
        };

        if (mod.Repo.Length > 0)
        {
            var homepage = "https://github.com/" + mod.Repo;
            var openHome = new Button
            {
                Content = "打开主页",
                Style = TryFindResource("FlatButton") as Style,
                MinWidth = 92,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = homepage,
            };
            openHome.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(homepage)
                    {
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // 打不开浏览器时忽略
                }
            };
            right.Children.Add(openHome);
        }

        right.Children.Add(install);

        var targetBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            ItemTemplate = TryFindResource("VersionComboTemplate") as DataTemplate,
            Width = 200,
            ToolTip = "选择要把模组装到哪个版本",
        };
        targetBox.ItemsSource = _targets;
        targetBox.SelectedItem = MarketTargetBox.SelectedItem ?? _targets.FirstOrDefault();

        var versionBox = new ComboBox
        {
            Style = TryFindResource("FlatCombo") as Style,
            ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
            ItemTemplate = TryFindResource("ReleaseComboTemplate") as DataTemplate,
            Width = 150,
            IsEnabled = false,
            ToolTip = "选择历史版本，留空则安装最新版",
        };

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        left.Children.Add(new TextBlock
        {
            Text = "安装到版本", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(targetBox);
        left.Children.Add(new TextBlock
        {
            Text = "版本", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 8, 0), Opacity = 0.7,
        });
        left.Children.Add(versionBox);

        actions.Children.Add(right);
        actions.Children.Add(left);
        footer.Children.Add(actions);
        footer.Children.Add(status);

        // 头部信息
        ComboBox? editionBox = null;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = mod.Name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = $"版本 {(mod.Version.Length > 0 ? mod.Version : "未知")}　·　作者 {(mod.Author.Length > 0 ? mod.Author : "未知")}" +
                   (mod.Category.Length > 0 ? $"　·　{mod.Category}" : "") +
                   (mod.Repo.Length > 0 ? $"　·　{mod.Repo}" : ""),
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        if (mod.Description.Length > 0)
        {
            header.Children.Add(new TextBlock
            {
                Text = mod.Description,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        if (mod.Editions.Count > 1)
        {
            editionBox = new ComboBox
            {
                Style = TryFindResource("FlatCombo") as Style,
                ItemContainerStyle = TryFindResource("FlatComboItem") as Style,
                ItemTemplate = TryFindResource("EditionComboTemplate") as DataTemplate,
                Width = 260,
                ToolTip = "同一个模组的不同包型 / 包体系列，安装前请选对。",
            };
            editionBox.ItemsSource = mod.Editions;
            editionBox.SelectedIndex = 0;

            var editionRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
            };
            editionRow.Children.Add(new TextBlock
            {
                Text = "包型",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Opacity = 0.7,
            });
            editionRow.Children.Add(editionBox);
            header.Children.Add(editionRow);
        }
        if (mod.Installed)
        {
            header.Children.Add(new TextBlock
            {
                Text = mod.BadgeText,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = TryFindResource("AccentBrush") as System.Windows.Media.Brush,
            });
        }
        DockPanel.SetDock(header, Dock.Top);

        var readme = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 6, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = MarkdownService.Render("（正在读取 README……）"),
        };

        install.Click += async (_, _) =>
        {
            var target = targetBox.SelectedItem as VersionVm;
            if (target == null || target.Info.IsFileMode)
            {
                status.Text = "请先选择一个磁盘版本。";
                return;
            }

            install.IsEnabled = false;
            status.Text = "准备中…";
            try
            {
                await InstallMarketModAsync(mod, target, versionBox.SelectedItem as GameRelease,
                    t => status.Text = t, editionBox?.SelectedItem as MarketEdition);
                if (status.Text == "准备中…") status.Text = "";
            }
            finally
            {
                install.IsEnabled = true;
            }
        };

        root.Children.Add(footer);
        root.Children.Add(header);
        root.Children.Add(readme);
        win.Content = root;
        // 去掉「关闭」按钮后，Esc 直接关窗
        win.PreviewKeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape) win.Close();
        };

        _ = LoadMarketReadmeAsync(mod, readme);
        _ = LoadMarketReleasesAsync(mod, versionBox, status);
        win.ShowDialog();
    }

    private async Task LoadMarketReadmeAsync(MarketMod mod, RichTextBox box)
    {
        var key = mod.Id.Length > 0 ? mod.Id : mod.Name;
        if (_readmeCache.TryGetValue(key, out var cached))
        {
            box.Document = MarkdownService.Render(cached.Length > 0 ? cached : "（这个模组没有提供 README）");
            return;
        }

        try
        {
            var text = await DownloadService.FetchReadmeAsync(mod);
            _readmeCache[key] = text;
            box.Document = MarkdownService.Render(text.Length > 0 ? text : "（这个模组没有提供 README）");
        }
        catch (Exception ex)
        {
            box.Document = MarkdownService.Render("（读取 README 失败：" + ex.Message + "）");
        }
    }

    private async Task LoadMarketReleasesAsync(MarketMod mod, ComboBox box, TextBlock status)
    {
        try
        {
            var releases = await DownloadService.FetchModReleasesAsync(mod);
            box.ItemsSource = releases;
            box.IsEnabled = releases.Count > 0;
            if (releases.Count > 0) box.SelectedIndex = 0;
        }
        catch
        {
            status.Text = "没有取到历史版本，将直接安装最新版。";
        }
    }

    private string? PromptText(string title, string label, string initial)
    {
        var win = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 14) };
        panel.Children.Add(box);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button { Content = "确定", Padding = new Thickness(18, 5, 18, 5), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(8, 0, 0, 0) };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);

        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
        cancel.Click += (_, _) => win.Close();
        win.Content = panel;
        // 自适应高度的小窗口要在尺寸稳定后再居中，否则会偏出主窗口中心
        win.Loaded += (_, _) => AppDialog.CenterOnOwner(win);
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
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
            ServerManager.OpenInBrowser(server.SaveHelperUrl);
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
                "该目录下的游戏本体和全部模组都会被永久删除（浏览器里的存档不受影响）。",
                "删除版本", "删除", danger: true)) return;

        try
        {
            ServerManager.Stop(version.Folder);
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

    // ---------- 下载中心 ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (VersionPage == null || DownloadPage == null || BackupPage == null || SettingsPage == null) return;
        var showDownload = NavDownload.IsChecked == true;
        var showBackup = NavBackup.IsChecked == true;
        var showSettings = NavSettings.IsChecked == true;

        VersionPage.Visibility = !showDownload && !showBackup && !showSettings ? Visibility.Visible : Visibility.Collapsed;
        DownloadPage.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = showBackup ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;

        var shown = showSettings ? SettingsPage : showBackup ? BackupPage : showDownload ? DownloadPage : VersionPage;
        UiAnimations.ShowPage(shown);

        if (showDownload && !_gameLoaded) _ = RefreshGameReleasesAsync();
        if (showBackup) RefreshBackups();
    }

    private void DownloadNav_Checked(object sender, RoutedEventArgs e)
    {
        if (GamePane == null || ModPane == null) return;
        var showMod = DownloadNavMod.IsChecked == true;
        GamePane.Visibility = showMod ? Visibility.Collapsed : Visibility.Visible;
        ModPane.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        MarketTargetPanel.Visibility = showMod ? Visibility.Visible : Visibility.Collapsed;
        UiAnimations.ShowPage(showMod ? ModPane : GamePane);
        if (showMod && !_marketLoaded) _ = RefreshMarketAsync();
    }

    private void SyncMarketTarget()
    {
        var current = (MarketTargetBox.SelectedItem as VersionVm)?.Info.Folder;
        if (current != null && _targets.Any(v => v.Info.Folder == current)) return;
        var preferred = _selected?.Info.Folder;
        MarketTargetBox.SelectedItem =
            _targets.FirstOrDefault(v => v.Info.Folder == preferred) ?? _targets.FirstOrDefault();
        RefreshMarketStatus();
    }

    private void RefreshMarket_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        if (ModPane.Visibility == Visibility.Visible) _ = RefreshMarketAsync();
        else _ = RefreshGameReleasesAsync();
    }

    /// <summary>按设置里的「启动时预取索引」时序，在后台拉取游戏本体与模组市场索引。</summary>
    private async Task PrefetchIndexesAsync()
    {
        var mode = AppConfig.Load().General.Prefetch;
        if (mode == "none") return;
        if (mode == "modFirst")
        {
            await RefreshMarketAsync();
            await RefreshGameReleasesAsync();
            return;
        }
        if (mode == "gameFirst")
        {
            await RefreshGameReleasesAsync();
            await RefreshMarketAsync();
            return;
        }
        await Task.WhenAll(RefreshMarketAsync(), RefreshGameReleasesAsync());
    }

    private async Task RefreshGameReleasesAsync()
    {
        if (_gameLoading) return;
        _gameLoading = true;
        GameStatusText.Text = "正在获取 Release 列表…";
        try
        {
            var list = await DownloadService.FetchGameReleasesAsync();
            _gameReleases.Clear();
            foreach (var release in list.Where(r => r.StandardAsset != null || r.PolyfillAsset != null))
                _gameReleases.Add(release);
            if (_gameReleases.Count > 0) GameReleaseList.SelectedIndex = 0;
            _gameLoaded = true;
            GameStatusText.Text = $"共 {_gameReleases.Count} 个 Release";
            Log($"已获取 {_gameReleases.Count} 个 Release");
        }
        catch (Exception ex)
        {
            _gameLoaded = false;
            GameStatusText.Text = "获取失败：" + ex.Message;
            Log("获取本体列表失败：" + ex.Message);
        }
        finally
        {
            _gameLoading = false;
        }
    }

    private async Task RefreshMarketAsync()
    {
        if (_marketLoading) return;
        _marketLoading = true;
        MarketStatusText.Text = "正在获取市场索引…";
        try
        {
            var catalog = await DownloadService.FetchMarketAsync(Log);
            _marketCatalog = catalog;
            _marketAll.Clear();
            foreach (var mod in catalog.Mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
                _marketAll.Add(mod);

            _categories.Clear();
            _categories.Add("全部分类");
            foreach (var category in catalog.Categories) _categories.Add(category);
            if (MarketCategoryBox.SelectedItem == null) MarketCategoryBox.SelectedIndex = 0;

            RefreshMarketStatus();
            ApplyMarketFilter();
            _marketLoaded = true;
            MarketStatusText.Text = "";
            MarketHintText.Text = $"索引来自 ModHub 市场，共 {_marketAll.Count} 个模组；已安装的会标出状态，缺少的前置可以自动补装。";
            Log($"市场索引就绪：{_marketAll.Count} 个模组" +
                (catalog.GeneratedAt.Length > 0 ? $"（生成于 {catalog.GeneratedAt}）" : ""));
            // 索引到位后才能把本地模组对到市场条目，这里补做一次可能冲突检查
            _ = CheckConflictsAsync();
        }
        catch (Exception ex)
        {
            _marketLoaded = false;
            MarketStatusText.Text = "获取失败：" + ex.Message;
            Log("获取市场索引失败：" + ex.Message);
        }
        finally
        {
            _marketLoading = false;
        }
    }

    private void ApplyMarketFilter()
    {
        if (MarketCountText == null) return;
        var keyword = MarketSearchBox.Text.Trim().ToLowerInvariant();
        var category = MarketCategoryBox.SelectedItem as string;
        var filter = MarketFilterBox.SelectedIndex;

        _marketView.Clear();
        foreach (var mod in _marketAll)
        {
            if (category != null && category != "全部分类" &&
                !mod.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
            if (keyword.Length > 0 && !mod.SearchText.Contains(keyword)) continue;
            if (filter == 1 && mod.Installed) continue;
            if (filter == 2 && !mod.UpdateAvailable) continue;
            _marketView.Add(mod);
        }

        MarketCountText.Text = $"{_marketView.Count} / {_marketAll.Count} 个";
        if (_marketView.Count > 0 && MarketList.SelectedItem == null) MarketList.SelectedIndex = 0;
    }

    private void MarketSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyMarketFilter();

    private void MarketCategory_Changed(object sender, SelectionChangedEventArgs e) => ApplyMarketFilter();

    private void MarketFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyMarketFilter();

    private void MarketTarget_Changed(object sender, SelectionChangedEventArgs e)
    {
        RefreshMarketStatus();
        ApplyMarketFilter();
    }

    /// <summary>用目标版本磁盘上的模组刷新“已安装 / 可更新”标记。</summary>
    private void RefreshMarketStatus()
    {
        if (_marketAll.Count == 0) return;
        var target = MarketTargetBox.SelectedItem as VersionVm;
        var installed = target != null && !target.Info.IsFileMode
            ? ModService.Load(target.Info.FullPath)
            : new List<ModInfo>();

        foreach (var mod in _marketAll)
        {
            var version = DownloadService.FindInstalledVersion(mod, installed);
            mod.InstalledVersion = version;
            mod.UpdateAvailable = version.Length > 0 && VersionUtil.Compare(version, mod.Version) < 0;
        }
    }

    private void MarketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _marketReleases.Clear();
        MarketEditionBox.ItemsSource = null;
        MarketEditionPanel.Visibility = Visibility.Collapsed;

        var mod = MarketList.SelectedItem as MarketMod;
        if (mod == null)
        {
            // 点空白处取消选中后，顺带清掉上一个模组的版本提示
            MarketStatusText.Text = "";
            return;
        }

        if (mod.Editions.Count > 1)
        {
            MarketEditionBox.ItemsSource = mod.Editions;
            MarketEditionBox.SelectedIndex = 0;
            MarketEditionPanel.Visibility = Visibility.Visible;
        }

        if (mod.Installed && mod.Dependencies.Count > 0)
        {
            MarketStatusText.Text = "前置：" + string.Join("、", mod.Dependencies.Select(d => d.Id));
        }
        else
        {
            MarketStatusText.Text = "";
        }

        if (_releaseCache.TryGetValue(mod.Repo, out var cached))
        {
            foreach (var release in cached) _marketReleases.Add(release);
            if (_marketReleases.Count > 0) MarketVersionBox.SelectedIndex = 0;
            return;
        }

        // 选中模组后自动拉取该模组的版本列表，不用再点「加载版本」
        if (mod.Repo.Length > 0) _ = LoadModReleasesAsync(mod, true);
    }

    private async void LoadModReleases_Click(object sender, RoutedEventArgs e)
    {
        var mod = MarketList.SelectedItem as MarketMod;
        if (mod == null) return;
        await LoadModReleasesAsync(mod, false);
    }

    private async Task LoadModReleasesAsync(MarketMod mod, bool auto)
    {
        if (mod.Repo.Length == 0)
        {
            if (!auto) MarketStatusText.Text = "这个模组没有配置 GitHub 仓库。";
            return;
        }

        if (!auto)
        {
            MarketVersionBtn.IsEnabled = false;
            MarketStatusText.Text = "正在获取版本列表…";
        }

        try
        {
            if (!_releaseCache.TryGetValue(mod.Repo, out var list))
            {
                list = await DownloadService.FetchModReleasesAsync(mod);
                _releaseCache[mod.Repo] = list;
            }

            // 等待期间选中项可能已经变了，变了就不再覆盖下拉框
            if (MarketList.SelectedItem is not MarketMod current || current.Repo != mod.Repo) return;

            _marketReleases.Clear();
            foreach (var release in list) _marketReleases.Add(release);
            if (_marketReleases.Count > 0)
            {
                MarketVersionBox.SelectedIndex = 0;
                MarketStatusText.Text = $"共 {_marketReleases.Count} 个版本";
            }
            else
            {
                MarketStatusText.Text = "没有可下载的 Release";
            }
        }
        catch (Exception ex)
        {
            MarketStatusText.Text = "获取版本失败：" + ex.Message;
            Log("获取模组版本失败：" + ex.Message);
        }
        finally
        {
            if (!auto) MarketVersionBtn.IsEnabled = true;
        }
    }

    private async void DownloadGame_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        var release = GameReleaseList.SelectedItem as GameRelease;
        if (release == null)
        {
            AppDialog.Info("请先在上面的列表选择一个 Release。", "下载中心");
            return;
        }

        var usePolyfill = PolyfillCheck.IsChecked == true;
        if (release.PickAsset(usePolyfill) == null)
        {
            AppDialog.Info("这个 Release 没有可用的游戏压缩包。", "下载中心");
            return;
        }

        var extras = new List<string>();
        if (I18nCheck.IsChecked == true) extras.Add("ModI18N");
        if (ImagePackCheck.IsChecked == true) extras.Add("图片包");
        if (!AppDialog.Confirm(
                $"将下载并安装「{release.Title}」" + (usePolyfill ? "（polyfill）" : "") + "。\n" +
                (extras.Count > 0 ? "同时安装：" + string.Join("、", extras) + "。\n" : "") +
                "\n下载体积较大，安装完成后会出现在左侧版本列表。继续吗？",
                "下载游戏本体", "开始下载")) return;

        SetDownloadBusy(true);
        GameProgress.Visibility = Visibility.Visible;
        GameProgress.Value = 0;
        GameStatusText.Text = "0%";
        try
        {
            Log($"开始下载本体：{release.Title}");
            var folder = await DownloadService.InstallGameAsync(release, usePolyfill,
                I18nCheck.IsChecked == true, ImagePackCheck.IsChecked == true,
                Log, p =>
                {
                    GameProgress.Value = p;
                    GameStatusText.Text = $"{p:P0}";
                });
            GameProgress.Value = 1;
            GameStatusText.Text = "安装完成";
            Log($"本体安装完成：versions\\{folder}");
            ReloadVersions(folder);
            NavVersions.IsChecked = true;
        }
        catch (Exception ex)
        {
            GameStatusText.Text = "安装失败";
            Log("本体下载/安装失败：" + ex.Message);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            SetDownloadBusy(false);
        }
    }

    private async void DownloadMod_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadBusy) return;
        var mod = MarketList.SelectedItem as MarketMod;
        if (mod == null)
        {
            AppDialog.Info("请先在列表里选择一个模组。", "下载中心");
            return;
        }

        var target = MarketTargetBox.SelectedItem as VersionVm;
        if (target == null || target.Info.IsFileMode)
        {
            AppDialog.Info("请先在左侧「安装到版本」里选中一个磁盘版本。", "下载中心");
            return;
        }

        await InstallMarketModAsync(mod, target, MarketVersionBox.SelectedItem as GameRelease, null,
            MarketEditionBox.SelectedItem as MarketEdition);
    }

    private async Task InstallMarketModAsync(MarketMod mod, VersionVm target, GameRelease? preferred,
        Action<string>? status, MarketEdition? edition = null)
    {
        void SetStatus(string text)
        {
            MarketStatusText.Text = text;
            status?.Invoke(text);
        }

        SetDownloadBusy(true);
        InstallCheckResult check;
        try
        {
            SetStatus("正在检查前置与冲突…");
            check = await DownloadService.CheckInstallAsync(mod, _marketCatalog ?? new MarketCatalog(),
                target.Info.FullPath, target.Info.GameVersion, Log);
        }
        catch (Exception ex)
        {
            Log("安装前检查失败：" + ex.Message);
            check = new InstallCheckResult();
        }

        var dialog = new InstallCheckDialog(mod, target.Info, check) { Owner = this };
        SetStatus(string.Empty);
        if (dialog.ShowDialog() != true)
        {
            SetDownloadBusy(false);
            return;
        }

        // 先前置、后本体；用户在对话框里取消勾选的前置不会安装
        var plan = dialog.SelectedDependencies.Concat(new[] { mod }).ToList();

        MarketProgress.Visibility = Visibility.Visible;
        MarketProgress.Value = 0;
        SetStatus("0%");
        MarkInternalModsChange();
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                SetStatus($"正在安装 {i + 1}/{plan.Count}：{item.Name}");
                Log($"开始下载模组：{item.Title}" + (item == mod && preferred != null ? $"（{preferred.Tag}）" : ""));
                var tag = item == mod && preferred != null ? preferred.Tag : null;
                var useEdition = item == mod ? edition : null;
                var file = await DownloadService.InstallModAsync(item, target.Info.FullPath, Log, p =>
                {
                    var overall = (i + p) / Math.Max(1, plan.Count);
                    MarketProgress.Value = overall;
                    SetStatus($"{overall:P0}　{ item.Name}");
                }, tag, useEdition);
                Log("模组安装完成：" + file);
            }

            RefreshMarketStatus();
            ApplyMarketFilter();
            if (_selected != null && _selected.Info.Folder == target.Info.Folder)
                LoadMods(_selected.Info);
            // 放在刷新列表之后，避免 SelectionChanged 把完成提示清掉
            SetStatus($"安装完成（{plan.Count} 个）");
        }
        catch (Exception ex)
        {
            SetStatus("安装失败");
            Log("模组下载/安装失败：" + ex.Message);
            AppDialog.Error(ex.Message, "下载失败");
        }
        finally
        {
            // 装完就把进度条收起来，只留下面那行状态文字
            MarketProgress.Value = 0;
            MarketProgress.Visibility = Visibility.Collapsed;
            SetDownloadBusy(false);
        }
    }

    private void SetDownloadBusy(bool busy)
    {
        _downloadBusy = busy;
        GameDownloadBtn.IsEnabled = !busy;
        MarketDownloadBtn.IsEnabled = !busy;
        RefreshMarketBtn.IsEnabled = !busy;
    }

    // ---------- 备份与诊断 ----------

    private void SyncBackupTarget()
    {
        var current = (BackupTargetBox.SelectedItem as VersionVm)?.Info.Folder;
        if (current != null && _targets.Any(v => v.Info.Folder == current)) return;
        var preferred = _selected?.Info.Folder;
        BackupTargetBox.SelectedItem =
            _targets.FirstOrDefault(v => v.Info.Folder == preferred) ?? _targets.FirstOrDefault();
        RefreshBackups();
    }

    private VersionInfo? BackupVersion()
    {
        var vm = BackupTargetBox.SelectedItem as VersionVm;
        return vm == null || vm.Info.IsFileMode ? null : vm.Info;
    }

    private void BackupTarget_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshBackups();

    private void LoadBackupSettings()
    {
        _loadingBackupSettings = true;
        try
        {
            var cfg = AppConfig.Load();
            AutoBackupCheck.IsChecked = cfg.Backup.Enabled;
            AutoSaveCheck.IsChecked = cfg.Backup.SaveAuto;

            var keep = Math.Max(1, cfg.Backup.Keep);
            foreach (var item in BackupKeepBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Content?.ToString() == keep.ToString())
                {
                    BackupKeepBox.SelectedItem = item;
                    break;
                }
            }
            if (BackupKeepBox.SelectedItem == null) BackupKeepBox.SelectedIndex = 1;
        }
        finally
        {
            _loadingBackupSettings = false;
        }
    }

    private void SaveBackupSettings()
    {
        if (_loadingBackupSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.Backup.Enabled = AutoBackupCheck.IsChecked == true;
            cfg.Backup.SaveAuto = AutoSaveCheck.IsChecked == true;
            var text = (BackupKeepBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "5";
            if (int.TryParse(text, out var keep)) cfg.Backup.Keep = Math.Max(1, keep);
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            Log("保存备份设置失败：" + ex.Message);
        }
    }

    private void AutoBackup_Checked(object sender, RoutedEventArgs e) => SaveBackupSettings();

    private void AutoSave_Checked(object sender, RoutedEventArgs e) => SaveBackupSettings();

    private void BackupKeep_Changed(object sender, SelectionChangedEventArgs e) => SaveBackupSettings();

    // ---------- 设置 ----------

    private void LoadGeneralSettings()
    {
        _loadingGeneralSettings = true;
        try
        {
            var cfg = AppConfig.Load();
            WriteModMetaCheck.IsChecked = cfg.General.WriteModMetadata;
            PrefetchBox.SelectedItem = PrefetchBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.Prefetch);
            if (PrefetchBox.SelectedItem == null) PrefetchBox.SelectedIndex = 0;

            MarketSourceBox.SelectedItem = MarketSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.MarketSource);
            if (MarketSourceBox.SelectedItem == null) MarketSourceBox.SelectedIndex = 0;

            DownloadSourceBox.SelectedItem = DownloadSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.ModDownloadSource);
            if (DownloadSourceBox.SelectedItem == null) DownloadSourceBox.SelectedIndex = 0;

            ApiSourceBox.SelectedItem = ApiSourceBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.GithubApiSource);
            if (ApiSourceBox.SelectedItem == null) ApiSourceBox.SelectedIndex = 0;

            VersionGapBox.SelectedItem = VersionGapBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == cfg.General.VersionGapThreshold.ToString());
            if (VersionGapBox.SelectedItem == null) VersionGapBox.SelectedIndex = 0;

            var theme = ThemeService.Normalize(cfg.General.ThemeMode);
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (i.Tag as string) == theme);
            if (ThemeBox.SelectedItem == null) ThemeBox.SelectedIndex = 0;

            AnimationCheck.IsChecked = cfg.General.ShowAnimations;
            UiAnimations.Enabled = cfg.General.ShowAnimations;

            VersionInfoText.Text = BuildInfo.Display;
        }
        finally
        {
            _loadingGeneralSettings = false;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        Log("检查更新：开始");
        try
        {
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                UpdateStatusText.Text = "没有可用的 Release 信息。";
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            if (hasUpdate)
            {
                UpdateStatusText.Text = BuildInfo.Tag == "dev"
                    ? $"当前为开发版构建，最新 Release 为 {release.Tag}。"
                    : $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。";
                Log("检查更新：发现新版本 " + release.Tag);
                if (release.Url.Length > 0 &&
                    AppDialog.Confirm(
                        $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。\n要打开下载页面吗？",
                        "检查更新", "打开下载页"))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url)
                    {
                        UseShellExecute = true,
                    });
                }
            }
            else
            {
                UpdateStatusText.Text = $"已是最新版本（{BuildInfo.Tag}）。";
                Log("检查更新：已是最新版本");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = ex.Message.Contains("404")
                ? "仓库还没有发布任何 Release。"
                : "检查更新失败：" + ex.Message;
            Log("检查更新失败：" + ex.Message);
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private void Prefetch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var mode = (PrefetchBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "parallel";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.Prefetch = mode;
            AppConfig.Save(cfg);
            Log("设置：启动时预取索引 = " +
                ((PrefetchBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mode));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void MarketSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (MarketSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.MarketSource = source;
            AppConfig.Save(cfg);
            Log("设置：模组市场索引源 = " +
                ((MarketSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void ApiSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (ApiSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.GithubApiSource = source;
            AppConfig.Save(cfg);
            Log("设置：GitHub 接口源 = " +
                ((ApiSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void VersionGap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var raw = (VersionGapBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1";
        var threshold = ProblemService.NormalizeThreshold(int.TryParse(raw, out var value) ? value : 1);
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.VersionGapThreshold = threshold;
            AppConfig.Save(cfg);
            Log("设置：游戏版本差异告警阈值 = " + threshold);
            if (_selected != null) ApplyModIssues();
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void DownloadSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var source = (DownloadSourceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ModDownloadSource = source;
            AppConfig.Save(cfg);
            Log("设置：模组下载源 = " +
                ((DownloadSourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? source));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void WriteModMeta_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.WriteModMetadata = WriteModMetaCheck.IsChecked == true;
            AppConfig.Save(cfg);
            Log("设置：补全信息写回模组包 = " + (cfg.General.WriteModMetadata ? "开" : "关"));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    // ---------- 设置分类导航 ----------

    /// <summary>进入某个设置分类；不传参数表示回到分类首页。路径可多级，用 / 分隔。</summary>
    private void NavigateSettings(params string[] path)
    {
        _settingsPath.Clear();
        if (path != null) _settingsPath.AddRange(path.Where(p => p.Length > 0));

        SettingsRootPanel.Visibility = Visibility.Collapsed;
        SettingsNetworkPanel.Visibility = Visibility.Collapsed;
        SettingsDataPanel.Visibility = Visibility.Collapsed;
        SettingsModInfoPanel.Visibility = Visibility.Collapsed;
        SettingsProblemPanel.Visibility = Visibility.Collapsed;
        SettingsAppearancePanel.Visibility = Visibility.Collapsed;
        SettingsVersionPanel.Visibility = Visibility.Collapsed;

        var panel = SettingsPanelForPath();
        panel.Visibility = Visibility.Visible;
        BuildSettingsBreadcrumb();
        SettingsDescText.Text = SettingsDescription();
        SettingsScroll.ScrollToTop();

        if (_settingsPath.Count == 0) UiAnimations.Fade(SettingsRootPanel, 0.5, 1, 150);
        else UiAnimations.ShowPage(panel);
    }

    private FrameworkElement SettingsPanelForPath()
    {
        return string.Join("/", _settingsPath) switch
        {
            "网络下载" => SettingsNetworkPanel,
            "数据与索引" => SettingsDataPanel,
            "数据与索引/模组信息" => SettingsModInfoPanel,
            "问题检查" => SettingsProblemPanel,
            "界面与外观" => SettingsAppearancePanel,
            "版本信息" => SettingsVersionPanel,
            _ => SettingsRootPanel,
        };
    }

    private string SettingsDescription()
    {
        var key = string.Join("/", _settingsPath);
        return key switch
        {
            "" => "选择一个分类查看相关设置。",
            "网络下载" => "下载安装包与访问 GitHub 接口时使用的线路，失败会自动回退。",
            "数据与索引" => "模组市场索引的拉取地址与时机，以及模组信息写回。",
            "数据与索引/模组信息" => "是否把索引里补全的作者 / 主页写回模组包。",
            "问题检查" => "「可能存在问题」里版本差异告警的敏感度。",
            "界面与外观" => "界面主题与过渡动画效果，修改后立即生效。",
            "版本信息" => "当前启动器的版本、提交与构建时间，并可检查更新。",
            _ => "",
        };
    }

    private void BuildSettingsBreadcrumb()
    {
        SettingsCrumbPanel.Children.Clear();

        if (_settingsPath.Count == 0)
        {
            SettingsCrumbPanel.Children.Add(new TextBlock
            {
                Text = "设置",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        AddSettingsCrumb("设置", "", false);
        var prefix = new List<string>();
        for (var i = 0; i < _settingsPath.Count; i++)
        {
            prefix.Add(_settingsPath[i]);
            SettingsCrumbPanel.Children.Add(new TextBlock
            {
                Text = "›",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 15,
                Margin = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            AddSettingsCrumb(_settingsPath[i], string.Join("/", prefix), i == _settingsPath.Count - 1);
        }
    }

    private void AddSettingsCrumb(string text, string path, bool current)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("LinkButton"),
            FontSize = 17,
            FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
            Tag = path,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += SettingsCrumb_Click;
        SettingsCrumbPanel.Children.Add(button);
    }

    private void SettingsCrumb_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string raw) return;
        NavigateSettings(raw.Length == 0 ? Array.Empty<string>() : raw.Split('/'));
    }

    private void SettingsCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string category || category.Length == 0) return;
        NavigateSettings(category.Split('/'));
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var mode = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? ThemeService.ModeSystem;
        try
        {
            ThemeService.SetMode(mode);
            Log("设置：界面主题 = " +
                ((ThemeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? mode));
        }
        catch (Exception ex)
        {
            Log("切换主题失败：" + ex.Message);
        }
    }

    private void Animation_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingGeneralSettings) return;
        var enabled = AnimationCheck.IsChecked == true;
        UiAnimations.Enabled = enabled;
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.ShowAnimations = enabled;
            AppConfig.Save(cfg);
            Log("设置：界面过渡动画 = " + (enabled ? "开" : "关"));
        }
        catch (Exception ex)
        {
            Log("保存设置失败：" + ex.Message);
        }
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        PaneSplitter.RestoreDefaults(this);
        Log("设置：已恢复默认界面布局尺寸");
    }

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
            _versionWatcher = CreateModsWatcher(version.FullPath);
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
        if (name == null) return true;
        var file = Path.GetFileName(name);
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

    private void RefreshBackups()
    {
        var version = BackupVersion();
        _backupEntries.Clear();
        if (version == null)
        {
            BackupCountText.Text = "请选择一个磁盘版本";
            return;
        }

        foreach (var entry in BackupService.List(version.Folder)) _backupEntries.Add(entry);
        BackupCountText.Text = $"{_backupEntries.Count} 项";
        RestoreBackupBtn.IsEnabled = false;
        DeleteBackupBtn.IsEnabled = false;
    }

    private void BackupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = BackupList.SelectedItem is BackupEntry;
        RestoreBackupBtn.IsEnabled = has;
        DeleteBackupBtn.IsEnabled = has;
    }

    private void RefreshBackup_Click(object sender, RoutedEventArgs e) => RefreshBackups();

    private void BackupOpenSaves_Click(object sender, RoutedEventArgs e) => OpenSavesPage(false);

    private void BackupSavesNow_Click(object sender, RoutedEventArgs e) => OpenSavesPage(true);

    private void OpenSavesPage(bool autoBackup)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            var server = ServerManager.EnsureStarted(version, saveHelperOnly: true);
            RefreshListStates();
            ServerManager.OpenInBrowser(autoBackup ? server.SaveHelperUrl + "?auto=1" : server.SaveHelperUrl);
            Log(autoBackup ? "已打开自动存档备份页，完成后可关闭。" : "已打开存档助手。");
        }
        catch (Exception ex)
        {
            AppDialog.Error("打开存档助手失败：" + ex.Message);
        }
    }

    private void ExportModsConfig_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            var path = BackupService.ExportModsConfig(version);
            Log("已导出模组配置：" + path);
            RefreshBackups();
            AppDialog.Info("模组顺序与启停已保存：\n" + path, "导出模组配置");
        }
        catch (Exception ex)
        {
            AppDialog.Error("导出失败：" + ex.Message, "导出模组配置");
        }
    }

    private void RestoreModsConfig_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择模组配置",
            Filter = "模组配置 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        if (!AppDialog.Confirm("将用这个文件里的顺序与启停覆盖当前模组列表，继续吗？",
                "还原模组配置", "覆盖")) return;

        try
        {
            BackupService.RestoreModsConfig(version.FullPath, dialog.FileName, Log);
            LoadMods(version);
            RefreshMarketStatus();
        }
        catch (Exception ex)
        {
            AppDialog.Error("还原失败：" + ex.Message, "还原模组配置");
        }
    }

    private async void CreateSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;

        var includeGame = SnapshotGameCheck.IsChecked == true;
        if (!AppDialog.Confirm(
                $"为版本「{version.DisplayName}」创建快照？\n" +
                (includeGame ? "包含游戏本体，体积较大。\n" : "只包含模组与配置，体积较小。\n") +
                "快照保存在启动器的 backups 目录。",
                "创建快照", "创建")) return;

        CreateSnapshotBtn.IsEnabled = false;
        try
        {
            Log("正在创建快照…");
            var path = await Task.Run(() => BackupService.CreateSnapshot(version, includeGame, Log));
            Log("快照完成：" + path);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            Log("创建快照失败：" + ex.Message);
            AppDialog.Error("创建快照失败：" + ex.Message, "快照");
        }
        finally
        {
            CreateSnapshotBtn.IsEnabled = true;
        }
    }

    private void BackupList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RestoreBackup_Click(sender, e);

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        if (BackupList.SelectedItem is not BackupEntry entry) return;

        if (entry.IsSaves)
        {
            AppDialog.Info(
                "存档备份只能在浏览器里还原。已为你打开存档助手，请在「启动器备份」一栏点「还原」。",
                "还原存档");
            OpenSavesPage(false);
            return;
        }

        if (entry.IsModsConfig)
        {
            if (!AppDialog.Confirm($"将按「{entry.FileName}」还原模组顺序与启停，继续吗？",
                    "还原模组配置", "覆盖")) return;
            try
            {
                BackupService.RestoreModsConfig(version.FullPath, entry.FullPath, Log);
                LoadMods(version);
                RefreshMarketStatus();
            }
            catch (Exception ex)
            {
                AppDialog.Error("还原失败：" + ex.Message, "还原模组配置");
            }
            return;
        }

        if (!entry.IsSnapshot) return;

        var hasGame = PackService.PeekHasGame(entry.FullPath);
        var message = hasGame
            ? $"「{entry.FileName}」含游戏本体，将作为新版本导入到 versions 目录。继续吗？"
            : $"「{entry.FileName}」只含模组，将导入到版本「{version.DisplayName}」，同名模组会被覆盖。继续吗？";
        if (!AppDialog.Confirm(message, "还原快照", "还原")) return;

        try
        {
            Log("正在还原快照：" + entry.FileName);
            var targetPath = hasGame ? null : version.FullPath;
            var result = await Task.Run(() => PackService.Import(entry.FullPath, targetPath, Log));
            if (result.NewVersionFolder != null)
            {
                ReloadVersions(result.NewVersionFolder);
                Log($"快照已导入为新版本：{result.NewVersionFolder}");
            }
            else
            {
                LoadMods(version);
                RefreshMarketStatus();
                RefreshBackups();
                Log($"快照已还原到版本「{version.DisplayName}」。");
            }
        }
        catch (Exception ex)
        {
            Log("还原快照失败：" + ex.Message);
            AppDialog.Error("还原失败：" + ex.Message, "还原快照");
        }
    }

    private void DeleteBackup_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        if (BackupList.SelectedItem is not BackupEntry entry) return;

        if (!AppDialog.Confirm($"确定删除备份「{entry.FileName}」吗？",
                "删除备份", "删除", danger: true)) return;
        try
        {
            BackupService.Delete(version.Folder, entry.FileName, entry.IsSaves);
            Log("已删除备份：" + entry.FileName);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            AppDialog.Error("删除失败：" + ex.Message, "删除备份");
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        var dir = BackupService.VersionDir(version.Folder);
        Directory.CreateDirectory(dir);
        ServerManager.OpenFolder(dir);
    }

    private void CompatCheck_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;
        try
        {
            DiagBox.Text = string.Join(Environment.NewLine, DiagnosticService.Compatibility(version));
            DiagBox.ScrollToHome();
            Log("兼容性检查完成。");
        }
        catch (Exception ex)
        {
            DiagBox.Text = "检查失败：" + ex.Message;
        }
    }

    private async void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        var version = BackupVersion();
        if (version == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "保存诊断包",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"dol-diagnostic-{version.Folder}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
        };
        if (dialog.ShowDialog(this) != true) return;

        ExportDiagBtn.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => DiagnosticService.ExportPackage(version, dialog.FileName, Log));
            Log("诊断包已导出：" + path);
            AppDialog.Info("诊断包已导出：\n" + path, "导出诊断包");
        }
        catch (Exception ex)
        {
            Log("导出诊断包失败：" + ex.Message);
            AppDialog.Error("导出失败：" + ex.Message, "导出诊断包");
        }
        finally
        {
            ExportDiagBtn.IsEnabled = true;
        }
    }

    private void ViewLauncherLog_Click(object sender, RoutedEventArgs e)
    {
        DiagBox.Text = LogService.ReadTail(500);
        DiagBox.ScrollToEnd();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开游戏日志",
            Filter = "日志文件 (*.log;*.txt)|*.log;*.txt|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        DiagBox.Text = DiagnosticService.ReadTail(dialog.FileName, 500);
        DiagBox.ScrollToHome();
        Log("已载入日志：" + dialog.FileName);
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
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = TryFindResource("BgBrush") as Brush ?? Brushes.White,
            Foreground = TryFindResource("TextBrush") as Brush ?? Brushes.Black,
        };
        win.Loaded += (_, _) =>
        {
            AppDialog.SnapWindowSize(win, 470);
            ThemeService.Refresh(win);
            AppDialog.CenterOnOwner(win);
        };
        win.ContentRendered += (_, _) =>
        {
            AppDialog.SnapWindowSize(win, 470);
            ThemeService.Refresh(win);
            AppDialog.CenterOnOwner(win);
        };
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
