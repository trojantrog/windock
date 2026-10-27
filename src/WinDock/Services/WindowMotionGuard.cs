using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;

namespace WinDock.Services;

// This is an ordinary second instance of our executable, not code injected into
// the application being animated. It owns the temporary alpha change, so a crash
// or a blocked UI in the Dock cannot leave another application's window hidden.
sealed class WindowMotionGuard : IDisposable
{
    const int PacketSize = 45;
    const int LeaseMilliseconds = 1400;
    const byte Acquire = 1, Release = 2;
    readonly SemaphoreSlim exchange = new(1);
    NamedPipeServerStream? pipe;
    Task<bool>? starting;
    bool disposed;
    public bool Failed => disposed || (starting is { IsCompletedSuccessfully: true } && !starting.Result);

    public Task<bool> Prepare() => starting ??= Task.Run(Start);

    async Task<bool> Start()
    {
        if (disposed || Environment.ProcessPath is not { } executable
            || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;
        var name = $"WinDock.Motion.{Environment.ProcessId}.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe = server;
        var accepted = false;
        try
        {
            using var self = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--window-motion-guard");
            start.ArgumentList.Add(self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(name);
            using var child = Process.Start(start);
            if (child is null) return false;
            using var deadline = new CancellationTokenSource(2500);
            await server.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            if (!WindowMotionNative.GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var client)
                || client != child.Id) return false;
            var ready = new byte[1];
            await server.ReadExactlyAsync(ready, deadline.Token).ConfigureAwait(false);
            accepted = ready[0] == 1 && !disposed;
            return accepted;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
        finally
        {
            if (!accepted || disposed) server.Dispose();
        }
    }

    public async Task<Lease?> Hide(nint window, int pid, nint operation, CancellationToken cancellation)
    {
        if (!await Prepare().WaitAsync(cancellation)) return null;
        long started;
        try
        {
            using var process = Process.GetProcessById(pid);
            started = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        var token = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var lease = new Lease(this, window, pid, started, token, operation);
        if (await Send(Acquire, lease, cancellation)) return lease;
        return null;
    }

    async Task<bool> Send(byte command, Lease lease, CancellationToken cancellation)
    {
        await exchange.WaitAsync(cancellation);
        try
        {
            if (disposed || pipe is not { IsConnected: true } channel) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(250);
            var packet = new byte[PacketSize];
            packet[0] = command;
            BitConverter.TryWriteBytes(packet.AsSpan(1), (long)lease.Window);
            BitConverter.TryWriteBytes(packet.AsSpan(9), lease.Pid);
            BitConverter.TryWriteBytes(packet.AsSpan(13), lease.Started);
            BitConverter.TryWriteBytes(packet.AsSpan(21), (long)lease.Token);
            BitConverter.TryWriteBytes(packet.AsSpan(29), Environment.TickCount64 + LeaseMilliseconds);
            BitConverter.TryWriteBytes(packet.AsSpan(37), (long)lease.Operation);
            await channel.WriteAsync(packet, deadline.Token);
            var answer = new byte[1];
            await channel.ReadExactlyAsync(answer, deadline.Token);
            return answer[0] == 1;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing the connection is itself a release request. If the helper
            // is stuck in a native call, its fixed deadline remains in force.
            Dispose();
            return false;
        }
        finally { exchange.Release(); }
    }

    public void Dispose()
    {
        disposed = true;
        pipe?.Dispose();
    }

    internal sealed class Lease(WindowMotionGuard owner, nint window, int pid, long started, nint token, nint operation) : IAsyncDisposable
    {
        public nint Window { get; } = window;
        public int Pid { get; } = pid;
        public long Started { get; } = started;
        public nint Token { get; } = token;
        public nint Operation { get; } = operation;
        public bool IsHeld => WindowMotionNative.SameWindow(Window, Pid)
            && WindowMotionNative.GetProp(Window, WindowMotionNative.LeaseProperty) == Token
            && WindowMotionNative.GetProp(Window, WindowMotionNative.OperationProperty) == Operation;
        public async ValueTask DisposeAsync() => await owner.Send(Release, this, CancellationToken.None);
    }

    public static async Task Run(int parentPid, long parentStarted, string pipeName)
    {
        if (!pipeName.StartsWith($"WinDock.Motion.{parentPid}.", StringComparison.Ordinal)) return;
        GuardedWindow? held = null;
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != parentStarted) return;
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2500);
            if (!WindowMotionNative.GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var server)
                || server != parentPid) return;
            await client.WriteAsync(new byte[] { 1 });
            var packet = new byte[PacketSize];
            var pending = client.ReadExactlyAsync(packet).AsTask();
            while (!parent.HasExited)
            {
                if (held is not null && Environment.TickCount64 >= held.Deadline)
                {
                    if (held.Restore()) held = null;
                }
                if (await Task.WhenAny(pending, Task.Delay(25)) != pending) continue;
                await pending;
                var window = (nint)BitConverter.ToInt64(packet, 1);
                var pid = BitConverter.ToInt32(packet, 9);
                var started = BitConverter.ToInt64(packet, 13);
                var token = (nint)BitConverter.ToInt64(packet, 21);
                var requestedDeadline = BitConverter.ToInt64(packet, 29);
                var operation = (nint)BitConverter.ToInt64(packet, 37);
                var accepted = false;
                if (packet[0] == Acquire)
                {
                    if (held is not null && held.Restore()) held = null;
                    if (held is null && requestedDeadline > Environment.TickCount64)
                    {
                        // Remember the recovery obligation before the first style
                        // mutation. A failed partial acquisition is restored too.
                        held = new GuardedWindow(window, pid, started, token, operation,
                            Math.Min(requestedDeadline, Environment.TickCount64 + LeaseMilliseconds));
                        accepted = held.Hide();
                        if (!accepted && held.Restore()) held = null;
                    }
                }
                else if (packet[0] == Release)
                {
                    accepted = held is null;
                    if (held is not null && held.Window == window && held.Token == token && held.Restore())
                    {
                        held = null;
                        accepted = true;
                    }
                }
                await client.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 });
                pending = client.ReadExactlyAsync(packet).AsTask();
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        finally
        {
            // Retry transient compositor/style failures. The helper never
            // terminates while it still owns an alpha-zero window.
            while (held is not null && !held.Restore()) await Task.Delay(50);
        }
    }

    sealed class GuardedWindow(nint window, int pid, long started, nint token, nint operation, long deadline)
    {
        public nint Window { get; } = window;
        public nint Token { get; } = token;
        public long Deadline { get; } = deadline;
        bool marked;
        nint processHandle;

        bool SameProcess()
        {
            if (!WindowMotionNative.SameWindow(Window, pid)) return false;
            if (processHandle == 0)
            {
                processHandle = Native.OpenProcess(0x00101000 /* SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
                if (processHandle == 0 || !WindowMotionNative.GetProcessTimes(processHandle, out var created, out _, out _, out _)
                    || DateTime.FromFileTimeUtc(created).Ticks != started) return false;
            }
            // Hold this process handle for the entire lease. Reopening a PID at
            // cleanup could mistake a recycled PID or a transient access failure.
            return WindowMotionNative.ProcessWait(processHandle, 0) == 258 /* WAIT_TIMEOUT */;
        }

        public bool Hide()
        {
            if (!SameProcess() || !WindowMotionNative.CanAnimate(Window, true, out var current) || current != pid
                || WindowMotionNative.GetProp(Window, WindowMotionNative.OperationProperty) != operation
                || WindowMotionNative.GetProp(Window, WindowMotionNative.LeaseProperty) != 0) return false;
            var style = (long)Native.GetWindowLongPtr(Window, Native.GWL_EXSTYLE);
            if ((style & WindowMotionNative.Layered) != 0) return false;
            if (!WindowMotionNative.SetProp(Window, WindowMotionNative.LeaseProperty, Token)) return false;
            marked = true;
            Native.SetWindowLongPtr(Window, Native.GWL_EXSTYLE, (nint)(style | WindowMotionNative.Layered));
            if (Environment.TickCount64 >= Deadline || !SameProcess()
                || WindowMotionNative.GetProp(Window, WindowMotionNative.LeaseProperty) != Token
                || WindowMotionNative.GetProp(Window, WindowMotionNative.OperationProperty) != operation) return false;
            if (WindowMotionNative.GetWindowDisplayAffinity(Window, out var affinity) && affinity != 0) return false;
            if (!WindowMotionNative.SetLayeredWindowAttributes(Window, 0, 0, 2 /* LWA_ALPHA */)) return false;
            return Environment.TickCount64 < Deadline
                && WindowMotionNative.GetLayeredWindowAttributes(Window, out _, out var alpha, out var flags)
                && alpha == 0 && (flags & 2) != 0;
        }

        public bool Restore()
        {
            var restored = RestoreCore();
            if (restored && processHandle != 0)
            {
                Native.CloseHandle(processHandle);
                processHandle = 0;
            }
            return restored;
        }

        bool RestoreCore()
        {
            if (!marked || !WindowMotionNative.SameWindow(Window, pid)
                || WindowMotionNative.GetProp(Window, WindowMotionNative.LeaseProperty) != Token) return true;
            if (processHandle == 0 || WindowMotionNative.ProcessWait(processHandle, 0) != 258) return true;
            var style = (long)Native.GetWindowLongPtr(Window, Native.GWL_EXSTYLE);
            if ((style & WindowMotionNative.Layered) != 0)
            {
                // The window was not layered before acquisition; therefore it
                // had no layered alpha/color-key state to overwrite. Preserve
                // every unrelated style bit the application changed meanwhile.
                WindowMotionNative.SetLayeredWindowAttributes(Window, 0, 255, 2);
                Native.SetWindowLongPtr(Window, Native.GWL_EXSTYLE, (nint)(style & ~WindowMotionNative.Layered));
                if (((long)Native.GetWindowLongPtr(Window, Native.GWL_EXSTYLE) & WindowMotionNative.Layered) != 0) return false;
                WindowMotionNative.RedrawWindow(Window, 0, 0, 0x0001 | 0x0004 | 0x0080 | 0x0400);
            }
            WindowMotionNative.RemoveProp(Window, WindowMotionNative.LeaseProperty);
            marked = false;
            return true;
        }
    }
}
