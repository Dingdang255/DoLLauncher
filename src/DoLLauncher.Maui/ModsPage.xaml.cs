using System.Collections.ObjectModel;
using System.ComponentModel;
using DoLLauncher.Maui.Controls;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class ModsPage : ContentPage
{
    private readonly VersionInfo _version;
    private readonly ObservableCollection<ModsRow> _rows = new();
    private readonly List<ModConflict> _conflicts = new();
    private readonly List<ProblemItem> _problems = new();

    /// <summary>单击与双击共存：单击延后执行，双击时取消。</summary>
    private CancellationTokenSource? _tapCts;
    private ModsRow? _lastTapRow;
    private long _lastTapAt;
    private bool _loading;
    private bool _importing;
    /// <summary>批量重建列表时置位，避免开关的程序化赋值触发保存。</summary>
    private bool _syncing;
    private bool _multiSelect;
    private bool _busy;
    private int _loadToken;
    /// <summary>批量操作栏是否已经播放过展开动画，避免每次刷新重复触发。</summary>
    private bool _bulkBarShown;

    public ModsPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        Title = version.DisplayName;
        ModList.ItemsSource = _rows;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AndroidKeyRouter.Handler = HandleHardwareKey;
        _ = ReloadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _tapCts?.Cancel();
        _tapCts?.Dispose();
        _tapCts = null;
        if (AndroidKeyRouter.Handler == HandleHardwareKey) AndroidKeyRouter.Handler = null;
    }

    /// <summary>外接键盘：Ctrl+A 切换全选 / 取消全选，Delete 删除选中（沿用现有二次确认）。</summary>
    private bool HandleHardwareKey(Android.Views.KeyEvent e)
    {
        if (_busy) return false;

        if (e.IsCtrlPressed && e.KeyCode == Android.Views.Keycode.A)
        {
            if (_rows.Count == 0) return false;
            if (!_multiSelect) EnterMultiSelect();
            ToggleSelectAll();
            return true;
        }

        if (e.KeyCode == Android.Views.Keycode.Del || e.KeyCode == Android.Views.Keycode.ForwardDel)
        {
            var targets = GetSelectedRows();
            if (targets.Count == 0) return false;
            _ = DeleteRowsAsync(targets);
            return true;
        }

        return false;
    }

    // ---------- 加载与刷新 ----------

    private async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        var token = ++_loadToken;
        ModSnapshot? snapshot = null;
        SetBusy(true);
        try
        {
            snapshot = await Task.Run(CollectSnapshot);
            if (token != _loadToken) return;
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            LogService.Error("读取模组列表失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取模组列表失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            Refresher.IsRefreshing = false;
            SetBusy(false);
        }

        if (snapshot != null)
            _ = CheckConflictsAsync(token, snapshot.Mods);
    }

    /// <summary>后台读取磁盘模组并汇总缺前置 / 重复 / 版本不匹配等问题。</summary>
    private ModSnapshot CollectSnapshot()
    {
        ModService.EnsureModList(_version.FullPath);
        var mods = ModService.Load(_version.FullPath);
        var cfg = AppConfig.Load();
        var threshold = ProblemService.NormalizeThreshold(cfg.General.VersionGapThreshold);

        var snapshot = new ModSnapshot { Mods = mods };
        var issues = snapshot.Issues;

        foreach (var missing in ProblemService.MissingDependencies(mods))
        {
            AddIssue(issues, missing.ModFile,
                missing.LocalVersion.Length == 0
                    ? $"缺少前置「{missing.DependencyName}」"
                    : missing.Disabled
                        ? $"前置未启用「{missing.DependencyName}」"
                        : $"前置版本不满足「{missing.DependencyName}」",
                red: false);
            snapshot.Problems.Add(new ProblemItem(missing.Title, missing.Detail));
        }

        foreach (var duplicate in ProblemService.Duplicates(mods))
        {
            snapshot.Problems.Add(new ProblemItem(duplicate.Title, duplicate.Detail));
            foreach (var member in duplicate.Members)
                AddIssue(issues, member.FileName, "重复模组", red: false);
        }

        var ignored = new HashSet<string>(
            cfg.Versions.FirstOrDefault(v => v.Folder == _version.Folder)?.IgnoredVersionIssues
                ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var mismatches = ProblemService.VersionMismatches(mods, _version.GameVersion, threshold);
        foreach (var issue in RuntimeIssueScanner.Scan(_version.FullPath, mods, _version.GameVersion))
        {
            // boot.json 已经报过的模组不再重复列一条。
            var duplicate = issue.ModFile.Length > 0
                ? mismatches.Any(m => string.Equals(m.ModFile, issue.ModFile,
                    StringComparison.OrdinalIgnoreCase))
                : mismatches.Any(m => m.ModFile.Length == 0 &&
                    string.Equals(m.ModName, issue.ModName, StringComparison.OrdinalIgnoreCase));
            if (!duplicate) mismatches.Add(issue);
        }

        foreach (var issue in mismatches)
        {
            issue.Ignored = ignored.Contains(issue.Key);
            if (issue.ModFile.Length == 0) continue;
            AddIssue(issues, issue.ModFile,
                issue.Runtime ? "运行日志：" + issue.RuntimeReason : "版本不匹配",
                issue.Severity == ProblemSeverity.Red);
            if (!issue.Ignored)
                snapshot.Problems.Add(new ProblemItem(issue.Title, issue.Detail));
        }

        foreach (var mod in mods.Where(m => !m.BootParsed))
            AddIssue(issues, mod.FileName, "boot.json 无法解析", red: false);

        return snapshot;
    }

    private static void AddIssue(Dictionary<string, IssueInfo> issues, string fileName,
        string text, bool red)
    {
        if (fileName.Length == 0) return;
        if (!issues.TryGetValue(fileName, out var info))
            issues[fileName] = info = new IssueInfo();
        info.Add(text, red);
    }

    private void ApplySnapshot(ModSnapshot snapshot)
    {
        var keepSelected = _rows.Where(r => r.IsSelected)
            .Select(r => r.Mod.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _problems.Clear();
        _problems.AddRange(snapshot.Problems);
        _conflicts.Clear();

        _syncing = true;
        try
        {
            _rows.Clear();
            foreach (var mod in snapshot.Mods)
            {
                var row = new ModsRow(mod)
                {
                    IsMultiSelect = _multiSelect,
                    IsSelected = _multiSelect && keepSelected.Contains(mod.FileName),
                };
                if (snapshot.Issues.TryGetValue(mod.FileName, out var issue))
                    row.SetIssue(issue.Text, issue.Red);
                _rows.Add(row);
            }
            RefreshMoveFlags();
        }
        finally
        {
            _syncing = false;
        }

        if (_multiSelect && !_rows.Any(r => r.IsSelected)) UpdateToolbar();
    }

    private async Task CheckConflictsAsync(int token, List<ModInfo> snapshotMods)
    {
        // 没有已启用模组时冲突检查没有意义，避免无谓的市场索引请求。
        if (snapshotMods.All(m => !m.Enabled)) return;
        try
        {
            var catalog = await ModMarketCache.GetAsync();
            if (catalog == null || catalog.Mods.Count == 0 || token != _loadToken) return;

            var found = await ModConflictService.CheckAsync(snapshotMods, catalog,
                msg => LogService.Append("[模组冲突] " + msg));
            if (token != _loadToken) return;

            var ignored = new HashSet<string>(
                AppConfig.Load().Versions.FirstOrDefault(v => v.Folder == _version.Folder)
                    ?.IgnoredConflicts ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (var conflict in found) conflict.Ignored = ignored.Contains(conflict.PairKey);

            MainThread.BeginInvokeOnMainThread(() => ApplyConflicts(found));
        }
        catch (Exception ex)
        {
            LogService.Error("检查模组冲突失败", ex);
        }
    }

    /// <summary>把冲突写成行内红色提示；停用的一方不在启用集合里，自然消失。</summary>
    private void ApplyConflicts(List<ModConflict> found)
    {
        _conflicts.Clear();
        _conflicts.AddRange(found.Where(c => !c.Ignored));

        foreach (var row in _rows)
        {
            var others = new List<string>();
            foreach (var conflict in _conflicts)
            {
                if (conflict.LeftFile.Length > 0 &&
                    string.Equals(conflict.LeftFile, row.Mod.FileName, StringComparison.OrdinalIgnoreCase))
                    others.Add(conflict.RightName);
                else if (conflict.RightFile.Length > 0 &&
                         string.Equals(conflict.RightFile, row.Mod.FileName, StringComparison.OrdinalIgnoreCase))
                    others.Add(conflict.LeftName);
            }

            row.SetConflict(others.Count == 0
                ? string.Empty
                : "可能与「" + string.Join("」「", others.Distinct(StringComparer.OrdinalIgnoreCase)) + "」冲突");
        }
        UpdateToolbar();
    }

    /// <summary>启用 / 停用后重新计算行内问题提示。</summary>
    private async Task RefreshIssuesAsync()
    {
        var mods = _rows.Select(r => r.Mod).ToList();
        var token = ++_loadToken;
        try
        {
            var snapshot = await Task.Run(CollectSnapshot);
            if (token != _loadToken) return;

            _problems.Clear();
            _problems.AddRange(snapshot.Problems);
            foreach (var row in _rows)
            {
                if (snapshot.Issues.TryGetValue(row.Mod.FileName, out var issue))
                    row.SetIssue(issue.Text, issue.Red);
                else
                    row.SetIssue(string.Empty, red: false);
            }
        }
        catch (Exception ex)
        {
            LogService.Error("重新计算模组问题失败", ex);
        }

        UpdateToolbar();
        _ = CheckConflictsAsync(token, mods);
    }

    // ---------- 工具条 ----------

    private void UpdateToolbar()
    {
        var selected = GetSelectedRows();
        var enabled = _rows.Count(r => r.Enabled);

        CountLabel.Text = $"共 {_rows.Count} 个 · 启用 {enabled} 个";
        BulkCountLabel.Text = $"已选 {selected.Count} 个";
        BulkBar.IsVisible = _multiSelect;
        AnimateBulkBar(_multiSelect);

        var canAct = !_busy && selected.Count > 0;
        BulkDetailButton.IsEnabled = canAct && selected.Count == 1;
        BulkRenameButton.IsEnabled = canAct && selected.Count == 1;
        BulkEnableButton.IsEnabled = canAct;
        BulkDisableButton.IsEnabled = canAct;
        BulkDeleteButton.IsEnabled = canAct;
        ImportButton.IsEnabled = !_busy && !_importing;
        CheckButton.IsEnabled = !_busy && !_loading;
        RefreshButton.IsEnabled = !_busy && !_loading;
        ProblemsButton.IsEnabled = !_busy;
        SelectButton.Text = _multiSelect ? "退出选择" : "选择";
    }

    private List<ModsRow> GetSelectedRows() => _rows.Where(r => r.IsSelected).ToList();

    /// <summary>进入选择模式时让批量栏轻微下落淡入，退出时直接隐藏。</summary>
    private void AnimateBulkBar(bool visible)
    {
        if (_bulkBarShown == visible) return;
        _bulkBarShown = visible;
        if (!visible) return;
        if (!UiMotion.Enabled)
        {
            BulkBar.Opacity = 1;
            BulkBar.TranslationY = 0;
            return;
        }

        BulkBar.Opacity = 0;
        BulkBar.TranslationY = -8;
        _ = Task.WhenAll(
            BulkBar.FadeToAsync(1, 140, Easing.CubicOut),
            BulkBar.TranslateToAsync(0, 0, 160, Easing.CubicOut));
    }

    private void OnRowChecked(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is not CheckBox { BindingContext: ModsRow row }) return;
        if (_syncing) return;

        _syncing = true;
        try
        {
            row.IsSelected = e.Value;
            if (e.Value && !_multiSelect) EnterMultiSelect(select: row, selectOnly: true);
        }
        finally
        {
            _syncing = false;
        }
        UpdateToolbar();
    }

    private void OnSelectClicked(object? sender, EventArgs e)
    {
        if (_multiSelect) ExitMultiSelect();
        else EnterMultiSelect();
        UpdateToolbar();
    }

    /// <summary>进入选择模式（工具栏按钮、Ctrl+A 与单击行共用）。</summary>
    private void EnterMultiSelect(ModsRow? select = null, bool selectOnly = false)
    {
        _multiSelect = true;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                row.IsMultiSelect = true;
                if (!selectOnly) row.IsSelected = false;
            }
            if (select != null) select.IsSelected = true;
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private void OnSelectAllClicked(object? sender, EventArgs e) => ToggleSelectAll();

    /// <summary>全选与取消全选共用：全部已选时清空，否则全选。</summary>
    private void ToggleSelectAll()
    {
        var all = _rows.Count > 0 && _rows.All(r => r.IsSelected);
        foreach (var row in _rows) row.IsSelected = !all;
        UpdateToolbar();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        LogService.Append("已重新读取磁盘模组。");
        await ReloadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e) => await ReloadAsync();

    // ---------- 行内操作 ----------

    private async void OnModToggled(object? sender, ToggledEventArgs e)
    {
        if (_syncing || _busy) return;
        if (sender is not Switch { BindingContext: ModsRow row }) return;
        if (row.Mod.Enabled == e.Value) return;

        row.SetEnabled(e.Value);
        await SaveAsync();
        await RefreshIssuesAsync();
    }

    private async void OnMoveUpClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ModsRow row }) return;
        await MoveRowAsync(row, -1);
    }

    private async void OnMoveDownClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: ModsRow row }) return;
        await MoveRowAsync(row, 1);
    }

    private async Task MoveRowAsync(ModsRow row, int delta)
    {
        if (_busy) return;
        var index = _rows.IndexOf(row);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rows.Count) return;

        _rows.Move(index, target);
        RefreshMoveFlags();
        await SaveAsync();
    }

    /// <summary>首项禁用上移、末项禁用下移，并同步 IsFirst / IsLast。</summary>
    private void RefreshMoveFlags()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            row.IsFirst = i == 0;
            row.IsLast = i == _rows.Count - 1;
            row.CanMoveUp = !row.IsFirst;
            row.CanMoveDown = !row.IsLast;
        }
    }

    /// <summary>单击 / 双击的判定窗口（毫秒）。</summary>
    private const int DoubleTapWindow = 300;

    /// <summary>单击行：进入选择模式并选中该项；再次单击取消 / 复选，选中数为 0 时退出。</summary>
    private async void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element { BindingContext: ModsRow row } || _busy) return;

        // 选择模式下没有「双击打开详情」的歧义，直接立刻切换勾选，避免等待判定窗口。
        if (_multiSelect)
        {
            row.IsSelected = !row.IsSelected;
            UpdateToolbar();
            return;
        }

        var now = Environment.TickCount64;
        var isDouble = ReferenceEquals(row, _lastTapRow) && now - _lastTapAt < DoubleTapWindow;
        _lastTapRow = isDouble ? null : row;
        _lastTapAt = now;

        _tapCts?.Cancel();
        _tapCts?.Dispose();
        _tapCts = null;

        if (isDouble)
        {
            // 双击只在非选择模式下打开详情；选择模式下双击视为无效。
            if (!_multiSelect) await OpenDetailAsync(row);
            return;
        }

        var cts = _tapCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(260, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_multiSelect) EnterMultiSelect(select: row, selectOnly: true);
        else row.IsSelected = !row.IsSelected;

        UpdateToolbar();
    }

    private async Task OpenDetailAsync(ModsRow row)
    {
        await Navigation.PushAsync(new ModDetailPage(_version, row.Mod));
    }

    // ---------- 单条操作 ----------

    private async void OnDetailClicked(object? sender, EventArgs e)
    {
        if (GetSelectedRows() is not { Count: 1 } rows) return;
        await OpenDetailAsync(rows[0]);
    }

    private async void OnRenameClicked(object? sender, EventArgs e)
    {
        if (GetSelectedRows() is not { Count: 1 } rows) return;
        await RenameAsync(rows[0]);
    }

    private async Task RenameAsync(ModsRow row)
    {
        if (_busy) return;
        var initial = Path.GetFileNameWithoutExtension(row.Mod.FileName);
        var input = await DisplayPromptAsync("重命名模组",
            "新的文件名（不含 .zip 也可以）：", "确定", "取消", "文件名",
            maxLength: 120, keyboard: Keyboard.Text, initialValue: initial);
        if (string.IsNullOrWhiteSpace(input)) return;
        if (string.Equals(input.Trim(), initial, StringComparison.Ordinal)) return;

        SetBusy(true);
        try
        {
            await Task.Run(() => ModService.Rename(_version.FullPath, row.Mod.FileName, input.Trim()));
            LogService.Append($"已重命名：{row.Mod.FileName} → {input.Trim()}");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("重命名模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("重命名失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnEnableClicked(object? sender, EventArgs e) =>
        await SetEnabledAsync(GetSelectedRows(), true);

    private async void OnDisableClicked(object? sender, EventArgs e) =>
        await SetEnabledAsync(GetSelectedRows(), false);

    private async Task SetEnabledAsync(List<ModsRow> targets, bool enabled)
    {
        if (_busy || targets.Count == 0) return;
        var changed = 0;
        foreach (var row in targets)
        {
            if (row.Enabled == enabled) continue;
            row.SetEnabled(enabled);
            changed++;
        }
        if (changed == 0) return;

        await SaveAsync();
        await RefreshIssuesAsync();
        if (_multiSelect) ExitMultiSelect();
        UpdateToolbar();
        LogService.Append($"已将 {changed} 个模组{(enabled ? "启用" : "停用")}。");
    }

    private async void OnDeleteClicked(object? sender, EventArgs e) =>
        await DeleteRowsAsync(GetSelectedRows());

    private async Task DeleteRowsAsync(List<ModsRow> targets)
    {
        if (_busy || targets.Count == 0) return;

        var fileList = string.Join("\n", targets.Select(m => "· " + m.Mod.FileName));
        var message = targets.Count == 1
            ? $"确定删除模组「{targets[0].Mod.Name}」吗？\n\n文件：{targets[0].Mod.FileName}\n\n" +
              "只删除磁盘上的模组包，不影响浏览器里的存档。"
            : $"确定删除选中的 {targets.Count} 个模组吗？\n\n{fileList}\n\n" +
              "只删除磁盘上的模组包，不影响浏览器里的存档。";
        if (!await PlatformServices.Dialogs.ConfirmAsync(message, "删除模组", "删除", danger: true))
            return;

        SetBusy(true);
        try
        {
            var files = targets.Select(m => m.Mod.FileName).ToList();
            await Task.Run(() =>
            {
                foreach (var file in files) ModService.Delete(_version.FullPath, file);
            });
            LogService.Append($"已删除 {files.Count} 个模组：{string.Join("、", files)}");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("删除模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("删除失败：" + ex.Message);
            await ReloadAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExitMultiSelect()
    {
        _multiSelect = false;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                row.IsSelected = false;
                row.IsMultiSelect = false;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    // ---------- 检查与问题 ----------

    private async void OnCheckClicked(object? sender, EventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var lines = await Task.Run(() => ModService.Check(_version));
            await Task.Run(() => ModService.Apply(_version.FullPath));

            var errors = lines.Count(l => l.StartsWith("[错误]", StringComparison.Ordinal));
            var warns = lines.Count(l => l.StartsWith("[警告]", StringComparison.Ordinal));
            var summary = errors > 0
                ? $"检查完成：已重新生成 modList.json，存在 {errors + warns} 个问题（错误 {errors}，警告 {warns}）。"
                : $"检查完成：已重新生成 modList.json，未发现阻断性问题（{warns} 个提醒）。";

            var text = summary + "\n\n" + string.Join("\n", lines);
            if (text.Length > 4000) text = text[..4000] + "\n…（内容过长，完整结果见启动器日志）";
            LogService.Append("[模组检查] " + summary);

            if (errors > 0) await PlatformServices.Dialogs.WarnAsync(text, "模组检查");
            else await PlatformServices.Dialogs.InfoAsync(text, "模组检查");
        }
        catch (Exception ex)
        {
            LogService.Error("模组完整性检查失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("检查失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadAsync();
    }

    private async void OnProblemsClicked(object? sender, EventArgs e)
    {
        var items = new List<ProblemItem>(_problems);
        items.AddRange(_conflicts.Select(c => new ProblemItem(c.Title, c.Detail)));
        if (items.Count == 0)
        {
            await PlatformServices.Dialogs.InfoAsync("暂未检测到问题。", "模组问题");
            return;
        }

        var stack = new VerticalStackLayout { Padding = new Thickness(16, 12, 16, 24), Spacing = 10 };
        foreach (var item in items)
        {
            var block = new VerticalStackLayout { Spacing = 3 };
            block.Children.Add(new Label
            {
                Text = item.Title,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.CharacterWrap,
            });
            block.Children.Add(new Label
            {
                Text = item.Detail,
                FontSize = 12,
                LineBreakMode = LineBreakMode.CharacterWrap,
            });

            var border = new Border
            {
                Padding = 12,
                StrokeThickness = 0,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Content = block,
            };
            border.SetAppThemeColor(Border.BackgroundColorProperty,
                Color.FromArgb("#F2F2F2"), Color.FromArgb("#2A2A2A"));
            stack.Children.Add(border);
        }

        await Navigation.PushAsync(new ContentPage
        {
            Title = "模组问题",
            Content = new ScrollView { Content = stack },
        });
    }

    // ---------- 导入 ----------

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        if (_importing || _busy) return;

        var temps = new List<string>();
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = "选择模组包" });
            var picked = files?.OfType<FileResult>().ToList() ?? new List<FileResult>();
            if (picked.Count == 0) return;

            _importing = true;
            SetBusy(true);

            var dir = Path.Combine(FileSystem.CacheDirectory, "import-mods");
            Directory.CreateDirectory(dir);
            foreach (var file in picked)
            {
                var target = UniquePath(dir, string.IsNullOrWhiteSpace(file.FileName) ? "mod.zip" : file.FileName);
                await using (var source = await file.OpenReadAsync())
                await using (var dest = File.Create(target))
                {
                    await source.CopyToAsync(dest);
                }
                temps.Add(target);
            }

            var count = await Task.Run(() => ModService.ImportFiles(_version.FullPath, temps,
                msg => LogService.Append("[模组导入] " + msg)));

            await ReloadAsync();

            var message = $"已导入 {count} 个模组。";
            if (count < picked.Count) message += "\n有文件被跳过，详见日志。";
            await PlatformServices.Dialogs.InfoAsync(message, "导入模组");
        }
        catch (Exception ex)
        {
            LogService.Error("导入模组失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导入模组失败：" + ex.Message);
        }
        finally
        {
            _importing = false;
            SetBusy(false);
            foreach (var path in temps)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }

    // ---------- 保存与忙碌状态 ----------

    private async Task SaveAsync()
    {
        var mods = _rows.Select(r => r.Mod).ToList();
        try
        {
            await Task.Run(() => ModService.Save(_version.FullPath, mods));
            UpdateToolbar();
        }
        catch (Exception ex)
        {
            LogService.Error("保存模组状态失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("保存模组状态失败：" + ex.Message);
            await ReloadAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyOverlay.IsVisible = busy;
        Refresher.IsEnabled = !busy;
        UpdateToolbar();
    }

    /// <summary>SAF 复制到缓存的同名文件改为加序号，避免覆盖。</summary>
    private static string UniquePath(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; i < 999; i++)
        {
            target = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(target)) return target;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    // ---------- 快照类型 ----------

    private sealed class ModSnapshot
    {
        public List<ModInfo> Mods { get; init; } = new();
        public Dictionary<string, IssueInfo> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ProblemItem> Problems { get; } = new();
    }

    private sealed class IssueInfo
    {
        private readonly List<string> _texts = new();
        public bool Red { get; private set; }
        public string Text => string.Join("；", _texts);

        public void Add(string text, bool red)
        {
            if (!_texts.Contains(text)) _texts.Add(text);
            Red |= red;
        }
    }

    private sealed record ProblemItem(string Title, string Detail);
}

/// <summary>模组列表行：包装 ModInfo，并提供状态、问题提示与多选状态。</summary>
public sealed class ModsRow : INotifyPropertyChanged
{
    private bool _canMoveUp;
    private bool _canMoveDown;
    private bool _isSelected;
    private bool _isMultiSelect;
    private string _issueText = string.Empty;
    private bool _issueRed;
    private string _conflictText = string.Empty;

    public ModsRow(ModInfo mod) => Mod = mod;

    public ModInfo Mod { get; }
    public string Title => Mod.Title;
    public string FileName => Mod.FileName;
    public bool Enabled => Mod.Enabled;
    public string StateText => Mod.Enabled ? "已启用" : "已停用";
    public Color StateColor => Mod.Enabled
        ? Color.FromArgb("#2E7D32")
        : Color.FromArgb("#808080");

    public bool IsFirst { get; set; }
    public bool IsLast { get; set; }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set
        {
            if (_canMoveUp == value) return;
            _canMoveUp = value;
            OnPropertyChanged(nameof(CanMoveUp));
        }
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set
        {
            if (_canMoveDown == value) return;
            _canMoveDown = value;
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public bool IsMultiSelect
    {
        get => _isMultiSelect;
        set
        {
            if (_isMultiSelect == value) return;
            _isMultiSelect = value;
            OnPropertyChanged(nameof(IsMultiSelect));
            OnPropertyChanged(nameof(IsNormalMode));
        }
    }

    public bool IsNormalMode => !_isMultiSelect;

    public string IssueText => _issueText;
    public bool HasIssue => _issueText.Length > 0;
    public Color IssueColor => _issueRed
        ? Color.FromArgb("#C62828")
        : Color.FromArgb("#E65100");

    public string ConflictText => _conflictText;
    public bool HasConflict => _conflictText.Length > 0;

    /// <summary>启用 / 停用后刷新开关与状态文字。</summary>
    public void SetEnabled(bool value)
    {
        if (Mod.Enabled == value) return;
        Mod.Enabled = value;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateColor));
    }

    public void SetIssue(string text, bool red)
    {
        if (_issueText == text && _issueRed == red) return;
        _issueText = text;
        _issueRed = red;
        OnPropertyChanged(nameof(IssueText));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(IssueColor));
    }

    public void SetConflict(string text)
    {
        if (_conflictText == text) return;
        _conflictText = text;
        OnPropertyChanged(nameof(ConflictText));
        OnPropertyChanged(nameof(HasConflict));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>模组市场索引的进程级缓存：冲突检查与详情元数据补全共用一次拉取结果。</summary>
internal static class ModMarketCache
{
    private static readonly object Sync = new();
    private static Task<MarketCatalog?>? _task;

    public static Task<MarketCatalog?> GetAsync()
    {
        lock (Sync) return _task ??= LoadAsync();
    }

    private static async Task<MarketCatalog?> LoadAsync()
    {
        try
        {
            return await DownloadService.FetchMarketAsync(msg => LogService.Append("[市场] " + msg));
        }
        catch (Exception ex)
        {
            LogService.Error("加载模组市场索引失败", ex);
            return null;
        }
    }
}
