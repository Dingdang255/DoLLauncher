using System.Collections.ObjectModel;
using System.ComponentModel;
using DoLLauncher.Core.Search;
using DoLLauncher.Core.Versions;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Maui.Dispatching;

namespace DoLLauncher.Maui.Controls;

/// <summary>下载中心右侧的「模组市场」子面板，由下载中心页面托管。</summary>
public partial class MarketPane : ContentView
{
    private static readonly string[] MatchValues = { "contains", "exact" };
    private static readonly string[] ScopeValues = { "all", "title", "author", "description", "tag" };
    private static readonly string[] SortValues = { "default", "updated", "name" };

    private readonly ObservableCollection<MarketRow> _rows = new();
    private readonly Dictionary<string, MarketRow> _rowCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarketMod> _all = new();

    private MarketCatalog? _catalog;
    private bool _loading;
    private bool _loaded;
    private bool _suppressPrefs;
    private bool _filterOpen = true;
    private int _chip;
    private int _filterSeq;

    private VersionInfo? _target;

    private IDispatcherTimer? _searchTimer;

    public MarketPane()
    {
        InitializeComponent();
        MarketList.ItemsSource = _rows;
        _suppressPrefs = true;
        MatchPicker.ItemsSource = new[] { "部分匹配", "完全匹配" };
        ScopePicker.ItemsSource = new[] { "全部", "标题", "作者", "简介", "标签" };
        SortPicker.ItemsSource = new[] { "默认", "最近更新", "名称" };
        LoadPrefs();
        _suppressPrefs = false;
        StyleChips();
    }

    /// <summary>安装完成后通知宿主刷新版本列表。</summary>
    public event EventHandler? InstallCompleted;

    /// <summary>页面切入市场面板时调用：首次进入才真正拉取索引。</summary>
    public async Task ActivateAsync()
    {
        if (!_loaded) await LoadMarketAsync();
        else
        {
            await RefreshInstalledStatusAsync();
            ApplyFilter();
        }
    }

    /// <summary>页面离开时停止搜索防抖计时器。</summary>
    public void Deactivate() => _searchTimer?.Stop();

    /// <summary>宿主切换「安装到版本」后重算已装标记。</summary>
    public async Task SetTargetAsync(VersionInfo? target)
    {
        _target = target;
        if (_all.Count == 0)
        {
            if (!_loaded) await LoadMarketAsync();
            return;
        }
        await RefreshInstalledStatusAsync();
        ApplyFilter();
    }

    public async Task RefreshAsync()
    {
        await LoadMarketAsync(forceRefresh: true);
    }

    // ---------- 加载 ----------

    private async Task LoadMarketAsync(bool forceRefresh = false)
    {
        if (_loading) return;
        _loading = true;
        ErrorPanel.IsVisible = false;
        MarketList.IsVisible = false;
        LoadingPanel.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var catalog = await DownloadService.FetchMarketAsync(
                msg => LogService.Append("[市场] " + msg), forceRefresh);
            _catalog = catalog;
            _all.Clear();
            _all.AddRange(catalog.Mods.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase));
            _rowCache.Clear();

            var categories = new List<string> { "全部分类" };
            categories.AddRange(catalog.Categories);
            _suppressPrefs = true;
            try
            {
                var selected = CategoryPicker.SelectedItem as string;
                CategoryPicker.ItemsSource = categories;
                var index = selected == null ? 0 : categories.IndexOf(selected);
                CategoryPicker.SelectedIndex = index >= 0 ? index : 0;
            }
            finally
            {
                _suppressPrefs = false;
            }

