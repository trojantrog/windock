using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using WinDock.ViewModels;

namespace WinDock.Services;

// Observes only a launch initiated through this Dock. A process receipt and an
// HWND born after the request are both required; titles/folder-name guesses and
// global interception of unrelated windows are deliberately excluded.
sealed class WindowLaunchObserver : IDisposable
{
    const string LaunchProperty = "WinDock.WindowMotion.Launch.v1";
    const int Lifetime = 10_000;
    public static WindowLaunchObserver Shared { get; } = new();
    readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    readonly List<Pending> pending = [];
    readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Native.WinEventProc callback;
    GCHandle callbackRoot;
    nint hook;
    Pending? launching;
    bool disposed;

    WindowLaunchObserver()
    {
        callback = OnEvent;
        expiry.Tick += (_, _) =>
        {
            foreach (var item in pending.ToArray())
                if (Environment.TickCount64 - item.Began >= Lifetime || (!item.Launching && !item.ProcessAlive)) Remove(item);
        };
        Launcher.Started += OnStarted;
    }

    internal Pending? Begin(AppTile app, Rect anchor, bool enabled, int duration, object owner)
    {
        if (!enabled || disposed || app.IsRunning || app.IsFolder || anchor.IsEmpty || app.Opener() is not { } opener) return null;
        WindowTransitions.Shared.Prepare(true);
        if (!WindowTransitions.Shared.ReadyForOpening) return null;
        var exe = Normalize(opener.Exe);
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsGamePath(exe)) return null;
        Cancel(owner);
        var baseline = new HashSet<nint>();
        Native.EnumWindows((window, _) => { baseline.Add(window); return true; }, 0);
        var item = new Pending(this, owner, exe, anchor, Math.Clamp(duration, 120, 600), baseline);
        pending.Add(item);
        launching = item;
        if (hook == 0)
        {
            callbackRoot = GCHandle.Alloc(callback);
            hook = Native.SetWinEventHook(0x8000 /* CREATE */, 0x8002 /* SHOW */, 0,
                callback, 0, 0, 2 /* OUTOFCONTEXT | SKIPOWNPROCESS */);
            if (hook == 0) { Remove(item); return null; }
            expiry.Start();
        }
        return item;
    }

    void OnStarted(Launcher.LaunchReceipt receipt)
    {
        if (launching is { Launching: true } item) item.Attach(receipt);
    }

    void OnEvent(nint callbackHook, uint ev, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (disposed || window == 0 || objectId != 0 || childId != 0 || callbackHook != hook) return;
        // Do not call capture or mutate a target inside an accessibility callback.
        // The original event timestamp, not dispatcher arrival, sets the budget.
        dispatcher.BeginInvoke(() => Observe(ev, window, time), DispatcherPriority.Input);
    }

    void Observe(uint ev, nint window, uint eventTime)
    {
        if (disposed || !Native.IsWindow(window)) return;
        Native.GetWindowThreadProcessId(window, out var pid);
        foreach (var item in pending.ToArray())
        {
            if (item.Claimed || item.Launching || !item.ProcessMatches(pid) || item.Baseline.Contains(window)) continue;
            if (ev == 0x8001 /* DESTROY */) { item.Created.Remove(window); continue; }
            var elapsed = unchecked((uint)Environment.TickCount - eventTime);
            if (elapsed > WindowTransitions.OpeningHandoffBudget) continue;
            var occurred = Environment.TickCount64 - elapsed;
            if (occurred < item.Began) continue;
            if (ev == 0x8000) { item.Created.Add(window); continue; }
            if (ev != 0x8002 || !item.Seen.Add(window) || !item.Created.Contains(window)) continue;
            // The first SHOW is the only opportunity. A later render or repeat
            // SHOW must never rewind an already visible window into an animation.
            if (!EligibleMainWindow(window, pid)) continue;
            item.Claimed = true;
            item.Window = window;
            if (!WindowMotionNative.SetProp(window, LaunchProperty, item.Token)) { Remove(item); continue; }
            _ = Animate(item, occurred);
        }
    }

    async Task Animate(Pending item, long appearedAt)
    {
        try
        {
            await WindowTransitions.Shared.Appear(item.Window, item.Anchor, item.Duration, item.Owner, appearedAt,
                () => !item.Removed && item.ProcessMatches(item.Pid)
                    && WindowMotionNative.SameWindow(item.Window, item.Pid)
                    && WindowMotionNative.GetProp(item.Window, LaunchProperty) == item.Token);
        }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"Opening animation skipped: {e.GetType().Name}"); }
        finally { Remove(item); }
    }

    static bool EligibleMainWindow(nint window, int expectedPid)
    {
        if (!WindowMotionNative.CanAnimate(window, false, out var pid) || pid != expectedPid
            || Native.GetWindow(window, 4 /* GW_OWNER */) != 0 || Native.TitleOf(window).Length == 0) return false;
        var style = (long)Native.GetWindowLongPtr(window, -16);
        const long mainStyle = 0x00C00000L | 0x00080000L | 0x00020000L; // CAPTION, SYSMENU, MINIMIZEBOX
        if ((style & mainStyle) != mainStyle || (style & 0x80000000L /* WS_POPUP */) != 0) return false;
        var name = Native.ClassOf(window);
        if (name == "#32770" || name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Unreal", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SDL", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GLFW", StringComparison.OrdinalIgnoreCase) || name == "Valve001") return false;
        var frame = WindowMotionNative.Frame(window);
        return !frame.IsEmpty && frame.Width >= 240 && frame.Height >= 160;
    }

    internal void Cancel(object? owner = null)
    {
        foreach (var item in pending.ToArray())
            if (owner is null || ReferenceEquals(owner, item.Owner)) Remove(item);
    }

    void Remove(Pending item)
    {
        if (item.Removed) return;
        item.Removed = true;
        pending.Remove(item);
        if (launching == item) launching = null;
        if (item.Window != 0 && WindowMotionNative.SameWindow(item.Window, item.Pid)
            && WindowMotionNative.GetProp(item.Window, LaunchProperty) == item.Token)
            WindowMotionNative.RemoveProp(item.Window, LaunchProperty);
        item.CloseProcess();
        if (pending.Count == 0)
        {
            if (hook != 0) Native.UnhookWinEvent(hook);
            hook = 0;
            if (callbackRoot.IsAllocated) callbackRoot.Free();
            expiry.Stop();
        }
    }

    public void Dispose()
    {
        disposed = true;
        Cancel();
        Launcher.Started -= OnStarted;
    }

    static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    static bool IsGamePath(string exe) => exe.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)
        || exe.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase);

    internal sealed class Pending(WindowLaunchObserver observer, object owner, string exe, Rect anchor,
        int duration, HashSet<nint> baseline)
    {
        public object Owner { get; } = owner;
        public Rect Anchor { get; } = anchor;
        public int Duration { get; } = duration;
        public HashSet<nint> Baseline { get; } = baseline;
        public HashSet<nint> Created { get; } = [];
        public HashSet<nint> Seen { get; } = [];
        public long Began { get; } = Environment.TickCount64;
        readonly long beganUtcTicks = DateTime.UtcNow.Ticks;
        public nint Token { get; } = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        public bool Launching { get; private set; } = true;
        public bool Claimed { get; set; }
        public bool Removed { get; set; }
        public int Pid { get; private set; }
        public nint Window { get; set; }
        nint process;
        public bool ProcessAlive => process != 0 && WindowMotionNative.ProcessWait(process, 0) == 258;
        public bool ProcessMatches(int pid) => !Removed && pid == Pid && ProcessAlive;

        public void Launched(bool success)
        {
            Launching = false;
            if (observer.launching == this) observer.launching = null;
            if (!success || process == 0) observer.Remove(this);
        }

        public void Attach(Launcher.LaunchReceipt receipt)
        {
            if (Removed || process != 0 || receipt.StartedUtcTicks < beganUtcTicks - TimeSpan.TicksPerMillisecond * 20) return;
            var handle = Native.OpenProcess(0x00101000, false, receipt.Pid);
            if (handle == 0) return;
            try
            {
                if (!WindowMotionNative.GetProcessTimes(handle, out var started, out _, out _, out _)
                    || DateTime.FromFileTimeUtc(started).Ticks != receipt.StartedUtcTicks
                    || WindowMotionNative.ProcessWait(handle, 0) != 258) return;
                var path = new StringBuilder(32768);
                var length = path.Capacity;
                if (!Native.QueryFullProcessImageName(handle, 0, path, ref length)
                    || !StringComparer.OrdinalIgnoreCase.Equals(exe, Normalize(path.ToString()))) return;
                process = handle;
                Pid = receipt.Pid;
                handle = 0;
            }
            finally { if (handle != 0) Native.CloseHandle(handle); }
        }

        public void CloseProcess()
        {
            if (process != 0) Native.CloseHandle(process);
            process = 0;
        }
    }
}
