using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinDock.Services;

internal static class SystemTrayInterop
{
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int hook, nint callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SendNotifyMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint ProtocolVersion();

    internal static NamedPipeServerStream CreateTrayPipe(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new UnauthorizedAccessException();
        var logon = identity.Groups?.OfType<SecurityIdentifier>()
            .FirstOrDefault(group => group.Value.StartsWith("S-1-5-5-", StringComparison.Ordinal))?.Value ?? user;
        var sddl = $"O:{user}D:P(A;;GA;;;{logon})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception();
        try
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080001, 0x8, 1, 0, 65536, 0, ref security);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose(); throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.In, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint bytes);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout,
        ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);

    internal static (nint Window, uint Process, uint Thread) ExplorerTray()
    {
        (nint Window, uint Process, uint Thread) found = default;
        Native.EnumWindows((window, _) =>
        {
            if (Native.ClassOf(window) != "Shell_TrayWnd") return true;
            var thread = Native.GetWindowThreadProcessId(window, out var process);
            var handle = Native.OpenProcess(0x1000, false, (int)process);
            if (handle == 0) return true;
            try
            {
                var path = new StringBuilder(32768); var size = path.Capacity;
                if (!Native.QueryFullProcessImageName(handle, 0, path, ref size) ||
                    !string.Equals(path.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) return true;
                found = (window, (uint)process, thread); return false;
            }
            finally { Native.CloseHandle(handle); }
        }, 0);
        return found;
    }
}

