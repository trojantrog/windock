using System.Windows;
using System.Windows.Threading;

namespace WinDock.Services;

// Confirmed callbacks continue through SystemTrayService.Invoke. This route is
// only a default action for a real, currently visible native icon whose callback
// protocol was not observed; it never upgrades CanInvoke or retries a callback.
internal static class TrayActionRouter
{
    internal static bool CanAttempt(SystemTrayItem item) => !item.CanInvoke && !item.Hidden
        && item.Window != 0 && item.ProcessId != 0 && item.ProcessStarted > 0 && (!item.CallbackObserved || !item.VersionObserved)
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
        if (!service.Ready || !CanAttempt(item) || !service.Icons.Contains(item) || cancellation.IsCancellationRequested
            || !anchor.IsVisible || !window.IsVisible)
            return NativeTrayActionResult.Unavailable;
        var identity = new NativeTrayIdentity(item.Window, item.ProcessId, item.Id, item.Guid, item.ProcessStarted);
        var generation = service.RegistrationGeneration;
        NativeTrayOperation? operation = null;

        bool Current() => !cancellation.IsCancellationRequested && service.Ready && service.RegistrationGeneration == generation
            && service.Icons.Contains(item) && CanAttempt(item) && anchor.IsVisible && window.IsVisible
            && Window.GetWindow(anchor) == window && identity.Window == item.Window && identity.ProcessId == item.ProcessId
            && identity.Id == item.Id && identity.Guid == item.Guid && identity.ProcessStarted == item.ProcessStarted;

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
                DispatcherPriority.Send, cancellation, TimeSpan.FromMilliseconds(400)), cancellation, started =>
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
