using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace WinDock.Services;

internal enum NativeTrayActionResult { Requested, Unavailable, Busy, Cancelled, TimedOut, Pending }

internal sealed record NativeTrayIdentity(nint Window, uint ProcessId, uint Id, Guid Guid, long ProcessStarted);

internal sealed record NativeTraySurface(nint Window, uint ProcessId, Native.Rect IconBounds)
{
    internal bool IsCurrent()
    {
        Native.GetWindowThreadProcessId(Window, out var pid);
        return Window != 0 && pid == ProcessId && Native.IsWindowVisible(Window);
    }

    internal bool Contains(Native.Point point) => IsCurrent() && point.X >= IconBounds.Left && point.X < IconBounds.Right
        && point.Y >= IconBounds.Top && point.Y < IconBounds.Bottom
        && Native.GetAncestor(NativeTrayAction.WindowFromPoint(point), Native.GA_ROOT) == Window;
}

internal sealed class NativeTrayOperation
{
    readonly object sync = new();
    readonly TaskCompletionSource<NativeTrayActionResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationToken cancellation;
    readonly long deadline = Environment.TickCount64 + 2500;
    bool expired, cancelled, submitted;
    internal NativeTrayIdentity Identity { get; }
    internal Func<NativeTraySurface, bool> Prepare { get; }

    internal NativeTrayOperation(NativeTrayIdentity identity, Func<NativeTraySurface, bool> prepare, CancellationToken cancellation)
    { Identity = identity; Prepare = prepare; this.cancellation = cancellation; }

    internal bool IsResolving => !completion.Task.IsCompleted && !Stopped;
    internal bool Submitted { get { lock (sync) return submitted; } }
    internal bool Stopped
    {
        get { lock (sync) return expired || cancelled || cancellation.IsCancellationRequested || Environment.TickCount64 >= deadline; }
    }

    internal bool TrySubmit()
    {
        lock (sync)
        {
            if (submitted || expired || cancelled || cancellation.IsCancellationRequested || Environment.TickCount64 >= deadline) return false;
            submitted = true;
            return true;
        }
    }

    internal void Cancel()
    {
        lock (sync)
        {
            cancelled = true;
            completion.TrySetResult(submitted ? NativeTrayActionResult.Pending : NativeTrayActionResult.Cancelled);
        }
    }

    internal bool TryCancelPending()
    {
        lock (sync)
        {
            if (submitted || completion.Task.IsCompleted) return false;
            cancelled = true;
            completion.TrySetResult(NativeTrayActionResult.Cancelled);
            return true;
        }
    }

    internal void Complete(NativeTrayActionResult result) => completion.TrySetResult(result);

    internal async Task<NativeTrayActionResult> WaitAsync()
    {
        using var registration = cancellation.Register(Cancel);
        var remaining = (int)Math.Clamp(deadline - Environment.TickCount64, 0, 2500);
        if (await Task.WhenAny(completion.Task, Task.Delay(remaining)) == completion.Task) return await completion.Task;
        lock (sync)
        {
            expired = true;
            completion.TrySetResult(submitted ? NativeTrayActionResult.Pending : NativeTrayActionResult.TimedOut);
        }
        return await completion.Task;
    }
}

