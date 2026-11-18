using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using DoLLauncher.Core.Story;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.ViewModels;

/// <summary>剧情页视图模型：读取游戏内导出的分支模拟结果并展示节点详情。</summary>
public sealed class StoryViewModel : INotifyPropertyChanged
{
    private const string ModRepoUrl = "https://github.com/Dingdang255/StoryBranchSim";

    private VersionItemVm? _selectedTarget;
    private StoryBranchTree? _tree;
    private StoryNodeVm? _selectedNode;
    private bool _hasMod;
    private bool _loading;
    private string _statusText = "请选择一个版本。";
    private string _summaryText = string.Empty;
    private string _detailText = string.Empty;
    private string _warningText = string.Empty;

    public StoryViewModel()
    {
        RefreshCommand = new RelayCommand(_ => _ = LoadAsync(), _ => SelectedTarget != null && !_loading);
        OpenRepoCommand = new RelayCommand(_ => OpenRepo());
        OpenFolderCommand = new RelayCommand(_ => OpenResultFolder(), _ => SelectedTarget != null);
        ExportJsonCommand = new RelayCommand(_ => _ = ExportJsonAsync(), _ => _tree != null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<VersionItemVm> Targets { get; } = new();

    public ObservableCollection<StoryNodeVm> Nodes { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand OpenRepoCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public ICommand ExportJsonCommand { get; }

    public VersionItemVm? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value)) return;
            RaiseCommands();
            _ = LoadAsync();
        }
    }

    public StoryNodeVm? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value)) return;
            BuildDetail();
        }
    }

    public bool HasMod
    {
        get => _hasMod;
        private set
        {
            if (!Set(ref _hasMod, value)) return;
            OnPropertyChanged(nameof(IsModMissing));
        }
    }

    public bool IsModMissing => !_hasMod;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => Set(ref _detailText, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set
        {
            if (!Set(ref _warningText, value)) return;
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => _warningText.Length > 0;

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

    public void EnsureLoaded()
    {
        if (Targets.Count == 0) return;
        if (_tree == null && !_loading) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null || _loading) return;

        _loading = true;
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        StatusText = "正在读取模拟结果…";
        try
        {
            var folder = version.FullPath;
            var hasMod = await Task.Run(() => HasStoryBranchMod(folder));
            HasMod = hasMod;
            WarningText = string.Empty;
            if (!hasMod)
            {
                ClearNodes();
                StatusText = "未检测到「剧情分支模拟」模组。";
                return;
            }

            var result = await Task.Run(() =>
            {
                var tree = StoryBranchReader.Load(folder, out var error);
                return (Tree: tree, Error: error);
            });

            if (SelectedTarget?.Info.Folder != version.Folder) return;
            if (result.Tree == null)
            {
                ClearNodes();
                StatusText = result.Error ?? "没有找到模拟结果。";
                return;
            }

            _tree = result.Tree;
            BuildNodes(result.Tree);
            (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _tree = null;
            ClearNodes();
            StatusText = "读取模拟结果失败：" + ex.Message;
            LogService.Error("读取剧情模拟结果失败", ex);
        }
        finally
        {
            _loading = false;
            (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void BuildNodes(StoryBranchTree tree)
    {
        Nodes.Clear();
        foreach (var node in tree.Nodes) Nodes.Add(new StoryNodeVm(node));
        SelectedNode = Nodes.FirstOrDefault();

        var counts = tree.CountByStatus();
        var sb = new StringBuilder();
        sb.Append("起点：").Append(string.IsNullOrEmpty(tree.RootPassage) ? "（未知）" : tree.RootPassage);
        if (tree.SaveSlot.Length > 0) sb.Append("　存档槽：").Append(tree.SaveSlot);
        if (tree.GameVersion.Length > 0) sb.Append("　游戏版本：").Append(tree.GameVersion);
        sb.AppendLine();
        sb.Append($"节点 {tree.Nodes.Count} 个：成功 {counts.Success} / 失败 {counts.Failed} / 合并 {counts.Merged} / 跳过 {counts.Skipped}");
        if (tree.Depth > 0) sb.Append($"　深度上限 {tree.Depth}");
        if (tree.Budget > 0) sb.Append($"　节点预算 {tree.Budget}");
        if (tree.Truncated) sb.Append("　（因预算截断）");
        SummaryText = sb.ToString();

        var warnings = new List<string>();
        if (tree.VersionMismatch)
            warnings.Add(string.IsNullOrEmpty(tree.VersionWarning) ? "模拟结果来自其它游戏版本。" : tree.VersionWarning);
        if (!tree.StateIntact) warnings.Add("模拟结束后玩家状态与开始前不一致，结果可能受副作用影响。");
        WarningText = string.Join("\n", warnings);
        StatusText = $"读取成功：{tree.Nodes.Count} 个节点" +
                     (tree.GeneratedAt.Length > 0 ? $"（导出于 {tree.GeneratedAt}）" : string.Empty);
    }

    private void BuildDetail()
    {
        var vm = SelectedNode;
        if (vm == null)
        {
            DetailText = "在左侧选择一个节点查看详情。";
            return;
        }

        var node = vm.Node;
        var sb = new StringBuilder();
        sb.AppendLine("== 节点 ==");
        sb.AppendLine("选项：" + node.DisplayTitle);
        sb.AppendLine("状态：" + vm.StatusText);
        sb.AppendLine($"深度：{node.Depth}　序号：{node.OptionIndex}");
        sb.AppendLine($"出发段落：{node.FromPassage}");
        sb.AppendLine($"指向段落：{node.ToPassage}");
        if (node.ResultPassage.Length > 0 && node.ResultPassage != node.ToPassage)
            sb.AppendLine($"实际到达：{node.ResultPassage}");
        if (node.CutByDepth) sb.AppendLine("注意：该节点因深度上限被截断。");
        if (node.MergedInto.Length > 0) sb.AppendLine("合并到节点：" + node.MergedInto);
        if (node.Error.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== 失败原因 ==");
            sb.AppendLine(node.Error);
        }

        if (node.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== 副作用告警 ==");
            foreach (var warning in node.Warnings) sb.AppendLine("· " + warning);
        }

        sb.AppendLine();
        sb.AppendLine("== 随机 ==");
        sb.AppendLine($"种子：{node.Seed.Raw}（{node.Seed.Source}，{node.Seed.Hash}）");
        sb.AppendLine($"随机数调用：{node.RandomCalls} 次　耗时：{node.DurationMs} ms");

        if (node.Diff.Count > 0 || node.DiffOmitted > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== 变量差异 ==");
            foreach (var change in node.Diff)
            {
                var kind = change.Kind switch
                {
                    "added" => "新增",
                    "removed" => "删除",
                    _ => "变化",
                };
                sb.AppendLine($"[{kind}] {change.Key}：{change.Before ?? "—"} → {change.After ?? "—"}");
            }
            if (node.DiffOmitted > 0) sb.AppendLine($"…另有 {node.DiffOmitted} 项差异未列出");
        }

        if (node.Children.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"子节点：{node.Children.Count} 个");
        }

        DetailText = sb.ToString().TrimEnd();
    }

    private void ClearNodes()
    {
        _tree = null;
        Nodes.Clear();
        SelectedNode = null;
        SummaryText = string.Empty;
        DetailText = "在左侧选择一个节点查看详情。";
        (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static bool HasStoryBranchMod(string versionDir)
    {
        try
        {
            return ModService.Load(versionDir).Any(mod =>
                mod.Enabled && ModService.NameKeys(mod)
                    .Any(key => key.Contains("storybranchsim", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private void OpenRepo()
    {
        try
        {
            PlatformServices.Links.OpenUrl(ModRepoUrl);
        }
        catch (Exception ex)
        {
            LogService.Error("打开剧情模组主页失败", ex);
        }
    }

    private void OpenResultFolder()
    {
        var version = SelectedTarget?.Info;
        if (version == null) return;
        try
        {
            var dir = StoryBranchReader.FolderOf(version.FullPath);
            Directory.CreateDirectory(dir);
            PlatformServices.Links.OpenPath(dir);
        }
        catch (Exception ex)
        {
            LogService.Error("打开模拟结果目录失败", ex);
        }
    }

    private async Task ExportJsonAsync()
    {
        var version = SelectedTarget?.Info;
        if (version == null || _tree == null) return;

        var source = StoryBranchReader.PathOf(version.FullPath);
        if (!File.Exists(source))
        {
            await PlatformServices.Dialogs.InfoAsync("还没有模拟结果文件。", "导出 JSON");
            return;
        }

        var target = PlatformServices.FilePicker.PickSaveFile(
            "导出模拟结果", "JSON 文件 (*.json)|*.json",
            $"story-sim-{version.Folder}-{DateTime.Now:yyyyMMdd-HHmm}.json", ".json");
        if (target == null) return;

        try
        {
            File.Copy(source, target, true);
            await PlatformServices.Dialogs.InfoAsync("已导出：\n" + target, "导出 JSON");
        }
        catch (Exception ex)
        {
            LogService.Error("导出剧情 JSON 失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("导出失败：" + ex.Message, "导出 JSON");
        }
    }

    private void RaiseCommands()
    {
        (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportJsonCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

/// <summary>剧情分支树的一个节点（列表展示用）。</summary>
public sealed class StoryNodeVm
{
    public StoryNodeVm(StoryBranchNode node)
    {
        Node = node;
        Indent = new Avalonia.Thickness(Math.Min(node.Depth, 12) * 16, 0, 0, 0);
        StatusText = node.Status switch
        {
            StoryBranchStatus.Root => "起点",
            StoryBranchStatus.Failed => "失败",
            StoryBranchStatus.Merged => "合并",
            StoryBranchStatus.Skipped => "跳过",
            _ => "成功",
        };
        var target = node.ResultPassage.Length > 0 ? node.ResultPassage : node.ToPassage;
        TargetText = target.Length > 0 ? "→ " + target : string.Empty;
        Marker = node.Status switch
        {
            StoryBranchStatus.Failed => "✕",
            StoryBranchStatus.Merged => "＝",
            StoryBranchStatus.Skipped => "－",
            StoryBranchStatus.Root => "●",
            _ => "✓",
        };
    }

    public StoryBranchNode Node { get; }

    public Avalonia.Thickness Indent { get; }

    public string StatusText { get; }

    public string Marker { get; }

    public string Title => Node.DisplayTitle;

    public string TargetText { get; }
}
