using System.Runtime.InteropServices;

namespace WinDock.Services;

// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendmessagecallbackw
// The callback marks message processing complete; a modal save prompt may still
// disable the owner. No request retains a preview window, control, or GCHandle.
internal static class WindowCloseRequest
{
    const int MaximumPending = 128;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void Completion(nint window, uint message, nuint request, nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageCallbackW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SendMessageCallback(nint window, uint message, nuint wparam, nint lparam, Completion callback, nuint request);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowEnabled(nint window);

    static readonly object Gate = new();
    static readonly Dictionary<nuint, AppWindow> Pending = [];
    static readonly Completion Completed = OnCompleted;
    static nuint sequence;

    public static bool CanClose(AppWindow window)
    {
        if (!window.IsCurrent || window.ProcessId == Environment.ProcessId || !IsWindowEnabled(window.Handle)) return false;
        lock (Gate)
        {
            PruneLocked();
            return Pending.Count < MaximumPending && !Pending.Values.Any(pending => SameWindow(pending, window));
        }
    }

    public static bool TryClose(AppWindow window)
    {
        if (!window.IsCurrent || window.ProcessId == Environment.ProcessId || !IsWindowEnabled(window.Handle)) return false;
        nuint request;
        lock (Gate)
        {
            PruneLocked();
            // Hung receivers must not grow the table indefinitely or become
            // eligible for another close merely because a timer elapsed.
            if (Pending.Count >= MaximumPending || Pending.Values.Any(pending => SameWindow(pending, window))) return false;
            do { request = unchecked(++sequence); } while (request == 0 || Pending.ContainsKey(request));
            Pending.Add(request, window);
        }
        var sent = false;
        try
        {
            // Reject a stale identity or a newly disabled owner immediately before
            // sending. The static delegate remains valid even after a preview hides.
            sent = window.IsCurrent && IsWindowEnabled(window.Handle)
                && SendMessageCallback(window.Handle, Native.WM_CLOSE, 0, 0, Completed, request);
            return sent;
        }
        finally
        {
            if (!sent) lock (Gate) Pending.Remove(request);
        }
    }

    static void OnCompleted(nint window, uint message, nuint request, nint result)
    {
        lock (Gate)
            if (message == Native.WM_CLOSE && Pending.TryGetValue(request, out var pending) && pending.Handle == window)
                Pending.Remove(request);
    }

    public static void Prune()
    {
        lock (Gate) PruneLocked();
    }

    static void PruneLocked()
    {
        foreach (var request in Pending.ToArray())
            if (!request.Value.IsCurrent) Pending.Remove(request.Key);
    }

    static bool SameWindow(AppWindow first, AppWindow second) => first.Handle == second.Handle
        && first.ProcessId == second.ProcessId && first.ThreadId == second.ThreadId && first.WindowClass == second.WindowClass;
}
