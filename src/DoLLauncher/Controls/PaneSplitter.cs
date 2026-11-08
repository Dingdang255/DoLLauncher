using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DoLLauncher.Services;

namespace DoLLauncher.Controls;

public enum SplitAxis
{
    Columns,
    Rows,
}

/// <summary>
/// 通用分栏拖动手柄：放在 Grid 的间隙列 / 行里，自动判断相邻两个定义该改哪一侧。
/// 一侧是 * 时只改另一侧的像素尺寸，窗口缩放仍然自适应；两侧都固定时同时调整两侧。
/// 尺寸在拖动结束后写入配置，启动时按 SettingKey 恢复。
/// </summary>
public sealed class PaneSplitter : Thumb
{
    public static readonly DependencyProperty AxisProperty = DependencyProperty.Register(
        nameof(Axis), typeof(SplitAxis), typeof(PaneSplitter),
        new PropertyMetadata(SplitAxis.Columns, (d, _) => ((PaneSplitter)d).UpdateShape()));

    public static readonly DependencyProperty SettingKeyProperty = DependencyProperty.Register(
        nameof(SettingKey), typeof(string), typeof(PaneSplitter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DefaultSizeProperty = DependencyProperty.Register(
        nameof(DefaultSize), typeof(double), typeof(PaneSplitter), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MinPreviousProperty = DependencyProperty.Register(
        nameof(MinPrevious), typeof(double), typeof(PaneSplitter), new PropertyMetadata(120.0));

    public static readonly DependencyProperty MinNextProperty = DependencyProperty.Register(
        nameof(MinNext), typeof(double), typeof(PaneSplitter), new PropertyMetadata(120.0));

    private Border? _bar;
    private double _lastPrimary = double.NaN;

    public PaneSplitter()
    {
        Focusable = false;
        DragDelta += OnDragDelta;
        DragCompleted += OnDragCompleted;
        Loaded += (_, _) => ApplySavedOrDefault();
        UpdateShape();
    }

    public SplitAxis Axis
    {
        get => (SplitAxis)GetValue(AxisProperty);
        set => SetValue(AxisProperty, value);
    }

    public string SettingKey
    {
        get => (string)GetValue(SettingKeyProperty);
        set => SetValue(SettingKeyProperty, value);
    }

    public double DefaultSize
    {
        get => (double)GetValue(DefaultSizeProperty);
        set => SetValue(DefaultSizeProperty, value);
    }

    public double MinPrevious
    {
        get => (double)GetValue(MinPreviousProperty);
        set => SetValue(MinPreviousProperty, value);
    }

    public double MinNext
    {
        get => (double)GetValue(MinNextProperty);
        set => SetValue(MinNextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bar = GetTemplateChild("PART_Bar") as Border;
        UpdateShape();
    }

    private void UpdateShape()
    {
        Cursor = Axis == SplitAxis.Columns ? Cursors.SizeWE : Cursors.SizeNS;
        if (_bar == null) return;
        if (Axis == SplitAxis.Columns)
        {
            _bar.Width = 2;
            _bar.Height = double.NaN;
            _bar.HorizontalAlignment = HorizontalAlignment.Center;
            _bar.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            _bar.Width = double.NaN;
            _bar.Height = 2;
            _bar.HorizontalAlignment = HorizontalAlignment.Stretch;
            _bar.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private bool Resolve(out Grid grid, out ColumnDefinition? prevColumn, out ColumnDefinition? nextColumn,
        out RowDefinition? prevRow, out RowDefinition? nextRow)
    {
        grid = null!;
        prevColumn = nextColumn = null;
        prevRow = nextRow = null;
        if (Parent is not Grid parent) return false;

        var index = Axis == SplitAxis.Columns ? Grid.GetColumn(this) : Grid.GetRow(this);
        var count = Axis == SplitAxis.Columns ? parent.ColumnDefinitions.Count : parent.RowDefinitions.Count;
        if (index <= 0 || index >= count - 1) return false;

        if (Axis == SplitAxis.Columns)
        {
            prevColumn = parent.ColumnDefinitions[index - 1];
            nextColumn = parent.ColumnDefinitions[index + 1];
        }
        else
        {
            prevRow = parent.RowDefinitions[index - 1];
            nextRow = parent.RowDefinitions[index + 1];
        }
        grid = parent;
        return true;
    }

    private void ApplySavedOrDefault() => ApplySize(LayoutStore.Get(SettingKey) ?? DefaultSize);

    private void ApplySize(double size)
    {
        if (size <= 0) return;
        if (!Resolve(out var grid, out var prevColumn, out var nextColumn, out var prevRow, out var nextRow))
            return;

        if (Axis == SplitAxis.Columns)
        {
            var prevStar = prevColumn!.Width.IsStar;
            var nextStar = nextColumn!.Width.IsStar;
            var primaryPrevious = PrimaryIsPrevious(prevStar, nextStar);
            var available = Available(grid.ActualWidth, grid, Grid.GetColumn(this), SplitAxis.Columns);
            size = Clamp(size, primaryPrevious, available);
            if (primaryPrevious) prevColumn.Width = new GridLength(size);
            else nextColumn.Width = new GridLength(size);
            return;
        }

        var topStar = prevRow!.Height.IsStar;
        var bottomStar = nextRow!.Height.IsStar;
        var primaryTop = PrimaryIsPrevious(topStar, bottomStar);
        var availableRows = Available(grid.ActualHeight, grid, Grid.GetRow(this), SplitAxis.Rows);
        size = Clamp(size, primaryTop, availableRows);
        if (primaryTop) prevRow.Height = new GridLength(size);
        else nextRow.Height = new GridLength(size);
    }

    private static bool PrimaryIsPrevious(bool prevStar, bool nextStar) => !(prevStar && !nextStar);

    private double Available(double total, Grid grid, int gapIndex, SplitAxis axis)
    {
        if (total <= 0) return double.NaN;
        var gap = axis == SplitAxis.Columns
            ? grid.ColumnDefinitions[gapIndex].ActualWidth
            : grid.RowDefinitions[gapIndex].ActualHeight;
        return total - gap;
    }

    private double Clamp(double size, bool primaryPrevious, double available)
    {
        if (double.IsNaN(available) || available <= 0) return size;
        var min = primaryPrevious ? MinPrevious : MinNext;
        var otherMin = primaryPrevious ? MinNext : MinPrevious;
        var max = Math.Max(min, available - otherMin);
        return Math.Max(min, Math.Min(size, max));
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!Resolve(out var grid, out var prevColumn, out var nextColumn, out var prevRow, out var nextRow))
            return;

        if (Axis == SplitAxis.Columns)
        {
            var available = Available(grid.ActualWidth, grid, Grid.GetColumn(this), SplitAxis.Columns);
            if (double.IsNaN(available) || available <= 0) return;

            var prevStar = prevColumn!.Width.IsStar;
            var nextStar = nextColumn!.Width.IsStar;
            var prev = prevColumn.ActualWidth;
            var next = nextColumn.ActualWidth;

            if (prevStar && nextStar)
            {
                var totalStar = prevColumn.Width.Value + nextColumn.Width.Value;
                if (totalStar <= 0) totalStar = 2;
                var newPrev = Math.Max(MinPrevious, Math.Min(prev + e.HorizontalChange, available - MinNext));
                var ratio = newPrev / available;
                prevColumn.Width = new GridLength(Math.Max(0.0001, totalStar * ratio), GridUnitType.Star);
                nextColumn.Width = new GridLength(Math.Max(0.0001, totalStar * (1 - ratio)), GridUnitType.Star);
                _lastPrimary = newPrev;
            }
            else if (nextStar)
            {
                var newPrev = Math.Max(MinPrevious, Math.Min(prev + e.HorizontalChange, available - MinNext));
                prevColumn.Width = new GridLength(newPrev);
                _lastPrimary = newPrev;
            }
            else if (prevStar)
            {
                var newNext = Math.Max(MinNext, Math.Min(next - e.HorizontalChange, available - MinPrevious));
                nextColumn.Width = new GridLength(newNext);
                _lastPrimary = newNext;
            }
            else
            {
                var newPrev = Math.Max(MinPrevious, Math.Min(prev + e.HorizontalChange, available - MinNext));
                prevColumn.Width = new GridLength(newPrev);
                nextColumn.Width = new GridLength(Math.Max(MinNext, available - newPrev));
                _lastPrimary = newPrev;
            }
            return;
        }

        var availableRows = Available(grid.ActualHeight, grid, Grid.GetRow(this), SplitAxis.Rows);
        if (double.IsNaN(availableRows) || availableRows <= 0) return;

        var topStar = prevRow!.Height.IsStar;
        var bottomStar = nextRow!.Height.IsStar;
        var top = prevRow.ActualHeight;
        var bottom = nextRow.ActualHeight;

        if (topStar && bottomStar)
        {
            var totalStar = prevRow.Height.Value + nextRow.Height.Value;
            if (totalStar <= 0) totalStar = 2;
            var newTop = Math.Max(MinPrevious, Math.Min(top + e.VerticalChange, availableRows - MinNext));
            var ratio = newTop / availableRows;
            prevRow.Height = new GridLength(Math.Max(0.0001, totalStar * ratio), GridUnitType.Star);
            nextRow.Height = new GridLength(Math.Max(0.0001, totalStar * (1 - ratio)), GridUnitType.Star);
            _lastPrimary = newTop;
        }
        else if (bottomStar)
        {
            var newTop = Math.Max(MinPrevious, Math.Min(top + e.VerticalChange, availableRows - MinNext));
            prevRow.Height = new GridLength(newTop);
            _lastPrimary = newTop;
        }
        else if (topStar)
        {
            var newBottom = Math.Max(MinNext, Math.Min(bottom - e.VerticalChange, availableRows - MinPrevious));
            nextRow.Height = new GridLength(newBottom);
            _lastPrimary = newBottom;
        }
        else
        {
            var newTop = Math.Max(MinPrevious, Math.Min(top + e.VerticalChange, availableRows - MinNext));
            prevRow.Height = new GridLength(newTop);
            nextRow.Height = new GridLength(Math.Max(MinNext, availableRows - newTop));
            _lastPrimary = newTop;
        }
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!double.IsNaN(_lastPrimary) && _lastPrimary > 0)
        {
            LayoutStore.Set(SettingKey, _lastPrimary);
        }
        _lastPrimary = double.NaN;
    }

    /// <summary>恢复所有分栏的默认尺寸，并清掉配置里记住的值。</summary>
    public static void RestoreDefaults(DependencyObject root)
    {
        LayoutStore.Clear();
        foreach (var splitter in Enumerate(root))
        {
            splitter.ApplySize(splitter.DefaultSize);
        }
    }

    private static IEnumerable<PaneSplitter> Enumerate(DependencyObject root)
    {
        if (root is PaneSplitter splitter) yield return splitter;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var child in Enumerate(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
    }
}
