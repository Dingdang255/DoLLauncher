using System.Globalization;
using DoLLauncher.Core.Story;
using DoLLauncher.Models;
using DoLLauncher.Services;
using Microsoft.Maui.Controls.Shapes;

namespace DoLLauncher.Maui;

public partial class StoryPage : ContentPage
{
    /// <summary>每层缩进与最大缩进层数，避免深层节点把内容挤出屏幕。</summary>
    private const double IndentStep = 12;
    private const int MaxIndentDepth = 8;

    private readonly VersionInfo _version;
    private Border? _selectedRow;
    private StoryBranchNode? _selectedNode;
    private bool _loading;
    private bool _loaded;

    public StoryPage(VersionInfo version)
    {
        InitializeComponent();
        _version = version;
        HeaderLabel.Text = version.DisplayName;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) _ = LoadAsync();
    }

    private async void OnRefreshClicked(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        SetBusy(true);
        try
        {
            // Core 里的 FolderOf 直接用版本目录拼 data/story-sim，这里同样传绝对路径。
            var (tree, error) = await Task.Run(() =>
            {
                var loaded = StoryBranchReader.Load(_version.FullPath, out var message);
                return (Tree: loaded, Error: message);
            });

            if (tree == null)
            {
                ShowEmpty(error);
                return;
            }

            RenderTree(tree);
        }
        catch (Exception ex)
        {
            LogService.Error("读取剧情分支结果失败", ex);
            ShowEmpty("读取模拟结果失败：" + ex.Message);
        }
        finally
        {
            _loading = false;
            _loaded = true;
            SetBusy(false);
        }
    }

    private void ShowEmpty(string? error)
    {
        SummaryCard.IsVisible = false;
        WarningCard.IsVisible = false;
        NodeCard.IsVisible = false;
        DetailCard.IsVisible = false;
        NodeList.Children.Clear();
        DetailList.Children.Clear();
        _selectedNode = null;
        _selectedRow = null;

        EmptyState.IsVisible = true;
        ErrorLabel.Text = error ?? "";
        ErrorLabel.IsVisible = !string.IsNullOrWhiteSpace(error);
    }

    private void RenderTree(StoryBranchTree tree)
    {
        EmptyState.IsVisible = false;
        SummaryCard.IsVisible = true;
        NodeCard.IsVisible = true;
        DetailCard.IsVisible = false;
        _selectedNode = null;
        _selectedRow = null;

        RenderSummary(tree);

        DetailList.Children.Clear();
        NodeList.Children.Clear();
        foreach (var node in tree.Nodes) NodeList.Children.Add(MakeNodeRow(node));
    }

    private void RenderSummary(StoryBranchTree tree)
    {
        SummaryList.Children.Clear();
        AddLine(SummaryList, "起点段落", tree.RootPassage.Length > 0 ? tree.RootPassage : "未知");
        AddLine(SummaryList, "游戏版本", tree.GameVersion.Length > 0 ? tree.GameVersion : "未知");
        AddLine(SummaryList, "生成时间", FormatGeneratedAt(tree));
        AddLine(SummaryList, "深度 / 预算", $"{tree.Depth} 层 / {tree.Budget} 个节点");
        AddLine(SummaryList, "是否截断", tree.Truncated ? "是（节点预算已用完）" : "否");

        var (success, failed, merged, skipped) = tree.CountByStatus();
        AddLine(SummaryList, "状态计数",
            $"成功 {success} / 失败 {failed} / 跳过 {skipped} / 合并 {merged}");

        WarningCard.IsVisible = tree.VersionMismatch && tree.VersionWarning.Length > 0;
        WarningLabel.Text = tree.VersionWarning;
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
            return;
        }

        if (_selectedRow != null) ApplyRowBackground(_selectedRow, false);
        _selectedNode = node;
        _selectedRow = row;
        ApplyRowBackground(row, true);

        DetailTitle.Text = node.DisplayTitle;
        DetailList.Children.Clear();
        AddLine(DetailList, "状态", StatusView(node.Status).Name);
        if (node.Error.Length > 0) AddLine(DetailList, "错误", node.Error);

        var diff = node.DiffOmitted > 0
            ? $"{node.Diff.Count} 条（另有 {node.DiffOmitted} 条未列出）"
            : $"{node.Diff.Count} 条";
        AddLine(DetailList, "变量差异", diff);
        AddLine(DetailList, "随机调用", $"{node.RandomCalls} 次");
        AddLine(DetailList, "耗时", $"{node.DurationMs} ms");

        DetailCard.IsVisible = true;
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

    private void SetBusy(bool busy) => BusyOverlay.IsVisible = busy;
}
