using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DoLLauncher.Core.Story;
using DoLLauncher.Services;

namespace DoLLauncher;

public partial class MainWindow
{
    // ---------- 剧情页 ----------

    private readonly ObservableCollection<VersionVm> _storyVersions = new();
    private readonly ObservableCollection<StorySlotVm> _storySlots = new();
    private readonly Dictionary<string, StorySlotInfo> _storySlotMap = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Slot, int Step), IReadOnlyDictionary<string, string>> _detailCache = new();

    private StoryArchive? _storyArchive;
    private StoryPath? _storyPath;
    private StoryRules _storyRules = StoryRules.Default;
    private string _storyRulesWarning = "";
    private CancellationTokenSource? _storyLoading;
    private CancellationTokenSource? _storyDetailLoading;
    private DispatcherTimer? _storySearchTimer;
    private DispatcherTimer? _storySimTimer;
    private StoryStep? _storySelectedStep;
    private bool _storyInitialized;
    private bool _storySuppressEvents;

    /// <summary>剧情页首次显示时初始化规则、版本列表并读取存档。</summary>
    private void OnStoryPageShown()
    {
        if (_storyInitialized) return;
        _storyInitialized = true;

        StoryMap.NodeSelected += StoryMap_NodeSelected;
        StoryBranchGraph.NodeSelected += (_, node) => ShowStoryBranchDetail(node);

        var rulesPath = Path.Combine(Paths.Root, "user", "story-rules.json");
        _storyRules = StoryRules.Load(rulesPath, out var warning);
        _storyRulesWarning = string.IsNullOrWhiteSpace(warning) ? "" : warning!;

        StoryVersionBox.ItemsSource = _storyVersions;
        StorySlotBox.ItemsSource = _storySlots;

        _storySearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _storySearchTimer.Tick += (_, _) =>
        {
            _storySearchTimer!.Stop();
            ApplyStorySearch();
        };

        _storySimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _storySimTimer.Tick += (_, _) => UpdateStorySimButton();
        _storySimTimer.Start();

        RefreshStoryVersions(null);
        if (_storyRulesWarning.Length > 0) SetStoryStatus("");
        UpdateStorySimButton();
        _ = LoadStoryArchiveAsync();
    }

    /// <summary>把主窗口的版本列表同步到剧情页（只保留 http 版本）。</summary>
    private void RefreshStoryVersions(string? keepFolder)
    {
        _storySuppressEvents = true;
        try
        {
            _storyVersions.Clear();
            foreach (var vm in _versions)
            {
                if (vm.Info.IsFileMode || string.IsNullOrEmpty(vm.Info.FullPath)) continue;
                _storyVersions.Add(vm);
            }

            var preferred = keepFolder ?? _selected?.Info.Folder;
            VersionVm? target = preferred == null
                ? null
                : _storyVersions.FirstOrDefault(v => v.Info.Folder == preferred);
            target ??= _storyVersions.FirstOrDefault();
            StoryVersionBox.SelectedItem = target;
        }
        finally
        {
            _storySuppressEvents = false;
        }
    }

    /// <summary>读取所选版本的存档索引（后台执行），成功后自动加载最新的存档槽。</summary>
    private async Task LoadStoryArchiveAsync()
    {
        var version = StoryVersionBox.SelectedItem as VersionVm;
        if (version == null || string.IsNullOrEmpty(version.Info.FullPath))
        {
            _storyArchive = null;
            _storySlots.Clear();
            _storySlotMap.Clear();
            StoryMap.SetPath(null, _storyRules);
            ClearStoryDetails();
            StorySourceText.Text = "未选择版本";
            ShowStoryEmpty("请先选择一个游戏版本。");
            SetStoryStatus("没有可用的版本目录。");
            return;
        }

        var cts = RenewStoryToken();
        var token = cts.Token;
        SetStoryBusy(true, "正在解析存档…");
        StorySourceText.Text = "正在读取存档索引…";
        StoryMap.SetPath(null, _storyRules);
        ClearStoryDetails();
        SetStoryStatus("正在解析存档索引…");

        try
        {
            var folder = version.Info.FullPath;
            var result = await Task.Run(() => LoadArchiveCore(folder, token), token);
            if (!IsCurrentStoryLoad(token)) return;

            if (result.Archive == null)
            {
                _storyArchive = null;
                _storySlots.Clear();
                _storySlotMap.Clear();
                StorySourceText.Text = result.Source != null
                    ? BuildSourceSummary(result.Source)
                    : "未找到存档文件";
                ShowStoryEmpty("没有找到存档数据。\n先启动一次游戏完成数据落盘，再回到这里刷新。");
                SetStoryStatus(result.Error ?? "没有找到该版本的存档文件。");
                await LoadStoryBranchAsync();
                return;
            }

            _storyArchive = result.Archive;
            _detailCache.Clear();
            StorySourceText.Text = result.Source != null
                ? BuildSourceSummary(result.Source)
                : "存档文件";
            FillStorySlots(result.Archive);

            if (result.Archive.Slots.Count == 0)
            {
                ShowStoryEmpty("存档里没有可用的存档槽。");
                SetStoryStatus("存档文件中没有找到存档槽。");
                await LoadStoryBranchAsync();
                return;
            }

            SetStoryStatus($"共 {result.Archive.Slots.Count} 个存档槽，已选中最近的存档。");
            await LoadStoryBranchAsync();
        }
        catch (OperationCanceledException)
        {
            // 切换版本时主动取消，忽略
        }
        catch (Exception ex)
        {
            ShowStoryEmpty("读取存档失败。");
            SetStoryStatus("读取存档失败：" + ex.Message);
        }
        finally
        {
            if (IsCurrentStoryLoad(token)) SetStoryBusy(false);
        }
    }

    /// <summary>读取当前存档槽的剧情路径（后台执行）。</summary>
    private async Task LoadStoryPathAsync()
    {
        var archive = _storyArchive;
        var slotVm = StorySlotBox.SelectedItem as StorySlotVm;
        if (archive == null || slotVm == null) return;

        var cts = RenewStoryToken();
        var token = cts.Token;
        SetStoryBusy(true, "正在解析剧情路径…");
        StoryMap.SetPath(null, _storyRules);
        ClearStoryDetails();

        try
        {
            var slotId = slotVm.Slot.SlotId;
            var result = await Task.Run(() => LoadPathCore(archive, slotId, token), token);
            if (!IsCurrentStoryLoad(token)) return;

            if (result.Path == null)
            {
                _storyPath = null;
                ShowStoryEmpty("该存档没有可展示的剧情历史。");
                SetStoryStatus(result.Error ?? "该存档没有剧情历史。");
                return;
            }

            var path = result.Path;
            ApplyStoryRules(path);
            _storyPath = path;
            StoryMap.SetPath(path, _storyRules);
            StoryMap.FitToView();

            if (path.Steps.Count == 0)
            {
                ShowStoryEmpty("该存档没有记录任何剧情节点。");
                SetStoryStatus("该存档没有记录任何剧情节点。");
                return;
            }

            HideStoryEmpty();
            var suffix = path.IsDelta ? "（增量存档）" : "";
            SetStoryStatus($"已加载 {path.Steps.Count} 个节点{suffix}。" +
                           (path.SourceNote.Length > 0 ? "来源：" + path.SourceNote : ""));
        }
        catch (OperationCanceledException)
        {
            // 切换槽位时主动取消，忽略
        }
        catch (Exception ex)
        {
            ShowStoryEmpty("解析剧情路径失败。");
            SetStoryStatus("解析剧情路径失败：" + ex.Message);
        }
        finally
        {
            if (IsCurrentStoryLoad(token)) SetStoryBusy(false);
        }
    }

    /// <summary>用用户规则覆盖节点的分类与分组。</summary>
    private void ApplyStoryRules(StoryPath path)
    {
        foreach (var step in path.Steps)
        {
            step.Category = StoryCategory.Unknown;
            step.GroupName = "";
            if (_storyRules.TryClassify(step.Title, out var category, out var group))
            {
                step.Category = category;
                step.GroupName = group;
            }
        }
    }

    private void FillStorySlots(StoryArchive archive)
    {
        _storySuppressEvents = true;
        try
        {
            _storySlots.Clear();
            _storySlotMap.Clear();

            foreach (var slot in archive.Slots)
            {
                _storySlots.Add(new StorySlotVm(slot));
                _storySlotMap[slot.SlotId] = slot;
            }

            StorySlotVm? latest = null;
            foreach (var item in _storySlots)
            {
                if (latest == null || item.Slot.SavedAt > latest.Slot.SavedAt) latest = item;
            }

            StorySlotBox.SelectedItem = latest ?? _storySlots.FirstOrDefault();
        }
        finally
        {
            _storySuppressEvents = false;
            UpdateStorySimButton();
        }
    }

    private void StoryVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_storyInitialized || _storySuppressEvents) return;
        UpdateStorySimButton();
        _ = LoadStoryArchiveAsync();
    }

    private void StorySlot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_storyInitialized || _storySuppressEvents) return;
        UpdateStorySimButton();
        _ = LoadStoryBranchAsync();
    }

    private void StoryRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!_storyInitialized) return;
        var keep = (StoryVersionBox.SelectedItem as VersionVm)?.Info.Folder;
        RefreshStoryVersions(keep);
        _ = LoadStoryArchiveAsync();
    }

    private void StoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_storyInitialized) return;

        var categories = new List<StoryCategory>();
        if (StoryCatCharacter.IsChecked == true) categories.Add(StoryCategory.Character);
        if (StoryCatPlace.IsChecked == true) categories.Add(StoryCategory.Place);
        if (StoryCatEvent.IsChecked == true) categories.Add(StoryCategory.Event);
        if (StoryCatUnknown.IsChecked == true) categories.Add(StoryCategory.Unknown);
        StoryMap.SetVisibleCategories(categories);
    }

    private void StorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_storySearchTimer == null) return;
        _storySearchTimer.Stop();
        if (string.IsNullOrWhiteSpace(StorySearchBox.Text))
        {
            StoryMap.Highlight("");
            return;
        }

        _storySearchTimer.Start();
    }

    private void StorySearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _storySearchTimer?.Stop();
        ApplyStorySearch();
        e.Handled = true;
    }

    private void ApplyStorySearch()
    {
        var keyword = StorySearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            StoryMap.Highlight("");
            return;
        }

        if (!StoryMap.Highlight(keyword)) SetStoryStatus($"没有找到包含「{keyword}」的节点。");
    }

    private void StoryFit_Click(object sender, RoutedEventArgs e) => StoryMap.FitToView();

    private void StoryReset_Click(object sender, RoutedEventArgs e) => StoryMap.ResetView();

    private void StoryExpand_Click(object sender, RoutedEventArgs e) => StoryMap.ExpandAll();

    /// <summary>导图中选中步骤节点后，后台回读该步的完整变量。</summary>
    private void StoryMap_NodeSelected(object? sender, StoryStep step)
    {
        _storySelectedStep = step;
        _ = LoadStoryDetailAsync(step);
    }

    private async Task LoadStoryDetailAsync(StoryStep step)
    {
        var archive = _storyArchive;
        var path = _storyPath;
        if (archive == null || path == null) return;

        StoryDetailTitle.Text = string.IsNullOrWhiteSpace(step.Title) ? "（无标题）" : step.Title;
        StoryDetailMeta.Text = BuildStoryMeta(step);
        StoryDetailHint.Text = "正在读取…";
        StoryDetailHint.Visibility = Visibility.Visible;
        StoryDetailBody.Visibility = Visibility.Collapsed;
        StoryKeyFields.ItemsSource = null;
        StoryVarList.ItemsSource = null;
        StoryVarCountText.Text = "";
        StoryJsonBox.Text = "";

        var key = (path.SlotId, step.Index);
        _storyDetailLoading?.Cancel();
        _storyDetailLoading?.Dispose();
        _storyDetailLoading = null;

        if (_detailCache.TryGetValue(key, out var cached))
        {
            FillStoryDetail(cached);
            return;
        }

        var cts = new CancellationTokenSource();
        _storyDetailLoading = cts;
        var token = cts.Token;

        try
        {
            var result = await Task.Run(
                () => LoadVariablesCore(archive, path, step.Index, token), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_storySelectedStep, step)) return;

            if (result.Variables == null)
            {
                StoryDetailHint.Text = result.Error ?? "读取该步骤的变量失败。";
                return;
            }

            if (_detailCache.Count > 64) _detailCache.Clear();
            _detailCache[key] = result.Variables;
            FillStoryDetail(result.Variables);
        }
        catch (OperationCanceledException)
        {
            // 切换到其它节点时主动取消，忽略
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StoryDetailHint.Text = "读取该步骤的变量失败：" + ex.Message;
        }
    }

    private void FillStoryDetail(IReadOnlyDictionary<string, string> variables)
    {
        var keyFields = new List<StoryFieldVm>();
        foreach (var name in new[] { "timeStamp", "startDate", "day", "weather", "weatherObj" })
        {
            foreach (var pair in variables)
            {
                if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                keyFields.Add(new StoryFieldVm(pair.Key, FormatStoryValue(pair.Value)));
                break;
            }
        }

        StoryKeyFields.ItemsSource = keyFields;
        StoryKeySection.Visibility = keyFields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var all = new List<StoryFieldVm>();
        foreach (var pair in variables.OrderBy(p => p.Key, StringComparer.Ordinal))
            all.Add(new StoryFieldVm(pair.Key, FormatStoryValue(pair.Value)));

        StoryVarList.ItemsSource = all;
        StoryVarCountText.Text = $"{all.Count} 项";
        StoryJsonBox.Text = BuildStoryJson(variables);

        StoryDetailHint.Visibility = Visibility.Collapsed;
        StoryDetailBody.Visibility = Visibility.Visible;
    }

    private void ClearStoryDetails()
    {
        _storySelectedStep = null;
        _storyDetailLoading?.Cancel();
        _storyDetailLoading?.Dispose();
        _storyDetailLoading = null;

        StoryDetailTitle.Text = "";
        StoryDetailMeta.Text = "";
        StoryDetailHint.Text = "在左侧导图中点击步骤节点查看详情。";
        StoryDetailHint.Visibility = Visibility.Visible;
        StoryDetailBody.Visibility = Visibility.Collapsed;
        StoryKeyFields.ItemsSource = null;
        StoryKeySection.Visibility = Visibility.Collapsed;
        StoryVarList.ItemsSource = null;
        StoryVarCountText.Text = "";
        StoryJsonBox.Text = "";
    }

    private void StoryCopyJson_Click(object sender, RoutedEventArgs e)
    {
        var text = StoryJsonBox.Text;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            Clipboard.SetText(text);
            Log("已复制剧情节点 JSON。");
        }
        catch (Exception ex)
        {
            Log("复制剧情节点 JSON 失败：" + ex.Message);
            AppDialog.Warn("复制失败：" + ex.Message);
        }
    }

    private void SetStoryBusy(bool busy, string? loadingText = null)
    {
        StoryRefreshBtn.IsEnabled = !busy;
        StoryVersionBox.IsEnabled = !busy;
        StorySlotBox.IsEnabled = !busy;

        if (busy)
        {
            StoryEmptyText.Text = loadingText ?? "正在加载…";
            StoryEmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ShowStoryEmpty(string message)
    {
        StoryEmptyText.Text = message;
        StoryEmptyText.Visibility = Visibility.Visible;
    }

    private void HideStoryEmpty() => StoryEmptyText.Visibility = Visibility.Collapsed;

    private void SetStoryStatus(string message)
    {
        var text = (message ?? "").Trim();
        if (_storyRulesWarning.Length > 0)
        {
            text = text.Length == 0 ? _storyRulesWarning : text + "　" + _storyRulesWarning;
        }

        StoryStatusText.Text = text;
    }

    private CancellationTokenSource RenewStoryToken()
    {
        _storyLoading?.Cancel();
        _storyLoading?.Dispose();
        var cts = new CancellationTokenSource();
        _storyLoading = cts;
        return cts;
    }

    private bool IsCurrentStoryLoad(CancellationToken token) =>
        _storyLoading != null && _storyLoading.Token == token;

    private static string BuildSourceSummary(StorySourceInfo source)
    {
        var name = Path.GetFileName(source.FilePath);
        var size = source.Length >= 1024 * 1024
            ? (source.Length / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : (source.Length / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return $"来源：{source.DisplayNote} · {name} · {size} · {source.ModifiedAt:yyyy-MM-dd HH:mm}";
    }

    private static string BuildStoryMeta(StoryStep step)
    {
        var lines = new List<string>
        {
            $"序号 {step.Index + 1}　原始步 {step.StepIndexes.Count} 步　合并 {step.RepeatCount} 步",
            "时间戳：" + FormatStoryTime(step.Timestamp),
            "游戏内日期：" + FormatStoryTime(step.StartDate),
            "分类：" + StoryCategoryNames.ToChinese(step.Category) +
            (string.IsNullOrWhiteSpace(step.GroupName) ? "" : " / " + step.GroupName),
        };

        if (step.StepIndexes.Count > 1)
            lines.Add("包含步：" + string.Join("、", step.StepIndexes.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture))));

        return string.Join("\n", lines);
    }

    private static string FormatStoryTime(long value)
    {
        if (value <= 0) return "未记录";

        // 游戏内的 timeStamp / startDate 是内部计数，不是 Unix 毫秒；只有明显是时间戳时才转换。
        if (value < 1_000_000_000_000L) return value.ToString(CultureInfo.InvariantCulture);

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).LocalDateTime
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>把变量的原始 JSON 文本转成适合阅读的文本：字符串去掉引号，对象/数组保留 JSON。</summary>
    private static string FormatStoryValue(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (string.Equals(raw, "null", StringComparison.Ordinal)) return "null";

        try
        {
            var node = JsonNode.Parse(raw);
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text ?? "";
            return node?.ToJsonString() ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>把键值字典按字母序拼成缩进 2 的完整 JSON。</summary>
    private static string BuildStoryJson(IReadOnlyDictionary<string, string> variables)
    {
        var root = new JsonObject();
        foreach (var pair in variables.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            try
            {
                root[pair.Key] = JsonNode.Parse(pair.Value);
            }
            catch (JsonException)
            {
                root[pair.Key] = pair.Value;
            }
        }

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static (StoryArchive? Archive, StorySourceInfo? Source, string? Error) LoadArchiveCore(
        string versionFolder, CancellationToken ct)
    {
        var source = SaveMirrorReader.FindSource(versionFolder);
        if (source == null)
            return (null, null, "没有找到该版本的存档文件（磁盘镜像或备份导出）。");

        var archive = SaveMirrorReader.LoadArchive(source, ct, out var error);
        return (archive, source, error);
    }

    private static (StoryPath? Path, string? Error) LoadPathCore(
        StoryArchive archive, string slotId, CancellationToken ct)
    {
        var path = SaveMirrorReader.LoadSlot(archive, slotId, ct, out var error);
        return (path, error);
    }

    private static (IReadOnlyDictionary<string, string>? Variables, string? Error) LoadVariablesCore(
        StoryArchive archive, StoryPath path, int stepIndex, CancellationToken ct)
    {
        var variables = SaveMirrorReader.LoadStepVariables(archive, path, stepIndex, ct, out var error);
        return (variables, error);
    }

    /// <summary>存档槽下拉项。</summary>
    private sealed class StorySlotVm
    {
        public StorySlotVm(StorySlotInfo slot)
        {
            Slot = slot;
            var name = string.IsNullOrWhiteSpace(slot.SaveName) ? "存档 " + slot.SlotId : slot.SaveName;
            var time = "";
            if (slot.SavedAt > 0)
            {
                try
                {
                    time = " · " + DateTimeOffset.FromUnixTimeMilliseconds(slot.SavedAt).LocalDateTime
                        .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                    time = " · " + slot.SavedAt.ToString(CultureInfo.InvariantCulture);
                }
            }

            DisplayName = name + time;
        }

        public StorySlotInfo Slot { get; }

        public string DisplayName { get; }
    }

    /// <summary>详情区分组/变量列表的一行。</summary>
    private sealed class StoryFieldVm
    {
        public StoryFieldVm(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }

        public string Value { get; }
    }
}
