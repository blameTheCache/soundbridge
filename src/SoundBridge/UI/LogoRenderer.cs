using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundBridge.UI;

/// <summary>
/// Draws the SoundBridge mark: two arches over a point. One drawing serves the app icon and the tray icon,
/// so they cannot drift apart. Also used by tools/IconGen to produce Assets/app.ico.
/// </summary>
internal static class LogoRenderer
{
    private static readonly Color AccentStart = Color.FromRgb(0x7F, 0xE3, 0xCF);
    private static readonly Color AccentEnd = Color.FromRgb(0x5E, 0x9B, 0xF6);

    public static BitmapSource RenderAppIcon(int size)
    {
        double s = size;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new LinearGradientBrush(Color.FromRgb(0x27, 0x2F, 0x3B), Color.FromRgb(0x0F, 0x13, 0x18), new Point(0, 0), new Point(1, 1));
            dc.DrawRoundedRectangle(background, null, new Rect(s * 0.03, s * 0.03, s * 0.94, s * 0.94), s * 0.22, s * 0.22);

            var accent = new LinearGradientBrush(AccentStart, AccentEnd, new Point(0, 0), new Point(1, 0));
            var center = new Point(s * 0.5, s * 0.67);
            var stroke = Math.Max(s * 0.085, 1.6);

            DrawArch(dc, accent, center, s * 0.27, stroke, 1.0);
            DrawArch(dc, accent, center, s * 0.135, stroke, 0.6);
            dc.DrawEllipse(new SolidColorBrush(AccentStart), null, center, stroke * 0.62, stroke * 0.62);
        }

        return Rasterize(visual, size);
    }

    /// <summary>Monochrome mark for the notification area; the centre point carries the connection status colour.</summary>
    public static BitmapSource RenderTrayIcon(int size, Color foreground, Color status)
    {
        double s = size;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new SolidColorBrush(foreground);
            var center = new Point(s * 0.5, s * 0.74);

            DrawArch(dc, brush, center, s * 0.39, Math.Max(s * 0.125, 1.5), 1.0);
            dc.DrawEllipse(new SolidColorBrush(status), null, center, s * 0.16, s * 0.16);
        }

        return Rasterize(visual, size);
    }

    public static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Builds a multi-resolution .ico with PNG-compressed frames.</summary>
    public static byte[] BuildAppIco(params int[] sizes)
    {
        var frames = sizes.Select(size => (Size: size, Png: EncodePng(RenderAppIcon(size)))).ToArray();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);

        var offset = 6 + (16 * frames.Length);
        foreach (var frame in frames)
        {
            // A dimension byte of 0 means 256.
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
            writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)frame.Png.Length);
            writer.Write((uint)offset);
            offset += frame.Png.Length;
        }

        foreach (var frame in frames)
        {
            writer.Write(frame.Png);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void DrawArch(DrawingContext dc, Brush brush, Point center, double radius, double thickness, double opacity)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X - radius, center.Y), isFilled: false, isClosed: false);
            context.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushOpacity(opacity);
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    private static BitmapSource Rasterize(Visual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