// UIA runs on one dedicated MTA thread. The reservation includes a blocked
// provider call: timing out the caller never starts another worker or retry.
internal static class NativeTrayAction
{
    static readonly BlockingCollection<NativeTrayOperation> requests = new(1);
    static readonly Lazy<Thread> worker = new(() =>
    {
        var thread = new Thread(Run) { Name = "WinDock native tray actions", IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return thread;
    });
    static int reserved;

    internal static NativeTrayOperation? TryStart(NativeTrayIdentity identity,
        Func<NativeTraySurface, bool> prepare, CancellationToken cancellation, Action<NativeTrayOperation> starting)
    {
        if (Interlocked.CompareExchange(ref reserved, 1, 0) != 0) return null;
        var operation = new NativeTrayOperation(identity, prepare, cancellation);
        var queued = false;
        try
        {
            // Establish the UI session before the worker can request its
            // foreground handoff. This also closes a queue/startup race.
            starting(operation);
            _ = worker.Value;
            if (requests.TryAdd(operation)) { queued = true; return operation; }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Security.SecurityException) { }
        finally
        {
            if (!queued) { operation.Cancel(); Interlocked.Exchange(ref reserved, 0); }
        }
        return null;
    }

    static void Run()
    {
        var initialized = CoInitializeEx(0, 0) >= 0;
        try
        {
            foreach (var operation in requests.GetConsumingEnumerable())
            {
                try { operation.Complete(initialized ? Activate(operation) : NativeTrayActionResult.Unavailable); }
                catch (Exception error) when (error is ElementNotAvailableException or ElementNotEnabledException
                    or InvalidOperationException or ArgumentException or COMException or NotSupportedException
                    or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException
                    or TimeoutException or OperationCanceledException)
                {
                    // An invocation can report failure after it has already
                    // crossed the process boundary. Never try another action.
                    operation.Complete(operation.Submitted ? NativeTrayActionResult.Pending : NativeTrayActionResult.Unavailable);
                }
                finally { Interlocked.Exchange(ref reserved, 0); }
            }
        }
        finally { if (initialized) CoUninitialize(); }
    }

    static NativeTrayActionResult Activate(NativeTrayOperation operation)
    {
        if (operation.Stopped) return NativeTrayActionResult.Cancelled;
        var identity = operation.Identity;
        var explorer = SystemTrayInterop.ExplorerTray();
        if (explorer.Window == 0 || explorer.Process == 0) return NativeTrayActionResult.Unavailable;
        var owner = Native.OpenProcess(0x101000, false, (int)identity.ProcessId);
        var shell = Native.OpenProcess(0x101000, false, (int)explorer.Process);
        try
        {
            if (owner == 0 || shell == 0 || !IsExplorerProcess(shell) || identity.ProcessStarted <= 0
                || !GetProcessTimes(owner, out var created, out _, out _, out _) || identity.ProcessStarted != created)
                return NativeTrayActionResult.Unavailable;

            bool Current()
            {
                Native.GetWindowThreadProcessId(identity.Window, out var pid);
                Native.GetWindowThreadProcessId(explorer.Window, out var shellPid);
                return !operation.Stopped && pid == identity.ProcessId && Native.IsWindow(identity.Window)
                    && shellPid == explorer.Process && Native.FindWindow("Shell_TrayWnd", null) == explorer.Window
                    && WaitForSingleObject(owner, 0) == 258 && WaitForSingleObject(shell, 0) == 258;
            }

            var first = Resolve(identity, explorer.Process, Current);
            if (first is null || !Current()) return NativeTrayActionResult.Unavailable;
            // Resolve again from the real native rectangle, without reusing an
            // icon name, sibling index, cached RuntimeId or former screen slot.
            var second = Resolve(identity, explorer.Process, Current);
            if (second is null || first.Surface != second.Surface || first.Bounds != second.Bounds
                || !Automation.Compare(first.Element, second.Element) || !Current()) return NativeTrayActionResult.Unavailable;
            if (!operation.Prepare(second.Surface) || !Current()) return NativeTrayActionResult.Unavailable;
            // UI handoff may yield to other user input. Recheck geometry and the
            // hit target once more before committing the single Invoke call.
            if (!TryRectangle(identity, out var finalRect) || !finalRect.Equals(second.Surface.IconBounds)
                || !ValidTarget(second, explorer.Process, Current) || !Current() || !operation.TrySubmit())
                return NativeTrayActionResult.Unavailable;
            // The explicit user action may hand foreground permission to this
            // verified shell process. Do not activate/reposition a window here.
            SystemTrayInterop.AllowSetForegroundWindow(explorer.Process);
            if (operation.Stopped) return NativeTrayActionResult.Pending;
            second.Pattern.Invoke();
            return NativeTrayActionResult.Requested;
        }
        finally
        {
            if (owner != 0) Native.CloseHandle(owner);
            if (shell != 0) Native.CloseHandle(shell);
        }
    }

    sealed record Target(AutomationElement Element, InvokePattern Pattern, NativeTraySurface Surface, Rect Bounds);

    static Target? Resolve(NativeTrayIdentity identity, uint explorerPid, Func<bool> current)
    {
        if (!current() || !TryRectangle(identity, out var rect) || !VisibleRectangle(rect)) return null;
        var center = Center(rect);
        var host = Native.GetAncestor(WindowFromPoint(center), Native.GA_ROOT);
        if (!NativeSurface(host, explorerPid, rect)) return null;
        var root = AutomationElement.FromHandle(host);
        if (root is null || root.Current.ProcessId != explorerPid || !current()) return null;
        var elements = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ProcessIdProperty, (int)explorerPid),
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
        if (elements.Count > 512 || !current()) return null;
        AutomationElement? matched = null;
        Rect bounds = default;
        foreach (AutomationElement element in elements)
        {
            if (!current()) return null;
            var candidate = element.Current.BoundingRectangle;
            if (!MatchesBounds(candidate, rect)) continue;
            if (matched is not null) return null;
            matched = element; bounds = candidate;
        }
        if (matched is null || !matched.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || pattern is not InvokePattern invoke)
            return null;
        var target = new Target(matched, invoke, new(host, explorerPid, rect), bounds);
        return ValidTarget(target, explorerPid, current) ? target : null;
    }

