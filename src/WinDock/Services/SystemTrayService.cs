using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinDock.Services;

public sealed class SystemTrayItem : INotifyPropertyChanged
{
    public string Key { get; internal set; } = "";
    public nint Window { get; internal set; }
    public uint ProcessId { get; internal set; }
    public uint ThreadId { get; internal set; }
    public uint Id { get; internal set; }
    public uint Callback { get; internal set; }
    public uint Version { get; internal set; }
    internal bool RegistrationObserved { get; set; }
    internal bool CallbackObserved { get; set; }
    internal bool VersionObserved { get; set; }
    internal bool IconObserved { get; set; }
    internal bool TipObserved { get; set; }
    internal bool NativeSnapshotObserved { get; set; }
    internal bool NativeModelObserved { get; set; }
    internal bool HiddenObserved { get; set; }
    internal long ProcessStarted { get; set; }
    public Guid Guid { get; internal set; }
    public string Title { get; internal set; } = "";
    public string ProcessPath { get; internal set; } = "";
    public string ApplicationName { get; internal set; } = "";
    public string OwnerClass { get; internal set; } = "";
    public string StableId => Guid != Guid.Empty ? "guid:" + Guid.ToString("D")
        : ProcessPath.Length > 0 ? $"exe:{ProcessPath.ToLowerInvariant()}:{OwnerClass}:{Id}" : "";
    public string Tip { get; internal set; } = "";
    public bool ShowStandardTip { get; internal set; }
    public string? HoverTip => Version >= 4 && CanInvoke && !ShowStandardTip ? null :
        Title.Length == 0 || Tip.Contains(Title, StringComparison.OrdinalIgnoreCase) ? (Tip.Length > 0 ? Tip : null) :
        Tip.Length == 0 ? Title : Title + "\n" + Tip;
    public BitmapSource? Icon { get; internal set; }
    public bool Hidden { get; internal set; }
    public bool HasHint { get; internal set; }
    public bool CanInvoke => CallbackObserved && VersionObserved && Callback >= 0x400 && Callback <= 0xFFFF && Window != 0;
    public string AccessibleName => Title.Length > 0 ? Title : Loc.T("systemTrayUnnamed");
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed record TrayHint(SystemTrayItem Icon, string Title, string Text, DateTime Received);

public sealed class SystemTrayService : INotifyPropertyChanged, IDisposable
{
    readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    readonly DispatcherTimer prune = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly DispatcherTimer recovery = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer diagnosticTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly TrayDiagnostics diagnostics = new();
    readonly Dictionary<string, SystemTrayItem> known = [];
    readonly HashSet<SystemTrayItem> retainedHints = [];
    readonly TrayIdentity identities = new();
    readonly Dictionary<nint, (long At, int Attempts)> requestedOwners = [];
    readonly Dictionary<uint, long> requestedProcesses = [];
    readonly Dictionary<string, byte[]> bootstrapCandidates = [];
    readonly Dictionary<string, uint> pendingVersions = [];
    readonly object pendingLock = new();
    readonly Queue<(uint Kind, byte[] Data)> pending = [];
    volatile CancellationTokenSource? lifetime;
    TrayHookSession? activeSession;
    Task? runner;
    bool scheduled, ready;
    int pendingBytes;
    int sessionNumber;
    long registrationGeneration;
    long receivedPackets, nativeLossReports, interfaceLossReports, registrationPackets, processedRegistrationPackets, rejectedOwners, rejectedRegistrations, confirmedCandidates;
    int nativeSnapshotRecords, nativeModelRecords, nativeModelApplied;
    bool snapshotPending;
    string? connectionError;
    string status = Loc.T("systemTrayOff");
    DateTime refreshAt, recoveryAt, snapshotAt;
    public ObservableCollection<SystemTrayItem> Icons { get; } = [];
    public ObservableCollection<TrayHint> Hints { get; } = [];
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool Ready { get => ready; private set { ready = value; Notify(); } }
    public bool HasStatusNotice { get; private set; }
    internal long RegistrationGeneration => Interlocked.Read(ref registrationGeneration);
    internal long ProtocolSequence => Interlocked.Read(ref registrationPackets);
    internal bool ProtocolCurrent => Interlocked.Read(ref processedRegistrationPackets) == Interlocked.Read(ref registrationPackets);
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LayoutChanged;
    public event Action<SystemTrayItem>? FocusRequested;

