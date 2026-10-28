using System.Runtime.InteropServices;
using System.Windows;

namespace WinDock.Services;

// The motion path only uses documented Win32 APIs. In particular, it does not
// change SPI_SETANIMATION or the write-only DWM transition policy of another app.
static class WindowMotionNative
{
    internal const string LeaseProperty = "WinDock.WindowMotion.Lease.v1";
    internal const string SurfaceProperty = "WinDock.WindowMotion.Surface.v1";
    internal const string SnapshotProperty = "WinDock.WindowMotion.Snapshot.v1";
    internal const string OperationProperty = "WinDock.WindowMotion.Operation.v1";

    [StructLayout(LayoutKind.Sequential)]
    struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Native.Point Minimum, Maximum;
        public Native.Rect Normal;
    }

    [DllImport("user32.dll")]
    static extern bool GetWindowPlacement(nint window, ref Placement placement);

    sealed record StateRequest(int Pid, bool Minimized, bool Maximized, long Until);
    static readonly object stateLock = new();
    static readonly Dictionary<nint, StateRequest> stateRequests = [];

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")]
    internal static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(nint window);
    [DllImport("gdi32.dll")]
    static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")]
    static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")]
    internal static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    static extern int GetFrameBounds(nint window, int attribute, out Native.Rect rect, int size);
    [DllImport("dwmapi.dll")]
    static extern int DwmFlush();
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeServerProcessId(nint pipe, out uint pid);
    [DllImport("kernel32.dll")]
    internal static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject")]
    internal static extern uint ProcessWait(nint process, uint milliseconds);
    [DllImport("user32.dll")]
    internal static extern bool IsWindowEnabled(nint window);

    internal static bool SameWindow(nint window, int pid) =>
        Native.IsWindow(window) && Native.GetWindowThreadProcessId(window, out var current) != 0 && current == pid;

    static readonly object presentationLock = new();
    static Task<bool>? presentation;

    internal static async Task<bool> FlushPresentation(CancellationToken cancellation)
    {
        Task<bool> pending;
        lock (presentationLock)
        {
            // Do not queue more workers if the display driver has stopped
            // presenting. Callers retain their original window in that case.
            if (presentation is { IsCompleted: false }) return false;
            pending = presentation = Task.Run(() => DwmFlush() == 0);
        }
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(100), cancellation); }
        catch (TimeoutException) { return false; }
    }

    internal static bool CanAnimate(nint window, bool allowMinimized, out int pid)
    {
        pid = 0;
        if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || !IsWindowEnabled(window) || IsHungAppWindow(window)
            || Native.GetAncestor(window, Native.GA_ROOT) != window
            || (!allowMinimized && Native.IsIconic(window))) return false;
        Native.GetWindowThreadProcessId(window, out pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        var style = (long)Native.GetWindowLongPtr(window, Native.GWL_EXSTYLE);
        // Do not commandeer tool/popup or always-on-top surfaces. Existing
        // layered alpha is left untouched; WGC carries the composed alpha.
        if ((style & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT)) != 0) return false;
        if (GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) return false;
        if (Native.DwmGetWindowAttribute(window, 14, out var cloak, sizeof(int)) != 0 || cloak != 0) return false;
        if (Native.ClassOf(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow") return false;
        return RegionKind(window) == 0 && (Native.IsIconic(window) || !IsFullscreen(window));
    }

    // ERROR (0) means no explicit region; existing custom regions are excluded.
    // The caller must also validate the HWND and permission/identity properties.
    internal static int RegionKind(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return -1;
        try { return GetWindowRgn(window, region); }
        finally { DeleteObject(region); }
    }

    internal static bool Mask(nint window)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return false;
        if (SetWindowRgn(window, region, false) != 0) return true;
        // A successful SetWindowRgn transfers ownership to Windows. Only the
        // failed call leaves the HRGN owned by this process.
        DeleteObject(region);
        return false;
    }

    internal static bool IsFullscreen(nint window)
    {
        if (!Native.GetWindowRect(window, out var rect)) return true;
        var monitor = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(Native.MonitorFromWindow(window, 2), ref monitor)) return true;
        var fillsMonitor = rect.Left <= monitor.Monitor.Left + 2 && rect.Top <= monitor.Monitor.Top + 2
            && rect.Right >= monitor.Monitor.Right - 2 && rect.Bottom >= monitor.Monitor.Bottom - 2;
        var hasCaption = ((long)Native.GetWindowLongPtr(window, -16) & 0x00C00000L) == 0x00C00000L;
        return fillsMonitor && (!IsZoomed(window) || !hasCaption);
    }

    internal static Rect Frame(nint window)
    {
        if (GetFrameBounds(window, 9, out var rect, Marshal.SizeOf<Native.Rect>()) != 0
            && !Native.GetWindowRect(window, out rect)) return Rect.Empty;
        return rect.Width > 0 && rect.Height > 0 ? new Rect(rect.Left, rect.Top, rect.Width, rect.Height) : Rect.Empty;
    }

    internal static void SetState(nint window, bool minimized)
    {
        lock (stateLock)
        {
            if (!Native.IsWindow(window) || Native.GetWindowThreadProcessId(window, out var pid) == 0) return;
            var now = Environment.TickCount64;
            foreach (var key in stateRequests.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) stateRequests.Remove(key);
            stateRequests.TryGetValue(window, out var pending);
            if (pending is not null && pending.Pid != pid) { stateRequests.Remove(window); pending = null; }
            var iconic = Native.IsIconic(window);
            if (pending is not null && iconic == pending.Minimized)
            {
                stateRequests.Remove(window);
                pending = null;
            }
            if (pending?.Minimized == minimized) return;
            if (pending is null && iconic == minimized) return;
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            var hasPlacement = GetWindowPlacement(window, ref placement);
            var maximize = IsZoomed(window) || (hasPlacement && (placement.ShowCommand == 3 || (placement.Flags & 2) != 0))
                || (pending is { Minimized: true, Maximized: true });
            var command = minimized ? Native.SW_MINIMIZE : maximize ? 3 : Native.SW_RESTORE;
            if (Native.ShowWindowAsync(window, command))
                stateRequests[window] = new(pid, minimized, maximize, now + 1500);
        }
    }

    internal static void ObserveState(nint window, bool minimized)
    {
        lock (stateLock)
            if (stateRequests.TryGetValue(window, out var request) && request.Minimized == minimized
                && Native.IsIconic(window) == minimized)
                stateRequests.Remove(window);
    }
}
