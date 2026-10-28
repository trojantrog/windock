using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WinDock.Services;

sealed class TrayMenuPlacement
{
    static readonly SemaphoreSlim queryGate = new(1, 1);
    readonly Task<Native.Rect?> query;
    readonly Native.Rect anchor;
    readonly uint processId;
    readonly long expires;
    bool attempted;

    TrayMenuPlacement(SystemTrayItem item, Native.Rect anchor)
    {
        this.anchor = anchor; processId = item.ProcessId; expires = Environment.TickCount64 + 1200;
        var identifier = new Identifier { Size = Marshal.SizeOf<Identifier>(), Window = item.Window, Id = item.Id, Guid = item.Guid };
        query = Task.Run(() => Query(identifier));
    }

    public static TrayMenuPlacement? Create(SystemTrayItem item, FrameworkElement element)
    {
        if (item.Window == 0 || item.ProcessId == 0 || !element.IsVisible || PresentationSource.FromVisual(element) is null) return null;
        var explorer = SystemTrayInterop.ExplorerTray();
        if (explorer.Window == 0 || explorer.Process == 0 || explorer.Process == item.ProcessId) return null;
        try
        {
            var first = element.PointToScreen(new Point());
            var last = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            if (!InRange(first.X) || !InRange(first.Y) || !InRange(last.X) || !InRange(last.Y)) return null;
            var rect = new Native.Rect { Left = (int)Math.Round(first.X), Top = (int)Math.Round(first.Y),
                Right = (int)Math.Round(last.X), Bottom = (int)Math.Round(last.Y) };
            return ValidIcon(rect) ? new TrayMenuPlacement(item, rect) : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    static Native.Rect? Query(Identifier identifier)
    {
        if (!queryGate.Wait(0)) return null;
        try
        {
            return Shell_NotifyIconGetRect(ref identifier, out var rect) == 0 && ValidIcon(rect) ? rect : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException or COMException)
        {
            return null;
        }
        finally { queryGate.Release(); }
    }

    public void TryPlace(nint window, nint callback, uint callbackThread, nint menuOwner)
    {
        if (Environment.TickCount64 > expires || attempted || callbackThread == 0 || !OwnedBy(menuOwner, callback)
            || !query.IsCompletedSuccessfully || query.Result is not { } origin
            || Native.ClassOf(window) != "#32768" || !Native.IsWindowVisible(window)) return;
        var thread = Native.GetWindowThreadProcessId(window, out var pid);
        if (pid != processId || thread != callbackThread || !Native.GetWindowRect(window, out var menu) || !Near(menu, origin) || Near(menu, anchor)) return;
        var width = (long)menu.Right - menu.Left; var height = (long)menu.Bottom - menu.Top;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return;
        var center = new Native.Point { X = anchor.Left + anchor.Width / 2, Y = anchor.Top + anchor.Height / 2 };
        var monitor = Native.MonitorFromPoint(center, 2);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var work = info.Work;
        if ((long)work.Right - work.Left < width || (long)work.Bottom - work.Top < height) return;
        var dx = (long)center.X - ((long)origin.Left + origin.Right) / 2;
        var dy = (long)center.Y - ((long)origin.Top + origin.Bottom) / 2;
        var x = (int)Math.Clamp((long)menu.Left + dx, work.Left, (long)work.Right - width);
        var y = (int)Math.Clamp((long)menu.Top + dy, work.Top, (long)work.Bottom - height);
        attempted = true;
        if ((x != menu.Left || y != menu.Top) && Environment.TickCount64 <= expires && Native.IsWindow(window)
            && Native.IsWindowVisible(window) && Native.GetWindowThreadProcessId(window, out var actual) == callbackThread && actual == processId)
            Native.SetWindowPos(window, 0, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4 | 0x200 | 0x4000);
    }

    static bool ValidIcon(Native.Rect rect) => (long)rect.Right - rect.Left is > 0 and <= 2048
        && (long)rect.Bottom - rect.Top is > 0 and <= 2048;

    static bool InRange(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;

    static bool Near(Native.Rect menu, Native.Rect icon) => (long)menu.Left <= (long)icon.Right + 32
        && (long)menu.Right >= (long)icon.Left - 32 && (long)menu.Top <= (long)icon.Bottom + 32
        && (long)menu.Bottom >= (long)icon.Top - 32;

    static bool OwnedBy(nint owner, nint callback)
    {
        for (var depth = 0; owner != 0 && depth < 16; ++depth, owner = Native.GetWindow(owner, Native.GW_OWNER))
            if (owner == callback) return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Identifier
    {
        public int Size;
        public nint Window;
        public uint Id;
        public Guid Guid;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int Shell_NotifyIconGetRect(ref Identifier identifier, out Native.Rect location);
}
