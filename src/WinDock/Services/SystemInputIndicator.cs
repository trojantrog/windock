using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinDock.Services;

internal enum SystemInputRequest { Requested, Unavailable, Busy, TimedOut, Pending }

internal static class SystemInputIndicator
{
    static readonly SemaphoreSlim gate = new(1, 1);
    const string XamlInputClass = "SystemTray.InputIndicatorButton";
    public static async Task<SystemInputRequest> OpenAsync(CancellationToken cancellation)
    {
        if (!gate.Wait(0)) return SystemInputRequest.Busy;
        var request = new Request(cancellation, Environment.TickCount64 + 2500);
        var operation = Task.Run(() =>
        {
            try { return Open(request); }
            finally { gate.Release(); }
        });
        var wait = Task.Delay(2500, cancellation);
        if (await Task.WhenAny(operation, wait) == operation || operation.IsCompleted) return await operation;
        _ = operation.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return request.Expire() ? SystemInputRequest.TimedOut : SystemInputRequest.Pending;
    }

    static SystemInputRequest Open(Request request)
    {
        try
        {
            if (request.Expired) return request.Stopped;
            var explorer = SystemTrayInterop.ExplorerTray();
            var taskbar = explorer.Window;
            if (taskbar == 0 || explorer.Process == 0 || explorer.Thread == 0 || !Native.IsWindowVisible(taskbar))
                return request.Stopped;
            var process = unchecked((int)explorer.Process);
            var root = AutomationElement.FromHandle(taskbar);
            if (root is null) return request.Stopped;
            var candidates = Candidates(root, taskbar, process);
            if (candidates.Length != 1 || request.Expired)
                return request.Stopped;
            var target = candidates[0];
            if (target.Current.ClassName == XamlInputClass)
            {
                if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out var xamlInvoke)
                    || !Valid(target, taskbar, process) || !request.TryBegin()) return request.Stopped;
                ((InvokePattern)xamlInvoke).Invoke();
                return SystemInputRequest.Requested;
            }
            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                var state = pattern.Current.ExpandCollapseState;
                if (state == ExpandCollapseState.Collapsed)
                {
                    if (!Valid(target, taskbar, process) || !request.TryBegin()) return request.Stopped;
                    pattern.Expand();
                }
                else if (state != ExpandCollapseState.Expanded || !Valid(target, taskbar, process) || request.Expired) return request.Stopped;
                return SystemInputRequest.Requested;
            }
            if (!target.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return request.Stopped;
            if (!Valid(target, taskbar, process) || !request.TryBegin()) return request.Stopped;
            ((InvokePattern)invoke).Invoke();
            return SystemInputRequest.Requested;
        }
        catch (Exception error) when (error is ElementNotAvailableException or InvalidOperationException or COMException
            or System.ComponentModel.Win32Exception or ArgumentException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or TimeoutException)
        {
            return request.Stopped;
        }
    }

    static AutomationElement[] Candidates(AutomationElement root, nint taskbar, int process)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "InputIndicatorButton"),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "InputIndicatorButton"),
            new PropertyCondition(AutomationElement.ClassNameProperty, XamlInputClass));
        return root.FindAll(TreeScope.Descendants, condition).Cast<AutomationElement>()
            .Where(element => Matches(element, taskbar, process)).Take(2).ToArray();
    }

    static bool Valid(AutomationElement element, nint taskbar, int process)
    {
        var explorer = SystemTrayInterop.ExplorerTray();
        if (explorer.Window != taskbar || explorer.Process != unchecked((uint)process)
            || explorer.Thread == 0 || !Native.IsWindowVisible(taskbar)) return false;
        var root = AutomationElement.FromHandle(taskbar);
        if (root is null) return false;
        var candidates = Candidates(root, taskbar, process);
        return candidates.Length == 1 && Automation.Compare(element, candidates[0]);
    }

    static bool Matches(AutomationElement element, nint taskbar, int process)
    {
        var current = element.Current;
        if (current.ProcessId != process || !current.IsEnabled || current.IsOffscreen) return false;
        if (current.ClassName == XamlInputClass) return current.ControlType == ControlType.Button;
        var window = (nint)current.NativeWindowHandle;
        if (window == 0 || !Native.IsWindow(window) || Native.ClassOf(window) != "InputIndicatorButton"
            || Native.GetAncestor(window, Native.GA_ROOT) != taskbar) return false;
        Native.GetWindowThreadProcessId(window, out var actual);
        return actual == process;
    }

    sealed class Request(CancellationToken cancellation, long deadline)
    {
        readonly object sync = new();
        bool started, expired;
        public bool Expired { get { lock (sync) return expired || cancellation.IsCancellationRequested || Environment.TickCount64 >= deadline; } }
        public SystemInputRequest Stopped
        {
            get
            {
                lock (sync)
                {
                    if (cancellation.IsCancellationRequested || (!expired && Environment.TickCount64 < deadline)) return SystemInputRequest.Unavailable;
                    return started ? SystemInputRequest.Pending : SystemInputRequest.TimedOut;
                }
            }
        }
        public bool TryBegin()
        {
            lock (sync)
            {
                if (started || expired || cancellation.IsCancellationRequested || Environment.TickCount64 >= deadline) return false;
                started = true;
                return true;
            }
        }
        public bool Expire()
        {
            lock (sync)
            {
                if (started) return false;
                expired = true;
                return true;
            }
        }
    }
}
