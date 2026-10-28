using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace WinDock.Services;

sealed record WindowSnapshot(BitmapSource Image, Rect Bounds, int Pid, bool Maximized, double CornerRadius)
{
    public long Bytes => (long)Image.PixelWidth * Image.PixelHeight * 4;
}

static class WindowSnapshots
{
    const long MaximumPixels = 12_000_000;
    static int busy;

    public static async Task<WindowSnapshot?> Capture(nint window, int pid, CancellationToken cancellation, bool opening = false)
    {
        // A stalled graphics driver cannot accumulate capture workers or block
        // the dispatcher. No foreign window has been altered at this stage.
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(opening ? 140 : 300);
        var token = deadline.Token;
        var work = Task.Run(async () =>
        {
            try { return await Read(window, pid, opening, token).ConfigureAwait(false); }
            catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException
                or NotSupportedException or OperationCanceledException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Window capture unavailable: {e.GetType().Name}");
                return null;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        try { return await work.WaitAsync(TimeSpan.FromMilliseconds(opening ? 160 : 350), cancellation); }
        catch (TimeoutException) { deadline.Cancel(); return null; }
    }

    static bool Eligible(nint window, int pid) => WindowMotionNative.SameWindow(window, pid)
        && Native.IsWindowVisible(window) && !Native.IsIconic(window) && !WindowMotionNative.IsHungAppWindow(window)
        && !(WindowMotionNative.GetWindowDisplayAffinity(window, out var affinity) && affinity != 0);

    static async Task<WindowSnapshot?> Read(nint window, int pid, bool opening, CancellationToken cancellation)
    {
        if (!GraphicsCaptureSession.IsSupported() || !Eligible(window, pid)) return null;
        using var device = WindowCaptureInterop.CreateDevice();
        var item = WindowCaptureInterop.CreateItem(window);
        if (!WithinBudget(item.Size.Width, item.Size.Height)) return null;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // Keep the system capture indicator. Borderless capture requires a
        // separate consent/capability flow and is never silently enabled here.
        session.StartCapture();
        WindowSnapshot? first = null;
        long firstAt = 0;
        TimeSpan? previousStamp = null;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Eligible(window, pid)) return null;
            using var frame = pool.TryGetNextFrame();
            if (frame is null)
            {
                // Static windows need not produce another compositor frame.
                if (first is not null && Environment.TickCount64 - firstAt >= (opening ? 24 : 35)
                    && WindowMotionNative.Frame(window) == first.Bounds) return first;
                await Task.Delay(8, cancellation).ConfigureAwait(false);
                continue;
            }
            if (previousStamp.HasValue && frame.SystemRelativeTime == previousStamp) continue;
            previousStamp = frame.SystemRelativeTime;
            var visible = WindowMotionNative.Frame(window);
            if (visible.IsEmpty || !Native.GetWindowRect(window, out var outer)) return null;
            var content = frame.ContentSize;
            if (!WithinBudget(content.Width, content.Height)) return null;
            var capturedBounds = MatchBounds(content.Width, content.Height, visible, outer);
            // A resizing window can deliver an old-size surface. Never stretch
            // that surface over the new frame, or guess invisible border widths.
            if (capturedBounds.IsEmpty) continue;
            using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface,
                BitmapAlphaMode.Premultiplied).AsTask(cancellation).ConfigureAwait(false);
            if (software.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                || !WithinBudget(software.PixelWidth, software.PixelHeight)
                || software.PixelWidth < content.Width || software.PixelHeight < content.Height
                || WindowMotionNative.Frame(window) != visible) continue;
            var crop = Rect.Intersect(capturedBounds, visible);
            if (crop != visible) continue;
            var width = (int)visible.Width;
            var height = (int)visible.Height;
            var offsetX = (int)(visible.X - capturedBounds.X);
            var offsetY = (int)(visible.Y - capturedBounds.Y);
            var buffer = new byte[checked(software.PixelWidth * software.PixelHeight * 4)];
            software.CopyToBuffer(buffer.AsBuffer());
            var pixels = new byte[checked(width * height * 4)];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(buffer, ((y + offsetY) * software.PixelWidth + offsetX) * 4,
                    pixels, y * width * 4, width * 4);
            if (!HasRenderedContent(pixels, width, height, opening)) { first = null; continue; }
            // WGC supplies the real composed frame. Preserve its premultiplied
            // alpha; forcing opaque pixels creates white/black window rectangles.
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze();
            var snapshot = new WindowSnapshot(bitmap, visible, pid, WindowMotionNative.IsZoomed(window), 0);
            if (first is not null && first.Bounds == snapshot.Bounds) return snapshot;
            first = snapshot;
            firstAt = Environment.TickCount64;
        }
    }

    static bool WithinBudget(int width, int height) => width >= 64 && height >= 48
        && (long)width * height <= MaximumPixels;

    static Rect MatchBounds(int width, int height, Rect frame, Native.Rect outer)
    {
        if (frame.Width == width && frame.Height == height) return frame;
        return outer.Width == width && outer.Height == height
            ? new Rect(outer.Left, outer.Top, width, height) : Rect.Empty;
    }

    static bool HasRenderedContent(byte[] pixels, int width, int height, bool opening)
    {
        var visible = 0;
        var white = 0;
        var black = 0;
        var edges = 0;
        var occupied = 0;
        // This is a readiness filter, not an alternative to compositor capture.
        // A plain or protected frame is deliberately left to the native path.
        for (var ty = 0; ty < 4; ty++)
        for (var tx = 0; tx < 6; tx++)
        {
            var low = 255;
            var high = 0;
            for (var sy = 0; sy < 7; sy++)
            for (var sx = 0; sx < 7; sx++)
            {
                var x = Math.Clamp((int)(width * (tx + (sx + 0.5) / 7) / 6), 0, width - 3);
                var y = Math.Clamp((int)(height * (0.125 + 0.75 * (ty + (sy + 0.5) / 7) / 4)), 0, height - 1);
                var index = (y * width + x) * 4;
                if (pixels[index + 3] < 16) continue;
                var luma = (pixels[index] + pixels[index + 1] + pixels[index + 2]) / 3;
                var adjacent = (pixels[index + 8] + pixels[index + 9] + pixels[index + 10]) / 3;
                visible++;
                if (luma >= 248) white++;
                if (luma <= 4) black++;
                if (Math.Abs(luma - adjacent) > 10) edges++;
                low = Math.Min(low, luma);
                high = Math.Max(high, luma);
            }
            if (high - low > 12) occupied++;
        }
        return visible >= 6 * 4 * 49 / 5 && white < visible * 0.975 && black < visible * 0.975
            && occupied >= (opening ? 4 : 2) && edges >= (opening ? 10 : 4);
    }
}
