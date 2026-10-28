using System.Windows;
using System.Windows.Threading;
using WinDock.Views;

namespace WinDock.Services;

sealed class WindowTransitions : IDisposable
{
    public static WindowTransitions Shared { get; } = new();
    internal static bool WindowsAnimations => SystemParameters.ClientAreaAnimation && SystemParameters.MinimizeAnimation;
    internal const int OpeningHandoffBudget = 180;
    const long CacheBudget = 96L * 1024 * 1024;
    readonly Dictionary<nint, Cached> cache = [];
    readonly DispatcherTimer cleanup = new() { Interval = TimeSpan.FromSeconds(20) };
    WindowMotionGuard guard = new();
    Task previous = Task.CompletedTask;
    Session? current;
    bool disposed;

    WindowTransitions()
    {
        cleanup.Tick += (_, _) => ObserveWindows();
        cleanup.Start();
    }

    public void Prepare(bool enabled)
    {
        if (guard.Failed && !disposed) guard = new WindowMotionGuard();
        if (enabled && SystemParameters.ClientAreaAnimation && !disposed) _ = guard.Prepare();
        if (!enabled)
        {
            Cancel(clearCache: true);
            guard.Dispose();
        }
    }

    public bool IsAnimating(nint window) => current is { Opening: false } session && session.Window == window;
    internal bool ReadyForOpening => !disposed && guard.Ready && SystemParameters.ClientAreaAnimation;

    internal Task Appear(nint window, Rect anchor, int milliseconds, object owner, long appearedAt, Func<bool> identity)
    {
        // An out-of-context event cannot own a foreign app's first Present.
        // Never replay a settled window or interrupt another Dock operation.
        if (current is not null || !ReadyForOpening || !ValidAnchor(anchor)
            || Environment.TickCount64 - appearedAt >= OpeningHandoffBudget || !identity()) return Task.CompletedTask;
        var session = new Session(window, anchor, false, Math.Clamp(milliseconds, 120, 600), owner)
        { Opening = true, AppearedAt = appearedAt, OpeningIdentity = identity };
        current = session;
        return previous = RunAfter(previous, session);
    }

    public void Reverse(nint window)
    {
        if (current is not { Opening: false } session || session.Window != window) return;
        session.Minimized = !session.Minimized;
        if (session.Held?.IsHeld == true)
        {
            session.Foreground = Native.GetForegroundWindow();
            WindowMotionNative.SetState(window, session.Minimized);
            session.StateChangedAt = Environment.TickCount64;
        }
    }

    public void Request(nint window, Rect anchor, bool minimized, bool enabled, int milliseconds, object owner)
    {
        if (current is { Opening: false } active && active.Window == window)
        {
            if (active.Minimized != minimized) Reverse(window);
            return;
        }
        Cancel();
        if (!enabled || disposed || !WindowsAnimations || !ValidAnchor(anchor))
        {
            NativeAction(window, minimized);
            return;
        }
        var session = new Session(window, anchor, minimized, Math.Clamp(milliseconds, 120, 600), owner);
        Prepare(true);
        current = session;
        previous = RunAfter(previous, session);
    }