            await RefreshInstalledStatusAsync();
            _loaded = true;
            HeaderCountLabel.Text = _target == null
                ? $"共 {_all.Count} 个模组 · 请先添加一个版本"
                : $"共 {_all.Count} 个模组";
            LoadingPanel.IsVisible = false;
            MarketList.IsVisible = true;
            ApplyFilter();
            LogService.Append($"[市场] 索引就绪：{_all.Count} 个模组" +
                (catalog.GeneratedAt.Length > 0 ? $"（生成于 {catalog.GeneratedAt}）" : ""));
        }
        catch (Exception ex)
        {
            LogService.Error("获取模组市场索引失败", ex);
            ErrorLabel.Text = "获取市场索引失败：" + ex.Message;
            ErrorPanel.IsVisible = true;
            MarketList.IsVisible = false;
        }
        finally
        {
            _loading = false;
            LoadingIndicator.IsRunning = false;
            LoadingPanel.IsVisible = false;
        }
    }

    /// <summary>用目标版本磁盘上的模组重算「已安装 / 可更新」。</summary>
    private async Task RefreshInstalledStatusAsync()
    {
        if (_all.Count == 0) return;
        var target = _target;
        var installed = new List<ModInfo>();
        if (target != null)
        {
            try
            {
                installed = await Task.Run(() => ModService.Load(target.FullPath));
            }
            catch (Exception ex)
            {
                LogService.Error("读取已安装模组失败", ex);
            }
        }

        foreach (var mod in _all)
        {
            var version = DownloadService.FindInstalledVersion(mod, installed);
            mod.InstalledVersion = version;
            mod.UpdateAvailable = version.Length > 0 && VersionUtil.Compare(version, mod.Version) < 0;
        }
    }

    // ---------- 折叠 ----------

    private void OnToggleFilterClicked(object? sender, TappedEventArgs e)
    {
        _filterOpen = !_filterOpen;
        FilterBody.IsVisible = _filterOpen;
        FilterChevron.Rotation = _filterOpen ? 90 : 0;
    }

    // ---------- 筛选 ----------

    private void ApplyFilter()
    {
        var seq = ++_filterSeq;
        if (_all.Count == 0)
        {
            _rows.Clear();
            FilterCountLabel.Text = "0 / 0 个";
            EmptyLabel.Text = _loaded ? "市场索引为空" : "没有符合条件的模组";
            return;
        }

        var keyword = SearchBox.Text;
        var match = CurrentMatchMode();
        var scope = CurrentScope();
        var sort = CurrentSort();
        var descending = DescSwitch.IsToggled;
        var category = CategoryPicker.SelectedItem as string;
        var chip = _chip;
        var snapshot = _all.ToList();

        var result = Task.Run(() =>
        {
            var kept = new List<MarketMod>();
            foreach (var mod in MarketSearch.Filter(snapshot, keyword, match, scope))
            {
                if (!string.IsNullOrEmpty(category) && category != "全部分类" &&
                    !mod.Category.Equals(category, StringComparison.OrdinalIgnoreCase)) continue;
                if (chip == 1 && mod.Installed) continue;
                if (chip == 2 && !mod.UpdateAvailable) continue;
                kept.Add(mod);
            }
            return MarketSearch.Sort(kept, sort, descending);
        });

        _ = ApplyFilterResultAsync(seq, result);
    }

    private async Task ApplyFilterResultAsync(int seq, Task<List<MarketMod>> work)
    {
        List<MarketMod> result;
        try
        {
            result = await work;
        }
        catch (Exception ex)
        {
            LogService.Error("筛选模组失败", ex);
            return;
        }

        if (seq != _filterSeq) return;
        _rows.Clear();
        foreach (var mod in result) _rows.Add(GetRow(mod));
        FilterCountLabel.Text = $"{_rows.Count} / {_all.Count} 个";
    }

    private MarketRow GetRow(MarketMod mod)
    {
        var key = mod.Id.Length > 0 ? mod.Id : mod.Name;
        if (_rowCache.TryGetValue(key, out var row) && ReferenceEquals(row.Mod, mod)) return row;
        row = new MarketRow(mod);
        _rowCache[key] = row;
        return row;
    }

    private MarketMatchMode CurrentMatchMode()
    {
        var scope = CurrentScope();
        if (scope == MarketSearchScope.Tags)
            return MatchPicker.SelectedIndex == 1 ? MarketMatchMode.Exact : MarketMatchMode.TagAny;
        return MatchPicker.SelectedIndex == 1 ? MarketMatchMode.Exact : MarketMatchMode.Contains;
    }

    private MarketSearchScope CurrentScope() => ScopePicker.SelectedIndex switch
    {
        1 => MarketSearchScope.Title,
        2 => MarketSearchScope.Author,
        3 => MarketSearchScope.Description,
        4 => MarketSearchScope.Tags,
        _ => MarketSearchScope.All,
    };

    private MarketSortMode CurrentSort() => SortPicker.SelectedIndex switch
    {
        1 => MarketSortMode.UpdateDate,
        2 => MarketSortMode.Name,
        _ => MarketSortMode.Default,
    };

    private void StyleChips()
    {
        StyleChip(ChipAll, _chip == 0);
        StyleChip(ChipUninstalled, _chip == 1);
        StyleChip(ChipUpdatable, _chip == 2);
    }

    private static void StyleChip(Button button, bool active)
    {
        if (active)
        {
            // 跟随设置里的主题色，而不是固定紫色。
            button.SetDynamicResource(Button.BackgroundColorProperty, "AccentColor");
            button.SetDynamicResource(Button.TextColorProperty, "AccentOnColor");
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty,
                Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
            button.SetAppThemeColor(Button.TextColorProperty,
                Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        }
    }

    // ---------- 配置读写 ----------

    private void LoadPrefs()
    {
        _suppressPrefs = true;
        try
        {
            var general = AppConfig.Load().General;
            ScopePicker.SelectedIndex = IndexOf(ScopeValues, general.MarketSearchScope, 0);
            MatchPicker.SelectedIndex =
                string.Equals(general.MarketMatchMode, "exact", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            SortPicker.SelectedIndex = IndexOf(SortValues, general.MarketSortMode, 0);
            DescSwitch.IsToggled = general.MarketSortDesc;
        }
        finally
        {
            _suppressPrefs = false;
        }
    }

    private void SavePrefs()
    {
        var scope = ScopePicker.SelectedIndex < 0 ? "all" : ScopeValues[ScopePicker.SelectedIndex];
        var match = ScopePicker.SelectedIndex == 4
            ? (MatchPicker.SelectedIndex == 1 ? "exact" : "tagany")
            : (MatchPicker.SelectedIndex == 1 ? "exact" : "contains");
        var sort = SortPicker.SelectedIndex < 0 ? "default" : SortValues[SortPicker.SelectedIndex];
        var desc = DescSwitch.IsToggled;

        try
        {
            var config = AppConfig.Load();
            config.General.MarketSearchScope = scope;
            config.General.MarketMatchMode = match;
            config.General.MarketSortMode = sort;
            config.General.MarketSortDesc = desc;
            _ = Task.Run(() =>
            {
                try { AppConfig.Save(config); }
                catch (Exception ex) { LogService.Error("保存市场搜索设置失败", ex); }
            });
        }
        catch (Exception ex)
        {
            LogService.Error("保存市场搜索设置失败", ex);
        }
    }

    private static int IndexOf(string[] values, string? current, int fallback)
    {
        var index = Array.FindIndex(values,
            v => string.Equals(v, current ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : fallback;
    }

    // ---------- 事件 ----------

    private async void OnRetryClicked(object? sender, EventArgs e) =>
        await LoadMarketAsync(forceRefresh: true);

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchTimer ??= Dispatcher.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchTimer.IsRepeating = false;
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchDebounceTick;
        _searchTimer.Tick += OnSearchDebounceTick;
        _searchTimer.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        ApplyFilter();
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        if (_suppressPrefs) return;
        SavePrefs();
        ApplyFilter();
    }

    private void OnSortDescToggled(object? sender, ToggledEventArgs e)
    {
        if (_suppressPrefs) return;
        SavePrefs();
        ApplyFilter();
    }

    private void OnChipAllClicked(object? sender, EventArgs e) => SetChip(0);

    private void OnChipUninstalledClicked(object? sender, EventArgs e) => SetChip(1);

    private void OnChipUpdatableClicked(object? sender, EventArgs e) => SetChip(2);

    private void SetChip(int chip)
    {
        _chip = chip;
        StyleChips();
        ApplyFilter();
    }

    // ---------- 行内版本选择 ----------

    private async void OnRowVersionsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MarketRow row }) return;
        row.ShowVersionPanel = !row.ShowVersionPanel;
        if (!row.ShowVersionPanel) return;
        if (row.Releases.Count == 0) await LoadReleasesAsync(row);
        else _ = LoadStarAsync(row);
    }

    private async void OnRowLoadVersionsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MarketRow row }) return;
        await LoadReleasesAsync(row);
    }

    private async void OnRowReleaseChanged(object? sender, EventArgs e)
    {
        if (sender is not Picker picker || picker.BindingContext is not MarketRow row) return;
        if (row.Syncing) return;

        row.SelectedReleaseIndex = picker.SelectedIndex;
        row.Syncing = true;
        try
        {
            row.SelectedEditionIndex = -1;
            row.Assets.Clear();
            row.SelectedAssetIndex = -1;
        }
        finally
        {
            row.Syncing = false;
        }
        await LoadAssetsAsync(row);
    }

    private async void OnRowEditionChanged(object? sender, EventArgs e)
    {
        if (sender is not Picker picker || picker.BindingContext is not MarketRow row) return;
        if (row.Syncing) return;

        row.SelectedEditionIndex = picker.SelectedIndex;
        row.Assets.Clear();
        row.SelectedAssetIndex = -1;
        await LoadAssetsAsync(row);
    }

    private async Task LoadReleasesAsync(MarketRow row)
    {
        if (row.LoadingVersions) return;
        row.LoadingVersions = true;
        row.LoadVersionVisible = false;
        row.StatusText = "正在获取版本列表…";
        try
        {
            var releases = await DownloadService.FetchModReleasesAsync(row.Mod);
            row.Syncing = true;
            try
            {
                row.Releases.Clear();
                foreach (var release in releases) row.Releases.Add(release);
                row.SelectedReleaseIndex = row.Releases.Count > 0 ? 0 : -1;
            }
            finally
            {
                row.Syncing = false;
            }

            if (row.Releases.Count > 0)
            {
                row.StatusText = $"共 {row.Releases.Count} 个版本";
                await LoadAssetsAsync(row);
            }
            else
            {
                row.StatusText = "没有可下载的 Release，将直接安装最新版";
            }
            _ = LoadStarAsync(row);
        }
        catch (Exception ex)
        {
            LogService.Error("获取模组版本失败", ex);
            row.StatusText = "获取版本失败：" + ex.Message;
        }
        finally
        {
            row.LoadingVersions = false;
            row.LoadVersionVisible = row.Assets.Count == 0;
        }
    }

    /// <summary>按当前选中的 Release / 包型加载可选的包版本。</summary>
    private async Task LoadAssetsAsync(MarketRow row)
    {
        var release = row.SelectedRelease;
        if (release == null || row.LoadingAssets) return;

        row.LoadingAssets = true;
        row.LoadVersionVisible = false;
        try
        {
            var assets = await DownloadService.FetchModAssetVersionsAsync(
                row.Mod, row.SelectedEdition, release.Tag);
            if (!ReferenceEquals(row.SelectedRelease, release)) return;

            row.Syncing = true;
            try
            {
                row.Assets.Clear();
                foreach (var asset in assets) row.Assets.Add(asset);
                row.SelectedAssetIndex = row.Assets.Count > 0 ? 0 : -1;
            }
            finally
            {
                row.Syncing = false;
            }
            row.StatusText = row.Assets.Count > 0
                ? $"包版本 {row.Assets.Count} 个"
                : "这个版本没有具体包版本，将自动挑选";
        }
        catch (Exception ex)
        {
            LogService.Error("获取包版本失败", ex);
            row.StatusText = "获取包版本失败，将自动挑选";
        }
        finally
        {
            row.LoadingAssets = false;
            row.LoadVersionVisible = row.Assets.Count == 0;
        }
    }

    /// <summary>Star 按需拉取（有 24 小时缓存），取不到就保持隐藏。</summary>
    private async Task LoadStarAsync(MarketRow row)
    {
        if (row.StarRequested) return;
        row.StarRequested = true;
        var stars = await ModStarService.TryGetAsync(row.Mod.Repo);
        if (stars == null) return;
        row.StarText = "★ " + (stars >= 10000 ? $"{stars / 10000.0:0.#} 万" : stars.ToString());
        row.HasStar = true;
    }

    // ---------- 安装 ----------

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MarketRow row }) return;

        var target = _target;
        if (target == null)
        {
            await PlatformServices.Dialogs.InfoAsync("请先在左侧选择一个安装目标版本。", "模组市场");
            return;
        }
        if (_catalog == null)
        {
            await PlatformServices.Dialogs.InfoAsync("市场索引还没有加载完成。", "模组市场");
            return;
        }

        InstallCheckResult check;
        try
        {
            check = await DownloadService.CheckInstallAsync(row.Mod, _catalog, target.FullPath,
                target.GameVersion, msg => LogService.Append("[市场] " + msg), row.SelectedEdition);
        }
        catch (Exception ex)
        {
            LogService.Error("安装前检查失败", ex);
            check = new InstallCheckResult();
        }

        var page = new InstallCheckPage(row.Mod, target, check,
            row.SelectedRelease, row.SelectedEdition, row.SelectedAssetName);
        page.Completed += OnInstallCompleted;
        await Navigation.PushAsync(page);
    }

    /// <summary>安装页弹出后刷新已装标记，并提示结果。</summary>
    private async Task OnInstallCompleted(int count)
    {
        await RefreshInstalledStatusAsync();
        ApplyFilter();
        // 索引本身不变，这里只把市场缓存预热一次，失败不影响界面。
        _ = Task.Run(async () =>
        {
            try { await DownloadService.FetchMarketAsync(); }
            catch (Exception ex) { LogService.Append("[市场] 刷新缓存失败：" + ex.Message); }
        });
        InstallCompleted?.Invoke(this, EventArgs.Empty);
        await PlatformServices.Dialogs.InfoAsync($"已安装 {count} 个模组。", "模组市场");
    }
}

