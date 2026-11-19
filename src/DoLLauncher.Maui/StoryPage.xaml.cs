using System.Globalization;
using System.Text;
using System.Text.Json;
using DoLLauncher.Core.Story;
using DoLLauncher.Maui.Platform;
using DoLLauncher.Maui.Services;
using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;
using IOPath = System.IO.Path;

namespace DoLLauncher.Maui;

public partial class StoryPage : ContentView
{
    /// <summary>每层缩进与最大缩进层数，避免深层节点把内容挤出屏幕。</summary>
    private const double IndentStep = 12;
    private const int MaxIndentDepth = 8;

    private VersionInfo _version;
    private List<VersionInfo> _versions = new();
    private List<SlotOption> _slotOptions = new();
    private StoryArchive? _archive;
    private StoryBranchTree? _tree;
    private StoryBranchNode? _selectedNode;
    private Border? _selectedRow;
    private string _latestSlotId = "";
    private string _slotPassage = "";
    private bool _hasMod;
    private bool _successOnly;
    private bool _initialized;
    private bool _loading;
    private bool _simulating;
    private bool _reloadOnAppear;
    private bool _suppressDisappearCancel;
    private bool _suppressVersionEvent;
    private bool _suppressSlotEvent;
    private int _loadSerial;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _simCts;

