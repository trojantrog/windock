using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    public uint Id { get; internal set; }
    public uint Callback { get; internal set; }
    public uint Version { get; internal set; }
    public Guid Guid { get; internal set; }
    public string Title { get; internal set; } = "";
    public string Tip { get; internal set; } = "";
    public bool ShowStandardTip { get; internal set; }
    public string? HoverTip => (Version >= 4 && !ShowStandardTip) || Tip.Length == 0 ? null : Tip;
    public BitmapSource? Icon { get; internal set; }
    public bool Hidden { get; internal set; }
    public bool HasHint { get; internal set; }
    public bool CanInvoke => Callback >= 0x400 && Callback <= 0xFFFF && Window != 0;
    public string AccessibleName => Title.Length > 0 ? Title : Loc.T("systemTrayUnnamed");
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

public sealed record TrayHint(SystemTrayItem Icon, string Title, string Text, DateTime Received);

public sealed class SystemTrayService : INotifyPropertyChanged, IDisposable
{
    readonly Dispatcher dispatcher = Application.Current.Dispatcher;
    readonly DispatcherTimer prune = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly Dictionary<string, SystemTrayItem> known = [];
    readonly object pendingLock = new();
    readonly Queue<(uint Kind, byte[] Data)> pending = [];
    volatile CancellationTokenSource? lifetime;
    Task? runner;
    bool scheduled, ready;
    int pendingBytes;
    string status = Loc.T("systemTrayOff");
    DateTime refreshAt;
    public ObservableCollection<SystemTrayItem> Icons { get; } = [];
    public ObservableCollection<TrayHint> Hints { get; } = [];
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool Ready { get => ready; private set { ready = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LayoutChanged;

    public SystemTrayService()
    {
        prune.Tick += (_, _) =>
        {
            foreach (var item in Icons.ToArray())
            {
                Native.GetWindowThreadProcessId(item.Window, out var pid);
                if (pid != item.ProcessId || !Native.IsWindow(item.Window)) Remove(item);
            }
        };
    }

    public void Start()
    {
        if (lifetime is not null) return;
        lifetime = new CancellationTokenSource(); var run = lifetime;
        Status = Loc.T("systemTrayConnecting"); prune.Start();
        runner = Task.Run(() => Run(run));
    }

    async Task Run(CancellationTokenSource run)
    {
        var token = run.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var session = new TrayHookSession(token);
                await session.ReadAsync((kind, data) => Queue(run, kind, data), () => dispatcher.BeginInvoke(() =>
                {
                    if (lifetime != run) return;
                    Ready = true; Status = Loc.T("systemTrayConnected"); RequestRefresh();
                }));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or NotSupportedException or
                System.ComponentModel.Win32Exception or BadImageFormatException or EntryPointNotFoundException or DllNotFoundException)
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (lifetime != run) return;
                    Ready = false; ClearIcons();
                    Status = error switch
                    {
                        EndOfStreamException or TimeoutException => Loc.T("systemTrayReconnecting"),
                        BadImageFormatException or EntryPointNotFoundException => Loc.T("systemTrayVersionFailed"),
                        DllNotFoundException => Loc.T("systemTrayMissing"),
                        UnauthorizedAccessException => Loc.T("systemTrayHookFailed"),
                        _ => error.Message
                    };
                    lock (pendingLock) { pending.Clear(); pendingBytes = 0; }
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

    void Queue(CancellationTokenSource run, uint kind, byte[] data)
    {
        lock (pendingLock)
        {
            if (lifetime != run) return;
            if (pending.Count >= 256 || pendingBytes + data.Length > 8 * 1024 * 1024)
            { pending.Clear(); pendingBytes = 0; pending.Enqueue((3, [])); }
            pending.Enqueue((kind, data));
            pendingBytes += data.Length;
            if (scheduled) return;
            scheduled = true;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Background, () => Drain(run));
    }

    void Drain(CancellationTokenSource run)
    {
        (uint Kind, byte[] Data)[] batch;
        lock (pendingLock)
        {
            if (lifetime != run) return;
            batch = pending.ToArray(); pending.Clear(); pendingBytes = 0; scheduled = false;
        }
        if (!Ready) return;
        try
        {
            foreach (var (kind, data) in batch)
            {
                if (kind == 3) { Status = Loc.T("systemTrayUpdatesSkipped"); continue; }
                Apply(data);
            }
        }
        catch (InvalidDataException) { Stop(); Status = Loc.T("systemTrayDataFailed"); }
    }

    void Apply(byte[] bytes)
    {
        // Header is 20 bytes followed by the fixed 956-byte Explorer payload.
        if (bytes.Length < 976) throw new InvalidDataException();
        uint Read(int offset) => BitConverter.ToUInt32(bytes, offset);
        var operation = Read(0); var pid = Read(4); var width = Read(8); var height = Read(12); var pixels = Read(16);
        var rawWindow = Read(24); var window = (nint)(int)rawWindow; var id = Read(28); var flags = Read(32);
        var callback = Read(36); var state = Read(300); var stateMask = Read(304); var version = Read(820);
        var guid = new Guid(bytes.AsSpan(956, 16));
        if (operation is > 4 or 3 || width > 256 || height > 256 || pixels != width * height * 4 || bytes.Length != 976 + pixels)
            throw new InvalidDataException();
        var item = guid != Guid.Empty ? Icons.FirstOrDefault(i => i.Guid == guid) : null;
        item ??= Icons.FirstOrDefault(i => i.Window == window && i.Id == id);
        if (operation == 2) { if (item is not null && (pid == 0 || item.ProcessId == pid)) Remove(item); return; }
        if (window == 0 && item is not null && guid != Guid.Empty)
        { window = item.Window; pid = item.ProcessId; id = item.Id; }
        Native.GetWindowThreadProcessId(window, out var actualPid);
        if (pid == 0 || pid != actualPid || !Native.IsWindow(window)) return;
        if (item is not null && item.ProcessId != pid) { Remove(item); item = null; }
        if (operation == 4) { if (item is not null && version <= 4) { item.Version = version; item.Refresh(); } return; }
        bool added = item is null;
        if (added)
        {
            if (known.Count >= 512) return;
            item = new SystemTrayItem { Window = window, ProcessId = pid, Id = id, Guid = (flags & 0x20) != 0 ? guid : Guid.Empty };
            item.Key = item.Guid == Guid.Empty ? $"{rawWindow:X8}:{id}:{pid}" : item.Guid.ToString();
            known[item.Key] = item;
        }
        var wasHidden = item!.Hidden;
        item.Window = window; item.Id = id;
        if (operation == 0) item.Version = 0;
        if ((flags & 1) != 0) item!.Callback = callback;
        if ((flags & 4) != 0)
        {
            item!.Tip = ReadText(bytes, 44, 128);
            item.ShowStandardTip = (flags & 0x80) != 0;
            item.Title = item.Tip.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        }
        if ((flags & 8) != 0 && (stateMask & 1) != 0) item!.Hidden = (state & 1) != 0;
        if ((flags & 2) != 0)
        {
            item!.Icon = pixels == 0 ? null : BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Pbgra32,
                null, bytes.AsSpan(976).ToArray(), (int)width * 4);
            item.Icon?.Freeze();
        }
        if ((flags & 0x10) != 0)
        {
            var text = ReadText(bytes, 308, 256); var title = ReadText(bytes, 824, 64);
            if (!string.IsNullOrWhiteSpace(text))
            {
                item!.HasHint = true;
                Hints.Insert(0, new TrayHint(item, title, text, DateTime.Now));
                while (Hints.Count > 50) Hints.RemoveAt(Hints.Count - 1);
            }
        }
        item!.Refresh();
        if (added) Icons.Add(item);
        else if (wasHidden != item.Hidden) LayoutChanged?.Invoke();
    }

    static string ReadText(byte[] data, int offset, int characters) => Encoding.Unicode.GetString(data, offset, characters * 2).Split('\0')[0];
    void Remove(SystemTrayItem item) { Icons.Remove(item); known.Remove(item.Key); }
    void ClearIcons() { Icons.Clear(); known.Clear(); }

    public void MarkSeen(SystemTrayItem item) { item.HasHint = false; item.Refresh(); }
    public void ClearHints() { Hints.Clear(); foreach (var item in Icons) MarkSeen(item); }

    public void RequestRefresh()
    {
        if (!Ready || DateTime.UtcNow - refreshAt < TimeSpan.FromSeconds(10)) return;
        refreshAt = DateTime.UtcNow;
        // Applications that support TaskbarCreated re-register with Explorer.
        // Explorer remains the receiver; this service never impersonates it.
        var message = SystemTrayInterop.RegisterWindowMessage("TaskbarCreated");
        _ = Task.Run(() => SystemTrayInterop.SendMessageTimeoutW((nint)0xFFFF, message, 0, 0, 0x22, 100, out _));
    }

    public bool Invoke(SystemTrayItem item, uint message, Point physicalPoint)
    {
        if (!Ready || !item.CanInvoke) return false;
        Native.GetWindowThreadProcessId(item.Window, out var pid);
        if (pid != item.ProcessId || !Native.IsWindow(item.Window)) { Remove(item); return false; }
        var x = (short)Math.Clamp(Math.Round(physicalPoint.X), short.MinValue, short.MaxValue);
        var y = (short)Math.Clamp(Math.Round(physicalPoint.Y), short.MinValue, short.MaxValue);
        if (message is >= 0x201 and <= 0x209 or 0x7B or 0x400 or 0x401)
            SystemTrayInterop.AllowSetForegroundWindow((uint)pid);
        nuint wParam = item.Version >= 4 ? unchecked((uint)(ushort)x | ((uint)(ushort)y << 16)) : item.Id;
        nint lParam = item.Version >= 4 ? unchecked((nint)((item.Id & 0xFFFF) << 16 | (message & 0xFFFF))) : (nint)message;
        return SystemTrayInterop.SendNotifyMessageW(item.Window, item.Callback, wParam, lParam);
    }

    public void Stop()
    {
        var run = lifetime; lifetime = null; run?.Cancel();
        if (run is not null && runner is { } completing)
            _ = completing.ContinueWith(_ => run.Dispose(), TaskScheduler.Default);
        runner = null;
        // The runner owns cancellation registrations until it completes.
        prune.Stop(); Ready = false; ClearIcons(); Hints.Clear(); Status = Loc.T("systemTrayOff");
        lock (pendingLock) { pending.Clear(); pendingBytes = 0; scheduled = false; }
    }
    public void Dispose() => Stop();
    void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
