using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Imaging = System.Drawing.Imaging;

namespace WinDock.Services;

sealed record WindowSnapshot(BitmapSource Image, Rect Bounds, int Pid, bool Maximized, double CornerRadius)
{
    public long Bytes => (long)Image.PixelWidth * Image.PixelHeight * 4;
}

static class WindowSnapshots
{
    // PrintWindow can wait on an unresponsive application's paint handler. Only
    // one background worker may enter it, and the UI never waits without a limit.
    // A late result is discarded; no new worker is queued behind a stalled one.
    static int busy;
    const long MaximumPixels = 12_000_000;

    public static async Task<WindowSnapshot?> Capture(nint window, int pid, CancellationToken cancellation)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return null;
        var work = Task.Run(() =>
        {
            try { return Read(window, pid); }
            catch (Exception e) when (e is ArgumentException or ExternalException or InvalidOperationException) { return null; }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        try { return await work.WaitAsync(TimeSpan.FromMilliseconds(180), cancellation); }
        catch (TimeoutException) { return null; }
    }

    static WindowSnapshot? Read(nint window, int pid)
    {
        if (!WindowMotionNative.SameWindow(window, pid) || Native.IsIconic(window)
            || WindowMotionNative.IsHungAppWindow(window)
            || (WindowMotionNative.GetWindowDisplayAffinity(window, out var affinity) && affinity != 0)
            || !Native.GetWindowRect(window, out var outer)) return null;
        var frame = WindowMotionNative.Frame(window);
        if (frame.IsEmpty || outer.Width < 64 || outer.Height < 48
            || (long)outer.Width * outer.Height > MaximumPixels) return null;
        var crop = Rect.Intersect(frame, new Rect(outer.Left, outer.Top, outer.Width, outer.Height));
        if (crop.Width < 64 || crop.Height < 48) return null;

        using var bitmap = new Drawing.Bitmap(outer.Width, outer.Height, Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            // A marker distinguishes an untouched print target from valid dark UI.
            graphics.Clear(Drawing.Color.FromArgb(255, 1, 2, 3));
            var dc = graphics.GetHdc();
            try { if (!WindowMotionNative.PrintWindow(window, dc, 2 /* PW_RENDERFULLCONTENT */)) return null; }
            finally { graphics.ReleaseHdc(dc); }
        }
        if (!WindowMotionNative.SameWindow(window, pid) || Native.IsIconic(window)
            || WindowMotionNative.Frame(window) != frame) return null;

        var width = (int)crop.Width;
        var height = (int)crop.Height;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var locked = bitmap.LockBits(new Drawing.Rectangle(0, 0, outer.Width, outer.Height),
            Imaging.ImageLockMode.ReadOnly, Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var x = (int)crop.Left - outer.Left;
            var y = (int)crop.Top - outer.Top;
            for (var row = 0; row < height; row++)
                Marshal.Copy(locked.Scan0 + (y + row) * locked.Stride + x * 4, pixels, row * stride, stride);
        }
        finally { bitmap.UnlockBits(locked); }

        // Protected and unsupported GPU surfaces can report success but return a
        // uniform image. Reject those rather than displaying a black rectangle.
        var min = 255;
        var max = 0;
        for (var y = height / 5; y < height * 4 / 5; y += Math.Max(1, height / 23))
        for (var x = width / 8; x < width * 7 / 8; x += Math.Max(1, width / 23))
        {
            var p = y * stride + x * 4;
            var light = (pixels[p] + pixels[p + 1] + pixels[p + 2]) / 3;
            min = Math.Min(min, light);
            max = Math.Max(max, light);
        }
        if (max - min < 3) return null;
        // PrintWindow often leaves alpha zero even though RGB is valid.
        for (var p = 3; p < pixels.Length; p += 4) pixels[p] = 255;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        var maximized = WindowMotionNative.IsZoomed(window);
        var corner = 0.0;
        if (!maximized && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            && Native.DwmGetWindowAttribute(window, 33, out var preference, sizeof(int)) == 0 && preference != 1)
            corner = (preference == 3 ? 4 : 8) * Native.DpiScale(Native.MonitorFromWindow(window, 2));
        return new WindowSnapshot(image, crop, pid, maximized, corner);
    }
}
