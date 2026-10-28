using Windows.Foundation;

namespace WinDock.Services;

// A view may stop waiting before Windows completes an operation. Keep the slot
// until the original operation completes, including across closed/reopened views.
internal sealed class NotificationOperationSlot
{
    readonly SemaphoreSlim available = new(1, 1);

    internal async Task<TResult> RunAsync<TResult>(Func<IAsyncOperation<TResult>> begin, CancellationToken cancellation)
    {
        await available.WaitAsync(cancellation);
        IAsyncOperation<TResult>? operation = null;
        Task<TResult>? task = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            operation = begin();
            // A cancellable AsTask bridge can finish before the native
            // operation does. Keep an uncancelled completion task for ownership
            // and request native cancellation separately from the UI wait.
            task = operation.AsTask();
            var request = new CancellationRequest(operation);
            var registration = cancellation.Register(static state => ((CancellationRequest)state!).Request(), request);
            _ = ReleaseWhenFinished(task, registration, request);
            return await task.WaitAsync(cancellation);
        }
        finally
        {
            if (task is null)
            {
                if (operation is null) available.Release();
                else CancelUnobserved(operation);
            }
        }
    }

    async Task ReleaseWhenFinished(Task operation, CancellationTokenRegistration registration, CancellationRequest request)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception) { /* Observe a failure after the original view closed. */ }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await request.FinishAsync().ConfigureAwait(false);
            available.Release();
        }
    }

    void CancelUnobserved(IAsyncInfo operation)
    {
        // An adapter failure leaves no reliable completion task. Release only
        // after the provider reports a terminal state; otherwise retain this
        // one slot until process exit instead of accumulating unknown requests.
        _ = Task.Run(() =>
        {
            try { operation.Cancel(); }
            catch (Exception) { }
            try { if (operation.Status != AsyncStatus.Started) available.Release(); }
            catch (Exception) { }
        });
    }

    sealed class CancellationRequest(IAsyncInfo operation)
    {
        readonly object sync = new();
        Task? pending;

        internal void Request()
        {
            // Closing the view must not wait inside a cross-process Cancel.
            // At most one cancellation call exists per occupied slot, and the
            // slot stays held until both the operation and this call finish.
            lock (sync) pending ??= Task.Run(() =>
            {
                try { operation.Cancel(); }
                catch (Exception) { }
            });
        }

        internal Task FinishAsync()
        {
            lock (sync) return pending ?? Task.CompletedTask;
        }
    }
}