    public StoryPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        HeaderLabel.Text = version.DisplayName;
        VersionPicker.ItemDisplayBinding = new Binding(nameof(VersionInfo.DisplayName));
        SlotPicker.ItemDisplayBinding = new Binding(nameof(SlotOption.DisplayName));
    }

    private async void OnCopyJsonClicked(object? sender, EventArgs e) => await CopyJsonAsync();

    /// <summary>主界面切换到本面板时调用。</summary>
    public void Activate()
    {
        // 从模拟打开的游戏页返回：服务器已被游戏页停掉，模拟无法继续。
        if (_simulating)
        {
            CancelSimulation("已停止模拟：游戏页面已关闭。");
            _reloadOnAppear = true;
        }

        if (!_initialized)
        {
            _initialized = true;
            _ = InitializeAsync();
        }
        else if (_reloadOnAppear)
        {
            _reloadOnAppear = false;
            _ = LoadDataAsync();
        }
    }

    /// <summary>切走或离开主界面时调用。</summary>
    public void Deactivate()
    {
        // 模拟时打开的游戏页会覆盖本页，此时不能取消轮询。
        if (_suppressDisappearCancel)
        {
            _suppressDisappearCancel = false;
            return;
        }

        if (_simulating) CancelSimulation("已停止模拟。");
        _reloadOnAppear = true;
        _loadCts?.Cancel();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("初始化剧情页失败", ex);
            SetStatus("初始化失败：" + ex.Message);
        }
    }

    private async void OnRefreshClicked(object? sender, EventArgs e)
    {
        if (_loading || _simulating) return;
        try
        {
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("刷新剧情数据失败", ex);
            SetStatus("刷新失败：" + ex.Message);
        }
    }

    /// <summary>重新扫描版本并读取结果；全程禁用交互。</summary>
    private async Task ReloadAllAsync()
    {
        _loading = true;
        SetBusy(true);
        try
        {
            await LoadVersionsAsync();
            await LoadDataAsync();
        }
        finally
        {
            _loading = false;
            SetBusy(false);
        }
    }

    private async void OnVersionChanged(object? sender, EventArgs e)
    {
        if (_suppressVersionEvent) return;
        if (VersionPicker.SelectedItem is not VersionInfo selected) return;

        CancelSimulation("已停止模拟：已切换版本。");
        SetVersion(selected);
        _tree = null;
        _archive = null;
        _slotPassage = "";
        UpdateSlotPassageLabel();

        await LoadDataAsync();
    }

    private async void OnSlotChanged(object? sender, EventArgs e)
    {
        if (_suppressSlotEvent) return;

        CancelSimulation("已停止模拟：已切换存档槽。");
        var token = RenewLoadToken();
        var serial = ++_loadSerial;
        _loading = true;
        SetBusy(true);
        try
        {
            await UpdateSlotPassageAsync(serial, token);
        }
        catch (Exception ex)
        {
            LogService.Error("读取所选存档起点失败", ex);
        }
        finally
        {
            if (serial == _loadSerial)
            {
                _loading = false;
                SetBusy(false);
            }
        }
    }

    private void OnSuccessOnlyToggled(object? sender, ToggledEventArgs e)
    {
        _successOnly = e.Value;
        RenderNodes();
    }

    // ---------- 加载 ----------

    private async Task LoadVersionsAsync()
    {
        List<VersionInfo> scanned;
        try
        {
            scanned = await Task.Run(VersionService.Scan);
        }
        catch (Exception ex)
        {
            LogService.Error("扫描版本失败", ex);
            scanned = new List<VersionInfo>();
        }

        // 与 WPF 剧情页一致：只保留磁盘版本。
        var versions = scanned
            .Where(v => !v.IsFileMode && !string.IsNullOrWhiteSpace(v.FullPath))
            .ToList();
        // 当前版本可能因目录缺 HTML 没被扫描到，仍保留在列表里。
        if (!string.IsNullOrWhiteSpace(_version.FullPath) &&
            !versions.Any(v => v.Folder == _version.Folder))
        {
            versions.Insert(0, _version);
        }

        _versions = versions;

        _suppressVersionEvent = true;
        try
        {
            VersionPicker.ItemsSource = _versions;
            var target = _versions.FirstOrDefault(v => v.Folder == _version.Folder)
                         ?? _versions.FirstOrDefault();
            VersionPicker.SelectedItem = target;
            if (target != null) SetVersion(target);
        }
        finally
        {
            _suppressVersionEvent = false;
        }

        if (_versions.Count == 0) SetStatus("没有可用的版本目录。");
        UpdateSimButtons();
    }

    private async Task LoadDataAsync()
    {
        var version = _version;
        var token = RenewLoadToken();
        var serial = ++_loadSerial;
        _loading = true;
        SetBusy(true);
        SetStatus("正在读取模拟结果…");

        try
        {
            var snapshot = await Task.Run(() => LoadSnapshot(version, token), token);
            if (serial != _loadSerial || token.IsCancellationRequested) return;

            ApplyModState(snapshot.HasMod);
            ApplySlots(snapshot.Archive);

            if (snapshot.Tree == null)
            {
                ShowEmpty(snapshot.TreeError);
                SetStatus(snapshot.TreeError ?? "没有找到模拟结果。");
            }
            else
            {
                RenderTree(snapshot.Tree);
                SetStatus($"读取成功：{snapshot.Tree.Nodes.Count} 个节点。");
            }

            await UpdateSlotPassageAsync(serial, token);
        }
        catch (OperationCanceledException)
        {
            // 切换版本 / 存档槽时主动取消，忽略。
        }
        catch (Exception ex)
        {
            LogService.Error("读取剧情分支失败", ex);
            ShowEmpty("读取模拟结果失败：" + ex.Message);
            SetStatus("读取模拟结果失败：" + ex.Message);
        }
        finally
        {
            if (serial == _loadSerial)
            {
                _loading = false;
                SetBusy(false);
            }
        }
    }

    private static StoryDataSnapshot LoadSnapshot(VersionInfo version, CancellationToken ct)
    {
        var hasMod = HasStoryBranchMod(version.FullPath);

        StoryArchive? archive = null;
        var source = SaveMirrorReader.FindSource(version.FullPath);
        if (source != null)
            archive = SaveMirrorReader.LoadArchive(source, ct, out _);

        var tree = StoryBranchReader.Load(version.FullPath, out var treeError);
        return new StoryDataSnapshot(hasMod, archive, tree, treeError);
    }

    /// <summary>与 WPF 相同：只看启用的模组，名称键包含 storybranchsim。</summary>
    private static bool HasStoryBranchMod(string versionDir)
    {
        try
        {
            return ModService.Load(versionDir).Any(mod =>
                mod.Enabled && ModService.NameKeys(mod)
                    .Any(key => key.Contains("storybranchsim", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            LogService.Error("检查剧情模拟模组失败", ex);
            return false;
        }
    }

    private async Task UpdateSlotPassageAsync(int serial, CancellationToken token)
    {
        var archive = _archive;
        var slotId = ResolveSelectedSlotId();
        if (archive == null || string.IsNullOrEmpty(slotId))
        {
            _slotPassage = "";
            UpdateSlotPassageLabel();
            if (_tree != null) RenderSummary(_tree);
            return;
        }

        try
        {
            var passage = await Task.Run(() => ReadSlotPassage(archive, slotId, token), token);
            if (serial != _loadSerial || token.IsCancellationRequested) return;
            _slotPassage = passage;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LogService.Error("读取所选存档起点失败", ex);
            _slotPassage = "";
        }

        UpdateSlotPassageLabel();
        if (_tree != null) RenderSummary(_tree);
    }

    /// <summary>与剧情页一致：取所选存档路径的最后一个段落作为当前起点。</summary>
    private static string ReadSlotPassage(StoryArchive archive, string slotId, CancellationToken ct)
    {
        var path = SaveMirrorReader.LoadSlot(archive, slotId, ct, out _);
        if (path == null || path.Steps.Count == 0) return "";
        return path.Steps[^1].Title;
    }

    private CancellationToken RenewLoadToken()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        return _loadCts.Token;
    }

    // ---------- 数据应用 ----------

    private void SetVersion(VersionInfo version)
    {
        _version = version;
        HeaderLabel.Text = version.DisplayName;
    }

    private void ApplyModState(bool hasMod)
    {
        _hasMod = hasMod;
        SimCard.IsVisible = hasMod;
        ModMissingCard.IsVisible = !hasMod;
        if (!hasMod) CancelSimulation("未检测到「剧情分支模拟」模组。");
        UpdateSimButtons();
    }

    private void ApplySlots(StoryArchive? archive)
    {
        _archive = archive;

        StorySlotInfo? latest = null;
        if (archive != null)
        {
            foreach (var slot in archive.Slots)
            {
                if (latest == null || slot.SavedAt > latest.SavedAt) latest = slot;
            }
        }

        _latestSlotId = latest?.SlotId ?? "";
        var options = new List<SlotOption>
        {
            new("", latest == null ? "默认（当前状态）" : "最新存档（默认）", true, _latestSlotId),
        };
        if (archive != null)
        {
            foreach (var slot in archive.Slots)
                options.Add(new SlotOption(slot.SlotId, FormatSlotName(slot), false, slot.SlotId));
        }

        _slotOptions = options;
        _suppressSlotEvent = true;
        try
        {
            SlotPicker.ItemsSource = _slotOptions;
            SlotPicker.SelectedIndex = 0;
        }
        finally
        {
            _suppressSlotEvent = false;
        }
    }

    private static string FormatSlotName(StorySlotInfo slot)
    {
        var name = string.IsNullOrWhiteSpace(slot.SaveName) ? "存档 " + slot.SlotId : slot.SaveName;
        if (slot.SavedAt <= 0) return name;

        try
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(slot.SavedAt).LocalDateTime
                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return name + " · " + time;
        }
        catch (ArgumentOutOfRangeException)
        {
            return name + " · " + slot.SavedAt.ToString(CultureInfo.InvariantCulture);
        }
    }

    private string? ResolveSelectedSlotId()
    {
        if (SlotPicker.SelectedItem is not SlotOption option) return null;
        return option.IsFallback ? option.FallbackSlotId : option.SlotId;
    }

    private void UpdateSlotPassageLabel()
    {
        SlotPassageLabel.Text = string.IsNullOrEmpty(_slotPassage)
            ? ""
            : "起点：" + _slotPassage;
    }

    // ---------- 渲染 ----------

    private void ShowEmpty(string? error)
    {
        _tree = null;
        _selectedNode = null;
        _selectedRow = null;

        SummaryCard.IsVisible = false;
        WarningCard.IsVisible = false;
        NodeCard.IsVisible = false;
        DetailCard.IsVisible = false;
        NodeList.Children.Clear();
        DetailList.Children.Clear();

        EmptyState.IsVisible = true;
        ErrorLabel.Text = error ?? "";
        ErrorLabel.IsVisible = !string.IsNullOrWhiteSpace(error);
        UpdateSimButtons();
    }

    private void RenderTree(StoryBranchTree tree)
    {
        _tree = tree;
        EmptyState.IsVisible = false;
        ErrorLabel.IsVisible = false;
        SummaryCard.IsVisible = true;
        NodeCard.IsVisible = true;
        _selectedNode = null;
        _selectedRow = null;
        DetailCard.IsVisible = false;
        DetailList.Children.Clear();

        RenderSummary(tree);
        RenderNodes();
    }

    private void RenderSummary(StoryBranchTree tree)
    {
        SummaryList.Children.Clear();
        var root = tree.RootPassage.Length > 0
            ? tree.RootPassage
            : (_slotPassage.Length > 0 ? _slotPassage : "未知");
        AddLine(SummaryList, "起点段落", root);
        if (tree.SaveSlot.Length > 0) AddLine(SummaryList, "存档槽", tree.SaveSlot);
        AddLine(SummaryList, "游戏版本", tree.GameVersion.Length > 0 ? tree.GameVersion : "未知");
        AddLine(SummaryList, "生成时间", FormatGeneratedAt(tree));
        AddLine(SummaryList, "深度 / 预算", $"{tree.Depth} 层 / {tree.Budget} 个节点");
        AddLine(SummaryList, "是否截断", tree.Truncated ? "是（节点预算已用完）" : "否");
        AddLine(SummaryList, "全局种子",
            tree.GlobalSeed.Raw.Length > 0 ? tree.GlobalSeed.Raw : "—");
        AddLine(SummaryList, "节点数", tree.Nodes.Count.ToString(CultureInfo.InvariantCulture) + " 个");

        var (success, failed, merged, skipped) = tree.CountByStatus();
        AddLine(SummaryList, "状态计数",
            $"成功 {success} / 失败 {failed} / 跳过 {skipped} / 合并 {merged}");

        var warnings = new List<string>();
        if (tree.VersionMismatch && tree.VersionWarning.Length > 0) warnings.Add(tree.VersionWarning);
        if (!tree.StateIntact) warnings.Add("模拟结束后玩家状态与开始前不一致，结果可能受副作用影响。");
        WarningCard.IsVisible = warnings.Count > 0;
        WarningLabel.Text = string.Join("\n", warnings);
    }

    /// <summary>生成时间按本地时区显示；解析失败时退回文件写入时间。</summary>
    private static string FormatGeneratedAt(StoryBranchTree tree)
    {
        if (DateTimeOffset.TryParse(tree.GeneratedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var when))
        {
            return when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }

        return tree.ModifiedAt == default
            ? "未知"
            : tree.ModifiedAt.ToString("yyyy-MM-dd HH:mm");
    }

    private void RenderNodes()
    {
        NodeList.Children.Clear();
        _selectedRow = null;

        if (_tree == null)
        {
            UpdateSimButtons();
            return;
        }

        var visible = new List<StoryBranchNode>();
        foreach (var node in _tree.Nodes)
        {
            // WPF 的「仅成功」实际隐藏失败与跳过，保留起点与合并节点。
            if (_successOnly &&
                (node.Status == StoryBranchStatus.Failed || node.Status == StoryBranchStatus.Skipped))
            {
                continue;
            }

            visible.Add(node);
            NodeList.Children.Add(MakeNodeRow(node));
        }

        if (_selectedNode != null && !visible.Contains(_selectedNode))
        {
            _selectedNode = null;
            DetailCard.IsVisible = false;
            DetailList.Children.Clear();
        }

        UpdateSimButtons();
    }

    private View MakeNodeRow(StoryBranchNode node)
    {
        var (_, statusColor) = StatusView(node.Status);

        var title = new Label
        {
            Text = node.DisplayTitle,
            FontSize = 14,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center,
        };
        title.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));

        // 用状态色点代替文字前缀，读起来更紧凑。
        var dot = new Label
        {
            Text = "●",
            FontSize = 12,
            TextColor = statusColor,
            VerticalOptions = LayoutOptions.Center,
        };

        var head = new HorizontalStackLayout { Spacing = 6 };
        head.Children.Add(dot);
        head.Children.Add(title);

        var content = new VerticalStackLayout { Spacing = 2 };
        content.Children.Add(head);

        var passages = PassageText(node);
        if (passages.Length > 0)
        {
            var hint = new Label
            {
                Text = passages,
                FontSize = 11,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            hint.SetAppThemeColor(Label.TextColorProperty,
                Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));
            content.Children.Add(hint);
        }

        var indent = Math.Min(Math.Max(node.Depth, 0), MaxIndentDepth) * IndentStep;
        var row = new Border
        {
            Padding = new Thickness(10, 8),
            Margin = new Thickness(indent, 0, 0, 6),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = content,
        };
        ApplyRowBackground(row, false);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => ShowDetail(node, row);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    /// <summary>「出发 → 实际到达」，两端都为空时不显示这一行。</summary>
    private static string PassageText(StoryBranchNode node)
    {
        if (node.FromPassage.Length == 0) return node.ResultPassage;
        if (node.ResultPassage.Length == 0) return node.FromPassage;
        return node.FromPassage + " → " + node.ResultPassage;
    }

    /// <summary>点击节点行展开详情，再点同一行收起；点其它行替换内容。</summary>
    private void ShowDetail(StoryBranchNode node, Border row)
    {
        if (ReferenceEquals(_selectedNode, node))
        {
            _selectedNode = null;
            _selectedRow = null;
            ApplyRowBackground(row, false);
            DetailCard.IsVisible = false;
            UpdateSimButtons();
            return;
        }

        if (_selectedRow != null) ApplyRowBackground(_selectedRow, false);
        _selectedNode = node;
        _selectedRow = row;
        ApplyRowBackground(row, true);

        DetailTitle.Text = node.DisplayTitle;
        DetailList.Children.Clear();
        AddLine(DetailList, "节点",
            node.Id + (node.Depth > 0 ? $"（第 {node.Depth} 层）" : "（起点）"));
        AddLine(DetailList, "状态", StatusView(node.Status).Name);
        AddLine(DetailList, "选项", node.DisplayTitle);
        AddLine(DetailList, "段落", $"{node.FromPassage} → {node.ResultPassage}");
        if (node.ToPassage.Length > 0 && node.ToPassage != node.ResultPassage)
            AddLine(DetailList, "跳转链", $"{node.ToPassage} → {node.ResultPassage}");

        var seed = node.Seed.Raw.Length > 0 ? node.Seed.Raw : "—";
        seed += node.Seed.IsNodeOverride ? "（节点覆盖）" : "（全局）";
        seed += node.RandomCalls > 0 ? $" · 随机 {node.RandomCalls} 次" : " · 未使用随机";
        AddLine(DetailList, "种子", seed);
        AddLine(DetailList, "耗时", $"{node.DurationMs} ms");

        if (node.CutByDepth) AddLine(DetailList, "截断", "已达到深度上限，未继续展开");
        if (node.MergedInto.Length > 0)
            AddLine(DetailList, "合并", "该时间线已收束至节点 " + node.MergedInto);

        if (node.Warnings.Count > 0)
        {
            AddSection(DetailList, "副作用与提示");
            foreach (var warning in node.Warnings) AddPlain(DetailList, "· " + warning);
        }

        AddSection(DetailList, "错误全文");
        if (node.Error.Length > 0)
            AddLine(DetailList, "错误", node.Error);
        else
            AddPlain(DetailList, "无错误。");

        var diffTitle = $"变量差异（{node.Diff.Count} 项";
        if (node.DiffOmitted > 0) diffTitle += $"，另有 {node.DiffOmitted} 项未列出";
        diffTitle += "）";
        AddSection(DetailList, diffTitle);

        if (node.Diff.Count == 0)
        {
            AddPlain(DetailList, "无变量变化。");
        }
        else
        {
            foreach (var change in node.Diff) AddDiffRow(DetailList, change);
        }

        if (node.DiffOmitted > 0)
            AddPlain(DetailList, $"…另有 {node.DiffOmitted} 项差异未列出");

        DetailCard.IsVisible = true;
        UpdateSimButtons();
    }

    private static void ApplyRowBackground(Border row, bool selected)
    {
        if (selected)
        {
            row.SetAppThemeColor(VisualElement.BackgroundColorProperty,
                Color.FromArgb("#DCEBFF"), Color.FromArgb("#2E4258"));
        }
        else
        {
            row.SetAppThemeColor(VisualElement.BackgroundColorProperty,
                Color.FromArgb("#F7F7F7"), Color.FromArgb("#242424"));
        }
    }

    private static (string Name, Color Color) StatusView(StoryBranchStatus status) => status switch
    {
        StoryBranchStatus.Root => ("起点", Color.FromArgb("#808080")),
        StoryBranchStatus.Success => ("成功", Color.FromArgb("#2E9E5B")),
        StoryBranchStatus.Failed => ("失败", Color.FromArgb("#D9534F")),
        StoryBranchStatus.Skipped => ("跳过", Color.FromArgb("#808080")),
        StoryBranchStatus.Merged => ("合并", Color.FromArgb("#E08A00")),
        _ => ("未知", Color.FromArgb("#808080")),
    };

    private static void AddSection(VerticalStackLayout parent, string title)
    {
        var label = new Label
        {
            Text = title,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 6, 0, 0),
        };
        label.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#2F6FDB"), Color.FromArgb("#7FB4FF"));
        parent.Children.Add(label);
    }

    private static void AddPlain(VerticalStackLayout parent, string text)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        label.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));
        parent.Children.Add(label);
    }

    private static void AddDiffRow(VerticalStackLayout parent, StoryBranchVarChange change)
    {
        var (kindName, kindColor) = change.Kind switch
        {
            "added" => ("新增", Color.FromArgb("#2E9E5B")),
            "removed" => ("删除", Color.FromArgb("#D9534F")),
            _ => ("变更", Color.FromArgb("#E08A00")),
        };

        var head = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 6,
        };

        var key = new Label
        {
            Text = change.Key,
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        key.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#2F6FDB"), Color.FromArgb("#7FB4FF"));
        head.Add(key, 0);

        var kind = new Label { Text = kindName, FontSize = 10, TextColor = kindColor };
        head.Add(kind, 1);
        parent.Children.Add(head);

        var value = new Label
        {
            Text = (change.Before ?? "∅") + " → " + (change.After ?? "∅"),
            FontSize = 11,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        value.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
        parent.Children.Add(value);
    }

    /// <summary>「名称：值」一行，值可换行。</summary>
    private static void AddLine(VerticalStackLayout parent, string name, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(78), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 6,
        };

        var nameLabel = new Label { Text = name, FontSize = 12 };
        nameLabel.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));

        var valueLabel = new Label
        {
            Text = value,
            FontSize = 12,
            LineBreakMode = LineBreakMode.CharacterWrap,
        };
        valueLabel.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));

        grid.Add(nameLabel, 0);
        grid.Add(valueLabel, 1);
        parent.Children.Add(grid);
    }

    // ---------- 剧情模拟 ----------

    private async void OnStartSimClicked(object? sender, EventArgs e)
    {
        await StartSimulationAsync(null);
    }

    private async void OnContinueSimClicked(object? sender, EventArgs e)
    {
        var node = _selectedNode;
        if (node == null)
        {
            await PlatformServices.Dialogs.WarnAsync("请先在分支列表中选择一个末端节点。");
            return;
        }

        if (node.Children.Count > 0 ||
            (node.Status != StoryBranchStatus.Success && node.Status != StoryBranchStatus.Root))
        {
            await PlatformServices.Dialogs.WarnAsync(
                "只能从还没有分支的末端节点继续，请选分支列表最外层的成功节点。");
            return;
        }

        await StartSimulationAsync(node);
    }

    private async Task StartSimulationAsync(StoryBranchNode? fromNode)
    {
        if (_simulating) return;

        var version = _version;
        if (version == null || string.IsNullOrWhiteSpace(version.FullPath)) return;
        if (!_hasMod)
        {
            await PlatformServices.Dialogs.WarnAsync("未检测到「剧情分支模拟」模组，请先安装并启用。");
            return;
        }

        if (!File.Exists(version.HtmlPath))
        {
            await PlatformServices.Dialogs.WarnAsync("这个版本目录里找不到游戏本体 HTML，无法启动。");
            return;
        }

        SlotOption? slot = null;
        if (fromNode == null)
        {
            slot = SlotPicker.SelectedItem as SlotOption;
            if (slot == null)
            {
                await PlatformServices.Dialogs.WarnAsync("请先在剧情页选择一个存档槽。");
                return;
            }
        }

        var label = fromNode == null ? "完整模拟" : "从节点 " + fromNode.Id + " 继续模拟";
        var options = await ReadSimOptionsAsync(fromNode == null ? "模拟" : "继续模拟");
        if (options == null) return;

        var slotId = fromNode == null ? (ResolveSelectedSlotId() ?? "") : "";
        var slotLabel = fromNode == null ? (slot?.DisplayName ?? "") : "";
        var payload = fromNode == null
            ? JsonSerializer.Serialize(new
            {
                slot = slotId,
                slotLabel,
                depth = options.Depth,
                budget = options.Budget,
                seed = options.Seed,
            })
            : JsonSerializer.Serialize(new
            {
                fromNode = fromNode.Id,
                depth = options.Depth,
                budget = options.Budget,
                seed = options.Seed,
            });

        _simulating = true;
        var cts = new CancellationTokenSource();
        _simCts = cts;
        SetSimStatus(label + "：正在准备…");
        UpdateSimButtons();

        try
        {
            var server = await Task.Run(() =>
            {
                ModService.Apply(version.FullPath);
                return ServerManager.EnsureStarted(version);
            }, cts.Token);
            LauncherStatusService.Sync();

            // 没有现成页面时打开游戏页，让模组在游戏内接收命令并跑心跳。
            var hasPage = ServerManager.GetPageState(version.Folder)?.HasPages == true;
            if (!hasPage)
            {
                var gamePage = new GamePage(version, server.GameUrl);
                _suppressDisappearCancel = true;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                    await Navigation.PushAsync(gamePage, false));
                SetSimStatus("已打开游戏页面，等待加载…");
            }

            var baseUrl = $"http://127.0.0.1:{server.Port}";
            var result = await StorySimRunner.RunAsync(
                baseUrl, payload, label,
                message => MainThread.BeginInvokeOnMainThread(() => SetSimStatus(message)),
                cts.Token);

            SetSimStatus(result.Message);
            if (result.State == StorySimState.Done)
            {
                _reloadOnAppear = true;
                await LoadDataAsync();
                await NotifyGameAsync("模拟完成", result.Message);
            }
            else
            {
                await NotifyGameAsync("剧情模拟", result.Message);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetSimStatus("已停止模拟。");
        }
        catch (Exception ex)
        {
            LogService.Error("启动剧情模拟失败", ex);
            SetSimStatus("启动失败：" + ex.Message);
            await PlatformServices.Dialogs.ErrorAsync("启动模拟失败：" + ex.Message);
        }
        finally
        {
            _simulating = false;
            _simCts?.Dispose();
            _simCts = null;
            UpdateSimButtons();
        }
    }

    private async Task<SimOptions?> ReadSimOptionsAsync(string action)
    {
        var depth = ParsePositive(DepthEntry.Text, 3);
        var budget = ParsePositive(BudgetEntry.Text, 150);
        var seed = (SeedEntry.Text ?? "").Trim();
        if (seed.Length > 0 && !seed.All(char.IsDigit))
        {
            await PlatformServices.Dialogs.WarnAsync("种子必须是纯数字，留空则随机生成。");
            return null;
        }

        if (depth > 6 || budget > 400)
        {
            var message = $"这次{action}的参数比较大：\n深度 {depth} 层，节点预算 {budget} 个。\n\n" +
                          "节点可能很多或层次很深，模拟会明显变慢，甚至长时间没有响应。\n" +
                          "仍然" + action + "吗？";
            if (!await PlatformServices.Dialogs.ConfirmAsync(message, "模拟量较大", "仍然" + action))
                return null;
        }

        return new SimOptions(depth, budget, seed);
    }

    private static int ParsePositive(string? text, int fallback) =>
        int.TryParse(text, out var value) && value >= 1 ? value : fallback;

    private void CancelSimulation(string message)
    {
        if (!_simulating) return;
        try
        {
            _simCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        SetSimStatus(message);
    }

    /// <summary>模拟结束时在顶层页面提示，避免用户停在游戏页看不到结果。</summary>
    private async Task NotifyGameAsync(string title, string message)
    {
        try
        {
            var target = Navigation.NavigationStack.LastOrDefault();
            if (target == null)
            {
                await PlatformServices.Dialogs.InfoAsync(message, title);
                return;
            }
            await MainThread.InvokeOnMainThreadAsync(async () =>
                await target.DisplayAlertAsync(title, message, "好"));
        }
        catch (Exception ex)
        {
            LogService.Error("显示模拟结果提示失败", ex);
        }
    }

    private void SetSimStatus(string message) => SimStatusLabel.Text = message;

    private void UpdateSimButtons()
    {
        var busy = _loading || _simulating;
        StartSimButton.IsEnabled = !busy && _hasMod;
        RefreshButton.IsEnabled = !busy;
        VersionPicker.IsEnabled = !busy;
        SlotPicker.IsEnabled = !busy;

        var node = _selectedNode;
        var canContinue = !busy && _hasMod && node != null && node.Children.Count == 0 &&
                          (node.Status == StoryBranchStatus.Success ||
                           node.Status == StoryBranchStatus.Root);
        ContinueSimButton.IsEnabled = canContinue;
        ContinueHint.IsVisible = !busy && _hasMod && _tree != null && !canContinue;

        ExportJsonButton.IsEnabled = !busy && _tree != null;
        ExportImageButton.IsEnabled = !busy && _tree != null;
    }

    // ---------- 导出 ----------

    private async void OnExportJsonClicked(object? sender, EventArgs e)
    {
        if (_tree == null)
        {
            await PlatformServices.Dialogs.WarnAsync("还没有可导出的模拟结果。");
            return;
        }

        SetBusy(true);
        try
        {
            await ExportJsonCoreAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("导出模拟结果失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出结果");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ExportJsonCoreAsync()
    {
        var version = _version;
        var source = StoryBranchReader.PathOf(version.FullPath);
        if (!File.Exists(source))
        {
            await PlatformServices.Dialogs.WarnAsync("这个版本还没有模拟结果文件。", "导出结果");
            return;
        }

        var dir = IOPath.Combine(FileSystem.CacheDirectory, "export");
        Directory.CreateDirectory(dir);
        var name = "branch-result-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
        var path = IOPath.Combine(dir, name);

        var text = await Task.Run(() => FormatBranchJson(File.ReadAllText(source)));

        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        await ShareSavedFileAsync(path, "application/json", "导出结果", "模拟结果");
    }

    /// <summary>复制当前版本的模拟结果 JSON；与导出使用同一份格式化逻辑。</summary>
    private async Task CopyJsonAsync()
    {
        if (_tree == null)
        {
            await PlatformServices.Dialogs.WarnAsync("还没有可复制的模拟结果。", "复制 JSON");
            return;
        }

        var source = StoryBranchReader.PathOf(_version.FullPath);
        if (!File.Exists(source))
        {
            await PlatformServices.Dialogs.WarnAsync("这个版本还没有模拟结果文件。", "复制 JSON");
            return;
        }

        try
        {
            var text = await Task.Run(() => FormatBranchJson(File.ReadAllText(source)));
            await Clipboard.Default.SetTextAsync(text);
            LogService.Append("已复制剧情模拟 JSON 到剪贴板。");
            await PlatformServices.Dialogs.InfoAsync("模拟结果 JSON 已复制到剪贴板。", "复制 JSON");
        }
        catch (Exception ex)
        {
            LogService.Error("复制模拟结果 JSON 失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("复制失败：" + ex.Message, "复制 JSON");
        }
    }

    private static string FormatBranchJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private async void OnExportImageClicked(object? sender, EventArgs e)
    {
        if (_tree == null)
        {
            await PlatformServices.Dialogs.WarnAsync("还没有可导出的模拟结果。");
            return;
        }

        SetBusy(true);
        try
        {
            var png = await MainThread.InvokeOnMainThreadAsync(CaptureContentPng);
            if (png != null)
            {
                await ShareSavedFileAsync(png, "image/png", "导出图片", "分支长图");
                return;
            }

            // Android 上无法离屏排版时退回 JSON，避免导出流程彻底失败。
            await PlatformServices.Dialogs.InfoAsync(
                "当前环境不支持长图导出，已改为导出 JSON。", "导出图片");
            await ExportJsonCoreAsync();
        }
        catch (Exception ex)
        {
            LogService.Error("导出分支长图失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出图片失败：" + ex.Message, "导出图片");
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>把滚动内容整体绘制成一张长图；失败时返回 null 交给 JSON 兜底。</summary>
    private string? CaptureContentPng()
    {
        try
        {
            if (ContentStack.Handler?.PlatformView is not Android.Views.View view) return null;

            var width = view.Width;
            var height = view.Height;
            if (width <= 0 || height <= 0) return null;

            const int maxHeight = 20000;
            var scale = height > maxHeight ? (float)maxHeight / height : 1f;
            var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
            var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

            using var bitmap = Android.Graphics.Bitmap.CreateBitmap(
                pixelWidth, pixelHeight, Android.Graphics.Bitmap.Config.Argb8888!);
            using var canvas = new Android.Graphics.Canvas(bitmap);
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            canvas.DrawColor(dark
                ? Android.Graphics.Color.Rgb(30, 30, 30)
                : Android.Graphics.Color.White);
            canvas.Scale(scale, scale);
            view.Draw(canvas);

            var dir = IOPath.Combine(FileSystem.CacheDirectory, "export");
            Directory.CreateDirectory(dir);
            var path = IOPath.Combine(dir,
                "branch-graph-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            using var output = File.Create(path);
            if (!bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, output))
                return null;
            return path;
        }
        catch (Exception ex)
        {
            LogService.Error("绘制剧情长图失败", ex);
            return null;
        }
    }

    private static async Task ShareSavedFileAsync(
        string path, string mimeType, string title, string what)
    {
        var result = await SaveFileHelper.SaveAsync(path, IOPath.GetFileName(path), mimeType);
        switch (result)
        {
            case SaveFileResult.Saved:
                await PlatformServices.Dialogs.InfoAsync(what + "已保存。", title);
                break;
            case SaveFileResult.Canceled:
                await PlatformServices.Dialogs.InfoAsync(
                    "已取消保存。\n\n缓存副本仍保留在：\n" + path, title);
                break;
            default:
                await Share.RequestAsync(new ShareFileRequest
                {
                    Title = title,
                    File = new ShareFile(path),
                });
                await PlatformServices.Dialogs.InfoAsync(
                    "已打开系统分享面板，可另存为文件。\n\n缓存位置：\n" + path, title);
                break;
        }
    }

    // ---------- 模组引导 ----------

    private async void OnOpenModsClicked(object? sender, EventArgs e)
    {
        if (_version == null || string.IsNullOrWhiteSpace(_version.FullPath)) return;
        await Navigation.PushAsync(new ModsPage(_version));
    }

    private void OnOpenMarketClicked(object? sender, EventArgs e)
    {
        OpenMarketRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>嵌入主界面时由宿主接管「打开模组市场」。</summary>
    public event EventHandler? OpenMarketRequested;

    // ---------- 杂项 ----------

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        UpdateSimButtons();
    }

    private void SetStatus(string message) => StatusLabel.Text = message;

    private sealed record SlotOption(
        string SlotId, string DisplayName, bool IsFallback, string FallbackSlotId);

    private sealed record StoryDataSnapshot(
        bool HasMod, StoryArchive? Archive, StoryBranchTree? Tree, string? TreeError);

    private sealed record SimOptions(int Depth, int Budget, string Seed);
}
