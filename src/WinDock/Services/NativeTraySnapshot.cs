using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinDock.Services;

internal sealed record NativeTrayRegistration(nint Window, uint ProcessId, uint ThreadId, uint Id, Guid Guid,
    string Executable, string WindowClass, long ProcessStarted, string Tip, BitmapSource? Icon, uint Preference);

internal static class NativeTraySnapshot
{
    const string ClassId = "{25DEAD04-1EAC-4911-9E3A-AD0A4AB560FD}";
    const string InterfaceId = "{D133CE13-3537-48BA-93A7-AFCD5D2053B4}";
    const string CallbackId = "{D782CCBA-AFB0-43F1-94DB-FDA3779EACCB}";
    const string ProxyId = "{C90250F3-4D7D-4991-9B69-A5C5BC1C2AE6}";
    static readonly SemaphoreSlim Worker = new(1, 1);
    static volatile bool observerUnavailable;
    static volatile string status = "not_started";
    static int callbackCount, copiedCount, validatedCount;
    internal static string Status => status;
    internal static int CallbackCount => Volatile.Read(ref callbackCount);
    internal static int CopiedCount => Volatile.Read(ref copiedCount);
    internal static int ValidatedCount => Volatile.Read(ref validatedCount);
    internal static void CallbackReceived() => Interlocked.Increment(ref callbackCount);
    internal static void RecordCopied() => Interlocked.Increment(ref copiedCount);

    // Only the cookie-based Win8+ observer is used. The older interface has a
    // single callback slot and could replace another client's subscription.
    // ABI reference: Chromium status_tray_state_changer_win.{h,cc}, commit
    // 8d2dccf401a294e106d6ec9614dead0b567f7608. No preference setters are exposed.
    [ComImport, Guid("D133CE13-3537-48BA-93A7-AFCD5D2053B4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITrayObserver
    {
        [PreserveSig] int RegisterCallback([MarshalAs(UnmanagedType.Interface)] ITraySnapshotCallback sink, out uint cookie);
        [PreserveSig] int UnregisterCallback(ref uint cookie);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out ITrayObserver observer);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoInitializeEx(nint reserved, uint model);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern void CoUninitialize();

    public static async Task<IReadOnlyList<NativeTrayRegistration>> ReadAsync(nint explorerWindow, uint explorerProcess,
        CancellationToken cancellation)
    {
        if (observerUnavailable) return [];
        await Worker.WaitAsync(cancellation).ConfigureAwait(false);
        Interlocked.Exchange(ref callbackCount, 0);
        Interlocked.Exchange(ref copiedCount, 0);
        Interlocked.Exchange(ref validatedCount, 0);
        var result = new TaskCompletionSource<IReadOnlyList<NativeTrayRegistration>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                var initialized = false;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    initialized = CoInitializeEx(0, 2) >= 0;
                    if (!initialized) { status = "apartment_unavailable"; result.TrySetResult([]); return; }
                    result.TrySetResult(Read(explorerWindow, explorerProcess, cancellation));
                }
                catch (OperationCanceledException) { status = "cancelled"; result.TrySetCanceled(cancellation); }
                catch (Exception e) when (e is COMException or Win32Exception or IOException or UnauthorizedAccessException
                    or InvalidOperationException or ArgumentException or NotSupportedException or TypeLoadException
                    or System.Security.SecurityException)
                { status = "unavailable:" + e.GetType().Name; result.TrySetResult([]); }
                finally { if (initialized) CoUninitialize(); Worker.Release(); }
            }) { IsBackground = true, Name = "Native tray inventory" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch { Worker.Release(); throw; }
        // Cancellation does not release the actual worker slot while COM is still
        // executing. A stalled shell cannot create unlimited replacement threads.
        return await result.Task.WaitAsync(cancellation).ConfigureAwait(false);
    }

