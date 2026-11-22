using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DoLLauncher.Services;

namespace DoLLauncher.Controls;

/// <summary>
/// 主题边饰：把一张 PNG / SVG 按九宫格切分，四角固定 <c>slice</c> 像素、四边平铺，覆盖在内容四周。
/// SVG 会先栅格化成 1024px 位图再切分；加载失败返回 false，不影响其他样式。
/// </summary>
public sealed class SkinOrnamentBorder : Grid
{
    private const int DefaultSlice = 24;

    public SkinOrnamentBorder()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Visibility = Visibility.Collapsed;
        Panel.SetZIndex(this, 1000);
    }

    /// <summary>加载边饰图片并生成九宫格；<paramref name="imagePath"/> 为完整文件路径。</summary>
    public bool Apply(string imagePath, int slice)
    {
        Clear();
        if (string.IsNullOrWhiteSpace(imagePath)) return false;

        ImageSource image;
        try
        {
            if (!File.Exists(imagePath)) return false;
            if (SkinSvgRenderer.IsSvg(imagePath))
            {
                if (!SkinSvgRenderer.TryLoadImageSource(imagePath, 1024, out var svg) || svg == null)
                    return false;
                image = svg;
            }
            else
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception ex)
        {
            LogService.Error("读取美化包边饰图片失败", ex);
            return false;
        }

        // Viewbox 使用绝对模式时单位为 DIP，这里同样用 DIP 尺寸切分
        var width = image.Width;
        var height = image.Height;
        if (width <= 0 || height <= 0) return false;

        double size = slice > 0 ? slice : DefaultSlice;
        size = Math.Min(size, Math.Min(width, height) / 2);
        if (size < 1) return false;

        var tileWidth = Math.Max(1, Math.Min(size, width - size * 2));
        var tileHeight = Math.Max(1, Math.Min(size, height - size * 2));

        RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(size) });

        for (var i = 0; i < 3; i++)
            ColumnDefinitions.Add(i == 1
                ? new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                : new ColumnDefinition { Width = new GridLength(size) });

        // 四角
        AddPiece(image, new Rect(0, 0, size, size), 0, 0);
        AddPiece(image, new Rect(width - size, 0, size, size), 0, 2);
        AddPiece(image, new Rect(0, height - size, size, size), 2, 0);
        AddPiece(image, new Rect(width - size, height - size, size, size), 2, 2);

        // 四边用平铺画刷
        AddTiled(image, new Rect(size, 0, tileWidth, size), 0, 1,
            new Rect(0, 0, tileWidth, size));
        AddTiled(image, new Rect(size, height - size, tileWidth, size), 2, 1,
            new Rect(0, 0, tileWidth, size));
        AddTiled(image, new Rect(0, size, size, tileHeight), 1, 0,
            new Rect(0, 0, size, tileHeight));
        AddTiled(image, new Rect(width - size, size, size, tileHeight), 1, 2,
            new Rect(0, 0, size, tileHeight));

        Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>移除全部边饰并隐藏。</summary>
    public void Clear()
    {
        Children.Clear();
        RowDefinitions.Clear();
        ColumnDefinitions.Clear();
        Visibility = Visibility.Collapsed;
    }

    private void AddPiece(ImageSource image, Rect viewbox, int row, int column)
    {
        var brush = new ImageBrush(image)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = viewbox,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();

        var rectangle = new Rectangle { Fill = brush, IsHitTestVisible = false };
        SetRow(rectangle, row);
        SetColumn(rectangle, column);
        Children.Add(rectangle);
    }

    private void AddTiled(ImageSource image, Rect viewbox, int row, int column, Rect viewport)
    {
        var brush = new ImageBrush(image)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = viewbox,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = viewport,
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();

        var rectangle = new Rectangle { Fill = brush, IsHitTestVisible = false };
        SetRow(rectangle, row);
        SetColumn(rectangle, column);
        Children.Add(rectangle);
    }
}