    async Task RunAfter(Task prior, Session session)
    {
        try { await prior; }
        catch (Exception) { /* An older operation cannot block a newer request. */ }
        if (current != session || session.Cancellation.IsCancellationRequested)
        {
            session.Cancellation.Dispose();
            return;
        }
        var window = session.Window;
        WindowSnapshot? snapshot = null;
        var fallback = !session.Opening;
        var issued = false;
        var completed = false;
        try
        {
            var cancellation = session.Cancellation.Token;
            Native.GetWindowThreadProcessId(window, out var originalPid);
            session.Pid = originalPid;
            if (!WindowMotionNative.CanAnimate(window, true, out var pid)) return;
            session.Pid = pid;
            if (!WindowMotionNative.SetProp(window, WindowMotionNative.OperationProperty, session.Identity)) return;
            session.Marked = true;
            var wasMinimized = Native.IsIconic(window);
            if (session.Opening ? wasMinimized || !OpeningStillEarly(session) : wasMinimized == session.Minimized) return;
            if (wasMinimized)
            {
                // Restoring arbitrary minimized windows would require showing
                // them before capturing. We only unfold a verified snapshot from
                // a preceding Dock minimize, never a desktop/preview screenshot.
                snapshot = TakeCached(window, pid);
            }
            else
            {
                RemoveCached(window);
                snapshot = await WindowSnapshots.Capture(window, pid, cancellation, session.Opening);
            }
            cancellation.ThrowIfCancellationRequested();
            if (snapshot is null || !WindowMotionNative.SameWindow(window, pid)
                || WindowMotionNative.GetProp(window, WindowMotionNative.OperationProperty) != session.Identity
                || Native.IsIconic(window) != wasMinimized
                || (!session.Opening && wasMinimized == session.Minimized)) return;
            if (!wasMinimized && Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOTOWNER) != window) return;
            if (session.Opening && Native.GetForegroundWindow() != window) return;
            if (session.Opening && !OpeningStillEarly(session)) return;
            if (!await guard.Prepare().WaitAsync(TimeSpan.FromMilliseconds(300), cancellation)) return;

            session.Bounds = snapshot.Bounds;
            session.Surface = new WindowMotionSurface(snapshot, session.Anchor);
            await session.Surface.PrepareFrame(wasMinimized, session.Opening, cancellation);
            if (session.Opening && !OpeningStillEarly(session)) return;
            if (!wasMinimized && WindowMotionNative.Frame(window) != snapshot.Bounds) return;
            if (!wasMinimized && Native.GetForegroundWindow() != window) return;
            session.Foreground = Native.GetForegroundWindow();
            session.Held = await guard.Hide(window, pid, session.Identity, session.Surface.Handle, cancellation,
                session.Opening ? session.AppearedAt + OpeningHandoffBudget : long.MaxValue);
            if (session.Held is null) return;
            if (!session.Opening && wasMinimized == session.Minimized) return;
            if (!session.Held.IsHeld) return;
            session.StateChangedAt = Environment.TickCount64;
            if (session.Opening)
            {
                // The application owns its placement, activation and show state.
                // Only this early, valid composed frame gets a short expansion.
                await session.Surface.AnimateAppearance(() => StillOwned(session), session.Milliseconds, cancellation);
                completed = StillOwned(session);
                return;
            }
            WindowMotionNative.SetState(window, session.Minimized);
            session.Foreground = Native.GetForegroundWindow();
            issued = true;
            if (!await WaitForState(session, cancellation)) return;
            if (!session.Minimized)
            {
                var restored = WindowMotionNative.Frame(window);
                // Display topology, DPI or application-managed placement may
                // change while minimized. Never animate to stale coordinates.
                if (restored.IsEmpty || !SameBounds(restored, snapshot.Bounds)
                    || WindowMotionNative.IsFullscreen(window)) return;
            }
            fallback = false;
            await session.Surface.Animate(wasMinimized, () => session.Minimized,
                () => StillOwned(session), session.Milliseconds, cancellation);
            await WaitForState(session, cancellation);
            completed = StillOwned(session);
            if (completed && session.Minimized && Native.IsIconic(window)) Remember(window, snapshot);
        }
        catch (OperationCanceledException) { fallback = false; }
        catch (Exception e) { System.Diagnostics.Trace.WriteLine($"Window animation fallback: {e}"); }
        finally
        {
            // The helper unclamps the source then clamps the overlay in one
            // command. Do not hold two translucent copies during the handoff.
            try
            {
                if (session.Held is not null) await session.Held.DisposeAsync();
            }
            catch (Exception e)
            {
                // Pipe loss is an independent recovery trigger for the helper.
                guard.Dispose();
                System.Diagnostics.Trace.WriteLine($"Window handoff release: {e.GetType().Name}");
            }
            try { session.Surface?.Close(); }
            catch (InvalidOperationException) { /* WPF shutdown may have closed it already. */ }
            if (current == session)
            {
                current = null;
                if (!session.Cancellation.IsCancellationRequested && WindowMotionNative.SameWindow(window, session.Pid)
                    && (!session.Marked || WindowMotionNative.GetProp(window, WindowMotionNative.OperationProperty) == session.Identity))
                {
                    if (fallback && !issued) NativeAction(window, session.Minimized);
                    else if (completed && !session.Opening && !session.Minimized && !Native.IsIconic(window)) WindowTracker.Activate(window);
                }
            }
            session.Cancellation.Dispose();
            if (WindowMotionNative.GetProp(window, WindowMotionNative.OperationProperty) == session.Identity)
                WindowMotionNative.RemoveProp(window, WindowMotionNative.OperationProperty);
        }
    }

    static bool StillOwned(Session session)
    {
        if (!WindowMotionNative.SameWindow(session.Window, session.Pid) || session.Held?.IsHeld != true
            || !Native.IsWindowVisible(session.Window)
            || (WindowMotionNative.GetWindowDisplayAffinity(session.Window, out var affinity) && affinity != 0)) return false;
        if (session.Opening && (session.OpeningIdentity?.Invoke() != true || Native.IsIconic(session.Window)
            || !SameBounds(WindowMotionNative.Frame(session.Window), session.Bounds))) return false;
        var foreground = Native.GetForegroundWindow();
        if (!session.Minimized && foreground != 0 && foreground != session.Window && foreground != session.Foreground) return false;
        if (!session.Minimized && Environment.TickCount64 - session.StateChangedAt >= 160 && !Native.IsIconic(session.Window)
            && !SameBounds(WindowMotionNative.Frame(session.Window), session.Bounds)) return false;
        // Allow the asynchronous native command to settle. Afterwards, a state
        // change made outside the Dock wins and ends our visual immediately.
        return Environment.TickCount64 - session.StateChangedAt < 160
            || Native.IsIconic(session.Window) == session.Minimized;
    }

    static bool OpeningStillEarly(Session session) =>
        Environment.TickCount64 - session.AppearedAt < OpeningHandoffBudget && session.OpeningIdentity?.Invoke() == true;

    static async Task<bool> WaitForState(Session session, CancellationToken cancellation)
    {
        for (var i = 0; i < 18; i++)
        {
            if (!WindowMotionNative.SameWindow(session.Window, session.Pid) || session.Held?.IsHeld != true) return false;
            if (Native.IsIconic(session.Window) == session.Minimized) return true;
            await Task.Delay(12, cancellation);
        }
        return false;
    }

    static void NativeAction(nint window, bool minimized)
    {
        if (!Native.IsWindow(window)) return;
        if (minimized) WindowTracker.Minimize(window);
        else WindowTracker.Activate(window);
    }

    static bool ValidAnchor(Rect anchor) => !anchor.IsEmpty && double.IsFinite(anchor.X) && double.IsFinite(anchor.Y)
        && double.IsFinite(anchor.Width) && double.IsFinite(anchor.Height) && anchor.Width >= 8 && anchor.Height >= 8;

    static bool SameBounds(Rect left, Rect right) => left == right;

    void Remember(nint window, WindowSnapshot snapshot)
    {
        RemoveCached(window);
        var token = (nint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        if (!WindowMotionNative.SetProp(window, WindowMotionNative.SnapshotProperty, token)) return;
        cache[window] = new Cached(snapshot, token, Environment.TickCount64);
        while (cache.Count > 4 || cache.Values.Sum(value => value.Snapshot.Bytes) > CacheBudget)
            RemoveCached(cache.MinBy(pair => pair.Value.Added).Key);
    }

    WindowSnapshot? TakeCached(nint window, int pid)
    {
        if (!cache.TryGetValue(window, out var entry)) return null;
        var valid = entry.Snapshot.Pid == pid && Environment.TickCount64 - entry.Added < 180_000
            && WindowMotionNative.GetProp(window, WindowMotionNative.SnapshotProperty) == entry.Token;
        var result = valid ? entry.Snapshot : null;
        RemoveCached(window);
        return result;
    }

    void RemoveCached(nint window)
    {
        if (!cache.Remove(window, out var entry)) return;
        // A window property dies with its HWND, making handle reuse detectable
        // even when the replacement window belongs to the same process/thread.
        if (WindowMotionNative.GetProp(window, WindowMotionNative.SnapshotProperty) == entry.Token)
            WindowMotionNative.RemoveProp(window, WindowMotionNative.SnapshotProperty);
    }

    public void ObserveWindows()
    {
        foreach (var (window, value) in cache.ToArray())
        {
            if (!WindowMotionNative.SameWindow(window, value.Snapshot.Pid)
                || Environment.TickCount64 - value.Added >= 180_000
                || (current?.Window != window && !Native.IsIconic(window))) RemoveCached(window);
        }
    }

    public void Cancel(object? owner = null, bool clearCache = false)
    {
        WindowLaunchObserver.Shared.Cancel(owner);
        if (current is { } session && (owner is null || ReferenceEquals(owner, session.Owner)))
        {
            current = null;
            session.Cancellation.Cancel();
        }
        if (clearCache) foreach (var window in cache.Keys.ToArray()) RemoveCached(window);
    }

    public void Dispose()
    {
        disposed = true;
        Cancel(clearCache: true);
        cleanup.Stop();
        guard.Dispose();
    }

    sealed record Cached(WindowSnapshot Snapshot, nint Token, long Added);
    sealed class Session(nint window, Rect anchor, bool minimized, int milliseconds, object owner)
    {
        public nint Window { get; } = window;
        public Rect Anchor { get; } = anchor;
        public bool Minimized { get; set; } = minimized;
        public int Milliseconds { get; } = milliseconds;
        public object Owner { get; } = owner;
        public bool Opening { get; init; }
        public long AppearedAt { get; init; }
        public Func<bool>? OpeningIdentity { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
        public nint Identity { get; } = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        public bool Marked { get; set; }
        public int Pid { get; set; }
        public long StateChangedAt { get; set; }
        public nint Foreground { get; set; }
        public Rect Bounds { get; set; }
        public WindowMotionSurface? Surface { get; set; }
        public WindowMotionGuard.Lease? Held { get; set; }
    }
}
