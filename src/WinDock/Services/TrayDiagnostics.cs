using System.Reflection;
using System.Text.Json;

namespace WinDock.Services;

internal sealed class TrayDiagnostics
{
    sealed record Snapshot(string Content, JsonElement State, JsonElement? Observation, DateTimeOffset At, long CapturedAt);
    readonly object sync = new();
    readonly SemaphoreSlim wake = new(0, 1);
    Task? write;
    Snapshot? pending;
    bool writing;
    string? last;
    long lastCompletedAt, lastSavedCaptureAt, writeQueuedAt, writeStartedAt, writeCompletedAt;
    long coalescedWrites, writesCompleted, writesFailed;
    string? writeError;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly string Build = typeof(TrayDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    internal object Observe()
    {
        lock (sync) return new
        {
            queuedAt = Time(writeQueuedAt),
            startedAt = Time(writeStartedAt),
            completedAt = Time(writeCompletedAt),
            taskState = write?.Status.ToString() ?? "not started",
            writing,
            pending = pending is not null,
            coalesced = coalescedWrites,
            completed = writesCompleted,
            failed = writesFailed,
            error = writeError
        };
    }

    internal static string? Time(long ticks) => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc).ToString("O");

    public void Publish(object state, object? observation = null)
    {
        // Freeze the entire caller snapshot before crossing to the worker. It
        // must never enumerate mutable UI state after a service stop/disposal.
        var frozenState = JsonSerializer.SerializeToElement(state, Json);
        var content = frozenState.GetRawText();
        lock (sync)
        {
            // Idle unchanged states retain the 30-second heartbeat. While a
            // write is pending, even a return to the last saved state must
            // replace the queued snapshot, otherwise an older change can win.
            if (!writing && pending is null && last == content && Environment.TickCount64 - lastCompletedAt < 30_000) return;
            var frozenObservation = observation is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(observation, Json);
            if (writing || pending is not null) ++coalescedWrites;
            pending = new(content, frozenState, frozenObservation, DateTimeOffset.Now, Environment.TickCount64);
            writeQueuedAt = DateTime.UtcNow.Ticks;
            // This single async task waits when idle; it never exits between
            // a final dequeue and a new Publish, so the last value cannot lose
            // its worker. No dedicated thread is retained while waiting.
            write ??= Task.Run(WriteLoopAsync);
            if (wake.CurrentCount == 0) wake.Release();
        }
    }

    async Task WriteLoopAsync()
    {
        while (true)
        {
            await wake.WaitAsync().ConfigureAwait(false);
            while (true)
            {
                Snapshot snapshot;
                lock (sync)
                {
                    if (pending is null) { writing = false; break; }
                    // A same-state Publish during a successful write needs no
                    // duplicate I/O. Retain a newer heartbeat when the old
                    // capture itself is over 30 seconds behind, even if that
                    // slow write has only just finished.
                    if (last == pending.Content && Environment.TickCount64 - lastCompletedAt < 30_000
                        && pending.CapturedAt - lastSavedCaptureAt < 30_000)
                    { pending = null; writing = false; break; }
                    snapshot = pending; pending = null;
                    writing = true; writeStartedAt = DateTime.UtcNow.Ticks;
                }
                // One local current-state file, never a history of notification text.
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");
                var path = Path.Combine(directory, "tray-status.json");
                var temporary = path + "." + Environment.ProcessId + ".tmp";
                try
                {
                    var entry = JsonSerializer.Serialize(new
                    {
                        at = snapshot.At,
                        processId = Environment.ProcessId,
                        build = Build,
                        state = snapshot.State,
                        observation = snapshot.Observation,
                        writer = Observe()
                    }, Json);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(temporary, entry);
                    File.Move(temporary, path, overwrite: true);
                    lock (sync)
                    {
                        last = snapshot.Content; lastCompletedAt = Environment.TickCount64;
                        lastSavedCaptureAt = snapshot.CapturedAt;
                        ++writesCompleted; writeError = null;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    lock (sync) { ++writesFailed; writeError = error.GetType().Name + ":0x" + error.HResult.ToString("X8"); }
                    // Consume a failed snapshot once. Only a later Publish can
                    // supply another attempt; there is no self-retry loop.
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
                finally { lock (sync) writeCompletedAt = DateTime.UtcNow.Ticks; }
            }
        }
    }
}
