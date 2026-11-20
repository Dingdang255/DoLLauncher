using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DoLLauncher.Controls;

/// <summary>
/// 拖动复制光标（箭头 + 右下虚线框 + 加号）。WPF 没有内置该光标，
/// 这里用矢量绘制 32x32 图，再按 .cur 格式拼成字节流交给 <see cref="Cursor"/>。
/// </summary>
internal static class DragCopyCursor
{
    private static Cursor? _cursor;
    private static MemoryStream? _stream;

    public static Cursor Get()
    {
        if (_cursor != null) return _cursor;
        try
        {
            _stream = BuildCursorStream();
            _cursor = new Cursor(_stream);
        }
        catch
        {
            _cursor = Cursors.Arrow;
        }
        return _cursor;
    }

    private static MemoryStream BuildCursorStream()
    {
        const int size = 32;
        const int hotspotX = 1;
        const int hotspotY = 1;

        var visual = new DrawingVisual();
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
        {
            // 箭头
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(new Point(1, 1), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(1, 21), true, false);
                ctx.LineTo(new Point(6, 16), true, false);
                ctx.LineTo(new Point(9, 24), true, false);
                ctx.LineTo(new Point(12, 22), true, false);
                ctx.LineTo(new Point(9, 15), true, false);
                ctx.LineTo(new Point(16, 15), true, false);
            }
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), arrow);

            // 右下角的虚线框
            dc.DrawRectangle(null, new Pen(Brushes.Black, 1)
            {
                DashStyle = new DashStyle(new double[] { 2, 2 }, 0),
            }, new Rect(16, 17, 14, 14));

            // 虚线框中间的加号（白芯黑边）
            var plusPen = new Pen(Brushes.Black, 3);
            var corePen = new Pen(Brushes.White, 1);
            dc.DrawLine(plusPen, new Point(18, 24), new Point(28, 24));
            dc.DrawLine(plusPen, new Point(23, 19), new Point(23, 29));
            dc.DrawLine(corePen, new Point(18, 24), new Point(28, 24));
            dc.DrawLine(corePen, new Point(23, 19), new Point(23, 29));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            var andStride = (size + 31) / 32 * 4;
            var imageSize = 40 + stride * size + andStride * size;

            // ICONDIR + ICONDIRENTRY（type=2 表示光标）
            writer.Write((short)0);
            writer.Write((short)2);
            writer.Write((short)1);
            writer.Write((byte)size);
            writer.Write((byte)size);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)hotspotX);
            writer.Write((short)hotspotY);
            writer.Write(imageSize);
            writer.Write(22);

            // BITMAPINFOHEADER：高度翻倍（XOR 图 + AND 掩码）
            writer.Write(40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(stride * size);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            // XOR 位图自下而上
            for (var y = size - 1; y >= 0; y--) writer.Write(pixels, y * stride, stride);
            // AND 掩码全 0（完全由 alpha 通道决定）
            writer.Write(new byte[andStride * size]);
        }

        stream.Position = 0;
        return stream;
    }
}
