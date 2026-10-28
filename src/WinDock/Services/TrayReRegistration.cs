using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Services;

internal static class TrayReRegistration
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowExW(nint parent, nint after, string? className, string? title);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    public static void Request(IReadOnlyList<(nint Window, uint Pid)> known, CancellationToken cancellation)
    {
        var message = SystemTrayInterop.RegisterWindowMessage("TaskbarCreated");
        if (message == 0 || cancellation.IsCancellationRequested) return;
        // Keep the normal shell contract. The additional pass reaches hidden
        // message-only receivers, which HWND_BROADCAST explicitly excludes.
        SystemTrayInterop.SendNotifyMessageW((nint)0xFFFF, message, 0, 0);
        if (cancellation.IsCancellationRequested) return;
        var windows = new List<(nint Window, uint Pid)>();
        var broadcasted = new HashSet<nint>();
        void Collect(nint window)
        {
            if (window == 0 || windows.Count >= 4096 || cancellation.IsCancellationRequested) return;
            Native.GetWindowThreadProcessId(window, out var pid);
            if (pid != 0) windows.Add((window, (uint)pid));
        }
        Native.EnumWindows((window, _) =>
        {
            broadcasted.Add(window); Collect(window);
            return !cancellation.IsCancellationRequested && windows.Count < 2048;
        }, 0);
        nint previous = 0;
        var enumerated = new HashSet<nint>();
        for (var count = 0; count < 2048 && !cancellation.IsCancellationRequested; count++)
        {
            var window = FindWindowExW((nint)(-3) /* HWND_MESSAGE */, previous, null, null);
            if (window == 0 || !enumerated.Add(window)) break;
            previous = window; Collect(window);
        }
        var registered = TrayApps.Registered();
        var pids = known.Select(owner => owner.Pid).ToHashSet();
        pids.Add((uint)Environment.ProcessId);
        foreach (var pid in windows.Select(owner => owner.Pid).Distinct())
        {
            if (cancellation.IsCancellationRequested) return;
            if (pids.Contains(pid)) continue;
            var process = Native.OpenProcess(0x1000, false, (int)pid);
            if (process == 0) continue;
            try
            {
                var path = new StringBuilder(32768); var size = path.Capacity;
                if (Native.QueryFullProcessImageName(process, 0, path, ref size) && registered.Contains(path.ToString())) pids.Add(pid);
            }
            finally { Native.CloseHandle(process); }
        }
        var sent = new HashSet<nint>();
        foreach (var (window, pid) in known.Concat(windows))
        {
            if (cancellation.IsCancellationRequested || sent.Count >= 256) break;
            if (broadcasted.Contains(window) || !pids.Contains(pid) || !sent.Add(window)) continue;
            Native.GetWindowThreadProcessId(window, out var actual);
            if (actual == pid && Native.IsWindow(window)) PostMessageW(window, message, 0, 0);
        }
    }

    public static void RequestOwner(nint window, uint pid)
    {
        Native.GetWindowThreadProcessId(window, out var actual);
        if (actual != pid || !Native.IsWindow(window)) return;
        var message = SystemTrayInterop.RegisterWindowMessage("TaskbarCreated");
        if (message != 0) PostMessageW(window, message, 0, 0);
    }
}
