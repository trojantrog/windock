using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WinDock.Services;

namespace WinDock.Views;

// Original deformation: each row narrows towards the icon with a smooth spatial
// weight, then the whole sheet is transported into the icon. Texture coordinates
// remain attached to the sheet. No KWin or unlicensed Dock code is incorporated.
sealed class WindowMotionSurface : Window
{
    const int Rows = 64, Columns = 4;
    readonly MeshGeometry3D mesh = new();
    readonly Viewport3D viewport = new() { IsHitTestVisible = false, ClipToBounds = false };
    readonly Rect source, target, desktop;
    nint handle;
    bool closed;

    public WindowMotionSurface(WindowSnapshot snapshot, Rect anchor)
    {
        source = snapshot.Bounds;
        target = anchor;
        desktop = Rect.Union(source, target);
        desktop.Inflate(3, 3);
        if (desktop.Width > 8192 || desktop.Height > 8192 || desktop.Width * desktop.Height > 24_000_000)
            throw new InvalidOperationException("Animation area exceeds the renderer budget.");
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        IsHitTestVisible = false;
        Content = viewport;
        Opacity = 0;

        var indices = new Int32Collection(Rows * Columns * 6);
        var uv = new PointCollection((Rows + 1) * (Columns + 1));
        for (var row = 0; row <= Rows; row++)
        for (var column = 0; column <= Columns; column++)
        {
            uv.Add(new Point((double)column / Columns, (double)row / Rows));
            if (row == Rows || column == Columns) continue;
            var i = row * (Columns + 1) + column;
            indices.Add(i); indices.Add(i + Columns + 1); indices.Add(i + 1);
            indices.Add(i + 1); indices.Add(i + Columns + 1); indices.Add(i + Columns + 2);
        }
        indices.Freeze();
        uv.Freeze();
        mesh.TriangleIndices = indices;
        mesh.TextureCoordinates = uv;
        var imageBounds = new Rect(0, 0, snapshot.Image.PixelWidth, snapshot.Image.PixelHeight);
        var drawing = new DrawingGroup
        {
            ClipGeometry = new RectangleGeometry(imageBounds, snapshot.CornerRadius, snapshot.CornerRadius)
        };
        drawing.Children.Add(new ImageDrawing(snapshot.Image, imageBounds));
        drawing.Freeze();
        var texture = new DrawingBrush(drawing) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = imageBounds };
        RenderOptions.SetBitmapScalingMode(texture, BitmapScalingMode.HighQuality);
        texture.Freeze();
        var material = new EmissiveMaterial(texture);
        material.Freeze();
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
        viewport.Children.Add(new ModelVisual3D { Content = model });
        viewport.Camera = new OrthographicCamera(
            new Point3D(desktop.Width / 2, -desktop.Height / 2, 10),
            new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), desktop.Width)
        { NearPlaneDistance = 1, FarPlaneDistance = 20 };
        RenderOptions.SetEdgeMode(viewport, EdgeMode.Unspecified);
        RenderOptions.SetBitmapScalingMode(viewport, BitmapScalingMode.HighQuality);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        handle = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE,
            (nint)(style | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        HwndSource.FromHwnd(handle)?.AddHook(Hook);
        Place();
    }

    nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x84 /* WM_NCHITTEST */) { handled = true; return -1; }
        if (message == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return 3; }
        return 0;
    }

    void Place()
    {
        // All geometry, including PointToScreen icon anchors, stays in physical
        // pixels. Only the WPF host size is converted, using its current DPI.
        var dpi = VisualTreeHelper.GetDpi(this);
        Width = desktop.Width / dpi.DpiScaleX;
        Height = desktop.Height / dpi.DpiScaleY;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, (int)desktop.Left, (int)desktop.Top,
            (int)Math.Ceiling(desktop.Width), (int)Math.Ceiling(desktop.Height), Native.SWP_NOACTIVATE);
    }

    public async Task ShowFrame(bool minimized, CancellationToken cancellation)
    {
        Draw(minimized ? 1 : 0);
        Show();
        await Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded, cancellation);
        Opacity = 1;
        // Yield through a render turn before handing the real window off.
        await NextFrame(cancellation);
        await NextFrame(cancellation);
    }

    async Task NextFrame(CancellationToken cancellation)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) => ready.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { await ready.Task.WaitAsync(TimeSpan.FromMilliseconds(120), cancellation); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    public async Task HoldForHandoff()
    {
        try
        {
            await NextFrame(CancellationToken.None);
            await NextFrame(CancellationToken.None);
        }
        catch (TimeoutException) { }
    }

    public async Task Animate(bool initiallyMinimized, Func<bool> minimized, Func<bool> valid,
        int durationMilliseconds, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var start = initiallyMinimized ? 1.0 : 0.0;
        var current = start;
        var velocity = 0.0;
        var targetValue = minimized() ? 1.0 : 0.0;
        var began = 0.0;
        var duration = durationMilliseconds / 1000.0;
        var initialVelocity = 0.0;
        var previousTime = 0.0;
        var previousValue = current;
        EventHandler handler = (_, _) =>
        {
            try
            {
                if (closed || cancellation.IsCancellationRequested || !valid() || !WindowTransitions.WindowsAnimations)
                { done.TrySetCanceled(); return; }
                var now = clock.Elapsed.TotalSeconds;
                var next = minimized() ? 1.0 : 0.0;
                if (next != targetValue)
                {
                    start = current;
                    targetValue = next;
                    began = now;
                    duration = durationMilliseconds / 1000.0 * Math.Max(0.45, Math.Abs(targetValue - current));
                    // Retarget from the current position and velocity; never
                    // restart from a full-size screenshot on a repeated click.
                    initialVelocity = Math.Clamp(velocity, -4, 4);
                }
                var t = Math.Clamp((now - began) / duration, 0, 1);
                var t2 = t * t;
                var t3 = t2 * t;
                current = Math.Clamp((2 * t3 - 3 * t2 + 1) * start
                    + (t3 - 2 * t2 + t) * duration * initialVelocity
                    + (-2 * t3 + 3 * t2) * targetValue, 0, 1);
                if (now > previousTime) velocity = (current - previousValue) / (now - previousTime);
                previousTime = now;
                previousValue = current;
                Draw(current);
                if (t >= 1) done.TrySetResult();
            }
            catch (Exception e) { done.TrySetException(e); }
        };
        CompositionTarget.Rendering += handler;
        try { await done.Task.WaitAsync(TimeSpan.FromMilliseconds(950), cancellation); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    static double Smooth(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    void Draw(double progress)
    {
        var travel = Smooth((progress - 0.10) / 0.90);
        var pinch = Smooth(progress / 0.58);
        var positions = new Point3DCollection((Rows + 1) * (Columns + 1));
        for (var row = 0; row <= Rows; row++)
        {
            var v = (double)row / Rows;
            var pull = pinch * v * v;
            var narrow = pull + travel * (1 - pull);
            var center = (source.Left + source.Width / 2) * (1 - narrow) + (target.Left + target.Width / 2) * narrow;
            var width = source.Width * (1 - narrow) + target.Width * narrow;
            var y = (source.Top + source.Height * v) * (1 - travel) + (target.Top + target.Height * v) * travel;
            for (var column = 0; column <= Columns; column++)
                positions.Add(new Point3D(center + width * ((double)column / Columns - 0.5) - desktop.Left,
                    -(y - desktop.Top), 0));
        }
        mesh.Positions = positions;
        viewport.Opacity = 1 - Smooth((progress - 0.93) / 0.07);
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        viewport.Children.Clear();
        base.OnClosed(e);
    }
}
