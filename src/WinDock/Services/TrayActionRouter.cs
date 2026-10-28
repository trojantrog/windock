using System.Windows;
using System.Windows.Threading;

namespace WinDock.Services;

// Owners with a verified creation time continue through SystemTrayService.Invoke.
// This route is only a default action for a real, currently visible native icon
// whose callback cannot be used. It never upgrades callback knowledge, supplies
// right/double-click semantics, or retries a submitted callback.
internal static class TrayActionRouter
{
    internal static bool CanAttempt(SystemTrayItem item) => !item.CanUseOwnerCallback && !item.Hidden
        && item.Window != 0 && item.ProcessId != 0 && item.ThreadId != 0 && item.OwnerClass.Length > 0 && item.Instance.IsValid
        && (item.Instance.Kind switch
        {
            ProcessInstanceKind.CreationTime => item.ProcessStarted > 0 && item.Instance.Value == (ulong)item.ProcessStarted
                && (!item.CallbackObserved || !item.VersionObserved),
            ProcessInstanceKind.SystemSequence => item.ProcessStarted == 0 && item.NativeModelObserved,
            _ => false
        })
        && (item.Guid == Guid.Empty || item.RegistrationObserved || item.NativeModelObserved)
        && (!item.CallbackObserved || item.Callback is >= 0x400 and <= 0xFFFF);

    internal static async Task<NativeTrayActionResult> ActivateAsync(SystemTrayService service, SystemTrayItem item,
        FrameworkElement anchor, CancellationToken cancellation)
    {
        anchor.Dispatcher.VerifyAccess();
        if (!anchor.IsVisible || Window.GetWindow(anchor) is not { IsVisible: true } window)
            return NativeTrayActionResult.Unavailable;
        var interaction = PopupInteraction.For(window);
        // A new explicit intent cancels the earlier unresolved icon even when
        // its provider is still occupying the worker and this request is Busy.
        interaction.CancelPendingNativeAction();
        if (!service.Ready || !service.ProtocolCurrent || !CanAttempt(item) || !service.Icons.Contains(item) || cancellation.IsCancellationRequested
            || !anchor.IsVisible || !window.IsVisible || !service.TryCaptureNativeActionSession(out var explorer))
            return NativeTrayActionResult.Unavailable;
        var identity = new NativeTrayIdentity(item.Window, item.ProcessId, item.Id, item.Guid, item.ProcessStarted,
            item.ThreadId, item.OwnerClass, item.Instance, explorer);
        var generation = service.RegistrationGeneration;
        var protocolSequence = service.ProtocolSequence;
        NativeTrayOperation? operation = null;

        bool ProtocolCurrent() => service.ProtocolCurrent && service.ProtocolSequence == protocolSequence
            && service.RegistrationGeneration == generation && service.IsCurrentNativeActionSession(explorer);

        bool Current() => !cancellation.IsCancellationRequested && service.Ready && ProtocolCurrent()
            && service.Icons.Contains(item) && CanAttempt(item) && anchor.IsVisible && window.IsVisible
            && Window.GetWindow(anchor) == window && identity.Window == item.Window && identity.ProcessId == item.ProcessId
            && identity.Id == item.Id && identity.Guid == item.Guid && identity.ProcessStarted == item.ProcessStarted
            && identity.ThreadId == item.ThreadId && identity.OwnerClass == item.OwnerClass && identity.Instance == item.Instance;

        void Changed(object? sender, EventArgs args)
        {
            if (!Current()) operation?.Cancel();
        }

        service.PropertyChanged += Changed;
        service.Icons.CollectionChanged += Changed;
        item.PropertyChanged += Changed;
        try
        {
            var queued = NativeTrayAction.TryStart(identity, surface => anchor.Dispatcher.Invoke(() =>
                Current() && operation is not null && interaction.PrepareNativeTrayAction(operation, surface),
                DispatcherPriority.Send, cancellation, TimeSpan.FromMilliseconds(400)), ProtocolCurrent, cancellation, started =>
            {
                operation = started;
                interaction.BeginNativeTrayAction(item, anchor, started);
            });
            if (queued is null)
            {
                if (operation is not null) interaction.CompleteNativeTrayAction(operation, NativeTrayActionResult.Cancelled);
                return NativeTrayActionResult.Busy;
            }
            var result = await queued.WaitAsync();
            interaction.CompleteNativeTrayAction(queued, result);
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException
            or TimeoutException or System.ComponentModel.Win32Exception)
        {
            operation?.Cancel();
            var result = operation?.Submitted == true ? NativeTrayActionResult.Pending : NativeTrayActionResult.Cancelled;
            if (operation is not null) interaction.CompleteNativeTrayAction(operation, result);
            return result;
        }
        finally
        {
            service.PropertyChanged -= Changed;
            service.Icons.CollectionChanged -= Changed;
            item.PropertyChanged -= Changed;
        }
    }
}