/// <summary>市场列表行：包装 MarketMod，并保存行内的版本 / 包型 / 包版本选择。</summary>
public sealed class MarketRow : INotifyPropertyChanged
{
    public MarketRow(MarketMod mod)
    {
        Mod = mod;
        Editions = new ObservableCollection<MarketEdition>(mod.Editions);
    }

    public MarketMod Mod { get; }
    public ObservableCollection<GameRelease> Releases { get; } = new();
    public ObservableCollection<MarketEdition> Editions { get; }
    public ObservableCollection<ModAssetOption> Assets { get; } = new();

    public bool HasEditions => Editions.Count > 1;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Mod.Description);

    /// <summary>程序化回填选中项时置位，避免事件处理里重复拉取。</summary>
    public bool Syncing { get; set; }

    /// <summary>Star 只按需拉取一次。</summary>
    public bool StarRequested { get; set; }

    private int _selectedReleaseIndex = -1;
    public int SelectedReleaseIndex
    {
        get => _selectedReleaseIndex;
        set => Set(ref _selectedReleaseIndex, value);
    }

    private int _selectedEditionIndex = -1;
    public int SelectedEditionIndex
    {
        get => _selectedEditionIndex;
        set => Set(ref _selectedEditionIndex, value);
    }

    private int _selectedAssetIndex = -1;
    public int SelectedAssetIndex
    {
        get => _selectedAssetIndex;
        set => Set(ref _selectedAssetIndex, value);
    }

    public GameRelease? SelectedRelease =>
        SelectedReleaseIndex >= 0 && SelectedReleaseIndex < Releases.Count
            ? Releases[SelectedReleaseIndex]
            : null;

    public MarketEdition? SelectedEdition =>
        SelectedEditionIndex >= 0 && SelectedEditionIndex < Editions.Count
            ? Editions[SelectedEditionIndex]
            : null;

    public string? SelectedAssetName =>
        SelectedAssetIndex >= 0 && SelectedAssetIndex < Assets.Count
            ? Assets[SelectedAssetIndex].AssetName
            : null;

    private bool _showVersionPanel;
    public bool ShowVersionPanel
    {
        get => _showVersionPanel;
        set => Set(ref _showVersionPanel, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private bool _loadVersionVisible = true;
    public bool LoadVersionVisible
    {
        get => _loadVersionVisible;
        set => Set(ref _loadVersionVisible, value);
    }

    private bool _loadingVersions;
    public bool LoadingVersions
    {
        get => _loadingVersions;
        set => Set(ref _loadingVersions, value);
    }

    private bool _loadingAssets;
    public bool LoadingAssets
    {
        get => _loadingAssets;
        set => Set(ref _loadingAssets, value);
    }

    private string _starText = "";
    public string StarText
    {
        get => _starText;
        set => Set(ref _starText, value);
    }

    private bool _hasStar;
    public bool HasStar
    {
        get => _hasStar;
        set => Set(ref _hasStar, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
