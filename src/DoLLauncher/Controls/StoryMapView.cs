using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DoLLauncher.Core.Story;

namespace DoLLauncher.Controls;

/// <summary>
/// 剧情思维导图：以自绘方式展示存档中的剧情路径（存档 → 分类 → 分组 → 步骤）。
/// 支持滚轮缩放、空白处拖拽平移、分类/分组折叠，以及按标题高亮定位。
/// 所有颜色来自依赖属性（XAML 用 <c>DynamicResource</c> 绑定）或主题资源查找，不写死颜色。
/// </summary>
public sealed class StoryMapView : FrameworkElement
{
    private const double ContentPadding = 24;
    private const double LevelGap = 190;
    private const double MaxNodeWidth = 168;
    private const double MinNodeWidth = 92;
    private const double PadX = 10;
    private const double PadY = 6;
    private const double RowSpacing = 14;
    private const double MinScale = 0.25;
    private const double MaxScale = 3.0;

    private static readonly StoryCategory[] CategoryOrder =
    {
        StoryCategory.Character,
        StoryCategory.Place,
        StoryCategory.Event,
        StoryCategory.Unknown,
    };

    /// <summary>节点背景画刷。</summary>
    public static readonly DependencyProperty NodeBackgroundProperty = DependencyProperty.Register(
        nameof(NodeBackground), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>节点边框画刷。</summary>
    public static readonly DependencyProperty NodeBorderBrushProperty = DependencyProperty.Register(
        nameof(NodeBorderBrush), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>节点文字画刷。</summary>
    public static readonly DependencyProperty NodeTextBrushProperty = DependencyProperty.Register(
        nameof(NodeTextBrush), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>连线画刷。</summary>
    public static readonly DependencyProperty LinkBrushProperty = DependencyProperty.Register(
        nameof(LinkBrush), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>选中/高亮节点使用的强调色画刷。</summary>
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>导图背景画刷。</summary>
    public static readonly DependencyProperty BackgroundBrushProperty = DependencyProperty.Register(
        nameof(BackgroundBrush), typeof(Brush), typeof(StoryMapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly List<MapNode> _flat = new();
    private readonly List<MapNode> _stepNodes = new();
    private readonly List<MapNode> _allStepNodes = new();
    private readonly HashSet<string> _collapsedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<StoryCategory> _visibleCategories = new()
    {
        StoryCategory.Character,
        StoryCategory.Place,
        StoryCategory.Event,
        StoryCategory.Unknown,
    };

    private StoryPath? _path;
    private MapNode? _root;
    private Rect _contentBounds = Rect.Empty;
    private Point _offset = new(ContentPadding, ContentPadding);
    private double _scale = 1;
    private bool _needFit = true;
    private bool _dragging;
    private Point _dragStart;
    private Point _dragOffset;
    private string _selectedKey = "";
    private string _highlight = "";

    /// <summary>创建导图控件。</summary>
    public StoryMapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>点击步骤节点时触发，参数为对应的剧情步骤。</summary>
    public event EventHandler<StoryStep>? NodeSelected;

    /// <summary>节点背景画刷。</summary>
    public Brush? NodeBackground
    {
        get => (Brush?)GetValue(NodeBackgroundProperty);
        set => SetValue(NodeBackgroundProperty, value);
    }

    /// <summary>节点边框画刷。</summary>
    public Brush? NodeBorderBrush
    {
        get => (Brush?)GetValue(NodeBorderBrushProperty);
        set => SetValue(NodeBorderBrushProperty, value);
    }

    /// <summary>节点文字画刷。</summary>
    public Brush? NodeTextBrush
    {
        get => (Brush?)GetValue(NodeTextBrushProperty);
        set => SetValue(NodeTextBrushProperty, value);
    }

    /// <summary>连线画刷。</summary>
    public Brush? LinkBrush
    {
        get => (Brush?)GetValue(LinkBrushProperty);
        set => SetValue(LinkBrushProperty, value);
    }

    /// <summary>强调色画刷（选中与搜索命中）。</summary>
    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    /// <summary>背景画刷。</summary>
    public Brush? BackgroundBrush
    {
        get => (Brush?)GetValue(BackgroundBrushProperty);
        set => SetValue(BackgroundBrushProperty, value);
    }

    /// <summary>设置当前展示的剧情路径；传 null 时清空导图。</summary>
    /// <param name="path">剧情路径。</param>
    /// <param name="rules">分类规则，用于覆盖节点的分类与分组。</param>
    public void SetPath(StoryPath? path, StoryRules rules)
    {
        _path = path;
        _root = null;
        _flat.Clear();
        _stepNodes.Clear();
        _allStepNodes.Clear();
        _selectedKey = "";
        _highlight = "";

        if (path == null || path.Steps.Count == 0)
        {
            _contentBounds = Rect.Empty;
            _needFit = true;
            InvalidateVisual();
            return;
        }

        var effectiveRules = rules ?? StoryRules.Default;
        foreach (var step in path.Steps)
        {
            step.Category = StoryCategory.Unknown;
            step.GroupName = "";
            if (effectiveRules.TryClassify(step.Title, out var category, out var group))
            {
                step.Category = category;
                step.GroupName = group;
            }
        }

        BuildTree(path);
        _needFit = true;
        InvalidateVisual();
    }

    /// <summary>设置要显示的分类；集合为空时导图不显示任何节点。</summary>
    public void SetVisibleCategories(IEnumerable<StoryCategory> categories)
    {
        _visibleCategories.Clear();
        foreach (var category in categories) _visibleCategories.Add(category);

        if (_path != null) BuildTree(_path);
        InvalidateVisual();
    }

    /// <summary>按标题关键词高亮并滚动到第一个命中的步骤节点；关键词为空时清除高亮。</summary>
    /// <returns>是否命中节点。</returns>
    public bool Highlight(string keyword)
    {
        _highlight = (keyword ?? "").Trim();
        if (_highlight.Length == 0)
        {
            InvalidateVisual();
            return false;
        }

        MapNode? target = null;
        foreach (var node in _allStepNodes)
        {
            if (node.Text.Contains(_highlight, StringComparison.OrdinalIgnoreCase))
            {
                target = node;
                break;
            }
        }

        if (target == null)
        {
            InvalidateVisual();
            return false;
        }

        // 展开命中节点的所有上级，保证节点可见
        for (var parent = target.Parent; parent != null; parent = parent.Parent)
            _collapsedKeys.Remove(parent.Key);

        RebuildLayout();

        MapNode? refreshed = null;
        foreach (var node in _stepNodes)
        {
            if (ReferenceEquals(node.Step, target.Step))
            {
                refreshed = node;
                break;
            }
        }

        CenterOn(refreshed ?? target);
        InvalidateVisual();
        return true;
    }

    /// <summary>缩放并平移导图，使全部内容适应可见区域。</summary>
    public void FitToView()
    {
        if (_root == null) return;
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            _needFit = true;
            return;
        }

        FitToViewCore();
        _needFit = false;
        InvalidateVisual();
    }

    /// <summary>恢复 100% 缩放并把内容移到左上角。</summary>
    public void ResetView()
    {
        _scale = 1;
        _offset = new Point(ContentPadding, ContentPadding);
        _needFit = false;
        InvalidateVisual();
    }

    /// <summary>展开所有被折叠的分类与分组。</summary>
    public void ExpandAll()
    {
        _collapsedKeys.Clear();
        if (_root != null) RebuildLayout();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var background = BrushFor(BackgroundBrushProperty, "CardBrush", SystemColors.WindowBrush);
        dc.DrawRectangle(background, null, new Rect(RenderSize));

        if (_root == null) return;

        if (_needFit && ActualWidth > 1 && ActualHeight > 1)
        {
            _needFit = false;
            FitToViewCore();
        }

        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        dc.PushTransform(new TranslateTransform(_offset.X, _offset.Y));
        dc.PushTransform(new ScaleTransform(_scale, _scale));

        DrawLinks(dc);
        foreach (var node in _flat) DrawNode(dc, node);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_root == null) return;

        var position = e.GetPosition(this);
        var factor = e.Delta >= 0 ? 1.12 : 1 / 1.12;
        var targetScale = Math.Clamp(_scale * factor, MinScale, MaxScale);
        if (Math.Abs(targetScale - _scale) < 0.0001) return;

        var ratio = targetScale / _scale;
        _offset = new Point(
            position.X - (position.X - _offset.X) * ratio,
            position.Y - (position.Y - _offset.Y) * ratio);
        _scale = targetScale;
        InvalidateVisual();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_root == null) return;

        Focus();
        var hit = HitTestNode(ToWorld(e.GetPosition(this)));
        if (hit != null)
        {
            if (hit.IsStep && hit.Step != null)
            {
                _selectedKey = hit.Key;
                InvalidateVisual();
                NodeSelected?.Invoke(this, hit.Step);
                e.Handled = true;
                return;
            }

            if (!hit.IsRoot)
            {
                if (!_collapsedKeys.Remove(hit.Key)) _collapsedKeys.Add(hit.Key);
                RebuildLayout();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        CaptureMouse();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var position = e.GetPosition(this);
        _offset = new Point(
            _dragOffset.X + (position.X - _dragStart.X),
            _dragOffset.Y + (position.Y - _dragStart.Y));
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>建立节点树：根 → 分类 → 分组 → 步骤。</summary>
    private void BuildTree(StoryPath path)
    {
        var root = new MapNode
        {
            Text = string.IsNullOrWhiteSpace(path.SaveName) ? "存档" : path.SaveName.Trim(),
            Key = "root",
            IsRoot = true,
        };

        foreach (var category in CategoryOrder)
        {
            if (!_visibleCategories.Contains(category)) continue;

            var steps = new List<StoryStep>();
            foreach (var step in path.Steps)
            {
                if (step.Category == category) steps.Add(step);
            }

            if (steps.Count == 0) continue;

            var categoryNode = new MapNode
            {
                Text = StoryCategoryNames.ToChinese(category),
                Key = "c:" + (int)category,
                IsCategory = true,
                Category = category,
            };

            var groups = new List<string>();
            foreach (var step in steps)
            {
                var groupName = string.IsNullOrWhiteSpace(step.GroupName) ? "未分组" : step.GroupName.Trim();
                if (!groups.Contains(groupName, StringComparer.Ordinal)) groups.Add(groupName);
            }

            foreach (var groupName in groups)
            {
                var groupNode = new MapNode
                {
                    Text = groupName,
                    Key = "g:" + (int)category + "|" + groupName,
                };

                foreach (var step in steps)
                {
                    var stepGroup = string.IsNullOrWhiteSpace(step.GroupName) ? "未分组" : step.GroupName.Trim();
                    if (!string.Equals(stepGroup, groupName, StringComparison.Ordinal)) continue;

                    var title = string.IsNullOrWhiteSpace(step.Title) ? "（无标题）" : step.Title.Trim();
                    if (step.RepeatCount > 1) title += " ×" + step.RepeatCount;

                    groupNode.Children.Add(new MapNode
                    {
                        Text = title,
                        Key = "s:" + path.SlotId + ":" + step.Index,
                        IsStep = true,
                        Step = step,
                    });
                }

                if (groupNode.Children.Count > 0)
                {
                    groupNode.Parent = categoryNode;
                    categoryNode.Children.Add(groupNode);
                }
            }

            if (categoryNode.Children.Count > 0)
            {
                categoryNode.Parent = root;
                root.Children.Add(categoryNode);
            }
        }

        _root = root;
        RebuildLayout();
    }

    /// <summary>重新计算展开状态、节点尺寸与坐标。</summary>
    private void RebuildLayout()
    {
        if (_root == null) return;

        ApplyExpansion(_root);
        _flat.Clear();
        _stepNodes.Clear();
        _allStepNodes.Clear();
        MeasureTree(_root);

        var cursor = 0.0;
        Arrange(_root, 0, ref cursor);
        ComputeBounds();
    }

    private void ApplyExpansion(MapNode node)
    {
        node.Expanded = node.IsRoot || !_collapsedKeys.Contains(node.Key);
        foreach (var child in node.Children) ApplyExpansion(child);
    }

    private void MeasureTree(MapNode node)
    {
        MeasureNode(node);
        if (node.IsStep && node.Step != null) _allStepNodes.Add(node);
        foreach (var child in node.Children) MeasureTree(child);
    }

    private void MeasureNode(MapNode node)
    {
        var textBrush = BrushFor(NodeTextBrushProperty, "TextBrush", SystemColors.ControlTextBrush);
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal,
            node.IsRoot || node.IsCategory ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);

        var fontSize = node.IsRoot ? 13.0 : 12.0;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var maxTextWidth = MaxNodeWidth - PadX * 2;
        var maxTextHeight = fontSize * 1.5 * 2;

        var probe = new FormattedText(
            node.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            textBrush,
            dpi)
        {
            MaxTextWidth = maxTextWidth,
            MaxTextHeight = maxTextHeight,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        var width = Math.Min(
            MaxNodeWidth,
            Math.Max(MinNodeWidth, Math.Ceiling(probe.WidthIncludingTrailingWhitespace) + PadX * 2));

        var layout = new FormattedText(
            node.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            textBrush,
            dpi)
        {
            MaxTextWidth = Math.Max(1, width - PadX * 2),
            MaxTextHeight = maxTextHeight,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        node.Width = width;
        node.Height = Math.Max(30, Math.Ceiling(layout.Height) + PadY * 2);
        node.TextLayout = layout;
    }

    /// <summary>按深度分配 x、按叶子顺序分配 y，父节点 y 取子节点 y 的中位数。</summary>
    private void Arrange(MapNode node, int depth, ref double cursor)
    {
        node.Depth = depth;
        node.X = ContentPadding + depth * LevelGap;

        if (node.Expanded && node.Children.Count > 0)
        {
            foreach (var child in node.Children) Arrange(child, depth + 1, ref cursor);

            var centers = new List<double>();
            foreach (var child in node.Children) centers.Add(child.Y);
            centers.Sort();
            node.Y = centers.Count % 2 == 1
                ? centers[centers.Count / 2]
                : (centers[centers.Count / 2 - 1] + centers[centers.Count / 2]) / 2;
        }
        else
        {
            node.Y = cursor + node.Height / 2;
            cursor += node.Height + RowSpacing;
        }

        _flat.Add(node);
        if (node.IsStep && node.Step != null) _stepNodes.Add(node);
    }

    private void ComputeBounds()
    {
        if (_flat.Count == 0)
        {
            _contentBounds = Rect.Empty;
            return;
        }

        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var node in _flat)
        {
            minX = Math.Min(minX, node.X);
            minY = Math.Min(minY, node.Y - node.Height / 2);
            maxX = Math.Max(maxX, node.X + node.Width);
            maxY = Math.Max(maxY, node.Y + node.Height / 2);
        }

        _contentBounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    private void DrawLinks(DrawingContext dc)
    {
        var linkBrush = BrushFor(LinkBrushProperty, "BorderBrush", SystemColors.ControlBrush);
        var pen = new Pen(linkBrush, 1.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var node in _flat)
            {
                if (!node.Expanded) continue;
                foreach (var child in node.Children)
                {
                    var start = new Point(node.X + node.Width, node.Y);
                    var end = new Point(child.X, child.Y);
                    var middle = (start.X + end.X) / 2;
                    context.BeginFigure(start, false, false);
                    context.QuadraticBezierTo(
                        new Point(middle, (start.Y + end.Y) / 2),
                        end,
                        true,
                        false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawNode(DrawingContext dc, MapNode node)
    {
        var background = BrushFor(NodeBackgroundProperty, "CardBrush", SystemColors.WindowBrush);
        var border = BrushFor(NodeBorderBrushProperty, "BorderBrush", SystemColors.ControlDarkBrush);
        var accent = BrushFor(AccentBrushProperty, "AccentBrush", SystemColors.HighlightBrush);

        var selected = node.IsStep && string.Equals(node.Key, _selectedKey, StringComparison.Ordinal);
        var matched = node.IsStep && _highlight.Length > 0 &&
                      node.Text.Contains(_highlight, StringComparison.OrdinalIgnoreCase);

        var pen = new Pen(selected || matched ? accent : border, selected ? 2 : 1);
        var rect = new Rect(node.X, node.Y - node.Height / 2, node.Width, node.Height);

        dc.DrawRoundedRectangle(background, pen, rect, 7, 7);

        if (node.TextLayout != null)
        {
            dc.DrawText(node.TextLayout, new Point(rect.X + PadX, rect.Y + PadY));
        }
    }

    private void FitToViewCore()
    {
        if (_contentBounds.IsEmpty || ActualWidth <= 1 || ActualHeight <= 1)
        {
            _scale = 1;
            _offset = new Point(ContentPadding, ContentPadding);
            return;
        }

        const double margin = 24;
        var scaleX = (ActualWidth - margin * 2) / _contentBounds.Width;
        var scaleY = (ActualHeight - margin * 2) / _contentBounds.Height;
        _scale = Math.Clamp(Math.Min(scaleX, scaleY), MinScale, 1.0);

        _offset = new Point(
            (ActualWidth - _contentBounds.Width * _scale) / 2 - _contentBounds.X * _scale,
            (ActualHeight - _contentBounds.Height * _scale) / 2 - _contentBounds.Y * _scale);
    }

    private void CenterOn(MapNode node)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1) return;
        _offset = new Point(
            ActualWidth / 2 - (node.X + node.Width / 2) * _scale,
            ActualHeight / 2 - node.Y * _scale);
    }

    private Point ToWorld(Point screen) =>
        new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private MapNode? HitTestNode(Point world)
    {
        for (var i = _flat.Count - 1; i >= 0; i--)
        {
            var node = _flat[i];
            var top = node.Y - node.Height / 2;
            if (world.X >= node.X && world.X <= node.X + node.Width &&
                world.Y >= top && world.Y <= top + node.Height)
            {
                return node;
            }
        }

        return null;
    }

    private Brush BrushFor(DependencyProperty property, string resourceKey, Brush fallback) =>
        (Brush?)GetValue(property) ?? TryFindResource(resourceKey) as Brush ?? fallback;

    /// <summary>导图中的一个节点。</summary>
    private sealed class MapNode
    {
        public string Text = "";
        public string Key = "";
        public MapNode? Parent;
        public readonly List<MapNode> Children = new();
        public bool IsRoot;
        public bool IsCategory;
        public bool IsStep;
        public StoryCategory Category;
        public StoryStep? Step;
        public bool Expanded = true;
        public int Depth;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public FormattedText? TextLayout;
    }
}
