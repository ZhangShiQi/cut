using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;

namespace Cut.Services;

public sealed record MonitorSnapshot(PixelRect Bounds, BitmapSource Image);

public sealed class DesktopSnapshot
{
    public IReadOnlyList<MonitorSnapshot> Monitors { get; }
    public PixelRect Bounds { get; }
    public DesktopSnapshot(IReadOnlyList<MonitorSnapshot> monitors)
    {
        if (monitors.Count == 0) throw new InvalidOperationException("没有可用的显示器。");
        Monitors = monitors;
        int x = monitors.Min(m => m.Bounds.X), y = monitors.Min(m => m.Bounds.Y);
        Bounds = new(x, y, monitors.Max(m => m.Bounds.Right) - x, monitors.Max(m => m.Bounds.Bottom) - y);
    }

    public BitmapSource Crop(PixelRect selection)
    {
        var area = selection.Intersect(Bounds);
        if (area.IsEmpty) throw new InvalidOperationException("请先拖拽选择截图区域。");
        // Bgr32 makes gaps between nonrectangular monitor arrangements opaque black.
        var result = new WriteableBitmap(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null);
        foreach (var monitor in Monitors)
        {
            var part = area.Intersect(monitor.Bounds);
            if (part.IsEmpty) continue;
            int stride = checked(part.Width * 4);
            var pixels = new byte[checked(stride * part.Height)];
            monitor.Image.CopyPixels(new Int32Rect(part.X - monitor.Bounds.X, part.Y - monitor.Bounds.Y, part.Width, part.Height), pixels, stride, 0);
            result.WritePixels(new Int32Rect(part.X - area.X, part.Y - area.Y, part.Width, part.Height), pixels, stride, 0);
        }
        result.Freeze();
        return result;
    }
}

public static class CaptureService
{
    public static DesktopSnapshot Capture()
    {
        var snapshots = new List<MonitorSnapshot>();
        foreach (var rect in NativeMethods.Monitors())
        {
            using var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var screenDc = NativeMethods.GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) throw new Win32Exception();
                try
                {
                    var targetDc = graphics.GetHdc();
                    try
                    {
                        // Graphics.CopyFromScreen rejects combined raster-operation flags.
                        if (!NativeMethods.BitBlt(targetDc, 0, 0, rect.Width, rect.Height, screenDc, rect.X, rect.Y, 0x40CC0020))
                            throw new Win32Exception();
                    }
                    finally { graphics.ReleaseHdc(targetDc); }
                }
                finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }
            }
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            try
            {
                var source = BitmapSource.Create(rect.Width, rect.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, checked(data.Stride * data.Height), data.Stride);
                source.Freeze();
                snapshots.Add(new(rect, source));
            }
            finally { bitmap.UnlockBits(data); }
        }
        return new(snapshots);
    }
}