    public SystemTrayService()
    {
        diagnosticTimer.Tick += (_, _) => RecordStatus();
        Loc.Instance.PropertyChanged += LanguageChanged;
        recovery.Tick += (_, _) =>
        {
            if (!Ready) { recovery.Stop(); return; }
            var now = DateTime.UtcNow;
            if (now - refreshAt < TimeSpan.FromSeconds(10) || now - recoveryAt < TimeSpan.FromSeconds(30)) return;
            recoveryAt = now;
            RequestRefresh();
        };
        prune.Tick += (_, _) =>
        {
            foreach (var item in Icons.ToArray())
            {
                if (!OwnerIsCurrent(item.Window, item.ProcessId, item.ProcessStarted, item.ThreadId, item.OwnerClass)) Remove(item);
            }
            identities.Keep(Icons.Select(item => item.ProcessId));
            var owners = Icons.Select(item => item.Window).ToHashSet();
            foreach (var window in requestedOwners.Keys.Where(window => !owners.Contains(window)
                && (!Native.IsWindow(window) || Environment.TickCount64 - requestedOwners[window].At > 120_000)).ToArray()) requestedOwners.Remove(window);
            var processes = Icons.Select(item => item.ProcessId).ToHashSet();
            foreach (var pid in requestedProcesses.Keys.Where(pid => !processes.Contains(pid)
                && Environment.TickCount64 - requestedProcesses[pid] > 120_000).ToArray()) requestedProcesses.Remove(pid);
            if (Ready) RequestNativeSnapshot();
        };
    }

