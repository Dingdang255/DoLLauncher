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
    private bool _togglingFilter;
    private int _chip;
    private int _filterSeq;
    private int _lastVisibleIndex;
    private int _pendingScrollIndex = -1;

    private bool _selectMode;
    private bool _suppressCheck;
    private bool _selectBarShown;
    private int _selectBarSeq;
    private long _suppressTapUntil;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private AndroidX.RecyclerView.Widget.RecyclerView? _recycler;

    private VersionInfo? _target;

    private IDispatcherTimer? _searchTimer;

    public MarketPane()
    {
        InitializeComponent();
        MarketList.ItemsSource = _rows;
        MarketList.HandlerChanged += (_, _) => AttachTouchListener();
        // 安装完模组后要重建列表，记住当前滚动位置，重建后按原位置恢复。
        MarketList.Scrolled += (_, e) =>
        {
            if (e.FirstVisibleItemIndex >= 0) _lastVisibleIndex = e.FirstVisibleItemIndex;
        };
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
            var catalog = await IndexPrefetchService.GetMarketAsync(
                forceRefresh, msg => LogService.Append("[市场] " + msg));
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

    private async void OnToggleFilterClicked(object? sender, TappedEventArgs e)
    {
        if (_togglingFilter) return;
        _togglingFilter = true;
        _filterOpen = !_filterOpen;
        try
        {
            await Task.WhenAll(
                UiMotion.TogglePanelAsync(FilterBody, _filterOpen),
                UiMotion.RotateAsync(FilterChevron, _filterOpen ? 90 : 0));
        }
        finally { _togglingFilter = false; }
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

        if (_pendingScrollIndex >= 0)
        {
            var index = Math.Min(_pendingScrollIndex, _rows.Count - 1);
            _pendingScrollIndex = -1;
            if (index >= 0)
            {
                Dispatcher.Dispatch(() =>
                    MarketList.ScrollTo(index, position: ScrollToPosition.Start, animate: false));
            }
        }
    }

    private MarketRow GetRow(MarketMod mod)
    {
        var key = KeyOf(mod);
        if (_rowCache.TryGetValue(key, out var row) && ReferenceEquals(row.Mod, mod)) return row;
        row = new MarketRow(mod)
        {
            SelectMode = _selectMode,
            IsChecked = _selected.Contains(key),
        };
        _rowCache[key] = row;
        return row;
    }

    private static string KeyOf(MarketMod mod) => mod.Id.Length > 0 ? mod.Id : mod.Name;

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
            // 换版本后包型仍保留「默认第一个」，不要重置成空白。
            row.SelectedEditionIndex = row.HasEditions ? 0 : -1;
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

    // ---------- 详情与选择模式 ----------

    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (Environment.TickCount64 < _suppressTapUntil) return;
        if (sender is not Element { BindingContext: MarketRow row }) return;
        // 选择模式下单击只切换勾选，不进详情；详情改用行内「详情」按钮。
        if (_selectMode)
        {
            SetRowChecked(row, !row.IsChecked);
            return;
        }
        await OpenDetailAsync(row);
    }

    private async void OnRowDetailClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: MarketRow row }) return;
        await OpenDetailAsync(row);
    }

    private Task OpenDetailAsync(MarketRow row) =>
        Navigation.PushAsync(new MarketDetailPage(row.Mod, row.SelectedRelease));

    private void OnRowCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_suppressCheck) return;
        if (sender is not CheckBox { BindingContext: MarketRow row }) return;
        var key = KeyOf(row.Mod);
        if (e.Value) _selected.Add(key);
        else _selected.Remove(key);
        UpdateSelectBar();
    }

    private void SetRowChecked(MarketRow row, bool value)
    {
        var key = KeyOf(row.Mod);
        if (value) _selected.Add(key);
        else _selected.Remove(key);
        _suppressCheck = true;
        try { row.IsChecked = value; }
        finally { _suppressCheck = false; }
        UpdateSelectBar();
    }

    /// <summary>切换选择模式；退出时清空所有勾选。</summary>
    private void SetSelectMode(bool on)
    {
        if (_selectMode == on) return;
        _selectMode = on;
        if (!on) _selected.Clear();

        _suppressCheck = true;
        try
        {
            foreach (var row in _rowCache.Values)
            {
                row.SelectMode = on;
                if (!on) row.IsChecked = false;
            }
        }
        finally
        {
            _suppressCheck = false;
        }
        UpdateSelectBar();
    }

    private void UpdateSelectBar()
    {
        SelectCountLabel.Text = $"已选 {_selected.Count} 个";
        BatchInstallButton.IsEnabled = _selected.Count > 0;
        ShowSelectBar(_selectMode);
    }

    /// <summary>底部操作栏：显示时自下而上，隐藏时自上而下。</summary>
    private async void ShowSelectBar(bool show)
    {
        if (_selectBarShown == show) return;
        _selectBarShown = show;
        var seq = ++_selectBarSeq;

        if (show)
        {
            SelectBar.IsVisible = true;
            if (!UiMotion.Enabled)
            {
                SelectBar.Opacity = 1;
                SelectBar.TranslationY = 0;
                return;
            }
            SelectBar.Opacity = 0;
            SelectBar.TranslationY = 14;
            await Task.WhenAll(
                SelectBar.FadeToAsync(1, 140, Easing.CubicOut),
                SelectBar.TranslateToAsync(0, 0, 170, Easing.CubicOut));
        }
        else
        {
            if (!UiMotion.Enabled)
            {
                SelectBar.IsVisible = false;
                return;
            }
            await Task.WhenAll(
                SelectBar.FadeToAsync(0, 110, Easing.CubicIn),
                SelectBar.TranslateToAsync(0, 14, 130, Easing.CubicIn));
            if (seq != _selectBarSeq) return;
            SelectBar.IsVisible = false;
            SelectBar.Opacity = 1;
            SelectBar.TranslationY = 0;
        }
    }

    private void OnExitSelectClicked(object? sender, EventArgs e) => SetSelectMode(false);

    private async void OnBatchInstallClicked(object? sender, EventArgs e)
    {
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

        var picked = _all.Where(m => _selected.Contains(KeyOf(m))).ToList();
        if (picked.Count == 0)
        {
            await PlatformServices.Dialogs.InfoAsync("请先勾选要安装的模组。", "模组市场");
            return;
        }

        var ordered = MarketSearch.Sort(picked, CurrentSort(), DescSwitch.IsToggled);
        SetSelectMode(false);

        var plans = new List<InstallPlan>();
        foreach (var mod in ordered)
        {
            _rowCache.TryGetValue(KeyOf(mod), out var row);
            InstallCheckResult check;
            try
            {
                check = await DownloadService.CheckInstallAsync(mod, _catalog, target.FullPath,
                    target.GameVersion, msg => LogService.Append("[市场] " + msg), row?.SelectedEdition);
            }
            catch (Exception ex)
            {
                LogService.Error("安装前检查失败", ex);
                check = new InstallCheckResult();
            }

            plans.Add(new InstallPlan
            {
                Mod = mod,
                Check = check,
                Release = row?.SelectedRelease,
                Edition = row?.SelectedEdition,
                AssetName = row?.SelectedAssetName,
            });
        }

        var page = new InstallCheckPage(plans, target, ShowInstallProgress);
        page.Completed += OnInstallCompleted;
        await Navigation.PushAsync(page);
    }

    /// <summary>安装进度覆盖层：市场面板本身不显示进度，安装时至少能看到百分比与当前模组。</summary>
    private void ShowInstallProgress(double value, string stage)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (value < 0)
            {
                InstallOverlay.IsVisible = false;
                return;
            }
            InstallOverlay.IsVisible = true;
            InstallOverlayProgress.Progress = Math.Clamp(value, 0, 1);
            InstallOverlayStage.Text = stage;
        });
    }

    // ---------- 长按拖选（Android 原生触摸） ----------

    private bool _touchAttached;

    private void AttachTouchListener()
    {
        if (MarketList.Handler?.PlatformView is not AndroidX.RecyclerView.Widget.RecyclerView recycler) return;
        if (_touchAttached && ReferenceEquals(_recycler, recycler)) return;
        _touchAttached = true;
        _recycler = recycler;
        recycler.AddOnItemTouchListener(new MarketTouchListener(this, recycler));
    }

    internal bool SelectMode => _selectMode;

    /// <summary>长按触发：进入选择模式并选中按到的项。</summary>
    internal void TouchBeginSelection(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _suppressTapUntil = Environment.TickCount64 + 900;
        SetSelectMode(true);
        SetRowChecked(_rows[index], true);
    }

    /// <summary>
    /// 按下点是否落在复选框上。刚进入选择模式时复选框还没完成布局，
    /// 用进入前那个位置的「安装」按钮作为等价区域。
    /// </summary>
    internal bool TouchPressOnCheckbox(float x, float y, bool selectModeBefore)
    {
        var recycler = _recycler;
        if (recycler == null) return false;
        var child = recycler.FindChildViewUnder(x, y);
        if (child == null) return false;

        if (HitControl(child, x - child.Left, y - child.Top, checkBox: selectModeBefore)) return true;

        if (selectModeBefore) return false;
        var density = child.Resources?.DisplayMetrics?.Density ?? 1f;
        return x >= child.Right - 78 * density && y <= child.Top + 64 * density;
    }

    /// <summary>在行视图里递归判断触点是否落在指定类型的控件上。</summary>
    private static bool HitControl(Android.Views.View view, float x, float y, bool checkBox)
    {
        var hit = checkBox ? view is Android.Widget.CheckBox : view is Android.Widget.Button;
        if (hit) return x >= 0 && y >= 0 && x <= view.Width && y <= view.Height;
        if (view is not Android.Views.ViewGroup group) return false;

        for (var i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child == null || child.Visibility != Android.Views.ViewStates.Visible) continue;
            if (x < child.Left || x > child.Right || y < child.Top || y > child.Bottom) continue;
            if (HitControl(child, x - child.Left, y - child.Top, checkBox)) return true;
        }
        return false;
    }

    internal void TouchSuppressTap() => _suppressTapUntil = Environment.TickCount64 + 900;

    /// <summary>拖选起点：记录拖选前的勾选集合，保证拖选范围之外原先勾选的项不被误取消。</summary>
    internal HashSet<string> TouchBeginRange(int anchor)
    {
        var baseKeys = new HashSet<string>(_selected, StringComparer.OrdinalIgnoreCase);
        if (anchor >= 0 && anchor < _rows.Count) baseKeys.Add(KeyOf(_rows[anchor].Mod));
        return baseKeys;
    }

    internal void TouchDragTo(int anchor, int current, HashSet<string> baseKeys)
    {
        if (anchor < 0 || current < 0 || _rows.Count == 0) return;
        var lo = Math.Min(anchor, current);
        var hi = Math.Max(anchor, current);

        var next = new HashSet<string>(baseKeys, StringComparer.OrdinalIgnoreCase);
        for (var i = lo; i <= hi && i < _rows.Count; i++) next.Add(KeyOf(_rows[i].Mod));

        _selected.Clear();
        _selected.UnionWith(next);

        _suppressCheck = true;
        try
        {
            foreach (var row in _rowCache.Values)
                row.IsChecked = _selected.Contains(KeyOf(row.Mod));
        }
        finally
        {
            _suppressCheck = false;
        }
        UpdateSelectBar();
    }

    internal void TouchEnd() => UpdateSelectBar();

    /// <summary>把触点换算成列表里最近的一项；触点越界时取最近的可见项。</summary>
    internal static int NearestIndex(AndroidX.RecyclerView.Widget.RecyclerView recycler, float y)
    {
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < recycler.ChildCount; i++)
        {
            var child = recycler.GetChildAt(i);
            if (child == null) continue;
            var distance = Math.Abs(child.Top + child.Height / 2f - y);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = recycler.GetChildAdapterPosition(child);
        }
        return best;
    }

    private sealed class MarketTouchListener : Java.Lang.Object,
        AndroidX.RecyclerView.Widget.RecyclerView.IOnItemTouchListener
    {
        private const int LongPressMs = 450;

        private readonly MarketPane _pane;
        private readonly AndroidX.RecyclerView.Widget.RecyclerView _recycler;
        private readonly int _slop;
        private readonly float _density;
        private readonly IDispatcherTimer _longTimer;
        private readonly IDispatcherTimer _scrollTimer;

        private float _downX;
        private float _downY;
        private float _lastY;
        private int _downIndex = -1;
        private bool _pending;
        private bool _dragging;
        private int _anchor = -1;
        private int _scrollDir;
        private HashSet<string> _baseKeys = new(StringComparer.OrdinalIgnoreCase);

        public MarketTouchListener(MarketPane pane, AndroidX.RecyclerView.Widget.RecyclerView recycler)
        {
            _pane = pane;
            _recycler = recycler;
            var context = recycler.Context;
            _slop = context == null ? 8 : Android.Views.ViewConfiguration.Get(context)!.ScaledTouchSlop;
            _density = recycler.Resources?.DisplayMetrics?.Density ?? 1f;

            _longTimer = pane.Dispatcher.CreateTimer();
            _longTimer.Interval = TimeSpan.FromMilliseconds(LongPressMs);
            _longTimer.IsRepeating = false;
            _longTimer.Tick += (_, _) => OnLongPress();

            _scrollTimer = pane.Dispatcher.CreateTimer();
            _scrollTimer.Interval = TimeSpan.FromMilliseconds(16);
            _scrollTimer.IsRepeating = true;
            _scrollTimer.Tick += (_, _) => AutoScrollTick();
        }

        public bool OnInterceptTouchEvent(AndroidX.RecyclerView.Widget.RecyclerView rv,
            Android.Views.MotionEvent e)
        {
            // 抬起事件里 Track 会把 _dragging 复位，这里要先记住本次是否处于拖动中，
            // 否则最后一次抬起不会被拦截，行内的按钮仍会收到点击。
            var wasDragging = _dragging;
            Track(e);
            return wasDragging || _dragging;
        }

        public void OnTouchEvent(AndroidX.RecyclerView.Widget.RecyclerView rv, Android.Views.MotionEvent e)
        {
            if (_dragging) Track(e);
        }

        public void OnRequestDisallowInterceptTouchEvent(bool disallowIntercept) { }

        private void Track(Android.Views.MotionEvent e)
        {
            switch (e.ActionMasked)
            {
                case Android.Views.MotionEventActions.Down:
                    _downX = e.GetX();
                    _downY = e.GetY();
                    _lastY = _downY;
                    _downIndex = MarketPane.NearestIndex(_recycler, _downY);
                    _pending = _downIndex >= 0;
                    _dragging = false;
                    if (_pending) _longTimer.Start();
                    break;

                case Android.Views.MotionEventActions.Move:
                    _lastY = e.GetY();
                    if (_dragging)
                    {
                        UpdateDrag();
                        return;
                    }
                    if (!_pending) return;
                    if (Math.Abs(_lastY - _downY) > _slop) StopPending();
                    break;

                case Android.Views.MotionEventActions.Up:
                case Android.Views.MotionEventActions.Cancel:
                    StopPending();
                    if (_dragging) EndDrag();
                    break;
            }
        }

        private void StopPending()
        {
            _pending = false;
            _longTimer.Stop();
        }

        private void OnLongPress()
        {
            if (_dragging) return;
            _longTimer.Stop();
            if (!_pending) return;
            _pending = false;

            var index = MarketPane.NearestIndex(_recycler, _lastY);
            if (index < 0) index = _downIndex;
            if (index < 0) return;

            // 先进入选择模式并选中该项，复选框随后才会出现在右上角；
            // 已经处于选择模式时长按本身不再改变勾选。
            _pane.TouchSuppressTap();
            var selectModeBefore = _pane.SelectMode;
            if (!selectModeBefore) _pane.TouchBeginSelection(index);
            if (!_pane.TouchPressOnCheckbox(_downX, _downY, selectModeBefore)) return;

            _anchor = index;
            _baseKeys = _pane.TouchBeginRange(_anchor);
            _dragging = true;
            AllowSelfIntercept();
            UpdateDrag();
        }

        private void UpdateDrag()
        {
            var current = MarketPane.NearestIndex(_recycler, _lastY);
            if (current < 0) current = _anchor;
            if (_anchor >= 0 && current >= 0) _pane.TouchDragTo(_anchor, current, _baseKeys);

            var edge = 48 * _density;
            var dir = 0;
            if (_lastY < edge) dir = -1;
            else if (_lastY > _recycler.Height - edge) dir = 1;

            if (dir == 0)
            {
                _scrollTimer.Stop();
                _scrollDir = 0;
            }
            else
            {
                _scrollDir = dir;
                if (!_scrollTimer.IsRunning) _scrollTimer.Start();
            }
        }

        private void AutoScrollTick()
        {
            if (!_dragging || _scrollDir == 0) return;
            _recycler.ScrollBy(0, _scrollDir * (int)Math.Max(4, 8 * _density));
            UpdateDrag();
        }

        /// <summary>
        /// 行内控件按下时会禁止 RecyclerView 拦截事件，导致拖动收不到后续事件。
        /// 这里清掉 RecyclerView 自己的标记，只让它的父级继续禁止拦截。
        /// </summary>
        private void AllowSelfIntercept()
        {
            var group = (Android.Views.ViewGroup)_recycler;
            group.RequestDisallowInterceptTouchEvent(false);
            if (group.Parent is Android.Views.ViewGroup parent)
                parent.RequestDisallowInterceptTouchEvent(true);
        }

        private void EndDrag()
        {
            _dragging = false;
            _scrollDir = 0;
            _scrollTimer.Stop();
            var group = (Android.Views.ViewGroup)_recycler;
            group.RequestDisallowInterceptTouchEvent(false);
            if (group.Parent is Android.Views.ViewGroup parent)
                parent.RequestDisallowInterceptTouchEvent(false);
            _anchor = -1;
            _baseKeys.Clear();
            _pane.TouchEnd();
        }
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
            row.SelectedRelease, row.SelectedEdition, row.SelectedAssetName, ShowInstallProgress);
        page.Completed += OnInstallCompleted;
        await Navigation.PushAsync(page);
    }

    /// <summary>安装页弹出后刷新已装标记，并提示结果。</summary>
    private async Task OnInstallCompleted(int count)
    {
        SetSelectMode(false);
        await RefreshInstalledStatusAsync();
        _pendingScrollIndex = _lastVisibleIndex;
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
        // 多包型时默认选中第一个：否则下拉框空白，而下载实际又按第一个包型走。
        _selectedEditionIndex = Editions.Count > 1 ? 0 : -1;
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

    private bool _selectMode;
    /// <summary>选择模式下隐藏「安装」按钮、显示复选框与「详情」。</summary>
    public bool SelectMode
    {
        get => _selectMode;
        set
        {
            if (_selectMode == value) return;
            _selectMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectMode)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallVisible)));
        }
    }

    public bool InstallVisible => !_selectMode;

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
