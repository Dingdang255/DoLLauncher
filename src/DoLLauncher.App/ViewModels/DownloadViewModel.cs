using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DoLLauncher.Core.Search;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.ViewModels;

/// <summary>下载中心视图模型：游戏本体下载与模组市场安装。</summary>
public sealed class DownloadViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _searchTimer;

    private bool _loaded;
    private bool _isGameTab = true;
    private bool _busy;
    private bool _gameLoading;
    private bool _marketLoading;
    private bool _gameLoaded;

    private GameSource _selectedGameSource;
    private bool _usePolyfill;
    private bool _installI18n = true;
    private bool _installImagePack = true;
    private GameRelease? _selectedGameRelease;
    private string _gameHint = string.Empty;
    private string _gameStatus = string.Empty;
    private double _gameProgress;
    private bool _gameProgressVisible;

    private VersionItemVm? _selectedTarget;
    private DownloadTaskVm? _selectedTask;

    private MarketCatalog _catalog = new();
    private string _searchText = string.Empty;
    private string _selectedCategory = "全部分类";
    private OptionItem _selectedFilter = null!;
    private OptionItem _selectedMatch = null!;
    private OptionItem _selectedScope = null!;
    private OptionItem _selectedSort = null!;
    private bool _sortDescending;
    private string _marketStatus = string.Empty;
    private string _marketHint = "索引来自 ModHub 市场；会标出已安装/可更新，安装前检查前置与本地冲突，并可选择历史版本。";
    private string _marketCountText = string.Empty;
    private double _marketProgress;
    private bool _marketProgressVisible;
    private MarketMod? _selectedMarketMod;
    private GameRelease? _selectedModRelease;
    private MarketEdition? _selectedEdition;
    private ModAssetOption? _selectedAsset;
    private int _versionToken;
    private string _logText = string.Empty;

    public DownloadViewModel()
    {
        SwitchTabCommand = new RelayCommand(p => SwitchTab(p as string ?? "game"));
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !_busy);
        DownloadGameCommand = new RelayCommand(_ => _ = DownloadGameAsync(), _ => CanDownloadGame);
        DownloadModCommand = new RelayCommand(_ => _ = DownloadModAsync(), _ => CanDownloadMod);
        LoadModVersionsCommand = new RelayCommand(_ => _ = LoadModVersionsAsync(true), _ => SelectedMarketMod != null);
        CancelTaskCommand = new RelayCommand(_ => SelectedTask?.Cancel(), _ => SelectedTask != null);

        GameSources = DownloadService.GameSources;
        var general = AppConfig.Load().General;
        _selectedGameSource = GameSources.FirstOrDefault(s => s.Id == general.GameSource) ?? GameSources[0];
        ApplyGameSourceHint();

        FilterOptions = new List<OptionItem>
        {
            new("all", "全部模组"),
            new("notInstalled", "未安装"),
            new("update", "可更新"),
        };
        MatchOptions = new List<OptionItem>
        {
            new("contains", "部分匹配"),
            new("exact", "完全匹配"),
        };
        ScopeOptions = new List<OptionItem>
        {
            new("all", "全部"),
            new("title", "标题"),
            new("author", "作者"),
            new("description", "简介"),
            new("tag", "标签"),
        };
        SortOptions = new List<OptionItem>
        {
            new("default", "默认排序"),
            new("updated", "最近更新"),
            new("name", "名称"),
        };
        _selectedFilter = Pick(FilterOptions, "all");
        _selectedMatch = Pick(MatchOptions, general.MarketMatchMode);
        _selectedScope = Pick(ScopeOptions, general.MarketSearchScope);
        _selectedSort = Pick(SortOptions, general.MarketSortMode);
        _sortDescending = general.MarketSortDesc;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyMarketFilter();
        };

        DownloadService.DownloadTasks.CollectionChanged += OnTasksChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>本体安装完成，参数是新版本目录名，主窗口据此刷新版本列表。</summary>
    public event Action<string>? VersionInstalled;

    /// <summary>安装模组前的检查确认；由视图弹出对话框并返回是否继续。</summary>
    public Func<InstallCheckRequest, Task<bool>>? ConfirmInstallAsync { get; set; }

    public ObservableCollection<VersionItemVm> Targets { get; } = new();

    public ObservableCollection<GameRelease> GameReleases { get; } = new();

    public ObservableCollection<MarketMod> MarketView { get; } = new();

    public ObservableCollection<string> Categories { get; } = new();

    public ObservableCollection<GameRelease> ModReleases { get; } = new();

    public ObservableCollection<MarketEdition> Editions { get; } = new();

    public ObservableCollection<ModAssetOption> AssetOptions { get; } = new();

    public ObservableCollection<DownloadTaskVm> Tasks => DownloadService.DownloadTasks;

    public GameSource[] GameSources { get; }

    public List<OptionItem> FilterOptions { get; }

    public List<OptionItem> MatchOptions { get; }

    public List<OptionItem> ScopeOptions { get; }

    public List<OptionItem> SortOptions { get; }

    public ICommand SwitchTabCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand DownloadGameCommand { get; }

    public ICommand DownloadModCommand { get; }

    public ICommand LoadModVersionsCommand { get; }

    public ICommand CancelTaskCommand { get; }

    public bool IsGameTab
    {
        get => _isGameTab;
        private set
        {
            if (!Set(ref _isGameTab, value)) return;
            OnPropertyChanged(nameof(IsModTab));
        }
    }

    public bool IsModTab => !_isGameTab;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanDownloadGame));
            OnPropertyChanged(nameof(CanDownloadMod));
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadGameCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadModCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public GameSource SelectedGameSource
    {
        get => _selectedGameSource;
        set
        {
            if (!Set(ref _selectedGameSource, value)) return;
            ApplyGameSourceHint();
            try
            {
                var cfg = AppConfig.Load();
                cfg.General.GameSource = value.Id;
                AppConfig.Save(cfg);
            }
            catch (Exception ex)
            {
                LogService.Error("保存游戏源失败", ex);
            }
            if (_gameLoaded) _ = LoadGameReleasesAsync();
        }
    }

    public bool UsePolyfill
    {
        get => _usePolyfill;
        set => Set(ref _usePolyfill, value);
    }

    public bool InstallI18n
    {
        get => _installI18n;
        set => Set(ref _installI18n, value);
    }

    public bool InstallImagePack
    {
        get => _installImagePack;
        set => Set(ref _installImagePack, value);
    }

    public string GameHint
    {
        get => _gameHint;
        private set => Set(ref _gameHint, value);
    }

    public string GameStatus
    {
        get => _gameStatus;
        private set => Set(ref _gameStatus, value);
    }

    public bool CanDownloadGame => !_busy;

    public GameRelease? SelectedGameRelease
    {
        get => _selectedGameRelease;
        set
        {
            if (Set(ref _selectedGameRelease, value)) OnPropertyChanged(nameof(CanDownloadGame));
        }
    }

    public double GameProgress
    {
        get => _gameProgress;
        private set => Set(ref _gameProgress, value);
    }

    public bool IsGameProgressVisible
    {
        get => _gameProgressVisible;
        private set => Set(ref _gameProgressVisible, value);
    }

    public VersionItemVm? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            OnPropertyChanged(nameof(CanDownloadMod));
            (DownloadModCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public DownloadTaskVm? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (Set(ref _selectedTask, value)) (CancelTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasTasks => Tasks.Count > 0;

    public bool HasNoTasks => Tasks.Count == 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!Set(ref _selectedCategory, value)) return;
            ApplyMarketFilter();
        }
    }

    public OptionItem SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (!Set(ref _selectedFilter, value)) return;
            ApplyMarketFilter();
        }
    }

    public OptionItem SelectedMatch
    {
        get => _selectedMatch;
        set
        {
            if (!Set(ref _selectedMatch, value)) return;
            SaveMarketPrefs();
            ApplyMarketFilter();
        }
    }

    public OptionItem SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!Set(ref _selectedScope, value)) return;
            SaveMarketPrefs();
            ApplyMarketFilter();
        }
    }

    public OptionItem SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!Set(ref _selectedSort, value)) return;
            SaveMarketPrefs();
            ApplyMarketFilter();
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (!Set(ref _sortDescending, value)) return;
            SaveMarketPrefs();
            ApplyMarketFilter();
        }
    }

    public string MarketStatus
    {
        get => _marketStatus;
        private set => Set(ref _marketStatus, value);
    }

    public string MarketHint
    {
        get => _marketHint;
        private set => Set(ref _marketHint, value);
    }

    public string MarketCountText
    {
        get => _marketCountText;
        private set => Set(ref _marketCountText, value);
    }

    public double MarketProgress
    {
        get => _marketProgress;
        private set => Set(ref _marketProgress, value);
    }

    public bool IsMarketProgressVisible
    {
        get => _marketProgressVisible;
        private set => Set(ref _marketProgressVisible, value);
    }

    public MarketMod? SelectedMarketMod
    {
        get => _selectedMarketMod;
        set
        {
            if (!Set(ref _selectedMarketMod, value)) return;
            OnPropertyChanged(nameof(CanDownloadMod));
            (LoadModVersionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DownloadModCommand as RelayCommand)?.RaiseCanExecuteChanged();
            _ = LoadModVersionsAsync(false);
        }
    }

    public bool CanDownloadMod => !_busy && SelectedMarketMod != null && SelectedTarget != null;

    /// <summary>只有多种包型时才需要显示包型下拉框（与 WPF 版一致）。</summary>
    public bool HasEditions => Editions.Count > 1;

    /// <summary>实际传给下载服务的包型：只有多项选择时才带，否则由服务自动挑选。</summary>
    public MarketEdition? CurrentEdition => HasEditions ? SelectedEdition : null;

    public GameRelease? SelectedModRelease
    {
        get => _selectedModRelease;
        set
        {
            if (!Set(ref _selectedModRelease, value)) return;
            _ = LoadAssetVersionsAsync();
        }
    }

    public MarketEdition? SelectedEdition
    {
        get => _selectedEdition;
        set
        {
            if (!Set(ref _selectedEdition, value)) return;
            _ = LoadAssetVersionsAsync();
        }
    }

    public ModAssetOption? SelectedAsset
    {
        get => _selectedAsset;
        set => Set(ref _selectedAsset, value);
    }

    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    /// <summary>首次进入下载页时按设置里的预取顺序在后台加载索引。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        var mode = AppConfig.Load().General.Prefetch;
        if (mode == "none") return;
        _ = PrefetchAsync(mode);
    }

    /// <summary>主窗口的版本列表刷新后同步「安装到版本」下拉框。</summary>
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

    private async Task PrefetchAsync(string mode)
    {
        if (mode == "modFirst")
        {
            await LoadMarketAsync(false);
            await LoadGameReleasesAsync();
            return;
        }
        if (mode == "gameFirst")
        {
            await LoadGameReleasesAsync();
            await LoadMarketAsync(false);
            return;
        }
        await Task.WhenAll(LoadGameReleasesAsync(), LoadMarketAsync(false));
    }

    private void SwitchTab(string key)
    {
        IsGameTab = key == "game";
        EnsureLoaded();
        if (IsModTab && !_gameLoaded) _ = LoadGameReleasesAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (IsModTab) await LoadMarketAsync(true);
        else await LoadGameReleasesAsync();
    }

    private void ApplyGameSourceHint()
    {
        GameHint = _selectedGameSource.Hint;
        if (!_selectedGameSource.IsZip) UsePolyfill = false;
        OnPropertyChanged(nameof(IsZipSource));
    }

    public bool IsZipSource => _selectedGameSource.IsZip;

    private async Task LoadGameReleasesAsync()
    {
        if (_gameLoading) return;
        _gameLoading = true;
        GameStatus = "正在获取 Release 列表…";
        try
        {
            var list = await DownloadService.FetchGameReleasesAsync(_selectedGameSource);
            GameReleases.Clear();
            foreach (var release in list.Where(r => r.HasInstallableAsset)) GameReleases.Add(release);
            SelectedGameRelease = GameReleases.FirstOrDefault();
            _gameLoaded = true;
            GameStatus = $"共 {GameReleases.Count} 个 Release";
            Log($"已获取 {GameReleases.Count} 个 Release（{_selectedGameSource.Name}）");
        }
        catch (Exception ex)
        {
            _gameLoaded = false;
            GameStatus = "获取失败：" + ex.Message;
            LogError("获取本体列表失败", ex);
        }
        finally
        {
            _gameLoading = false;
        }
    }

    private async Task LoadMarketAsync(bool forceRefresh)
    {
        if (_marketLoading) return;
        _marketLoading = true;
        MarketStatus = "正在获取市场索引…";
        try
        {
            _catalog = await DownloadService.FetchMarketAsync(Log, forceRefresh);

            Categories.Clear();
            Categories.Add("全部分类");
            foreach (var category in _catalog.Categories) Categories.Add(category);
            if (!Categories.Contains(_selectedCategory)) SelectedCategory = "全部分类";

            ApplyMarketFilter();
            MarketStatus = string.Empty;
            MarketHint = $"索引来自 ModHub 市场，共 {_catalog.Mods.Count} 个模组；已安装的会标出状态，缺少的前置可以自动补装。";
            Log($"市场索引就绪：{_catalog.Mods.Count} 个模组" +
                (_catalog.GeneratedAt.Length > 0 ? $"（生成于 {_catalog.GeneratedAt}）" : ""));
        }
        catch (Exception ex)
        {
            MarketStatus = "获取失败：" + ex.Message;
            LogError("获取市场索引失败", ex);
        }
        finally
        {
            _marketLoading = false;
        }
    }

    private void ApplyMarketFilter()
    {
        var keyword = _searchText;
        var match = ParseMatch(_selectedMatch.Key);
        var scope = ParseScope(_selectedScope.Key);
        var sort = ParseSort(_selectedSort.Key);
        var descending = _sortDescending;
        var category = _selectedCategory;
        var filter = _selectedFilter.Key;

        var result = MarketSearch.Filter(_catalog.Mods, keyword, match, scope)
            .Where(mod => category == "全部分类" ||
                          mod.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(mod => filter switch
            {
                "notInstalled" => !mod.Installed,
                "update" => mod.UpdateAvailable,
                _ => true,
            });

        var list = MarketSearch.Sort(result, sort, descending);
        MarketView.Clear();
        foreach (var mod in list) MarketView.Add(mod);
        MarketCountText = $"{MarketView.Count} / {_catalog.Mods.Count} 个";
        if (MarketView.Count > 0 && SelectedMarketMod == null) SelectedMarketMod = MarketView[0];
    }

    private void SaveMarketPrefs()
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.MarketMatchMode = _selectedMatch.Key;
            cfg.General.MarketSearchScope = _selectedScope.Key;
            cfg.General.MarketSortMode = _selectedSort.Key;
            cfg.General.MarketSortDesc = _sortDescending;
            AppConfig.Save(cfg);
        }
        catch (Exception ex)
        {
            LogService.Error("保存搜索设置失败", ex);
        }
    }

    private async Task DownloadGameAsync()
    {
        if (_busy) return;
        var release = SelectedGameRelease;
        if (release == null)
        {
            await PlatformServices.Dialogs.InfoAsync("请先在上面的列表选择一个 Release。", "下载中心");
            return;
        }

        var usePolyfill = release.Source.IsZip && UsePolyfill;
        if (release.PickAsset(usePolyfill) == null)
        {
            await PlatformServices.Dialogs.InfoAsync(
                release.Source.IsZip ? "这个 Release 没有可用的游戏压缩包。" : "这个 Release 没有可用的 HTML 文件。",
                "下载中心");
            return;
        }

        var extras = new List<string>();
        if (InstallI18n) extras.Add("ModI18N");
        if (InstallImagePack) extras.Add("图片包");
        var confirmed = await PlatformServices.Dialogs.ConfirmAsync(
            $"将下载并安装「{release.Title}」" + (usePolyfill ? "（polyfill）" : "") + "。\n" +
            (extras.Count > 0 ? "同时安装：" + string.Join("、", extras) + "。\n" : "") +
            "\n下载体积较大，安装完成后会出现在左侧版本列表。继续吗？",
            "下载游戏本体", "开始下载");
        if (!confirmed) return;

        Busy = true;
        IsGameProgressVisible = true;
        GameProgress = 0;
        GameStatus = "0%";
        try
        {
            Log($"开始下载本体：{release.Title}");
            var folder = await DownloadService.InstallGameAsync(release, usePolyfill,
                InstallI18n, InstallImagePack, Log, p => Post(() =>
                {
                    GameProgress = p;
                    GameStatus = $"{p:P0}";
                }));
            GameProgress = 1;
            GameStatus = "安装完成";
            Log($"本体安装完成：versions\\{folder}");
            VersionInstalled?.Invoke(folder);
        }
        catch (OperationCanceledException)
        {
            GameStatus = "已取消";
            Log("本体下载已取消");
        }
        catch (Exception ex)
        {
            GameStatus = "安装失败";
            LogError("本体下载/安装失败", ex);
            await PlatformServices.Dialogs.ErrorAsync(ex.Message, "下载失败");
        }
        finally
        {
            Busy = false;
            _ = HideGameProgressLaterAsync();
        }
    }

    private async Task HideGameProgressLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        GameProgress = 0;
        IsGameProgressVisible = false;
    }

    private async Task LoadModVersionsAsync(bool reload)
    {
        var mod = SelectedMarketMod;
        var token = ++_versionToken;
        if (mod == null || mod.Repo.Length == 0)
        {
            ModReleases.Clear();
            Editions.Clear();
            OnPropertyChanged(nameof(HasEditions));
            AssetOptions.Clear();
            SelectedEdition = null;
            return;
        }

        if (reload || ModReleases.Count == 0 || ModReleases[0].Assets.Count == 0)
        {
            try
            {
                var releases = await DownloadService.FetchModReleasesAsync(mod);
                if (token != _versionToken || SelectedMarketMod != mod) return;
                ModReleases.Clear();
                foreach (var release in releases) ModReleases.Add(release);
                SelectedModRelease = ModReleases.FirstOrDefault();
            }
            catch (Exception ex)
            {
                LogError("获取模组版本失败", ex);
                return;
            }
        }

        Editions.Clear();
        OnPropertyChanged(nameof(HasEditions));
        if (mod.Editions.Count > 1)
        {
            foreach (var edition in mod.Editions) Editions.Add(edition);
            SelectedEdition = Editions.FirstOrDefault();
        }
        else
        {
            SelectedEdition = null;
        }
        await LoadAssetVersionsAsync();
    }

    private async Task LoadAssetVersionsAsync()
    {
        var mod = SelectedMarketMod;
        var release = SelectedModRelease;
        var token = ++_versionToken;
        AssetOptions.Clear();
        if (mod == null || mod.Repo.Length == 0 || release == null) return;

        try
        {
            var list = await DownloadService.FetchModAssetVersionsAsync(mod, CurrentEdition, release.Tag);
            if (token != _versionToken || SelectedMarketMod != mod) return;
            foreach (var item in list) AssetOptions.Add(item);
            SelectedAsset = AssetOptions.FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogError("获取包版本失败", ex);
        }
    }

    private async Task DownloadModAsync()
    {
        if (_busy) return;
        var mod = SelectedMarketMod;
        if (mod == null)
        {
            await PlatformServices.Dialogs.InfoAsync("请先在列表里选择一个模组。", "下载中心");
            return;
        }
        var target = SelectedTarget;
        if (target == null || target.Info.IsFileMode)
        {
            await PlatformServices.Dialogs.InfoAsync("请先在左侧「安装到版本」里选中一个磁盘版本。", "下载中心");
            return;
        }

        Busy = true;
        MarketStatus = "正在检查前置与冲突…";
        InstallCheckResult check;
        try
        {
            check = await DownloadService.CheckInstallAsync(mod, _catalog, target.Info.FullPath,
                target.Info.GameVersion, Log, SelectedEdition);
        }
        catch (Exception ex)
        {
            LogError("安装前检查失败", ex);
            check = new InstallCheckResult();
        }

        MarketStatus = string.Empty;
        var subtitle = $"目标版本 {target.Info.DisplayName}";
        if (target.Info.GameVersion is { Length: > 0 } gameVersion) subtitle += $"（游戏 {gameVersion}）";
        if (ConfirmInstallAsync != null &&
            !await ConfirmInstallAsync(new InstallCheckRequest(mod.Name, subtitle, check)))
        {
            Busy = false;
            return;
        }

        var plan = check.Dependencies.Where(d => d.Selected).Select(d => d.Mod).Concat(new[] { mod }).ToList();
        IsMarketProgressVisible = true;
        MarketProgress = 0;
        MarketStatus = "0%";
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var item = plan[i];
                var index = i;
                MarketStatus = $"正在安装 {i + 1}/{plan.Count}：{item.Name}";
                Log($"开始下载模组：{item.Title}");
                var tag = item == mod ? SelectedModRelease?.Tag : null;
                var edition = item == mod ? CurrentEdition : null;
                var asset = item == mod ? SelectedAsset?.AssetName : null;
                var file = await DownloadService.InstallModAsync(item, target.Info.FullPath, Log, p => Post(() =>
                {
                    var overall = (index + p) / Math.Max(1, plan.Count);
                    MarketProgress = overall;
                    MarketStatus = $"{overall:P0}　{item.Name}";
                }), tag, edition, asset);
                Log("模组安装完成：" + file);
            }

            MarketStatus = $"安装完成（{plan.Count} 个）";
            _ = LoadMarketAsync(false);
        }
        catch (OperationCanceledException)
        {
            MarketStatus = "已取消";
            Log("模组下载已取消");
        }
        catch (Exception ex)
        {
            MarketStatus = "安装失败";
            LogError("模组下载/安装失败", ex);
            await PlatformServices.Dialogs.ErrorAsync(ex.Message, "下载失败");
        }
        finally
        {
            MarketProgress = 0;
            IsMarketProgressVisible = false;
            Busy = false;
        }
    }

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasNoTasks));
    }

    private static MarketMatchMode ParseMatch(string key) => key switch
    {
        "exact" => MarketMatchMode.Exact,
        "tagall" => MarketMatchMode.TagAll,
        "tagany" => MarketMatchMode.TagAny,
        _ => MarketMatchMode.Contains,
    };

    private static MarketSearchScope ParseScope(string key) => key switch
    {
        "title" => MarketSearchScope.Title,
        "author" => MarketSearchScope.Author,
        "description" => MarketSearchScope.Description,
        "tag" => MarketSearchScope.Tags,
        _ => MarketSearchScope.All,
    };

    private static MarketSortMode ParseSort(string key) => key switch
    {
        "updated" => MarketSortMode.UpdateDate,
        "name" => MarketSortMode.Name,
        _ => MarketSortMode.Default,
    };

    private static OptionItem Pick(List<OptionItem> options, string? key) =>
        options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase)) ?? options[0];

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void Log(string message)
    {
        try { LogService.Append(message); } catch { }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Post(() =>
        {
            var text = string.IsNullOrEmpty(LogText) ? line : LogText + Environment.NewLine + line;
            var lines = text.Split(Environment.NewLine);
            if (lines.Length > 300) text = string.Join(Environment.NewLine, lines[^300..]);
            LogText = text;
        });
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

/// <summary>安装前检查对话框的请求数据。</summary>
public sealed record InstallCheckRequest(string ModName, string TargetText, InstallCheckResult Check);