    void LanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var item in Icons) { item.Title = TrayIdentity.Name(item); item.Refresh(); }
        LayoutChanged?.Invoke();
    }

    public void Start()
    {
        if (lifetime is not null) return;
        lifetime = new CancellationTokenSource(); var run = lifetime;
        Status = Loc.T("systemTrayConnecting"); prune.Start();
        diagnosticTimer.Start(); RecordStatus();
        runner = Task.Run(() => Run(run));
    }

    async Task Run(CancellationTokenSource run)
    {
        var token = run.Token;
        while (!token.IsCancellationRequested)
        {
            var session = new TrayHookSession(token);
            try
            {
                using (session)
                {
                    if (!await dispatcher.InvokeAsync(() => BeginSession(run, session), DispatcherPriority.Normal, token).Task.ConfigureAwait(false)) break;
                    await session.ReadAsync((kind, data) => Queue(run, session, kind, data), () => dispatcher.BeginInvoke(() =>
                    {
                        if (!IsCurrent(run, session)) return;
                        HasStatusNotice = false; Ready = true; Status = Loc.T("systemTrayConnected");
                        refreshAt = recoveryAt = default; RequestRefresh();
                    }));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or NotSupportedException or
                System.ComponentModel.Win32Exception or BadImageFormatException or EntryPointNotFoundException or DllNotFoundException)
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (!IsCurrent(run, session)) return;
                    Ready = false; recovery.Stop(); ClearIcons();
                    connectionError = error.GetType().Name;
                    Status = error switch
                    {
                        EndOfStreamException or TimeoutException => Loc.T("systemTrayReconnecting"),
                        BadImageFormatException or EntryPointNotFoundException => Loc.T("systemTrayVersionFailed"),
                        DllNotFoundException => Loc.T("systemTrayMissing"),
                        UnauthorizedAccessException => Loc.T("systemTrayHookFailed"),
                        _ => error.Message
                    };
                    lock (pendingLock) { pending.Clear(); pendingBytes = 0; scheduled = false; processedRegistrationPackets = registrationPackets; }
                });
                // Missing/untrusted/incompatible components require an explicit
                // retry after repair; never repeatedly load the same bad DLL.
                if (error is FileNotFoundException or InvalidDataException or NotSupportedException or BadImageFormatException or EntryPointNotFoundException or DllNotFoundException)
                    break;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    bool BeginSession(CancellationTokenSource run, TrayHookSession session)
    {
        if (lifetime != run) return false;
        lock (pendingLock)
        {
            activeSession = session;
            pending.Clear(); pendingBytes = 0; scheduled = false;
            processedRegistrationPackets = registrationPackets;
        }
        Ready = false; recovery.Stop(); ClearIcons();
        ++sessionNumber; connectionError = null;
        snapshotAt = default;
        Interlocked.Exchange(ref receivedPackets, 0);
        Interlocked.Exchange(ref nativeLossReports, 0);
        Interlocked.Exchange(ref interfaceLossReports, 0);
        rejectedOwners = 0;
        rejectedRegistrations = confirmedCandidates = 0;
        return true;
    }

    bool IsCurrent(CancellationTokenSource run, TrayHookSession session)
    {
        lock (pendingLock) return lifetime == run && ReferenceEquals(activeSession, session);
    }

    void Queue(CancellationTokenSource run, TrayHookSession session, uint kind, byte[] data)
    {
        lock (pendingLock)
        {
            if (lifetime != run || !ReferenceEquals(activeSession, session)) return;
            if (kind is 1 or 4) Interlocked.Increment(ref receivedPackets);
            else if (kind == 3) Interlocked.Increment(ref nativeLossReports);
            // A model snapshot includes callback/version as well as identity.
            // Invalidate it when any accepted protocol change arrives, even if
            // its UI-thread packet has not yet been drained.
            if (kind == 3 || (kind == 1 && data.Length >= 36 && (BitConverter.ToUInt32(data, 0) is 0 or 2 or 4
                || (BitConverter.ToUInt32(data, 0) == 1 && (BitConverter.ToUInt32(data, 32) & 1) != 0))))
                Interlocked.Increment(ref registrationPackets);
            if (pending.Count >= 256 || pendingBytes + data.Length > 8 * 1024 * 1024)
            { Interlocked.Increment(ref interfaceLossReports); Interlocked.Increment(ref registrationPackets); pending.Clear(); pendingBytes = 0; pending.Enqueue((3, [])); }
            pending.Enqueue((kind, data));
            pendingBytes += data.Length;
            if (scheduled) return;
            scheduled = true;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, () => Drain(run, session));
    }

    void Drain(CancellationTokenSource run, TrayHookSession session)
    {
        (uint Kind, byte[] Data)[] batch;
        long batchSequence;
        lock (pendingLock)
        {
            if (lifetime != run || !ReferenceEquals(activeSession, session)) return;
            batch = pending.ToArray(); pending.Clear(); pendingBytes = 0; scheduled = false;
            batchSequence = registrationPackets;
        }
        if (!Ready) return;
        try
        {
            foreach (var (kind, data) in batch)
            {
                if (kind == 3)
                {
                    InvalidateProtocol(Icons.ToArray(), modelOnly: false);
                    bootstrapCandidates.Clear(); pendingVersions.Clear();
                    HasStatusNotice = true; Status = Loc.T("systemTrayUpdatesSkipped"); recovery.Start(); continue;
                }
                Apply(data, candidate: kind == 4);
            }
        }
        catch (InvalidDataException) { Stop(); Status = Loc.T("systemTrayDataFailed"); }
        finally { Interlocked.Exchange(ref processedRegistrationPackets, batchSequence); }
    }

    void Apply(byte[] bytes, bool candidate = false, bool presenceConfirmed = false)
    {
        // Header is 20 bytes followed by the fixed 956-byte Explorer payload.
        if (bytes.Length < 976) throw new InvalidDataException();
        uint Read(int offset) => BitConverter.ToUInt32(bytes, offset);
        var operation = Read(0); var pid = Read(4); var width = Read(8); var height = Read(12); var pixels = Read(16);
        var size = Read(20); var rawWindow = Read(24); var window = (nint)(int)rawWindow; var id = Read(28); var flags = Read(32);
        var callback = Read(36); var state = Read(300); var stateMask = Read(304); var version = Read(820);
        var useGuid = (flags & 0x20) != 0;
        var guid = useGuid ? new Guid(bytes.AsSpan(956, 16)) : Guid.Empty;
        if (operation > 4 || (candidate && operation != 0) || size is < 24 or > 956 || width > 256 || height > 256 || (width == 0) != (height == 0)
            || pixels != width * height * 4 || bytes.Length != 976 + pixels || (useGuid && (size < 952 || guid == Guid.Empty)))
            throw new InvalidDataException();
        var item = useGuid ? Icons.FirstOrDefault(i => i.Guid == guid)
            : Icons.FirstOrDefault(i => i.Guid == Guid.Empty && i.Window == window && i.Id == id);
        var key = item?.Key ?? (useGuid ? guid.ToString() : $"{rawWindow:X8}:{id}:{pid}");
        if (!candidate && operation is 0 or 2)
        { BumpRegistrationGeneration(); bootstrapCandidates.Remove(key); pendingVersions.Remove(key); }
        if (operation == 2) { if (item is not null && (useGuid || pid == 0 || item.ProcessId == pid)) Remove(item); return; }
        if (useGuid && !candidate && operation is 1 or 3 or 4)
        {
            // Explorer addresses these operations by GUID and keeps the
            // registered owner. The request's HWND/ID cannot replace it.
            if (item is null || (!item.RegistrationObserved && !item.NativeModelObserved)) { RequestNativeSnapshot(); return; }
            window = item.Window; pid = item.ProcessId; id = item.Id;
        }
        if (window == 0 && item is not null && useGuid)
        { window = item.Window; pid = item.ProcessId; id = item.Id; }
        var ownerThread = Native.GetWindowThreadProcessId(window, out var actualPid);
        if (pid == 0 || pid != actualPid || !Native.IsWindow(window)) { ++rejectedOwners; return; }
        var ownerClass = Native.ClassOf(window);
        if (candidate && !presenceConfirmed)
        {
            ++rejectedRegistrations;
            // A GUID-only GetRect lookup proves that GUID exists, not that it
            // belongs to the rejected request's HWND/PID. Require an already
            // accepted owner association before using such a candidate.
            if (useGuid && (item is null || item.Window != window || item.ProcessId != pid || item.Id != id)) return;
            // A failed ADD does not establish that an icon exists. Confirm it
            // through the shell before using any of its missing display fields.
            if (item is null || (!item.IconObserved && item.Icon is null) || (!item.TipObserved && item.Tip.Length == 0)
                || (!item.CallbackObserved && item.Callback == 0))
                QueueBootstrap(key, bytes, window, pid, id, guid);
            return;
        }
        if (item is not null && (item.ProcessId != pid || item.ThreadId != ownerThread || item.OwnerClass != ownerClass))
        {
            if (candidate || operation is 3 or 4) return;
            Remove(item); item = null;
        }
        if (candidate && item is not null && (item.Window != window || item.Id != id)) return;
        if (operation == 3) { if (item is not null) FocusRequested?.Invoke(item); return; }
        if (operation == 4)
        {
            if (version <= 4 && size >= 804)
            {
                if (item is not null) { item.Version = version; item.VersionObserved = true; item.Refresh(); }
                else
                {
                    // An app may acceptably set its existing icon's version
                    // before our delayed presence probe has produced its row.
                    if (pendingVersions.Count < 512) pendingVersions[key] = version;
                    RequestRegistration(window, pid);
                }
            }
            return;
        }
        if (useGuid && item is not null && operation == 1) id = item.Id;
        bool added = item is null;
        if (added)
        {
            if (known.Count >= 512) return;
            item = new SystemTrayItem { Window = window, ProcessId = pid, ThreadId = ownerThread, Id = id, OwnerClass = ownerClass, Guid = guid };
            item.Key = item.Guid == Guid.Empty ? $"{rawWindow:X8}:{id}:{pid}" : item.Guid.ToString();
            known[item.Key] = item;
            if (pendingVersions.Remove(item.Key, out var observedVersion))
            { item.Version = observedVersion; item.VersionObserved = true; }
        }
        var wasHidden = item!.Hidden;
        item.Window = window; item.Id = id;
        if (operation == 0 && !candidate)
        {
            // Only an accepted ADD creates a new registration and resets its
            // protocol. Stale fields from an earlier registration cannot leak in.
            item.Version = 0; item.RegistrationObserved = true; item.VersionObserved = true;
            item.NativeModelObserved = false;
            item.Callback = 0; item.CallbackObserved = true;
            item.Tip = ""; item.TipObserved = true; item.ShowStandardTip = false; item.Hidden = false;
            item.HiddenObserved = true;
            item.Icon = null; item.IconObserved = true;
        }
        if ((flags & 1) != 0 && (!candidate || (item.Callback == 0 && !item.CallbackObserved)))
        {
            item.Callback = callback;
            if (!candidate) item.CallbackObserved = true;
        }
        if ((flags & 4) != 0 && (!candidate || (!item.TipObserved && item.Tip.Length == 0)))
        {
            item!.Tip = ReadText(bytes, 44, 128);
            item.ShowStandardTip = (flags & 0x80) != 0;
            if (!candidate) item.TipObserved = true;
        }
        else if (!candidate && (flags & 0x80) != 0) item.ShowStandardTip = true;
        if (!candidate && (flags & 8) != 0 && (stateMask & 1) != 0)
        { item!.Hidden = (state & 1) != 0; item.HiddenObserved = true; }
        if ((flags & 2) != 0 && (!candidate || (!item.IconObserved && item.Icon is null)))
        {
            item!.Icon = pixels == 0 ? null : BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Pbgra32,
                null, bytes.AsSpan(976).ToArray(), (int)width * 4);
            item.Icon?.Freeze();
            if (!candidate) item.IconObserved = true;
        }
        if (!candidate && (flags & 0x50) == 0x10)
        {
            var text = ReadText(bytes, 308, 256); var title = ReadText(bytes, 824, 64);
            if (!string.IsNullOrWhiteSpace(text))
            {
                item!.HasHint = true;
                Hints.Insert(0, new TrayHint(item, title, text, DateTime.Now));
                while (Hints.Count > 50) Hints.RemoveAt(Hints.Count - 1);
            }
        }
        item!.Title = TrayIdentity.Name(item);
        item.Refresh();
        if (added) { Icons.Add(item); _ = ResolveIdentity(item); }
        else if (wasHidden != item.Hidden) LayoutChanged?.Invoke();
        if (operation == 1 && !item.RegistrationObserved && (!item.CallbackObserved || !item.VersionObserved))
            RequestRegistration(item.Window, item.ProcessId);
    }

    void QueueBootstrap(string key, byte[] bytes, nint window, uint pid, uint id, Guid guid)
    {
        if (lifetime is not { } run || activeSession is not { } session || bootstrapCandidates.ContainsKey(key)
            || bootstrapCandidates.Count >= 32) return;
        bootstrapCandidates[key] = bytes;
        _ = ResolveBootstrap();

        async Task ResolveBootstrap()
        {
            bool exists = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                exists = await TrayRegistrationProbe.ExistsAsync(window, pid, id, guid,
                    session.ExplorerWindow, session.ExplorerProcess, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            if (run.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
            await dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrent(run, session) || !bootstrapCandidates.TryGetValue(key, out var pending)
                    || !ReferenceEquals(pending, bytes)) return;
                bootstrapCandidates.Remove(key);
                if (!Ready || !exists) return;
                ++confirmedCandidates;
                Apply(bytes, candidate: true, presenceConfirmed: true);
            });
        }
    }

    void RequestRegistration(nint window, uint pid)
    {
        if ((!requestedOwners.ContainsKey(window) && requestedOwners.Count >= 512)
            || (!requestedProcesses.ContainsKey(pid) && requestedProcesses.Count >= 512)) return;
        var requested = requestedOwners.GetValueOrDefault(window, (At: long.MinValue / 2, Attempts: 0));
        var processAt = requestedProcesses.GetValueOrDefault(pid, long.MinValue / 2);
        var now = Environment.TickCount64;
        if (window != 0 && pid != 0 && requested.Attempts < 2 && now - requested.At > 30_000 && now - processAt > 30_000)
        {
            requestedOwners[window] = (now, requested.Attempts + 1);
            requestedProcesses[pid] = now;
            var cancellation = lifetime?.Token ?? CancellationToken.None;
            _ = Task.Run(() => TrayReRegistration.RequestOwner(window, pid, cancellation), cancellation);
        }
    }

    async Task ResolveIdentity(SystemTrayItem item)
    {
        var run = lifetime;
        var identity = await identities.Read(item.ProcessId);
        if (lifetime != run || !known.TryGetValue(item.Key, out var current) || !ReferenceEquals(item, current)) return;
        if (!OwnerIsCurrent(item.Window, item.ProcessId, item.ProcessStarted != 0 ? item.ProcessStarted : identity.Started,
            item.ThreadId, item.OwnerClass)) return;
        if (item.ProcessStarted != 0 && identity.Started != item.ProcessStarted) return;
        if (identity.Path.Length > 0) item.ProcessPath = identity.Path;
        if (identity.Name.Length > 0) item.ApplicationName = identity.Name;
        if (identity.Started != 0) item.ProcessStarted = identity.Started;
        if (identity.Started != 0)
        {
            var prior = retainedHints.Where(old => old.Key == item.Key && old.Window == item.Window
                && old.ProcessId == item.ProcessId && old.ProcessStarted == identity.Started).ToHashSet();
            if (prior.Count > 0)
            {
                item.HasHint |= prior.Any(old => old.HasHint);
                for (var index = 0; index < Hints.Count; ++index)
                    if (prior.Contains(Hints[index].Icon)) Hints[index] = Hints[index] with { Icon = item };
                retainedHints.ExceptWith(prior);
            }
        }
        item.Title = TrayIdentity.Name(item); item.Refresh(); LayoutChanged?.Invoke();
    }

    static string ReadText(byte[] data, int offset, int characters) => Encoding.Unicode.GetString(data, offset, characters * 2).Split('\0')[0];
    void Remove(SystemTrayItem item)
    { BumpRegistrationGeneration(); bootstrapCandidates.Remove(item.Key); pendingVersions.Remove(item.Key); Icons.Remove(item); known.Remove(item.Key); }
    void ClearIcons()
    {
        BumpRegistrationGeneration();
        nativeSnapshotRecords = 0;
        nativeModelRecords = nativeModelApplied = 0;
        retainedHints.RemoveWhere(old => !Hints.Any(hint => ReferenceEquals(hint.Icon, old)));
        foreach (var item in Icons)
            if (item.HasHint && item.ProcessStarted != 0 && Hints.Any(hint => ReferenceEquals(hint.Icon, item))) retainedHints.Add(item);
        Icons.Clear(); known.Clear(); identities.Clear(); requestedOwners.Clear(); requestedProcesses.Clear(); bootstrapCandidates.Clear(); pendingVersions.Clear();
    }

    public void MarkSeen(SystemTrayItem item) { item.HasHint = false; item.Refresh(); }
    public void ClearHints() { Hints.Clear(); retainedHints.Clear(); foreach (var item in Icons) MarkSeen(item); }

    public void RequestRefresh()
    {
        if (!Ready || DateTime.UtcNow - refreshAt < TimeSpan.FromSeconds(10)) return;
        recovery.Stop();
        refreshAt = DateTime.UtcNow;
        HasStatusNotice = false; Status = Loc.T("systemTrayConnected");
        var owners = Icons.Select(icon => (icon.Window, icon.ProcessId)).Distinct().ToArray();
        var cancellation = lifetime?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            try { TrayReRegistration.Request(owners, cancellation); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        });
        RequestNativeSnapshot();
    }

    void BumpRegistrationGeneration()
    {
        Interlocked.Increment(ref registrationGeneration);
        Notify(nameof(RegistrationGeneration));
    }

    void RequestNativeSnapshot()
    {
        if (snapshotPending || DateTime.UtcNow - snapshotAt < TimeSpan.FromSeconds(30)
            || lifetime is not { } run || activeSession is not { } session) return;
        snapshotAt = DateTime.UtcNow;
        snapshotPending = true;
        _ = ReadSnapshot();

        async Task ReadSnapshot()
        {
            var retry = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                // Let the application's initial re-registration packets settle.
                await Task.Delay(750, timeout.Token);
                if (!Ready || !IsCurrent(run, session)) return;
                Drain(run, session);
                if (!Ready || !ProtocolCurrent) { retry = Ready; return; }
                var generation = RegistrationGeneration;
                var packets = Interlocked.Read(ref registrationPackets);
                var model = await NativeTrayModelReader.ReadAsync(session.ExplorerWindow, session.ExplorerProcess, timeout.Token);
                if (!Ready || !IsCurrent(run, session) || generation != RegistrationGeneration
                    || packets != Interlocked.Read(ref registrationPackets) || !ProtocolCurrent || timeout.IsCancellationRequested)
                { retry = Ready && !timeout.IsCancellationRequested; return; }
                nativeModelRecords = model.Count;
                nativeModelApplied = 0;
                if (model.Count > 0)
                {
                    var merged = new List<SystemTrayItem>();
                    foreach (var record in model)
                        if (MergeNativeModel(record) is { } item) { merged.Add(item); ++nativeModelApplied; }
                    // Explorer can change while this UI-thread merge runs. Do
                    // not expose an earlier protocol until queued changes drain.
                    if (packets != Interlocked.Read(ref registrationPackets))
                    {
                        InvalidateProtocol(merged);
                        Drain(run, session);
                        retry = true; return;
                    }
                    if (!Icons.Any(item => (!item.IconObserved && item.Icon is null) || (!item.TipObserved && item.Tip.Length == 0))) return;
                    // Current model identity/protocol can coexist with display
                    // metadata from the older inventory. Our own merges changed
                    // the generation, so establish a fresh race barrier here.
                    generation = RegistrationGeneration;
                    packets = Interlocked.Read(ref registrationPackets);
                }
                var records = await NativeTraySnapshot.ReadAsync(session.ExplorerWindow, session.ExplorerProcess, timeout.Token);
                if (!Ready || !IsCurrent(run, session) || generation != RegistrationGeneration
                    || packets != Interlocked.Read(ref registrationPackets) || !ProtocolCurrent || timeout.IsCancellationRequested)
                { retry = Ready && !timeout.IsCancellationRequested; return; }
                nativeSnapshotRecords = records.Count;
                foreach (var record in records) MergeNativeRegistration(record);
                if (packets != Interlocked.Read(ref registrationPackets)) { Drain(run, session); retry = true; }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException
                or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            finally
            {
                snapshotPending = false;
                if (retry && Ready && IsCurrent(run, session))
                {
                    snapshotAt = default;
                    _ = dispatcher.BeginInvoke(DispatcherPriority.Background, RequestNativeSnapshot);
                }
            }
        }
    }

    void InvalidateProtocol(IEnumerable<SystemTrayItem> items, bool modelOnly = true)
    {
        var changed = false;
        foreach (var item in items)
        {
            if ((modelOnly && !item.NativeModelObserved) || !known.TryGetValue(item.Key, out var current) || !ReferenceEquals(current, item)) continue;
            item.NativeModelObserved = item.CallbackObserved = item.VersionObserved = false;
            if (!modelOnly) item.RegistrationObserved = item.HiddenObserved = false;
            item.Refresh(); changed = true;
        }
        if (changed) BumpRegistrationGeneration();
    }

    SystemTrayItem? MergeNativeModel(NativeTrayModelReader.Record record)
    {
        if (record.ProcessStarted <= 0 || !OwnerIsCurrent(record.Window, record.ProcessId, record.ProcessStarted, record.ThreadId, record.OwnerClass)
            || (record.Callback != 0 && record.Callback is < 0x400 or > 0xFFFF) || record.Version is not (0 or 3 or 4)) return null;
        var key = record.Guid == Guid.Empty ? $"{unchecked((uint)record.Window):X8}:{record.Id}:{record.ProcessId}" : record.Guid.ToString();
        var added = !known.TryGetValue(key, out var item);
        if (!added && (item!.Window != record.Window || item.ProcessId != record.ProcessId || item.Id != record.Id
            || item.ThreadId != record.ThreadId || item.OwnerClass != record.OwnerClass
            || (item.ProcessStarted != 0 && item.ProcessStarted != record.ProcessStarted)))
        {
            // A live, stable model can establish a new GUID owner. Keep old
            // hint history separate; never reuse its callback or pending probe.
            Remove(item!); added = true; item = null;
        }
        if (added)
        {
            if (known.Count >= 512) return null;
            item = new SystemTrayItem
            {
                Key = key, Window = record.Window, ProcessId = record.ProcessId, ThreadId = record.ThreadId, Id = record.Id, Guid = record.Guid,
                OwnerClass = record.OwnerClass, ProcessPath = record.ProcessPath, ProcessStarted = record.ProcessStarted
            };
            known[key] = item;
        }
        var wasHidden = item!.Hidden;
        if (added || !item.CallbackObserved || !item.VersionObserved || item.Callback != record.Callback || item.Version != record.Version)
            BumpRegistrationGeneration();
        // Unlike a historical inventory or a rejected ADD request, this reader
        // validates the live native object and its complete registration fields.
        item.Callback = record.Callback; item.Version = record.Version;
        item.CallbackObserved = item.VersionObserved = item.NativeModelObserved = true;
        item.ProcessStarted = record.ProcessStarted;
        if (record.ProcessPath.Length > 0) item.ProcessPath = record.ProcessPath;
        if (!item.IconObserved && item.Icon is null && record.Icon is not null) item.Icon = record.Icon;
        if (!item.TipObserved && item.Tip.Length == 0 && record.Tip is not null) item.Tip = record.Tip;
        if (record.Hidden is { } hidden) { item.Hidden = hidden; item.HiddenObserved = true; }
        // A pending historical candidate must never overwrite this protocol.
        pendingVersions.Remove(key);
        item.Title = TrayIdentity.Name(item); item.Refresh();
        if (added) { Icons.Add(item); _ = ResolveIdentity(item); }
        else if (wasHidden != item.Hidden) LayoutChanged?.Invoke();
        // Display data may be absent from the supported native layout. Ask the
        // owner for its existing icon without fabricating an HICON or tooltip.
        if (added || item.Icon is null) RequestRegistration(item.Window, item.ProcessId);
        return item;
    }

    static bool OwnerIsCurrent(nint window, uint processId, long processStarted, uint threadId, string windowClass)
    {
        var thread = Native.GetWindowThreadProcessId(window, out var current);
        if (window == 0 || processId == 0 || current != processId || thread == 0 || thread != threadId
            || !Native.IsWindow(window) || Native.ClassOf(window) != windowClass) return false;
        if (processStarted == 0) return true;
        var process = Native.OpenProcess(0x1000, false, (int)processId);
        if (process == 0) return false;
        try { return GetProcessTimes(process, out var started, out _, out _, out _) && started == processStarted; }
        finally { Native.CloseHandle(process); }
    }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessTimes(nint process, out long started, out long exited, out long kernel, out long user);

    void MergeNativeRegistration(NativeTrayRegistration record)
    {
        if (!OwnerIsCurrent(record.Window, record.ProcessId, record.ProcessStarted, record.ThreadId, record.WindowClass)) return;
        var key = record.Guid == Guid.Empty ? $"{unchecked((uint)record.Window):X8}:{record.Id}:{record.ProcessId}" : record.Guid.ToString();
        var added = !known.TryGetValue(key, out var item);
        // A historical GUID row plus GetRect does not prove current ownership:
        // the GUID lookup ignores HWND/ID. Only supplement an association that
        // was already observed through the live protocol; never invent one.
        if (added && record.Guid != Guid.Empty) return;
        if (record.Guid != Guid.Empty && (item!.ProcessStarted == 0 || item.ProcessStarted != record.ProcessStarted)) return;
        if (!added && (item!.Window != record.Window || item.ProcessId != record.ProcessId || item.Id != record.Id
            || item.ThreadId != record.ThreadId || item.OwnerClass != record.WindowClass
            || (item.ProcessStarted != 0 && item.ProcessStarted != record.ProcessStarted))) return;
        if (added)
        {
            if (known.Count >= 512) return;
            item = new SystemTrayItem
            {
                Key = key, Window = record.Window, ProcessId = record.ProcessId, ThreadId = record.ThreadId, Id = record.Id, Guid = record.Guid,
                OwnerClass = record.WindowClass, ProcessPath = record.Executable, ProcessStarted = record.ProcessStarted
            };
            // The inventory carries identity/display metadata, not the callback,
            // version or NIS_HIDDEN state. Its preference is never a state mask.
            if (pendingVersions.Remove(key, out var observedVersion))
            { item.Version = observedVersion; item.VersionObserved = true; }
            known[key] = item;
            BumpRegistrationGeneration();
        }
        item!.NativeSnapshotObserved = true;
        if (!item.IconObserved && item.Icon is null) item.Icon = record.Icon;
        if (!item.TipObserved && item.Tip.Length == 0) item.Tip = record.Tip;
        if (item.ProcessPath.Length == 0) item.ProcessPath = record.Executable;
        if (item.ProcessStarted == 0) item.ProcessStarted = record.ProcessStarted;
        item.Title = TrayIdentity.Name(item); item.Refresh();
        if (added) { Icons.Add(item); _ = ResolveIdentity(item); }
        if (!item.CallbackObserved || !item.VersionObserved) RequestRegistration(item.Window, item.ProcessId);
    }

    public bool Invoke(SystemTrayItem item, uint message, Point physicalPoint)
    {
        if (!ProtocolCurrent && lifetime is { } run && activeSession is { } session) Drain(run, session);
        if (!Ready || !ProtocolCurrent || !item.CanInvoke || !known.TryGetValue(item.Key, out var current) || !ReferenceEquals(item, current)) return false;
        if (!OwnerIsCurrent(item.Window, item.ProcessId, item.ProcessStarted, item.ThreadId, item.OwnerClass)) { Remove(item); return false; }
        if (!ProtocolCurrent) return false;
        var x = (short)Math.Clamp(Math.Round(physicalPoint.X), short.MinValue, short.MaxValue);
        var y = (short)Math.Clamp(Math.Round(physicalPoint.Y), short.MinValue, short.MaxValue);
        if (message is >= 0x201 and <= 0x209 or 0x7B or 0x400 or 0x401)
            SystemTrayInterop.AllowSetForegroundWindow(item.ProcessId);
        nuint wParam = item.Version >= 4 ? unchecked((uint)(ushort)x | ((uint)(ushort)y << 16)) : item.Id;
        nint lParam = item.Version >= 4 ? unchecked((nint)((item.Id & 0xFFFF) << 16 | (message & 0xFFFF))) : (nint)message;
        return item.ProcessId == Environment.ProcessId
            ? SystemTrayInterop.PostMessageW(item.Window, item.Callback, wParam, lParam)
            : SystemTrayInterop.SendNotifyMessageW(item.Window, item.Callback, wParam, lParam);
    }

    public void Stop()
    {
        var run = lifetime; lifetime = null; run?.Cancel();
        if (run is not null && runner is { } completing)
            _ = completing.ContinueWith(_ => run.Dispose(), TaskScheduler.Default);
        runner = null;
        // The runner owns cancellation registrations until it completes.
        prune.Stop(); recovery.Stop(); diagnosticTimer.Stop(); Ready = false; ClearIcons(); Hints.Clear(); retainedHints.Clear(); Status = Loc.T("systemTrayOff");
        lock (pendingLock) { activeSession = null; pending.Clear(); pendingBytes = 0; scheduled = false; processedRegistrationPackets = registrationPackets; }
        RecordStatus();
    }

    void RecordStatus() => diagnostics.Publish(new
    {
        enabled = lifetime is not null,
        ready = Ready,
        session = sessionNumber,
        connectionError,
        receivedPackets = Interlocked.Read(ref receivedPackets),
        nativeLossReports = Interlocked.Read(ref nativeLossReports),
        interfaceLossReports = Interlocked.Read(ref interfaceLossReports),
        rejectedOwners,
        rejectedRegistrations,
        confirmedCandidates,
        nativeSnapshotRecords,
        nativeSnapshotStatus = NativeTraySnapshot.Status,
        nativeSnapshotCallbacks = NativeTraySnapshot.CallbackCount,
        nativeSnapshotCopied = NativeTraySnapshot.CopiedCount,
        nativeSnapshotValidated = NativeTraySnapshot.ValidatedCount,
        nativeModelStatus = NativeTrayModelReader.Status,
        nativeModelCandidates = NativeTrayModelReader.CandidateCount,
        nativeModelValidated = NativeTrayModelReader.ValidatedCount,
        nativeModelRecords,
        nativeModelApplied,
        protocolCurrent = ProtocolCurrent,
        icons = Icons.Select(item => new
        {
            window = item.Window.ToInt64(),
            processId = item.ProcessId,
            threadId = item.ThreadId,
            executable = Path.GetFileName(item.ProcessPath),
            applicationName = item.ApplicationName,
            ownerClass = item.OwnerClass,
            id = item.Id,
            guid = item.Guid,
            callback = item.Callback,
            version = item.Version,
            registrationObserved = item.RegistrationObserved,
            callbackObserved = item.CallbackObserved,
            versionObserved = item.VersionObserved,
            nativeSnapshotObserved = item.NativeSnapshotObserved,
            nativeModelObserved = item.NativeModelObserved,
            hidden = item.Hidden,
            hiddenObserved = item.HiddenObserved,
            hasIcon = item.Icon is not null,
            canInvoke = item.CanInvoke
        }).ToArray()
    });
    public void Dispose() { Loc.Instance.PropertyChanged -= LanguageChanged; Stop(); }
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
