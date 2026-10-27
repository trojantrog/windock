using System.Runtime.InteropServices;
using System.Windows;

namespace WinDock.Services;

// The motion path only uses documented Win32 APIs. In particular, it does not
// change SPI_SETANIMATION or the write-only DWM transition policy of another app.
static class WindowMotionNative
{
    internal const long Layered = 0x80000;
    internal const string LeaseProperty = "WinDock.WindowMotion.Lease.v1";
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

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32.dll")]
    internal static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")]
    internal static extern bool GetLayeredWindowAttributes(nint window, out uint color, out byte alpha, out uint flags);
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

    internal static bool CanAnimate(nint window, bool allowMinimized, out int pid)
    {
        pid = 0;
        if (!Native.IsWindow(window) || !Native.IsWindowVisible(window) || !IsWindowEnabled(window) || IsHungAppWindow(window)
            || Native.GetAncestor(window, Native.GA_ROOT) != window
            || (!allowMinimized && Native.IsIconic(window))) return false;
        Native.GetWindowThreadProcessId(window, out pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        var style = (long)Native.GetWindowLongPtr(window, Native.GWL_EXSTYLE);
        // Existing layered/per-pixel windows own their alpha and redirection state.
        if ((style & (Layered | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
            | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT)) != 0) return false;
        if (GetWindowDisplayAffinity(window, out var affinity) && affinity != 0) return false;
        if (Native.DwmGetWindowAttribute(window, 14, out var cloak, sizeof(int)) != 0 || cloak != 0) return false;
        if (Native.ClassOf(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow") return false;
        return Native.IsIconic(window) || !IsFullscreen(window);
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
        if (!Native.IsWindow(window)) return;
        if (minimized) Native.ShowWindowAsync(window, Native.SW_MINIMIZE);
        else if (Native.IsIconic(window))
        {
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            var maximize = GetWindowPlacement(window, ref placement) && (placement.Flags & 2 /* WPF_RESTORETOMAXIMIZED */) != 0;
            Native.ShowWindowAsync(window, maximize ? 3 /* SW_SHOWMAXIMIZED */ : Native.SW_RESTORE);
        }
    }
}
