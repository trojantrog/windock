using System.Runtime.InteropServices;

namespace WinDock.Services;

internal static class TrayRegistrationProbe
{
    // A stalled Explorer can leave a synchronous shell query pending. Keep one
    // actual call in flight across sessions; cancellation never spawns unbounded
    // replacement calls or blocks the WPF/Explorer window thread.
    static readonly SemaphoreSlim gate = new(1, 1);

    internal static async Task<bool> ExistsAsync(nint window, uint pid, uint id, Guid guid,
        nint explorerWindow, uint explorerPid, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                bool Matches()
                {
                    Native.GetWindowThreadProcessId(window, out var owner);
                    Native.GetWindowThreadProcessId(explorerWindow, out var shell);
                    return !cancellation.IsCancellationRequested && window != 0 && pid != 0 && owner == pid
                        && Native.IsWindow(window) && explorerPid != 0 && shell == explorerPid
                        && Native.FindWindow("Shell_TrayWnd", null) == explorerWindow;
                }
                if (!Matches()) return false;
                var identifier = new Identifier { Size = Marshal.SizeOf<Identifier>(), Window = window, Id = id, Guid = guid };
                var result = Shell_NotifyIconGetRect(ref identifier, out _);
                // Qt documents S_FALSE for registered but hidden Windows 10
                // icons. Neither successful code tells us the callback/version.
                return (result is 0 or 1) && Matches();
            }, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ArgumentException or COMException)
        {
            return false;
        }
        finally { gate.Release(); }
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
