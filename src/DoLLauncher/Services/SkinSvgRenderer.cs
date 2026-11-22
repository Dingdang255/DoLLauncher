using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace DoLLauncher.Services;

/// <summary>
/// 受限 SVG 渲染器：只解析静态图形子集，用 WPF Geometry 绘制后栅格化成位图。
/// 不执行脚本、不加载外部资源、不支持渐变 / 滤镜 / 文本；任何失败都只记日志并返回 false。
/// </summary>
public static class SkinSvgRenderer
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxElements = 5000;
    private const int MaxPathChars = 100_000;
    private const int MaxDepth = 100;
    private const int CacheCapacity = 16;

    private static readonly Regex TransformRegex = new(
        @"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object CacheLock = new();
    private static readonly List<CacheEntry> Cache = new();

    private readonly record struct CacheEntry(string Path, long Ticks, int Size, BitmapSource Image);

    private struct SvgStyle
    {
        public string? Fill;
        public string? Stroke;
        public double StrokeWidth;
        public double FillOpacity;
        public double StrokeOpacity;
        public FillRule FillRule;
        public PenLineCap LineCap;
        public PenLineJoin LineJoin;

        public static SvgStyle CreateDefault() => new()
        {
            Fill = "black",
            Stroke = null,
            StrokeWidth = 1,
            FillOpacity = 1,
            StrokeOpacity = 1,
            FillRule = FillRule.Nonzero,
            LineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Miter,
        };
    }

    private sealed class RenderContext
    {
        private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
        private int _warnings;

        public void WarnOnce(string key, string message)
        {
            if (_warnings >= 6 || !_warned.Add(key)) return;
            _warnings++;
            LogService.Append("SVG：" + message);
        }
    }

    /// <summary>路径是否为 .svg 文件。</summary>
    public static bool IsSvg(string path)
    {
        var text = (path ?? string.Empty).Trim();
        return text.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把 SVG 栅格化为边长 <paramref name="pixelSize"/> 的透明背景位图；失败返回 false。</summary>
    public static bool TryLoadImageSource(string path, int pixelSize, out ImageSource? source)
    {
        source = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return false;

            var size = Math.Clamp(pixelSize, 16, 2048);
            var ticks = File.GetLastWriteTimeUtc(full).Ticks;

            lock (CacheLock)
            {
                foreach (var item in Cache)
                {
                    if (item.Ticks != ticks || item.Size != size) continue;
                    if (!item.Path.Equals(full, StringComparison.OrdinalIgnoreCase)) continue;
                    source = item.Image;
                    return true;
                }
            }

            if (new FileInfo(full).Length > MaxFileBytes)
            {
                LogService.Append("SVG 文件超过 2MB，已跳过：" + Path.GetFileName(full));
                return false;
            }

            if (!TryRender(full, size, out var bitmap)) return false;

            lock (CacheLock)
            {
                Cache.RemoveAll(item =>
                    item.Size == size && item.Path.Equals(full, StringComparison.OrdinalIgnoreCase));
                Cache.Add(new CacheEntry(full, ticks, size, bitmap));
                while (Cache.Count > CacheCapacity) Cache.RemoveAt(0);
            }

            source = bitmap;
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("SVG 渲染失败", ex);
            return false;
        }
    }

    private static bool TryRender(string fullPath, int size, out BitmapSource bitmap)
    {
        bitmap = null!;

        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return false;

        var ctx = new RenderContext();
        GuardDocument(root, ctx);

        var group = new DrawingGroup();
        Walk(root, SvgStyle.CreateDefault(), group, ctx, 0);

        var viewBox = GetViewBox(root);
        var stretch = (Attr(root, "preserveAspectRatio") ?? string.Empty)
            .Trim().StartsWith("none", StringComparison.OrdinalIgnoreCase);

        // 位图按 pixelSize 栅格化，但 DPI 按 SVG 自身尺寸换算：
        // 画面坐标保持设计尺寸（九宫格 slice 与组件尺寸才符合预期），像素密度由 DPI 提供
        var logicalWidth = TryNumber(root, "width", out var declaredWidth) && declaredWidth > 0
            ? declaredWidth
            : viewBox.Width;
        var logicalHeight = TryNumber(root, "height", out var declaredHeight) && declaredHeight > 0
            ? declaredHeight
            : viewBox.Height;
        var canvas = Math.Max(logicalWidth, logicalHeight);
        if (canvas <= 0 || double.IsNaN(canvas) || double.IsInfinity(canvas)) canvas = viewBox.Width;
        var dpi = Math.Clamp(96.0 * size / canvas, 1, 100_000);

        Matrix matrix;
        if (stretch)
        {
            var scaleX = canvas / viewBox.Width;
            var scaleY = canvas / viewBox.Height;
            matrix = new Matrix(scaleX, 0, 0, scaleY, -viewBox.X * scaleX, -viewBox.Y * scaleY);
        }
        else
        {
            var scale = Math.Min(canvas / viewBox.Width, canvas / viewBox.Height);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return false;
            var offsetX = (canvas - viewBox.Width * scale) / 2 - viewBox.X * scale;
            var offsetY = (canvas - viewBox.Height * scale) / 2 - viewBox.Y * scale;
            matrix = new Matrix(scale, 0, 0, scale, offsetX, offsetY);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(matrix));
            dc.DrawDrawing(group);
        }

        var target = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        bitmap = target;
        return true;
    }

    /// <summary>安全检查：数量上限、脚本 / 外部引用 / 危险元素。</summary>
    private static void GuardDocument(XElement root, RenderContext ctx)
    {
        var count = 0;
        foreach (var element in root.DescendantsAndSelf())
        {
            if (++count > MaxElements)
                throw new InvalidDataException($"SVG 元素数量超过 {MaxElements}");

            var name = element.Name.LocalName;
            if (name.Equals("script", StringComparison.OrdinalIgnoreCase)
                || name.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SVG 包含不允许的元素：<{name}>");

            foreach (var attribute in element.Attributes())
            {
                if (!attribute.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)) continue;
                var value = (attribute.Value ?? string.Empty).Trim();
                if (value.Length == 0 || value.StartsWith('#')) continue;
                throw new InvalidDataException("SVG 包含外部引用：" + value);
            }

            if (name.Equals("image", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Attr(element, "href")))
                throw new InvalidDataException("SVG 不允许 <image> 引用图片");
        }
    }

    private static void Walk(XElement element, SvgStyle parentStyle, DrawingGroup parent,
        RenderContext ctx, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("SVG 嵌套层级过深");

        var name = element.Name.LocalName;
        var props = CollectDeclarations(element);
        var style = ResolveStyle(props, parentStyle);
        var opacity = ParseOpacity(props, "opacity", 1);
        var transform = ParseTransform(props, ctx);

        if (name is "svg" or "g")
        {
            var group = new DrawingGroup();
            if (opacity < 1) group.Opacity = opacity;
            if (!transform.IsIdentity) group.Transform = new MatrixTransform(transform);
            parent.Children.Add(group);

            foreach (var child in element.Elements())
                Walk(child, style, group, ctx, depth + 1);
            return;
        }

        if (name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
        {
            ctx.WarnOnce("element:" + name, $"不支持的元素 <{name}>，已跳过");
            return;
        }

        var geometry = BuildGeometry(element, name, ctx);
        if (geometry == null) return;

        var node = new DrawingGroup();
        if (opacity < 1) node.Opacity = opacity;
        if (!transform.IsIdentity) node.Transform = new MatrixTransform(transform);
        using (var dc = node.Open())
            DrawShape(dc, geometry, style, ctx);
        parent.Children.Add(node);
    }

    private static void DrawShape(DrawingContext dc, Geometry geometry, SvgStyle style, RenderContext ctx)
    {
        if (geometry is PathGeometry path) path.FillRule = style.FillRule;

        var fill = ParsePaint(style.Fill, style.FillOpacity, ctx);
        Pen? pen = null;
        if (style.StrokeWidth > 0)
        {
            var strokeBrush = ParsePaint(style.Stroke, style.StrokeOpacity, ctx);
            if (strokeBrush != null)
            {
                pen = new Pen(strokeBrush, style.StrokeWidth)
                {
                    StartLineCap = style.LineCap,
                    EndLineCap = style.LineCap,
                    DashCap = style.LineCap,
                    LineJoin = style.LineJoin,
                };
                pen.Freeze();
            }
        }

        if (fill == null && pen == null) return;
        dc.DrawGeometry(fill, pen, geometry);
    }

    private static SvgStyle ResolveStyle(Dictionary<string, string> props, SvgStyle parent)
    {
        var style = parent;

        if (TryValue(props, "fill", out var fill)) style.Fill = fill;
        if (TryValue(props, "stroke", out var stroke)) style.Stroke = stroke;

        if (TryValue(props, "stroke-width", out var width)
            && TryParseNumber(width, out var widthValue) && widthValue >= 0)
            style.StrokeWidth = widthValue;

        if (TryValue(props, "fill-opacity", out var fillOpacity)
            && TryParseOpacity(fillOpacity, out var fillValue))
            style.FillOpacity = fillValue;

        if (TryValue(props, "stroke-opacity", out var strokeOpacity)
            && TryParseOpacity(strokeOpacity, out var strokeValue))
            style.StrokeOpacity = strokeValue;

        if (TryValue(props, "fill-rule", out var fillRule))
            style.FillRule = fillRule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
                ? FillRule.EvenOdd
                : FillRule.Nonzero;

        if (TryValue(props, "stroke-linecap", out var lineCap))
            style.LineCap = lineCap.Trim().ToLowerInvariant() switch
            {
                "round" => PenLineCap.Round,
                "square" => PenLineCap.Square,
                _ => PenLineCap.Flat,
            };

        if (TryValue(props, "stroke-linejoin", out var lineJoin))
            style.LineJoin = lineJoin.Trim().ToLowerInvariant() switch
            {
                "round" => PenLineJoin.Round,
                "bevel" => PenLineJoin.Bevel,
                _ => PenLineJoin.Miter,
            };

        return style;
    }

    private static Dictionary<string, string> CollectDeclarations(XElement element)
    {
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            props[attribute.Name.LocalName] = attribute.Value;
        }

        var styleText = Attr(element, "style");
        if (string.IsNullOrWhiteSpace(styleText)) return props;

        foreach (var part in styleText.Split(';'))
        {
            var index = part.IndexOf(':');
            if (index <= 0) continue;
            var key = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (key.Length > 0) props[key] = value;
        }
        return props;
    }

    private static Brush? ParsePaint(string? text, double opacity, RenderContext ctx)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;

        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            ctx.WarnOnce("paint:url", "不支持渐变 / 图案引用，已跳过：" + value);
            return null;
        }

        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) value = "white";
        if (!SkinColors.TryParse(value, out var color))
        {
            ctx.WarnOnce("paint:" + value, "无法识别的颜色：" + value);
            return null;
        }

        var alpha = (byte)Math.Round(Math.Clamp(color.A / 255.0 * opacity, 0, 1) * 255);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static Geometry? BuildGeometry(XElement element, string name, RenderContext ctx)
    {
        switch (name)
        {
            case "path":
            {
                var data = Attr(element, "d");
                if (string.IsNullOrWhiteSpace(data)) return null;
                if (data.Length > MaxPathChars)
                    throw new InvalidDataException($"SVG path 长度超过 {MaxPathChars}");
                try
                {
                    return Geometry.Parse(data);
                }
                catch (Exception ex)
                {
                    ctx.WarnOnce("path:parse", "path 解析失败，已跳过：" + ex.Message);
                    return null;
                }
            }

            case "rect":
            {
                var width = Number(element, "width");
                var height = Number(element, "height");
                if (width <= 0 || height <= 0) return null;

                var x = Number(element, "x");
                var y = Number(element, "y");
                var rx = TryNumber(element, "rx", out var parsedRx) ? parsedRx : -1;
                var ry = TryNumber(element, "ry", out var parsedRy) ? parsedRy : -1;
                if (rx < 0 && ry >= 0) rx = ry;
                if (ry < 0 && rx >= 0) ry = rx;
                if (rx < 0) rx = ry = 0;

                rx = Math.Min(rx, width / 2);
                ry = Math.Min(ry, height / 2);
                return new RectangleGeometry(new Rect(x, y, width, height), rx, ry);
            }

            case "circle":
            {
                var radius = Number(element, "r");
                if (radius <= 0) return null;
                return new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), radius, radius);
            }

            case "ellipse":
            {
                var rx = Number(element, "rx");
                var ry = Number(element, "ry");
                if (rx <= 0 || ry <= 0) return null;
                return new EllipseGeometry(new Point(Number(element, "cx"), Number(element, "cy")), rx, ry);
            }

            case "line":
                return new LineGeometry(
                    new Point(Number(element, "x1"), Number(element, "y1")),
                    new Point(Number(element, "x2"), Number(element, "y2")));

            case "polyline":
            case "polygon":
            {
                var points = ParsePoints(Attr(element, "points"));
                if (points.Count < 2) return null;

                var figure = new PathFigure
                {
                    StartPoint = points[0],
                    IsClosed = name == "polygon",
                    IsFilled = true,
                };
                figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));

                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                return geometry;
            }

            default:
                return null;
        }
    }

    private static List<Point> ParsePoints(string? text)
    {
        var points = new List<Point>();
        if (string.IsNullOrWhiteSpace(text)) return points;

        var tokens = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            if (!TryParseNumber(tokens[i], out var x) || !TryParseNumber(tokens[i + 1], out var y)) break;
            points.Add(new Point(x, y));
        }
        return points;
    }

    private static Matrix ParseTransform(Dictionary<string, string> props, RenderContext ctx)
    {
        if (!props.TryGetValue("transform", out var text) || string.IsNullOrWhiteSpace(text))
            return Matrix.Identity;

        var operations = new List<Matrix>();
        foreach (Match match in TransformRegex.Matches(text))
        {
            var name = match.Groups[1].Value;
            var args = match.Groups[2].Value
                .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var numbers = new List<double>();
            foreach (var arg in args)
            {
                if (!TryParseNumber(arg, out var number)) break;
                numbers.Add(number);
            }

            switch (name.ToLowerInvariant())
            {
                case "translate" when numbers.Count is 1 or 2:
                {
                    var matrix = Matrix.Identity;
                    matrix.Translate(numbers[0], numbers.Count > 1 ? numbers[1] : 0);
                    operations.Add(matrix);
                    break;
                }
                case "scale" when numbers.Count is 1 or 2:
                {
                    var matrix = Matrix.Identity;
                    matrix.Scale(numbers[0], numbers.Count > 1 ? numbers[1] : numbers[0]);
                    operations.Add(matrix);
                    break;
                }
                case "rotate" when numbers.Count is 1 or 3:
                {
                    var matrix = Matrix.Identity;
                    if (numbers.Count == 3) matrix.RotateAt(numbers[0], numbers[1], numbers[2]);
                    else matrix.Rotate(numbers[0]);
                    operations.Add(matrix);
                    break;
                }
                case "matrix" when numbers.Count == 6:
                    operations.Add(new Matrix(numbers[0], numbers[1], numbers[2],
                        numbers[3], numbers[4], numbers[5]));
                    break;
                case "skewx":
                case "skewy":
                    ctx.WarnOnce("transform:" + name, $"不支持 transform 函数 {name}，已忽略");
                    break;
            }
        }

        // SVG 的 transform 列表按列向量左乘，WPF Matrix 用行向量组合，因此逆序追加
        var result = Matrix.Identity;
        for (var i = operations.Count - 1; i >= 0; i--)
            result.Append(operations[i]);
        return result;
    }

    private static double ParseOpacity(Dictionary<string, string> props, string key, double fallback)
    {
        if (!props.TryGetValue(key, out var text)) return fallback;
        return TryParseOpacity(text, out var value) ? value : fallback;
    }

    private static bool TryParseOpacity(string? text, out double value)
    {
        value = 1;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var raw = text.Trim();
        var percent = raw.EndsWith('%');
        if (percent) raw = raw[..^1];
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (percent) parsed /= 100;
        value = Math.Clamp(parsed, 0, 1);
        return true;
    }

    private static Rect GetViewBox(XElement root)
    {
        var raw = Attr(root, "viewBox");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && TryParseNumber(parts[0], out var x) && TryParseNumber(parts[1], out var y)
                && TryParseNumber(parts[2], out var width) && TryParseNumber(parts[3], out var height)
                && width > 0 && height > 0)
                return new Rect(x, y, width, height);
        }

        if (TryNumber(root, "width", out var fallbackWidth) && TryNumber(root, "height", out var fallbackHeight)
            && fallbackWidth > 0 && fallbackHeight > 0)
            return new Rect(0, 0, fallbackWidth, fallbackHeight);

        return new Rect(0, 0, 100, 100);
    }

    private static string? Attr(XElement element, string name)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            if (attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return attribute.Value;
        }
        return null;
    }

    private static bool TryValue(Dictionary<string, string> props, string key, out string value)
    {
        value = string.Empty;
        if (!props.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;
        if (raw.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)) return false;
        value = raw;
        return true;
    }

    private static bool TryNumber(XElement element, string name, out double value)
    {
        value = 0;
        return TryParseNumber(Attr(element, name), out value);
    }

    private static double Number(XElement element, string name) =>
        TryNumber(element, name, out var value) ? value : 0;

    private static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var raw = text.Trim();
        if (raw.EndsWith("px", StringComparison.OrdinalIgnoreCase)) raw = raw[..^2];
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