internal sealed class TrayHookSession : IDisposable
{
    const uint Magic = 0x52544457, Version = 7;
    readonly CancellationToken token;
    readonly uint nonce = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) | 1;
    NamedPipeServerStream? pipe;
    nint module, hook, returnHook, target, explorerHandle;
    long lastHeaderAt, lastPacketAt, lastSourceSequence, completePackets;
    int lastPacketKind, imageSendError;
    volatile string readPhase = "not started";
    volatile bool connected;
    string imageSendResult = "not attempted";
    internal nint ExplorerWindow => target;
    internal uint ExplorerProcess { get; private set; }
    internal uint ExplorerThread { get; private set; }
    internal long ExplorerStarted { get; private set; }
    internal uint Nonce => nonce;
    internal bool IsConnected => connected && !token.IsCancellationRequested;
    public TrayHookSession(CancellationToken token) => this.token = token;

    public async Task ReadAsync(Action<uint, ulong, byte[]> receive, Action connected)
    {
        readPhase = "opening";
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || RuntimeInformation.OSArchitecture != Architecture.X64 ||
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new NotSupportedException(Loc.T("systemTrayUnsupported"));
        var path = Path.Combine(AppContext.BaseDirectory, "WinDock.TrayHook.v7.x64.dll");
        if (!File.Exists(path) || !File.Exists(path + ".sha256")) throw new FileNotFoundException(Loc.T("systemTrayMissing"));
        using (var stream = File.OpenRead(path))
        {
            var expected = (await File.ReadAllTextAsync(path + ".sha256", token)).Trim();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Loc.T("systemTrayHashFailed"));
        }
        var explorer = SystemTrayInterop.ExplorerTray();
        if (explorer.Window == 0 || explorer.Thread == 0) throw new IOException(Loc.T("systemTrayExplorerMissing"));
        explorerHandle = Native.OpenProcess(0x101000, false, (int)explorer.Process);
        var explorerPath = new StringBuilder(32768); var explorerPathLength = explorerPath.Capacity;
        if (explorerHandle == 0 || SystemTrayInterop.WaitForSingleObject(explorerHandle, 0) != 258
            || !SystemTrayInterop.GetProcessTimes(explorerHandle, out var started, out _, out _, out _) || started <= 0
            || !Native.QueryFullProcessImageName(explorerHandle, 0, explorerPath, ref explorerPathLength)
            || !string.Equals(explorerPath.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)
            || Native.GetWindowThreadProcessId(explorer.Window, out var explorerOwner) != explorer.Thread || explorerOwner != explorer.Process
            || Native.ClassOf(explorer.Window) != "Shell_TrayWnd") throw new IOException(Loc.T("systemTrayExplorerMissing"));
        target = explorer.Window;
        ExplorerProcess = explorer.Process;
        ExplorerThread = explorer.Thread;
        ExplorerStarted = started;
        pipe = SystemTrayInterop.CreateTrayPipe($"WinDock.Tray.{Environment.ProcessId}.{nonce:X8}");
        var connection = pipe.WaitForConnectionAsync(token);
        module = NativeLibrary.Load(path);
        var protocol = Marshal.GetDelegateForFunctionPointer<SystemTrayInterop.ProtocolVersion>(NativeLibrary.GetExport(module, "WinDockTrayProtocol"));
        if (protocol() != Version) throw new InvalidDataException(Loc.T("systemTrayVersionFailed"));
        returnHook = SystemTrayInterop.SetWindowsHookExW(12, NativeLibrary.GetExport(module, "WinDockTrayReturnHook"), module, explorer.Thread);
        if (returnHook == 0) throw new IOException(Loc.T("systemTrayHookFailed"));
        hook = SystemTrayInterop.SetWindowsHookExW(4, NativeLibrary.GetExport(module, "WinDockTrayHook"), module, explorer.Thread);
        if (hook == 0) throw new IOException(Loc.T("systemTrayHookFailed"));
        var connectMessage = SystemTrayInterop.RegisterWindowMessage("WinDock.TrayHook.Connect.v7");
        if (SystemTrayInterop.SendMessageTimeoutW(target, connectMessage, (nuint)Environment.ProcessId, (nint)nonce, 0x3, 250, out _) == 0)
            throw new IOException(Loc.T("systemTrayHookFailed"));
        await connection.WaitAsync(TimeSpan.FromSeconds(4), token);
        if (!SystemTrayInterop.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != explorer.Process)
            throw new InvalidDataException(Loc.T("systemTraySourceFailed"));
        bool ready = false;
        byte[] header = new byte[16];
        while (!token.IsCancellationRequested)
        {
            readPhase = "waiting header";
            await pipe.ReadExactlyAsync(header, token);
            Interlocked.Exchange(ref lastHeaderAt, DateTime.UtcNow.Ticks);
            var magic = BitConverter.ToUInt32(header, 0); var version = BitConverter.ToUInt32(header, 4);
            var kind = BitConverter.ToUInt32(header, 8); var length = BitConverter.ToUInt32(header, 12);
            if (magic != Magic || version != Version || length < 16 || length > 1000 + 256 * 256 * 4 || kind is < 1 or > 6)
                throw new InvalidDataException(Loc.T("systemTrayDataFailed"));
            byte[] body = new byte[length - 16];
            readPhase = "waiting body";
            if (body.Length > 0) await pipe.ReadExactlyAsync(body, token);
            Interlocked.Exchange(ref lastPacketAt, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref lastPacketKind, (int)kind);
            Interlocked.Increment(ref completePackets);
            readPhase = "dispatching";
            if (kind == 2)
            {
                if (ready || body.Length != 0) throw new InvalidDataException(Loc.T("systemTrayDataFailed"));
                ready = true; this.connected = true; connected();
            }
            else
            {
                if (!ready || (kind is 1 or 4 && body.Length < 984) || (kind == 3 && body.Length != 8)
                    || (kind == 5 && body.Length < 80) || (kind == 6 && body.Length != 24))
                    throw new InvalidDataException(Loc.T("systemTrayDataFailed"));
                var sequence = BitConverter.ToUInt64(body, kind is 5 or 6 ? 8 : 0);
                if (sequence == 0 || (kind is 5 or 6 && BitConverter.ToUInt32(body, 0) != nonce))
                    throw new InvalidDataException(Loc.T("systemTrayDataFailed"));
                Interlocked.Exchange(ref lastSourceSequence, unchecked((long)sequence));
                // Registration parsing keeps its existing, audited offsets.
                // Images retain the complete identity and session envelope.
                receive(kind, sequence, kind is 1 or 4 ? body.AsSpan(8).ToArray() : body);
            }
        }
    }

    internal bool RequestImages()
    {
        imageSendError = 0;
        var window = target;
        if (window == 0 || !IsConnected) { imageSendResult = "inactive"; return false; }
        var thread = Native.GetWindowThreadProcessId(window, out var process);
        if (thread == 0 || thread != ExplorerThread || process != ExplorerProcess || Native.ClassOf(window) != "Shell_TrayWnd")
        { imageSendResult = "target changed"; return false; }
        var sent = SystemTrayInterop.SendNotifyMessageW(window,
                SystemTrayInterop.RegisterWindowMessage("WinDock.TrayHook.Images.v7"),
                (nuint)Environment.ProcessId, (nint)nonce);
        if (!sent) imageSendError = Marshal.GetLastPInvokeError();
        imageSendResult = sent ? "sent" : "send failed";
        return sent;
    }

    internal object Observe() => new
    {
        readPhase, connected = IsConnected,
        lastHeaderAt = TrayDiagnostics.Time(Interlocked.Read(ref lastHeaderAt)),
        lastPacketAt = TrayDiagnostics.Time(Interlocked.Read(ref lastPacketAt)),
        lastKind = Volatile.Read(ref lastPacketKind),
        lastSourceSequence = unchecked((ulong)Interlocked.Read(ref lastSourceSequence)),
        packets = Interlocked.Read(ref completePackets),
        explorerWindow = target.ToInt64(), explorerProcess = ExplorerProcess, explorerThread = ExplorerThread,
        imageSendResult, imageSendError
    };

    public void Dispose()
    {
        connected = false;
        // Remove only our own hook. Never mutate Explorer's window procedure,
        // taskbar visibility, registry, or another application's hook chain.
        if (target != 0 && hook != 0)
            SystemTrayInterop.SendMessageTimeoutW(target, SystemTrayInterop.RegisterWindowMessage("WinDock.TrayHook.Disconnect.v7"),
                (nuint)Environment.ProcessId, (nint)nonce, 0x3, 150, out _);
        if (hook != 0) { SystemTrayInterop.UnhookWindowsHookEx(hook); hook = 0; }
        if (returnHook != 0) { SystemTrayInterop.UnhookWindowsHookEx(returnHook); returnHook = 0; }
        pipe?.Dispose(); pipe = null;
        if (module != 0) { NativeLibrary.Free(module); module = 0; }
        if (explorerHandle != 0) { Native.CloseHandle(explorerHandle); explorerHandle = 0; }
        target = 0;
        ExplorerProcess = 0;
        ExplorerThread = 0; ExplorerStarted = 0;
        readPhase = "closed";
    }
}