    static IReadOnlyList<NativeTrayRegistration> Read(nint explorerWindow, uint explorerProcess, CancellationToken cancellation)
    {
        if (observerUnavailable) return [];
        if (!ProviderRegistered()) { status = "provider_unavailable"; return []; }
        if (!SameExplorer(explorerWindow, explorerProcess)) { status = "explorer_changed"; return []; }
        status = "reading";
        ITrayObserver? observer = null;
        using var sink = new NativeTraySnapshotCallback(cancellation);
        IReadOnlyList<NativeTraySnapshotRecord> captured = [];
        uint cookie = 0;
        var subscribed = false;
        var succeeded = false;
        try
        {
            var clsid = new Guid(ClassId); var iid = new Guid(InterfaceId);
            // Explorer already owns the local server. Do not accept an in-process
            // replacement component from an application registration.
            var created = CoCreateInstance(in clsid, 0, 4, in iid, out observer);
            if (created < 0 || observer is null) { status = $"activation_failed:0x{created:X8}"; return []; }
            cancellation.ThrowIfCancellationRequested();
            subscribed = true;
            var registered = observer.RegisterCallback(sink, out cookie);
            if (registered >= 0)
            {
                // Windows 11 may deliver the inventory after RegisterCallback
                // returns. Keep this STA dispatching until the bounded stream
                // settles, then seal and unsubscribe before any Shell queries.
                WaitForCallbacks(sink, cancellation);
                captured = sink.Finish(); succeeded = true;
            }
            else status = $"subscription_failed:0x{registered:X8}";
        }
        finally
        {
            sink.Seal();
            if (observer is not null)
            {
                try
                {
                    if (subscribed && observer.UnregisterCallback(ref cookie) < 0)
                    { observerUnavailable = true; succeeded = false; status = "unsubscribe_failed"; }
                }
                catch (COMException) { observerUnavailable = true; succeeded = false; status = "unsubscribe_failed"; }
                finally { Marshal.FinalReleaseComObject(observer); }
            }
        }
        // RegisterCallback can synchronously call us from Explorer's UI thread.
        // Never query that thread, run Shell APIs, or inspect processes inside
        // the callback. Validate the copied inventory after unsubscribing.
        try
        {
            if (!succeeded || cancellation.IsCancellationRequested || !SameExplorer(explorerWindow, explorerProcess)) return [];
            var items = new Dictionary<string, NativeTrayRegistration>();
            var ambiguous = new HashSet<string>();
            var knownFolders = new Dictionary<Guid, string?>();
            foreach (var record in captured)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = Validate(record, knownFolders);
                if (item is null) continue;
                var key = item.Guid == Guid.Empty ? $"{item.Window:X}:{item.Id}:{item.ProcessId}" : item.Guid.ToString("D");
                if (ambiguous.Contains(key)) continue;
                if (items.TryGetValue(key, out var prior) && (prior.Window != item.Window || prior.ProcessId != item.ProcessId
                    || prior.ThreadId != item.ThreadId || prior.ProcessStarted != item.ProcessStarted || prior.Id != item.Id))
                { items.Remove(key); ambiguous.Add(key); continue; }
                items[key] = item;
            }
            if (!SameExplorer(explorerWindow, explorerProcess)) { status = "explorer_changed"; return []; }
            Interlocked.Exchange(ref validatedCount, items.Count);
            status = items.Count > 0 ? "available" : CallbackCount == 0 ? "no_callbacks" : "filtered";
            return items.Values.ToArray();
        }
        finally { foreach (var record in captured) record.Dispose(); }
    }

    static void WaitForCallbacks(NativeTraySnapshotCallback sink, CancellationToken cancellation)
    {
        const long firstArrivalLimit = 1000, quietLimit = 150, totalLimit = 3000;
        var started = Environment.TickCount64;
        var lastArrival = started;
        var seen = sink.ReceivedCount;
        using var cancelled = new EventWaitHandle(false, EventResetMode.ManualReset);
        // Own this wait handle independently: the caller may dispose its CTS
        // while the real COM worker is still unwinding after cancellation.
        using var registration = cancellation.Register(() => cancelled.Set());
        nint[] handles = [cancelled.SafeWaitHandle.DangerousGetHandle()];
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var arrived = sink.ReceivedCount;
            if (arrived != seen) { seen = arrived; lastArrival = now; }
            var remaining = Math.Min(totalLimit - (now - started),
                (seen == 0 ? firstArrivalLimit : quietLimit) - (now - lastArrival));
            if (remaining <= 0) return;
            // QS_ALLINPUT / MWMO_INPUTAVAILABLE; only this worker's messages are
            // dispatched, never the WPF UI thread's queue or global input.
            var ready = MsgWaitForMultipleObjectsEx(1, handles, (uint)remaining, 0x4FF, 4);
            if (ready == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            cancellation.ThrowIfCancellationRequested();
            if (ready != 1) continue;
            while (PeekMessageW(out var message, 0, 0, 0, 1))
            {
                cancellation.ThrowIfCancellationRequested();
                if (Environment.TickCount64 - started >= totalLimit) return;
                if (message.Id == 0x12) throw new InvalidOperationException("Tray observer message loop closed.");
                TranslateMessage(ref message);
                DispatchMessageW(ref message);
            }
        }
    }

    static NativeTrayRegistration? Validate(NativeTraySnapshotRecord record, Dictionary<Guid, string?> knownFolders)
    {
        try
        {
            var thread = Native.GetWindowThreadProcessId(record.Window, out var pid);
            if (thread == 0 || thread != record.ThreadId || pid <= 0 || pid != record.ProcessId || !Native.IsWindow(record.Window)) return null;
            var handle = Native.OpenProcess(0x101000, false, pid);
            if (handle == 0) return null;
            try
            {
                var text = new StringBuilder(32768); var length = text.Capacity;
                if (WaitForSingleObject(handle, 0) != 258 || !Native.QueryFullProcessImageName(handle, 0, text, ref length)
                    || !GetProcessTimes(handle, out var started, out _, out _, out _)) return null;
                var executable = text.ToString();
                var actualPath = Normalize(executable, knownFolders);
                var registeredPath = Normalize(record.Executable, knownFolders);
                if (actualPath is null || registeredPath is null
                    || !string.Equals(actualPath, registeredPath, StringComparison.OrdinalIgnoreCase)) return null;
                var identity = new Identifier { Size = Marshal.SizeOf<Identifier>(), Window = record.Window, Id = record.Id, Guid = record.Guid };
                var rectangle = Shell_NotifyIconGetRect(ref identity, out _);
                if (rectangle is not (0 or 1) || WaitForSingleObject(handle, 0) != 258
                    || Native.GetWindowThreadProcessId(record.Window, out var current) != thread || current != pid) return null;
                return new(record.Window, (uint)pid, thread, record.Id, record.Guid, executable, Native.ClassOf(record.Window),
                    started, record.Tip, record.ReadIcon(), record.Preference);
            }
            finally { Native.CloseHandle(handle); }
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or COMException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    static string? Normalize(string path, Dictionary<Guid, string?> knownFolders)
    {
        if (path.Length is 0 or > 32768) return null;
        if (path[0] == '{')
        {
            if (path.Length <= 38 || path[38] is not ('\\' or '/')
                || !Guid.TryParseExact(path[..38], "B", out var folderId)) return null;
            if (!knownFolders.TryGetValue(folderId, out var folder))
            {
                nint value = 0;
                try
                {
                    // KF_FLAG_DONT_VERIFY only: resolve the current user's
                    // known-folder prefix without visiting or creating it.
                    folder = SHGetKnownFolderPath(in folderId, 0x4000, 0, out value) == 0 && value != 0
                        ? Marshal.PtrToStringUni(value) : null;
                    knownFolders[folderId] = folder;
                }
                finally { if (value != 0) Marshal.FreeCoTaskMem(value); }
            }
            if (string.IsNullOrEmpty(folder)) return null;
            path = folder + path[38..];
        }
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return path.Length <= 32768 && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Identifier { public int Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int Shell_NotifyIconGetRect(ref Identifier icon, out Native.Rect rectangle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("shell32.dll", ExactSpelling = true)]
    static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Native.Point Point;
        public uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint MsgWaitForMultipleObjectsEx(uint count, nint[] handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessageW(out Message message, nint window, uint first, uint last, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", ExactSpelling = true)] static extern nint DispatchMessageW(ref Message message);

    static bool ProviderRegistered()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !Environment.Is64BitProcess) return false;
        foreach (var path in new[] { @"CLSID\" + ClassId, @"CLSID\" + ProxyId,
            @"Interface\" + InterfaceId, @"Interface\" + CallbackId })
        {
            using var replacement = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + path);
            if (replacement is not null) return false;
        }
        using var clsid = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ClassId);
        if (clsid?.GetValue("AppId") is not string appId || !Guid.TryParse(appId, out _)) return false;
        using var userApp = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppID\" + appId);
        using var app = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\AppID\" + appId);
        if (userApp is not null || !string.Equals(app?.GetValue("RunAs") as string, "Interactive User", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var id in new[] { InterfaceId, CallbackId })
        {
            using var stub = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\Interface\" + id + @"\ProxyStubClsid32");
            if (!string.Equals(stub?.GetValue(null) as string, ProxyId, StringComparison.OrdinalIgnoreCase)) return false;
        }
        using var proxy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + ProxyId + @"\InProcServer32");
        return proxy?.GetValue(null) is string module && string.Equals(Environment.ExpandEnvironmentVariables(module),
            Path.Combine(Environment.SystemDirectory, "ActXPrxy.dll"), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SameExplorer(nint window, uint process)
    {
        var shell = SystemTrayInterop.ExplorerTray();
        return window != 0 && process != 0 && shell.Window == window && shell.Process == process;
    }
}

// This public COM-visible interface/class pair is required for Explorer to call
// the observer. It has no class registration and exposes only the notification.
[ComVisible(true), Guid("D782CCBA-AFB0-43F1-94DB-FDA3779EACCB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITraySnapshotCallback
{
    [PreserveSig] int Notify(uint change, [In] ref NativeTraySnapshotItem item);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NativeTraySnapshotItem
{
    [MarshalAs(UnmanagedType.LPWStr)] public string? Executable;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Tip;
    public nint Icon, Window;
    public uint Preference, Id;
    public Guid Guid;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NativeTraySnapshotCallback : ITraySnapshotCallback, IDisposable
{
    readonly object gate = new();
    readonly List<NativeTraySnapshotRecord> items = [];
    readonly CancellationToken cancellation;
    bool closed;
    int receivedCount;
    internal int ReceivedCount => Volatile.Read(ref receivedCount);

    internal NativeTraySnapshotCallback(CancellationToken token) => cancellation = token;

    public int Notify(uint change, ref NativeTraySnapshotItem item)
    {
        Interlocked.Increment(ref receivedCount);
        NativeTraySnapshot.CallbackReceived();
        try
        {
            lock (gate)
            {
                if (closed || cancellation.IsCancellationRequested || items.Count >= 512
                    || item.Window == 0 || item.Executable is not { Length: > 0 and <= 32768 } || item.Preference > 2) return 0;
                // These local USER lookups do not send a message to Explorer.
                // Discard dead historical windows before they consume the cap.
                var thread = Native.GetWindowThreadProcessId(item.Window, out var pid);
                if (thread == 0 || pid <= 0 || !Native.IsWindow(item.Window)) return 0;
                var tip = item.Tip ?? "";
                items.Add(new(item.Window, (uint)pid, thread, item.Id, item.Guid, item.Executable, tip[..Math.Min(tip.Length, 4096)],
                    item.Icon == 0 ? 0 : CopyIcon(item.Icon), item.Preference));
                NativeTraySnapshot.RecordCopied();
            }
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or COMException or NotSupportedException or System.Security.SecurityException) { }
        return 0;
    }

    internal IReadOnlyList<NativeTraySnapshotRecord> Finish()
    {
        lock (gate) { closed = true; return items.ToArray(); }
    }

    internal void Seal() { lock (gate) closed = true; }

    public void Dispose()
    {
        lock (gate) { closed = true; foreach (var item in items) item.Dispose(); items.Clear(); }
    }

    [DllImport("user32.dll")] static extern nint CopyIcon(nint icon);
}

internal sealed record NativeTraySnapshotRecord(nint Window, uint ProcessId, uint ThreadId, uint Id, Guid Guid, string Executable, string Tip,
    nint CopiedIcon, uint Preference) : IDisposable
{
    nint icon = CopiedIcon;

    internal BitmapSource? ReadIcon()
    {
        if (icon == 0) return null;
        var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
        bitmap.Freeze();
        return bitmap;
    }

    public void Dispose() { var value = Interlocked.Exchange(ref icon, 0); if (value != 0) DestroyIcon(value); }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyIcon(nint icon);
}
