using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using WinDock.ViewModels;

namespace WinDock.Services;

sealed class NativeWindowMotion : IDisposable
{
    const int ConfigBytes = 12384, StatusBytes = 96, Capacity = 256, Build = 7;
    const uint Magic = 0x4d444e57;
    const string SystemHash = "de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863";
    public static NativeWindowMotion Shared { get; } = new();
    public readonly record struct Anchor(nint Window, uint Pid, string? Exe, Rect Bounds, uint Flags = 0, int Screen = 0);
    sealed record Remembered(Anchor Anchor, long Until);
    sealed record Request(uint Pid, bool Minimized, long Until);

    readonly Dictionary<object, Func<IEnumerable<Anchor>>> providers = [];
    readonly Dictionary<(nint, uint, ulong, int), Remembered> history = [];
    readonly Dictionary<nint, Request> requests = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly byte[] packet = new byte[ConfigBytes];
    DockOptions? options;
    SharedMap? config;
    SharedMap? status;
    bool activating, disposed, suspended;
    int sequence;
    string? errorKey;
    long started;

    public void Initialize(DockOptions value)
    {
        if (options is not null || disposed) return;
        options = value;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            config = new($"Local\\WinDock.Motion.Config.v1.{process.SessionId}", ConfigBytes, false);
            status = new($"Local\\WinDock.Motion.Status.v1.{process.SessionId}", StatusBytes, true);
        }
        catch (Exception e) when (e is Win32Exception or UnauthorizedAccessException or IOException)
        {
            errorKey = "nativeMotionMappingFailed";
            config?.Dispose(); config = null;
            status?.Dispose(); status = null;
        }
        options.Changed += Changed;
        Loc.Instance.PropertyChanged += LanguageChanged;
        timer.Tick += Tick;
        timer.Start();
        Publish();
    }

    void LanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatus();
    void Tick(object? sender, EventArgs e) => Publish();
    void Changed(string name)
    {
        if (name == nameof(DockOptions.NativeWindowAnimations))
        {
            suspended = false;
            errorKey = null;
        }
        Publish();
    }

    public void Register(object owner, Func<IEnumerable<Anchor>> source) { providers[owner] = source; Publish(); }
    public void Unregister(object owner) { providers.Remove(owner); Publish(); }
    public void Suspend() { suspended = true; Publish(); }

    public void PrepareLaunch(AppTile app, Rect anchor)
    {
        if (Flags == 0 || !Valid(anchor)) return;
        Add(new(0, 0, app.Opener()?.Exe ?? app.Exe ?? app.Pin?.ResolvedTarget, anchor, 1, int.MinValue), 8000);
        Publish();
    }

    public bool TryReverse(nint window, Rect anchor)
    {
        if (!requests.TryGetValue(window, out var request) || request.Until < Environment.TickCount64) return false;
        Native.GetWindowThreadProcessId(window, out var pid);
        if (unchecked((uint)pid) != request.Pid) { requests.Remove(window); return false; }
        RequestState(window, anchor, !request.Minimized);
        return true;
    }

    public void RequestState(nint window, Rect anchor, bool minimized)
    {
        if (!Native.IsWindow(window)) return;
        Native.GetWindowThreadProcessId(window, out var pid);
        Add(new(window, unchecked((uint)pid), null, anchor, 1, int.MinValue), 1500);
        var duration = minimized ? options?.WindowMotionDuration ?? 320 : options?.WindowRestoreDuration ?? 240;
        if (Flags != 0 && Connected && Marshal.ReadInt32(status!.View, 16) == 1)
            requests[window] = new(unchecked((uint)pid), minimized, Environment.TickCount64 + duration);
        else requests.Remove(window);
        Publish();
        WindowMotionNative.SetState(window, minimized);
        if (!minimized) WindowTracker.Activate(window);
    }

    static bool Valid(Rect rect) => !rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Y)
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) && rect.Width > 0 && rect.Height > 0;

    static ulong PathHash(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        ulong hash = 14695981039346656037;
        foreach (var original in path)
        {
            var c = original is >= 'a' and <= 'z' ? (char)(original - 32) : original == '/' ? '\\' : original;
            hash = unchecked((hash ^ c) * 1099511628211);
        }
        return hash;
    }

    void Add(Anchor anchor, int lifetime)
    {
        if (!Valid(anchor.Bounds)) return;
        history[(anchor.Window, anchor.Pid, PathHash(anchor.Exe), anchor.Screen)] = new(anchor, Environment.TickCount64 + lifetime);
    }

    uint Flags => options is { NativeWindowAnimations: true, Enabled: true } && !suspended
        && SystemParameters.ClientAreaAnimation && SystemParameters.MinimizeAnimation
        ? 1u | (options.WindowOpenAnimation ? 2u : 0) | (options.WindowCloseAnimation ? 4u : 0)
            | (options.WindowMotion ? 8u : 0) | (options.WindowRestoreAnimation ? 16u : 0)
            | (options.WindowHideAnimation ? 32u : 0) : 0;

    public void Publish()
    {
        if (disposed || options is null) return;
        if (config is not null)
        {
            var now = Environment.TickCount64;
            foreach (var key in history.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) history.Remove(key);
            foreach (var key in requests.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) requests.Remove(key);
            if (Flags != 0)
                foreach (var provider in providers.Values)
                    foreach (var anchor in provider()) Add(anchor, 1500);
            var anchors = history.Values.OrderByDescending(a => a.Until).Take(Capacity).Select(a => a.Anchor).ToArray();
            Array.Clear(packet);
            U32(0, Magic); U32(4, 1); U32(8, ConfigBytes);
            U32(16, (uint)Environment.ProcessId); U32(20, Flags); U64(24, (ulong)now);
            U32(32, (uint)options.WindowOpenDuration); U32(36, (uint)options.WindowCloseDuration);
            U32(40, (uint)options.WindowMotionDuration); U32(44, (uint)options.WindowRestoreDuration);
            Float(48, options.WindowCloseDistance); Float(52, options.WindowCloseScale); Float(56, options.WindowFadeHold);
            U32(60, options.WindowMotionCurve == "quick" ? 1u : 0); U32(64, (uint)anchors.Length);
            U64(72, (ulong)started);
            var fallback = anchors.FirstOrDefault(a => (a.Flags & 2) != 0).Bounds;
            if (Valid(fallback)) Bounds(80, fallback);
            for (var i = 0; i < anchors.Length; ++i)
            {
                var a = anchors[i]; var offset = 96 + 48 * i;
                U64(offset, (ulong)a.Window); U32(offset + 8, a.Pid); U32(offset + 12, a.Flags); U64(offset + 16, PathHash(a.Exe));
                Bounds(offset + 32, a.Bounds);
            }
            sequence = unchecked(sequence + 2);
            Marshal.WriteInt32(config.View, 12, sequence | 1);
            Thread.MemoryBarrier();
            Marshal.Copy(packet, 0, config.View, 12);
            Marshal.Copy(packet, 16, config.View + 16, packet.Length - 16);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(config.View, 12, sequence);
        }
        UpdateStatus();
    }

    void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), value);
    void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(offset, 8), value);
    void Float(int offset, double value) => BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset, 4), (float)value);
    void Bounds(int offset, Rect rect) { Float(offset, rect.X); Float(offset + 4, rect.Y); Float(offset + 8, rect.Width); Float(offset + 12, rect.Height); }

    bool Connected => status is not null && unchecked((uint)Marshal.ReadInt32(status.View)) == Magic
        && Marshal.ReadInt32(status.View, 4) == 1 && Marshal.ReadInt32(status.View, 8) == StatusBytes
        && Environment.TickCount64 - Marshal.ReadInt64(status.View, 32) is >= 0 and < 1500;

    void UpdateStatus()
    {
        if (options is null) return;
        string key;
        if (activating) key = "nativeMotionActivating";
        else if (!options.NativeWindowAnimations) key = "nativeMotionOff";
        else if (errorKey is not null) key = errorKey;
        else if (suspended) key = "nativeMotionPaused";
        else if (Flags == 0) key = "nativeMotionSystemOff";
        else if (!Connected) key = "nativeMotionNeedsActivation";
        else if (Marshal.ReadInt32(status!.View, 24) != Build) key = "nativeMotionSignOut";
        else key = Marshal.ReadInt32(status.View, 16) switch
        {
            1 => "nativeMotionReady",
            2 => "nativeMotionUnsupported",
            3 => "nativeMotionFailed",
            _ => "nativeMotionActivating"
        };
        options.WindowAnimationStatus = Loc.T(key);
    }

    public async Task ActivateAsync()
    {
        if (activating || disposed || options is null) return;
        if (config is null || status is null) { errorKey = "nativeMotionMappingFailed"; UpdateStatus(); return; }
        options.NativeWindowAnimations = true;
        options.Flush();
        if (Flags == 0) { UpdateStatus(); return; }
        if (Connected)
        {
            errorKey = Marshal.ReadInt32(status.View, 24) != Build ? "nativeMotionSignOut" : null;
            UpdateStatus(); return;
        }
        activating = true; errorKey = null; Publish();
        try
        {
            var validation = await Task.Run(ValidatePackage);
            if (validation is not null) { errorKey = validation; return; }
            if (!options.NativeWindowAnimations || disposed) return;
            Publish();
            using var process = await Task.Run(() => Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "WinDock.MotionHost.exe"),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            }));
            if (process is null) { errorKey = "nativeMotionFailed"; return; }
            await process.WaitForExitAsync();
            errorKey = process.ExitCode switch
            {
                0 => null,
                14 => "nativeMotionFilesChanged",
                15 or 17 or 22 => "nativeMotionAccessDenied",
                12 => "nativeMotionRequestExpired",
                _ => "nativeMotionFailed"
            };
            if (errorKey is null)
            {
                for (var i = 0; i < 20 && !Connected && !disposed; ++i) await Task.Delay(150);
                if (!Connected) errorKey = "nativeMotionFailed";
            }
        }
        catch (Win32Exception e) { errorKey = e.NativeErrorCode == 1223 ? "nativeMotionCanceled" : "nativeMotionAccessDenied"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or CryptographicException or KeyNotFoundException or FormatException)
        { errorKey = "nativeMotionFilesChanged"; }
        finally { activating = false; UpdateStatus(); }
    }

    static string? ValidatePackage()
    {
        if (!Environment.Is64BitProcess || RuntimeInformation.ProcessArchitecture != Architecture.X64) return "nativeMotionUnsupported";
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "WinDock.Motion.manifest.json");
        if (!File.Exists(manifestPath)) return "nativeMotionMissing";
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        if (root.GetProperty("protocol").GetInt32() != 1 || root.GetProperty("build").GetInt32() != Build
            || root.GetProperty("supportedImageSha256").GetString() != SystemHash) return "nativeMotionFilesChanged";
        foreach (var (entry, name) in new[] { ("payload", "WinDock.Motion.x64.dll"), ("host", "WinDock.MotionHost.exe") })
        {
            var item = root.GetProperty(entry);
            if (item.GetProperty("name").GetString() != name) return "nativeMotionFilesChanged";
            var file = Path.Combine(AppContext.BaseDirectory, name);
            if (!File.Exists(file)) return "nativeMotionMissing";
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                return "nativeMotionFilesChanged";
        }
        using var system = File.OpenRead(Path.Combine(Environment.SystemDirectory, "udwm.dll"));
        return Convert.ToHexString(SHA256.HashData(system)).Equals(SystemHash, StringComparison.OrdinalIgnoreCase) ? null : "nativeMotionUnsupported";
    }

    public void Dispose()
    {
        if (disposed) return;
        suspended = true; Publish();
        disposed = true;
        timer.Stop(); timer.Tick -= Tick;
        if (options is not null) options.Changed -= Changed;
        Loc.Instance.PropertyChanged -= LanguageChanged;
        providers.Clear(); history.Clear(); requests.Clear();
        config?.Dispose(); status?.Dispose(); config = null; status = null;
    }

    sealed class SharedMap : IDisposable
    {
        nint handle;
        public nint View { get; private set; }

        public SharedMap(string name, int bytes, bool writableByCompositor)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            var access = writableByCompositor ? "0x0006" : "0x0004";
            var sddl = $"D:P(A;;GA;;;{sid})(A;;GA;;;SY)(A;;GA;;;BA)(A;;{access};;;S-1-5-90-0)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new Win32Exception();
            try
            {
                var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
                handle = CreateFileMapping(new nint(-1), ref security, 4, 0, (uint)bytes, name);
                if (handle == 0) throw new Win32Exception();
                View = MapViewOfFile(handle, 6, 0, 0, (nuint)bytes);
                if (View == 0) { var error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
            }
            finally { LocalFree(descriptor); }
        }

        public void Dispose()
        {
            if (View != 0) UnmapViewOfFile(View);
            if (handle != 0) CloseHandle(handle);
            View = handle = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
        [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint bytes);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern nint CreateFileMapping(nint file, ref SecurityAttributes security, uint protection, uint high, uint low, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern nint MapViewOfFile(nint mapping, uint access, uint high, uint low, nuint bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(nint view);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
        [DllImport("kernel32.dll")] static extern nint LocalFree(nint memory);
    }
}
