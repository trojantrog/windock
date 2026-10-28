using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinDock.Services;

internal enum SystemNotificationAccess
{
    Disabled, IdentityRequired, Denied, Unspecified, Busy, Enabled, Unavailable, Cancelled
}

internal sealed record SystemNotificationItem(uint Id, DateTimeOffset Created, string AppId,
    string AppName, IReadOnlyList<string> Text);

internal sealed record SystemNotificationSnapshot(SystemNotificationAccess Access, IReadOnlyList<SystemNotificationItem> Items);

internal sealed class SystemNotificationReader : IDisposable
{
    const int InsufficientBuffer = 122;
    const int MaximumItems = 100, MaximumTextElements = 10, MaximumTextLength = 4096;
    readonly Dispatcher dispatcher;
    UserNotificationListener? listener;
    Task<IReadOnlyList<UserNotification>>? pendingRead;
    TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? changeHandler;
    bool enabled, starting, subscribed, disposed;
    int generation, readGeneration, changePending;

    public SystemNotificationReader(Dispatcher dispatcher) => this.dispatcher = dispatcher;

    public bool Enabled => enabled;
    public bool EventsAvailable => subscribed;
    public event Action? Changed;

    public SystemNotificationAccess CheckAccess()
    {
        dispatcher.VerifyAccess();
        if (disposed || !enabled || listener is null) return SystemNotificationAccess.Disabled;
        try
        {
            if (listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed) return SystemNotificationAccess.Enabled;
            Disable();
            return SystemNotificationAccess.Denied;
        }
        catch (Exception error) when (PlatformError(error))
        {
            Disable();
            return SystemNotificationAccess.Unavailable;
        }
    }

    public async Task<SystemNotificationAccess> EnableAsync(CancellationToken cancellation = default)
    {
        dispatcher.VerifyAccess();
        if (disposed || cancellation.IsCancellationRequested) return SystemNotificationAccess.Cancelled;
        if (enabled) return SystemNotificationAccess.Enabled;
        if (starting) return SystemNotificationAccess.Busy;
        if (!HasPackageIdentity()) return SystemNotificationAccess.IdentityRequired;
        starting = true;
        var current = ++generation;
        try
        {
            var candidate = UserNotificationListener.Current;
            var access = candidate.GetAccessStatus();
            if (access == UserNotificationListenerAccessStatus.Unspecified)
                access = await candidate.RequestAccessAsync().AsTask().WaitAsync(cancellation);
            if (disposed || cancellation.IsCancellationRequested || current != generation) return SystemNotificationAccess.Cancelled;
            if (access == UserNotificationListenerAccessStatus.Denied) return SystemNotificationAccess.Denied;
            if (access != UserNotificationListenerAccessStatus.Allowed) return SystemNotificationAccess.Unspecified;
            listener = candidate;
            enabled = true;
            try
            {
                changeHandler = (_, _) => OnNotificationChanged(current);
                candidate.NotificationChanged += changeHandler;
                subscribed = true;
            }
            catch (Exception error) when (PlatformError(error)) { changeHandler = null; subscribed = false; }
            return SystemNotificationAccess.Enabled;
        }
        catch (OperationCanceledException) { return SystemNotificationAccess.Cancelled; }
        catch (Exception error) when (PlatformError(error)) { return SystemNotificationAccess.Unavailable; }
        finally { starting = false; }
    }

    public async Task<SystemNotificationSnapshot> ReadAsync(CancellationToken cancellation = default)
    {
        dispatcher.VerifyAccess();
        if (disposed || cancellation.IsCancellationRequested) return Empty(SystemNotificationAccess.Cancelled);
        if (!enabled || listener is null) return Empty(SystemNotificationAccess.Disabled);
        var current = generation;
        var request = ++readGeneration;
        var source = listener;
        try
        {
            if (source.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Disable();
                return Empty(SystemNotificationAccess.Denied);
            }
            if (pendingRead is null || pendingRead.IsCompleted)
                pendingRead = source.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
            var notifications = await pendingRead.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
            if (disposed || cancellation.IsCancellationRequested || current != generation || request != readGeneration || !enabled)
                return Empty(SystemNotificationAccess.Cancelled);
            if (source.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Disable();
                return Empty(SystemNotificationAccess.Denied);
            }
            var ranked = new List<(UserNotification Notification, DateTimeOffset Created)>();
            foreach (var notification in notifications)
            {
                try { ranked.Add((notification, notification.CreationTime)); }
                catch (Exception error) when (PlatformError(error)) { }
            }
            var items = new List<SystemNotificationItem>();
            foreach (var (notification, created) in ranked.OrderByDescending(value => value.Created).Take(MaximumItems))
            {
                try
                {
                    var id = notification.Id;
                    string appId = "", appName = "";
                    string[] text = [];
                    try
                    {
                        var app = notification.AppInfo;
                        appId = Limit(app?.AppUserModelId);
                        appName = Limit(app?.DisplayInfo?.DisplayName);
                    }
                    catch (Exception error) when (PlatformError(error)) { }
                    try
                    {
                        var visual = notification.Notification?.Visual;
                        var binding = visual?.GetBinding(KnownNotificationBindings.ToastGeneric) ?? visual?.Bindings.FirstOrDefault();
                        text = binding?.GetTextElements().Select(value => Limit(value.Text)).Take(MaximumTextElements).ToArray() ?? [];
                    }
                    catch (Exception error) when (PlatformError(error)) { }
                    items.Add(new SystemNotificationItem(id, created, appId, appName, text));
                }
                catch (Exception error) when (PlatformError(error)) { }
            }
            if (disposed || cancellation.IsCancellationRequested || current != generation || request != readGeneration || !enabled)
                return Empty(SystemNotificationAccess.Cancelled);
            if (source.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Disable();
                return Empty(SystemNotificationAccess.Denied);
            }
            return new SystemNotificationSnapshot(SystemNotificationAccess.Enabled, items);
        }
        catch (OperationCanceledException) { return Empty(SystemNotificationAccess.Cancelled); }
        catch (TimeoutException) { return Empty(SystemNotificationAccess.Unavailable); }
        catch (Exception error) when (PlatformError(error)) { return Empty(SystemNotificationAccess.Unavailable); }
    }

    public void Disable()
    {
        dispatcher.VerifyAccess();
        ++generation; ++readGeneration;
        Interlocked.Exchange(ref changePending, 0);
        enabled = false;
        if (subscribed && listener is { } source && changeHandler is { } handler)
        {
            try { source.NotificationChanged -= handler; }
            catch (Exception error) when (PlatformError(error)) { }
        }
        subscribed = false;
        changeHandler = null;
        pendingRead = null;
        listener = null;
    }

    void OnNotificationChanged(int current)
    {
        if (current != Volatile.Read(ref generation) || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        if (Interlocked.CompareExchange(ref changePending, current, 0) != 0) return;
        try { dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.CompareExchange(ref changePending, 0, current);
            if (!disposed && enabled && current == generation) Changed?.Invoke();
        }); }
        catch (InvalidOperationException) { Interlocked.CompareExchange(ref changePending, 0, current); }
    }

    static SystemNotificationSnapshot Empty(SystemNotificationAccess access) => new(access, []);
    static string Limit(string? text) => text is null ? "" : text.Length <= MaximumTextLength ? text : text[..MaximumTextLength];
    static bool PlatformError(Exception error) => error is COMException or InvalidOperationException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or IOException;

    public static bool HasPackageIdentity()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return length > 0 && result is 0 or InsufficientBuffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

    public void Dispose()
    {
        if (disposed) return;
        Disable();
        disposed = true;
        Changed = null;
    }
}