    static bool ValidTarget(Target target, uint explorerPid, Func<bool> current)
    {
        var rect = target.Surface.IconBounds;
        var host = target.Surface.Window;
        if (!current() || !NativeSurface(host, explorerPid, rect)) return false;
        var info = target.Element.Current;
        if (info.ProcessId != explorerPid || info.ControlType != ControlType.Button || !info.IsEnabled || info.IsOffscreen
            || info.BoundingRectangle != target.Bounds || !MatchesBounds(info.BoundingRectangle, rect)) return false;
        var root = AutomationElement.FromHandle(host);
        if (!DescendsFrom(target.Element, root, current)) return false;
        if (!HitMatches(target.Element, host, Center(rect), current)
            || !target.Element.TryGetClickablePoint(out var point) || !FinitePoint(point)) return false;
        var clickable = new Native.Point { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
        return target.Bounds.Contains(point) && HitMatches(target.Element, host, clickable, current) && current();
    }

    static bool DescendsFrom(AutomationElement element, AutomationElement root, Func<bool> current)
    {
        AutomationElement? node = element;
        for (var depth = 0; node is not null && depth < 32 && current(); depth++)
        {
            if (Automation.Compare(node, root)) return true;
            node = TreeWalker.RawViewWalker.GetParent(node);
        }
        return false;
    }

    static bool HitMatches(AutomationElement target, nint host, Native.Point point, Func<bool> current)
    {
        if (!current() || Native.GetAncestor(WindowFromPoint(point), Native.GA_ROOT) != host) return false;
        var hit = AutomationElement.FromPoint(new Point(point.X, point.Y));
        for (var depth = 0; hit is not null && depth < 16 && current(); depth++)
        {
            if (Automation.Compare(hit, target)) return true;
            // Never climb from a different actionable child to its parent.
            if (hit.Current.ControlType == ControlType.Button) return false;
            hit = TreeWalker.RawViewWalker.GetParent(hit);
        }
        return false;
    }

    static bool NativeSurface(nint window, uint pid, Native.Rect icon)
    {
        Native.GetWindowThreadProcessId(window, out var actual);
        if (window == 0 || actual != pid || !Native.IsWindowVisible(window) || !Native.GetWindowRect(window, out var bounds)
            || !Contains(bounds, icon) || !VisibleRectangle(icon)) return false;
        return Native.ClassOf(window) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    static bool TryRectangle(NativeTrayIdentity identity, out Native.Rect rect)
    {
        var identifier = new Identifier { Size = Marshal.SizeOf<Identifier>(), Window = identity.Window, Id = identity.Id, Guid = identity.Guid };
        return Shell_NotifyIconGetRect(ref identifier, out rect) == 0 && (long)rect.Right - rect.Left is > 0 and <= 512
            && (long)rect.Bottom - rect.Top is > 0 and <= 512;
    }

    static bool MatchesBounds(Rect bounds, Native.Rect icon) => !bounds.IsEmpty && double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
        && bounds.Width is > 0 and <= 512 && bounds.Height is > 0 and <= 512
        && bounds.Width <= Math.Max(96, icon.Width * 3) && bounds.Height <= Math.Max(96, icon.Height * 3)
        && bounds.Left <= icon.Left + 1 && bounds.Top <= icon.Top + 1
        && bounds.Right >= icon.Right - 1 && bounds.Bottom >= icon.Bottom - 1;

    static bool FinitePoint(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && point.X >= int.MinValue && point.X <= int.MaxValue && point.Y >= int.MinValue && point.Y <= int.MaxValue;

    static bool IsExplorerProcess(nint process)
    {
        var path = new StringBuilder(32768); var size = path.Capacity;
        return Native.QueryFullProcessImageName(process, 0, path, ref size) && string.Equals(path.ToString(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase);
    }

    static bool VisibleRectangle(Native.Rect rect)
    {
        var monitor = Native.MonitorFromPoint(Center(rect), 0);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        return monitor != 0 && Native.GetMonitorInfo(monitor, ref info) && Contains(info.Monitor, rect);
    }

    static Native.Point Center(Native.Rect rect) => new() { X = (int)(((long)rect.Left + rect.Right) / 2), Y = (int)(((long)rect.Top + rect.Bottom) / 2) };
    static bool Contains(Native.Rect outer, Native.Rect inner) => outer.Left <= inner.Left && outer.Top <= inner.Top
        && outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    struct Identifier { public int Size; public nint Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll", ExactSpelling = true)] static extern int Shell_NotifyIconGetRect(ref Identifier identifier, out Native.Rect rect);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Native.Point point);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
}
