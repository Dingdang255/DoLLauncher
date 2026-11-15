using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DoLLauncher.Core.Story;

namespace DoLLauncher.Controls;

/// <summary>
/// 模拟分支图：按缩进列出模拟节点，并把「合并」节点用彩色箭头汇入它收束到的共同下一节点。
/// 指向同一节点的多条分支共用一条颜色的竖线，只在目标节点处保留一个箭头，类似 git graph。
/// </summary>
public sealed class StoryBranchGraphView : FrameworkElement
{
    private const double RowHeight = 26;
    private const double TopPad = 8;
    private const double LeftPad = 10;
    private const double Indent = 14;
    private const double BadgeWidth = 34;
    private const double BadgeHeight = 16;
    private const double BadgeGap = 8;
    private const double LaneGap = 12;
    private const double LaneRightPad = 12;
    private const double MaxTitleWidth = 420;

    private static readonly Color[] LaneColors =
    {
        Color.FromRgb(224, 164, 88),
        Color.FromRgb(127, 179, 213),
        Color.FromRgb(179, 157, 219),
        Color.FromRgb(139, 195, 74),
        Color.FromRgb(240, 98, 146),
        Color.FromRgb(77, 208, 225),
    };

    /// <summary>节点文字画刷。</summary>
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(StoryBranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>次要文字与连线画刷。</summary>
    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(StoryBranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>选中行强调画刷。</summary>
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StoryBranchGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private sealed class Row
    {
        public StoryBranchNode Node = null!;
        public int Index;
        public int Depth;
        public double Y;
        public double X;
    }

    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, Row> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _laneOfTarget = new(StringComparer.Ordinal);
    private readonly List<string> _laneTargets = new();

    private StoryBranchTree? _tree;
    private bool _successOnly;
    private string _selectedId = "";
    private double _rowRight;

    /// <summary>点击某一行时触发。</summary>
    public event EventHandler<StoryBranchNode>? NodeSelected;

    /// <summary>创建分支图控件。</summary>
    public StoryBranchGraphView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>节点文字画刷。</summary>
    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>次要文字与连线画刷。</summary>
    public Brush? MutedBrush
    {
        get => (Brush?)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    /// <summary>选中行强调画刷。</summary>
    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>设置要展示的模拟结果。</summary>
    public void SetTree(StoryBranchTree? tree, bool successOnly)
    {
        _tree = tree;
        _successOnly = successOnly;
        Rebuild();
    }

    /// <summary>切换「仅成功」筛选。</summary>
    public void SetSuccessOnly(bool successOnly)
    {
        if (_successOnly == successOnly) return;
        _successOnly = successOnly;
        Rebuild();
    }

    /// <summary>选中指定节点并刷新高亮。</summary>
    public void SelectNode(string nodeId)
    {
        _selectedId = nodeId;
        InvalidateVisual();
    }

    private void Rebuild()
    {
        _rows.Clear();
        _byId.Clear();
        _laneOfTarget.Clear();
        _laneTargets.Clear();

        if (_tree?.Root != null)
        {
            Walk(_tree.Root, 0);
        }

        foreach (var row in _rows)
        {
            if (row.Node.Status != StoryBranchStatus.Merged) continue;
            var target = row.Node.MergedInto;
            if (target.Length == 0 || !_byId.ContainsKey(target)) continue;
            if (_laneOfTarget.ContainsKey(target)) continue;
            _laneOfTarget[target] = _laneTargets.Count;
            _laneTargets.Add(target);
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Walk(StoryBranchNode node, int depth)
    {
        var row = new Row
        {
            Node = node,
            Index = _rows.Count,
            Depth = depth,
            X = LeftPad + depth * Indent,
            Y = TopPad + _rows.Count * RowHeight + RowHeight / 2,
        };
        _rows.Add(row);
        _byId[node.Id] = row;

        foreach (var child in node.Children)
        {
            if (_successOnly &&
                (child.Status == StoryBranchStatus.Failed || child.Status == StoryBranchStatus.Skipped))
            {
                continue;
            }
            Walk(child, depth + 1);
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var height = TopPad * 2 + Math.Max(1, _rows.Count) * RowHeight;
        _rowRight = ComputeRowRight();
        var lanes = _laneTargets.Count * LaneGap + LaneRightPad + 8;
        var width = Math.Max(320, _rowRight + 8 + lanes);
        return new Size(width, height);
    }

    /// <summary>最宽一行的右边界，用于计算画布宽度和汇入线位置。</summary>
    private double ComputeRowRight()
    {
        if (_rows.Count == 0) return LeftPad;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var max = 0.0;
        foreach (var row in _rows)
        {
            var title = MakeText(BuildTitle(row.Node), 12, Brushes.Black, dpi, MaxTitleWidth);
            var right = row.X + BadgeWidth + BadgeGap + title.WidthIncludingTrailingWhitespace;
            var suffix = BuildSuffix(row.Node);
            if (suffix.Length > 0)
            {
                var suffixText = MakeText(suffix, 11, Brushes.Black, dpi, MaxTitleWidth);
                right += 8 + suffixText.WidthIncludingTrailingWhitespace;
            }
            if (right > max) max = right;
        }
        return max;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        var index = (int)Math.Floor((pos.Y - TopPad) / RowHeight);
        if (index >= 0 && index < _rows.Count)
        {
            var node = _rows[index].Node;
            _selectedId = node.Id;
            InvalidateVisual();
            NodeSelected?.Invoke(this, node);
            e.Handled = true;
            return;
        }
        base.OnMouseLeftButtonDown(e);
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        var index = (int)Math.Floor((pos.Y - TopPad) / RowHeight);
        Cursor = index >= 0 && index < _rows.Count ? Cursors.Hand : Cursors.Arrow;
        base.OnMouseMove(e);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0 || _rows.Count == 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textBrush = TextBrush ?? Brushes.Gainsboro;
        var mutedBrush = MutedBrush ?? Brushes.Gray;
        var accentBrush = AccentBrush ?? Brushes.Cyan;

        var laneCount = _laneTargets.Count;
        var laneGap = LaneGap;
        var laneStart = _rowRight + 8;
        var maxStart = width - LaneRightPad - laneCount * laneGap;
        if (laneStart > maxStart) laneStart = Math.Max(160, maxStart);
        if (laneCount > 0 && laneStart + laneCount * laneGap > width - 4)
        {
            laneGap = Math.Max(3, (width - 4 - laneStart) / laneCount);
        }
        var contentRight = laneStart + 2;

        // 选中行底色
        foreach (var row in _rows)
        {
            if (row.Node.Id != _selectedId) continue;
            var rect = new Rect(4, row.Y - RowHeight / 2 + 1, Math.Max(10, contentRight - 6), RowHeight - 2);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                new Pen(accentBrush, 1), rect, 5, 5);
            break;
        }

        // 父子连线
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 150, 158, 170)), 1);
        foreach (var row in _rows)
        {
            if (row.Depth <= 0 || row.Node.ParentId.Length == 0) continue;
            if (!_byId.TryGetValue(row.Node.ParentId, out var parent)) continue;
            var x = row.X - 7;
            dc.DrawLine(guidePen, new Point(x, parent.Y), new Point(x, row.Y));
            dc.DrawLine(guidePen, new Point(x, row.Y), new Point(row.X - 3, row.Y));
        }

        // 汇入箭头：同一目标的多个来源共用一条竖线，只在目标行画一个箭头
        for (var lane = 0; lane < _laneTargets.Count; lane++)
        {
            var target = _laneTargets[lane];
            if (!_byId.TryGetValue(target, out var targetRow)) continue;
            var brush = new SolidColorBrush(LaneColors[lane % LaneColors.Length]);
            var pen = new Pen(brush, 1.6);
            var laneX = laneStart + lane * laneGap + 5;

            var top = targetRow.Y;
            var bottom = targetRow.Y;
            foreach (var row in _rows)
            {
                if (row.Node.Status != StoryBranchStatus.Merged) continue;
                if (!string.Equals(row.Node.MergedInto, target, StringComparison.Ordinal)) continue;
                top = Math.Min(top, row.Y);
                bottom = Math.Max(bottom, row.Y);
                dc.DrawLine(pen, new Point(contentRight, row.Y), new Point(laneX, row.Y));
            }

            dc.DrawLine(pen, new Point(laneX, top), new Point(laneX, bottom));
            dc.DrawLine(pen, new Point(laneX, targetRow.Y), new Point(laneStart - 4, targetRow.Y));

            // 指向目标节点的箭头
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(new Point(laneStart - 6, targetRow.Y), true, true);
                ctx.LineTo(new Point(laneStart + 1, targetRow.Y - 4), true, false);
                ctx.LineTo(new Point(laneStart + 1, targetRow.Y + 4), true, false);
            }
            arrow.Freeze();
            dc.DrawGeometry(brush, null, arrow);
        }

        // 节点行
        foreach (var row in _rows)
        {
            var node = row.Node;
            DrawBadge(dc, row, dpi);

            var titleX = row.X + BadgeWidth + BadgeGap;
            var maxWidth = Math.Min(MaxTitleWidth, Math.Max(40, contentRight - titleX));

            var title = BuildTitle(node);
            var titleText = MakeText(title, 12, textBrush, dpi, maxWidth);
            dc.DrawText(titleText, new Point(titleX, row.Y - titleText.Height / 2));
            var used = titleText.WidthIncludingTrailingWhitespace;

            var suffix = BuildSuffix(node);
            if (suffix.Length > 0)
            {
                var remain = maxWidth - used - 8;
                if (remain > 32)
                {
                    var suffixBrush = node.Status == StoryBranchStatus.Merged ? MergedLaneBrush(node) : mutedBrush;
                    var suffixText = MakeText(suffix, 11, suffixBrush, dpi, remain);
                    dc.DrawText(suffixText, new Point(titleX + used + 8, row.Y - suffixText.Height / 2));
                }
            }
        }
    }

    private Brush MergedLaneBrush(StoryBranchNode node)
    {
        if (node.MergedInto.Length > 0 &&
            _laneOfTarget.TryGetValue(node.MergedInto, out var lane))
        {
            return new SolidColorBrush(LaneColors[lane % LaneColors.Length]);
        }
        return new SolidColorBrush(Color.FromRgb(180, 130, 90));
    }

    private static string BuildTitle(StoryBranchNode node)
    {
        if (node.Status == StoryBranchStatus.Root)
        {
            return node.ResultPassage.Length > 0 ? node.ResultPassage + "（起点）" : "起点";
        }
        return node.DisplayTitle;
    }

    private static string BuildSuffix(StoryBranchNode node)
    {
        if (node.Status == StoryBranchStatus.Merged && node.MergedInto.Length > 0)
        {
            return "↳ 合并到 " + node.MergedInto;
        }
        if (node.ResultPassage.Length > 0)
        {
            return "→ " + node.ResultPassage;
        }
        return "";
    }

    private void DrawBadge(DrawingContext dc, Row row, double dpi)
    {
        var node = row.Node;
        var brush = StatusBrush(node.Status);
        var rect = new Rect(row.X, row.Y - BadgeHeight / 2, BadgeWidth, BadgeHeight);
        dc.DrawRoundedRectangle(brush, null, rect, 4, 4);

        var text = MakeText(StatusText(node.Status), 10, Brushes.White, dpi, BadgeWidth);
        dc.DrawText(text, new Point(row.X + (BadgeWidth - text.WidthIncludingTrailingWhitespace) / 2,
            row.Y - text.Height / 2));
    }

    private static FormattedText MakeText(string text, double size, Brush brush, double dpi, double maxWidth)
    {
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, dpi)
        {
            MaxTextWidth = Math.Max(10, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left,
        };
    }

    private static Brush StatusBrush(StoryBranchStatus status) => status switch
    {
        StoryBranchStatus.Root => new SolidColorBrush(Color.FromRgb(14, 116, 144)),
        StoryBranchStatus.Failed => new SolidColorBrush(Color.FromRgb(153, 27, 27)),
        StoryBranchStatus.Skipped => new SolidColorBrush(Color.FromRgb(71, 85, 105)),
        StoryBranchStatus.Merged => new SolidColorBrush(Color.FromRgb(120, 53, 15)),
        _ => new SolidColorBrush(Color.FromRgb(22, 101, 52)),
    };

    private static string StatusText(StoryBranchStatus status) => status switch
    {
        StoryBranchStatus.Root => "当前",
        StoryBranchStatus.Failed => "失败",
        StoryBranchStatus.Skipped => "跳过",
        StoryBranchStatus.Merged => "合并",
        _ => "成功",
    };
}
