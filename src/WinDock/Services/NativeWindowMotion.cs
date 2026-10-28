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
    const int ConfigBytes = 12384, StatusBytes = 96, ActivationBytes = 64, Capacity = 256, Build = 11;
    const uint Magic = 0x4d444e57;
    const string SystemHash = "de3a7bacdcbb49a98bfff41010e772d18a329b77d9bc6b2416e1bc9f5b415863";
    public static NativeWindowMotion Shared { get; } = new();
    public readonly record struct Anchor(nint Window, uint Pid, string? Exe, Rect Bounds, uint Flags = 0, int Screen = 0);
    sealed record Remembered(Anchor Anchor, long Until);
    sealed record Request(uint Pid, bool Minimized, long Until);
    sealed record MotionDiagnostic(bool Connected, uint PublishedFlags, int ComponentPid, int ComponentBuild,
        int ComponentState, int ComponentError, long Opened, long Closed, long Minimized, long Restored,
        long Hidden, long Skipped, long Failed);

    readonly Dictionary<object, Func<IEnumerable<Anchor>>> providers = [];
    readonly Dictionary<(nint, uint, ulong, int), Remembered> history = [];
    readonly Dictionary<nint, Request> requests = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly byte[] packet = new byte[ConfigBytes];
    DockOptions? options;
    SharedMap? config;
    SharedMap? status;
    bool activating, disposed, suspended;
    bool configuringLogin, refreshingLogin;
    int sequence;
    string? errorKey;
    string? failureCode;
    long started;
    MotionDiagnostic? lastDiagnostic;
    Task? diagnosticWrite;
    long diagnosticWrittenAt;

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
        _ = RefreshLoginAsync(true);
    }

    void LanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatus();
    void Tick(object? sender, EventArgs e) => Publish();
    void Changed(string name)
    {
        if (name == nameof(DockOptions.NativeWindowAnimations))
        {
            suspended = false;
            errorKey = null;
            failureCode = null;
        }
        Publish();
        if (!configuringLogin && (name is nameof(DockOptions.NativeWindowAnimations) or nameof(DockOptions.Enabled)))
            _ = RefreshLoginAsync(true);
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
        if (!minimized) WindowTracker.Focus(window);
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
        && !IncompatibleComponent
        && SystemParameters.ClientAreaAnimation && SystemParameters.MinimizeAnimation
        ? 1u | (options.WindowOpenAnimation ? 2u : 0) | (options.WindowCloseAnimation ? 4u : 0)
            | (options.WindowMotion ? 8u : 0) | (options.WindowRestoreAnimation ? 16u : 0)
            | (options.WindowHideAnimation ? 32u : 0) : 0;

    public void Publish()
    {
        if (disposed || options is null) return;
        var flags = Flags;
        if (config is not null)
        {
            var now = Environment.TickCount64;
            foreach (var key in history.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) history.Remove(key);
            foreach (var key in requests.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray()) requests.Remove(key);
            if (flags != 0)
                foreach (var provider in providers.Values)
                    foreach (var anchor in provider()) Add(anchor, 1500);
            var anchors = history.Values.OrderByDescending(a => a.Until).Take(Capacity).Select(a => a.Anchor).ToArray();
            Array.Clear(packet);
            U32(0, Magic); U32(4, 1); U32(8, ConfigBytes);
            U32(16, (uint)Environment.ProcessId); U32(20, flags); U64(24, (ulong)now);
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
        RecordStatus(flags);
    }

    void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), value);
    void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(offset, 8), value);
    void Float(int offset, double value) => BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(offset, 4), (float)value);
    void Bounds(int offset, Rect rect) { Float(offset, rect.X); Float(offset + 4, rect.Y); Float(offset + 8, rect.Width); Float(offset + 12, rect.Height); }

    bool Connected => status is not null && unchecked((uint)Marshal.ReadInt32(status.View)) == Magic
        && Marshal.ReadInt32(status.View, 4) == 1 && Marshal.ReadInt32(status.View, 8) == StatusBytes
        && Environment.TickCount64 - Marshal.ReadInt64(status.View, 32) is >= 0 and < 1500;

    bool IncompatibleComponent => Connected && Marshal.ReadInt32(status!.View, 24) != Build;

    void UpdateStatus()
    {
        if (options is null) return;
        string key;
        if (activating) key = "nativeMotionActivating";
        else if (!options.NativeWindowAnimations) key = "nativeMotionOff";
        else if (errorKey is not null) key = errorKey;
        else if (suspended) key = "nativeMotionPaused";
        else if (IncompatibleComponent) key = "nativeMotionSignOut";
        else if (Flags == 0) key = "nativeMotionSystemOff";
        else if (!Connected) key = "nativeMotionNeedsActivation";
        else key = Marshal.ReadInt32(status!.View, 16) switch
        {
            1 => MotionActivityKey(),
            2 => "nativeMotionUnsupported",
            3 => "nativeMotionInitializationFailed",
            _ => "nativeMotionActivating"
        };
        options.WindowAnimationStatus = Loc.T(key) + (errorKey == key && failureCode is not null ? $" ({failureCode})" : "");
    }

    string MotionActivityKey()
    {
        if ((Flags & 62) == 0) return "nativeMotionActionsOff";
        if (Marshal.ReadInt32(status!.View, 20) is 8 or 9 or 11 or 13 or 15 or 16 or 17 or 18)
            return "nativeMotionFallback";
        var processed = Enumerable.Range(0, 5).Sum(index => Marshal.ReadInt64(status!.View, 40 + index * 8));
        if (processed > 0) return "nativeMotionProcessing";
        return Marshal.ReadInt64(status!.View, 80) > 0 ? "nativeMotionNotProcessing" : "nativeMotionWaitingForWindow";
    }

    void RecordStatus(uint publishedFlags)
    {
        var now = Environment.TickCount64;
        if (now - diagnosticWrittenAt < 2000 || diagnosticWrite is { IsCompleted: false }) return;
        var connected = Connected;
        int Read32(int offset) => connected ? Marshal.ReadInt32(status!.View, offset) : 0;
        long Read64(int offset) => connected ? Marshal.ReadInt64(status!.View, offset) : 0;
        var snapshot = new MotionDiagnostic(connected, publishedFlags, Read32(12), Read32(24), Read32(16), Read32(20),
            Read64(40), Read64(48), Read64(56), Read64(64), Read64(72), Read64(80), Read64(88));
        if (snapshot == lastDiagnostic) return;
        lastDiagnostic = snapshot;
        diagnosticWrittenAt = now;
        var entry = new { at = DateTimeOffset.Now, clientBuild = Build, status = snapshot,
            reason = TransitionReason(snapshot.ComponentError) };
        diagnosticWrite = Task.Run(() =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "motion-status.json"),
                    JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }

    static string TransitionReason(int code) => code switch
    {
        0 => "none",
        8 => "window-state-lock-not-owned",
        9 => "timeline-traversal-in-progress",
        11 => "native-timeline-unavailable",
        12 => "window-excluded",
        13 => "window-surface-unavailable",
        14 => "window-geometry-excluded",
        15 => "invalid-animation-pose",
        16 => "tracking-capacity-exhausted",
        17 => "native-allocation-failed",
        18 => "native-action-mismatch",
        19 => "window-data-unavailable",
        20 => "special-clip-excluded",
        21 => "native-visibility-excluded",
        22 => "window-style-excluded",
        23 => "dock-or-system-window-excluded",
        24 => "owned-window-excluded",
        25 => "shell-window-excluded",
        26 => "window-identity-unavailable",
        _ => "component-initialization-error"
    };

    async Task RefreshLoginAsync(bool startIfReady)
    {
        if (refreshingLogin || disposed || options is null) return;
        refreshingLogin = true;
        try
        {
            var run = startIfReady && Flags != 0 && !Connected;
            var enabled = await Task.Run(() => InspectLoginTask(run));
            if (!disposed && options is not null) options.WindowAnimationLoginEnabled = enabled;
        }
        finally { refreshingLogin = false; }
    }

    static bool InspectLoginTask(bool run)
    {
        var objects = new List<object>();
        dynamic Keep(object value) { objects.Add(value); return value; }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (sid is null) return false;
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "WinDock.Motion.manifest.json")));
            var root = manifest.RootElement;
            var payload = root.GetProperty("payload").GetProperty("sha256").GetString();
            var host = root.GetProperty("loginHost").GetProperty("sha256").GetString();
            static bool Digest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
            if (!Digest(payload) || !Digest(host)) return false;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinDock.NativeMotion", payload!);
            var expected = Path.Combine(directory, $"WinDock.MotionLogin.{host}.exe");
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return false;
            dynamic service = Keep(Activator.CreateInstance(type)!);
            service.Connect();
            dynamic folder = Keep((object)service.GetFolder("\\"));
            dynamic task = Keep((object)folder.GetTask("WinDock.NativeMotion." + sid));
            dynamic definition = Keep((object)task.Definition);
            dynamic principal = Keep((object)definition.Principal);
            dynamic actions = Keep((object)definition.Actions);
            dynamic triggers = Keep((object)definition.Triggers);
            if (!(bool)task.Enabled || (string)principal.UserId != sid || (int)principal.LogonType != 3
                || (int)principal.RunLevel != 1 || (int)actions.Count != 1 || (int)triggers.Count != 1) return false;
            dynamic action = Keep((object)actions[1]);
            dynamic trigger = Keep((object)triggers[1]);
            if ((int)action.Type != 0 || (int)trigger.Type != 9 || (string)trigger.UserId != sid || !(bool)trigger.Enabled
                || !expected.Equals((string)action.Path, StringComparison.OrdinalIgnoreCase)
                || !directory.Equals((string)action.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
                || (string)action.Arguments != "--login " + sid) return false;
            if (run) Keep((object)task.Run(null));
            return true;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        finally
        {
            for (var i = objects.Count - 1; i >= 0; --i)
                if (Marshal.IsComObject(objects[i])) Marshal.FinalReleaseComObject(objects[i]);
        }
    }

    public async Task SetLoginEnabledAsync(bool enabled)
    {
        if (configuringLogin || activating || disposed || options is null) return;
        configuringLogin = true;
        try
        {
            var validation = await Task.Run(() => ValidatePackage(enabled));
            if (validation is not null) { errorKey = validation; return; }
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value;
            if (sid is null) { errorKey = "nativeMotionAccessDenied"; return; }
            using var process = await Task.Run(() => Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "WinDock.MotionHost.exe"),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { enabled ? "--register-login" : "--remove-login", sid }
            }));
            if (process is null) { errorKey = "nativeMotionLoginFailed"; return; }
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                errorKey = "nativeMotionLoginFailed";
                failureCode = process.ExitCode.ToString();
                return;
            }
            errorKey = null; failureCode = null;
            if (enabled) { options.NativeWindowAnimations = true; options.Flush(); }
        }
        catch (Win32Exception e)
        {
            errorKey = e.NativeErrorCode == 1223 ? "nativeMotionCanceled" : "nativeMotionAccessDenied";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException
            or CryptographicException or KeyNotFoundException or FormatException) { errorKey = "nativeMotionFilesChanged"; }
        finally
        {
            configuringLogin = false;
            await RefreshLoginAsync(false);
            UpdateStatus();
        }
    }

    public async Task ActivateAsync()
    {
        if (activating || disposed || options is null) return;
        if (config is null || status is null) { errorKey = "nativeMotionMappingFailed"; UpdateStatus(); return; }
        options.NativeWindowAnimations = true;
        options.Flush();
        if (Connected)
        {
            errorKey = Marshal.ReadInt32(status.View, 24) != Build ? "nativeMotionSignOut" : null;
            failureCode = null;
            UpdateStatus(); return;
        }
        if (Flags == 0) { UpdateStatus(); return; }
        activating = true; errorKey = null; failureCode = null; Publish();
        SharedMap? report = null;
        int? exitCode = null, helperPid = null, windowsError = null;
        try
        {
            var validation = await Task.Run(() => ValidatePackage());
            if (validation is not null) { errorKey = validation; return; }
            if (!options.NativeWindowAnimations || disposed) { errorKey = "nativeMotionCanceled"; return; }
            Publish();
            var nonce = Guid.NewGuid().ToString("N");
            using var current = Process.GetCurrentProcess();
            report = new($"Local\\WinDock.Motion.Activation.v1.{current.SessionId}.{nonce}", ActivationBytes, null);
            using var process = await Task.Run(() => Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "WinDock.MotionHost.exe"),
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { nonce }
            }));
            if (process is null) { errorKey = "nativeMotionFailed"; return; }
            helperPid = process.Id;
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
            errorKey = exitCode switch
            {
                0 => null,
                10 or 11 => "nativeMotionMappingFailed",
                13 => "nativeMotionMissing",
                14 => "nativeMotionFilesChanged",
                15 or 17 => "nativeMotionAccessDenied",
                12 => "nativeMotionRequestExpired",
                16 or 18 or 19 => "nativeMotionCompositorUnavailable",
                20 or 21 or 22 or 24 => "nativeMotionLoadFailed",
                23 => "nativeMotionActivationTimeout",
                25 => "nativeMotionSignOut",
                26 => "nativeMotionCacheFailed",
                40 => "nativeMotionAccessDenied",
                41 => "nativeMotionUnsupported",
                _ => "nativeMotionFailed"
            };
            if (errorKey is null)
            {
                for (var i = 0; i < 20 && !Connected && !disposed; ++i) await Task.Delay(150);
                if (!Connected) errorKey = "nativeMotionNoResponse";
                else if (Marshal.ReadInt32(status.View, 24) != Build) errorKey = "nativeMotionSignOut";
                else errorKey = Marshal.ReadInt32(status.View, 16) switch
                {
                    1 => null,
                    2 => "nativeMotionUnsupported",
                    3 => "nativeMotionInitializationFailed",
                    _ => "nativeMotionNoResponse"
                };
            }
        }
        catch (Win32Exception e)
        {
            windowsError = e.NativeErrorCode;
            errorKey = e.NativeErrorCode == 1223 ? "nativeMotionCanceled" : "nativeMotionAccessDenied";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or CryptographicException or KeyNotFoundException or FormatException)
        { errorKey = "nativeMotionFilesChanged"; }
        finally
        {
            RecordActivation(exitCode, helperPid, windowsError, report);
            report?.Dispose();
            activating = false; UpdateStatus();
        }
    }

    void RecordActivation(int? exitCode, int? helperPid, int? windowsError, SharedMap? report)
    {
        var view = report?.View ?? 0;
        var hasReport = view != 0 && unchecked((uint)Marshal.ReadInt32(view)) == Magic
            && Marshal.ReadInt32(view, 4) == 1 && Marshal.ReadInt32(view, 8) == ActivationBytes
            && Marshal.ReadInt32(view, 12) == helperPid && Marshal.ReadInt32(view, 36) == Build;
        var stage = hasReport ? Marshal.ReadInt32(view, 16) : (int?)null;
        if (hasReport) windowsError = Marshal.ReadInt32(view, 24);
        if (errorKey is not null && errorKey != "nativeMotionCanceled")
        {
            failureCode = exitCode is not null and not 0 ? exitCode.Value.ToString() : null;
            if (windowsError is not null and not 0)
                failureCode = failureCode is null ? windowsError.Value.ToString() : $"{failureCode}/{windowsError}";
        }
        var connected = Connected;
        if (errorKey == "nativeMotionInitializationFailed" && connected)
        {
            failureCode = Marshal.ReadInt32(status!.View, 20).ToString();
            var componentError = Marshal.ReadInt32(status.View, 28);
            if (componentError != 0) failureCode += $"/{componentError}";
        }
        var entry = new
        {
            at = DateTimeOffset.Now, build = Build, outcome = errorKey ?? "nativeMotionReady", helperPid, exitCode, stage,
            windowsError, helperReportedResult = hasReport ? Marshal.ReadInt32(view, 20) : (int?)null,
            remoteThreadResult = hasReport ? unchecked((uint)Marshal.ReadInt32(view, 28)) : (uint?)null,
            compositorPid = hasReport ? Marshal.ReadInt32(view, 32) : (int?)null,
            componentState = connected ? Marshal.ReadInt32(status!.View, 16) : (int?)null,
            componentError = connected ? Marshal.ReadInt32(status!.View, 20) : (int?)null,
            componentWindowsError = connected ? Marshal.ReadInt32(status!.View, 28) : (int?)null,
            componentBuild = connected ? Marshal.ReadInt32(status!.View, 24) : (int?)null,
            opened = connected ? Marshal.ReadInt64(status!.View, 40) : (long?)null,
            closed = connected ? Marshal.ReadInt64(status!.View, 48) : (long?)null,
            minimized = connected ? Marshal.ReadInt64(status!.View, 56) : (long?)null,
            restored = connected ? Marshal.ReadInt64(status!.View, 64) : (long?)null,
            hidden = connected ? Marshal.ReadInt64(status!.View, 72) : (long?)null,
            skipped = connected ? Marshal.ReadInt64(status!.View, 80) : (long?)null,
            failed = connected ? Marshal.ReadInt64(status!.View, 88) : (long?)null
        };
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "motion-activation.json"), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string? ValidatePackage(bool requireSupportedSystem = true)
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
        if (!requireSupportedSystem) return null;
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

        public SharedMap(string name, int bytes, bool? writableByCompositor)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            var access = writableByCompositor == true ? "0x0006" : "0x0004";
            var sddl = $"D:P(A;;GA;;;{sid})(A;;GA;;;SY)(A;;GA;;;BA)"
                + (writableByCompositor.HasValue ? $"(A;;{access};;;S-1-5-90-0)" : "");
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
