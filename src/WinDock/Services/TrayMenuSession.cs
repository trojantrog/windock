using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinDock.Services;

sealed class TrayMenuSession : IDisposable
{
    readonly Window source;
    readonly nint callback;
    readonly uint callbackThread;
    readonly HashSet<nint> baseline, popups = [];
    readonly HashSet<uint> threads = [];
    nint menuOwner;
    long deadline, lastOpen;
    bool sent, seen, disposed;
    public uint ProcessId { get; }
    public bool EscapePressed { get; private set; }
    public bool PreserveSource { get; private set; }

    public TrayMenuSession(Window source, SystemTrayItem item)
    {
        this.source = source; callback = item.Window; ProcessId = item.ProcessId;
        callbackThread = Native.GetWindowThreadProcessId(callback, out _);
        threads.Add(callbackThread);
        baseline = VisiblePopups();
        deadline = Environment.TickCount64 + 1200;
    }

    public void CompleteRequest() { sent = true; deadline = Environment.TickCount64 + 900; }
    public void SetEscapeIntent(bool value) => EscapePressed = value;

    public void Observe(uint ev, nint window, uint thread)
    {
        if (disposed) return;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (pid != ProcessId && !threads.Contains(thread)) return;
        threads.Add(thread);
        if (ev is 4 or 6) { EscapePressed = false; seen = true; lastOpen = Environment.TickCount64; }
        if (ev is 5 or 7 && (GetAsyncKeyState(0x1B) & 0x8000) != 0) EscapePressed = true;
    }

    public bool AllowsForeground(nint window)
    {
        if (window == 0 || window == new WindowInteropHelper(source).Handle) return true;
        Native.GetWindowThreadProcessId(window, out var pid);
        return pid == ProcessId || (pid == Environment.ProcessId && PopupInteraction.For(source).HasMenu);
    }

    public bool Update(bool managedMenu)
    {
        if (disposed) return false;
        var now = Environment.TickCount64;
        Native.GetWindowThreadProcessId(callback, out var callbackPid);
        if (!Native.IsWindow(callback) || callbackPid != ProcessId)
        {
            PreserveSource = source.IsActive;
            return false;
        }
        if (!sent && !PointerPressed()) CompleteRequest();
        popups.Clear();
        foreach (var popup in VisiblePopups())
            if (!baseline.Contains(popup)) popups.Add(popup);
        var foreground = Native.GetForegroundWindow();
        var foregroundThread = Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (foregroundPid == ProcessId) threads.Add(foregroundThread);
        var nativeMenu = false;
        foreach (var thread in threads.Where(value => value != 0))
        {
            var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(thread, ref info) || (info.Flags & 0x1C) == 0) continue;
            nativeMenu = true; menuOwner = info.MenuOwner;
        }
        if (managedMenu || nativeMenu || popups.Count > 0)
        {
            seen = true; lastOpen = now; return true;
        }
        if (!sent || now < (seen ? lastOpen + 160 : deadline)) return true;
        PreserveSource = source.IsActive || foreground == 0 || !Native.IsWindowVisible(foreground)
            || foreground == callback || (menuOwner != 0 && foreground == menuOwner);
        return false;
    }

    public bool Contains(Native.Point point)
    {
        var window = Native.GetAncestor(WindowFromPoint(point), Native.GA_ROOT);
        if (window == 0) return false;
        if (Application.Current.Windows.OfType<Window>().Any(owner => new WindowInteropHelper(owner).Handle == window)) return false;
        Native.GetWindowThreadProcessId(window, out var pid);
        return pid == ProcessId && (popups.Contains(window) || IsPopup(window) && !baseline.Contains(window));
    }

    HashSet<nint> VisiblePopups()
    {
        var own = Application.Current.Windows.OfType<Window>().Select(window => new WindowInteropHelper(window).Handle).ToHashSet();
        var result = new HashSet<nint>();
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window) || own.Contains(window)) return true;
            Native.GetWindowThreadProcessId(window, out var pid);
            if (pid == ProcessId && IsPopup(window)) result.Add(window);
            return true;
        }, 0);
        return result;
    }

    static bool IsPopup(nint window)
    {
        var name = Native.ClassOf(window);
        if (name == "#32768") return true;
        if (name.Contains("tooltip", StringComparison.OrdinalIgnoreCase)) return false;
        var style = (long)Native.GetWindowLongPtr(window, -16);
        var extended = (long)Native.GetWindowLongPtr(window, Native.GWL_EXSTYLE);
        return (style & 0x80000000L) != 0 && (style & 0x00C00000L) == 0 && (extended & Native.WS_EX_APPWINDOW) == 0;
    }

    public void Dispose() { disposed = true; popups.Clear(); }

    static bool PointerPressed() => (GetAsyncKeyState(1) & 0x8000) != 0 || (GetAsyncKeyState(2) & 0x8000) != 0
        || (GetAsyncKeyState(4) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Native.Rect CaretRect;
    }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Native.Point point);
}
