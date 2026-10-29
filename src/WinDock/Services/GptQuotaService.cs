namespace WinDock.Services;

public sealed class GptQuotaService(Func<CancellationToken, Task<GptQuotaResult>> read)
{
    public static GptQuotaService Shared { get; } = new(new GptQuotaReader().ReadAsync);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);
    readonly object gate = new();
    DateTimeOffset? lastAttempt;
    Task? pending;
    GptQuotaStatus status = GptQuotaStatus.Loading;
    GptQuotaLimits? limits;

    public event Action? Updated;

    public GptQuotaSnapshot Snapshot
    {
        get { lock (gate) return new(status, limits, pending is { IsCompleted: false }); }
    }

    public Task RefreshAsync(DateTimeOffset now)
    {
        lock (gate)
        {
            if (pending is { IsCompleted: false }) return pending;
            if (lastAttempt is { } last && now - last < RefreshInterval) return Task.CompletedTask;
            lastAttempt = now;
            return pending = Task.Run(Fetch);
        }
    }

    async Task Fetch()
    {
        GptQuotaResult result;
        try { result = await read(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException)
        {
            result = new(GptQuotaStatus.Unavailable);
        }
        lock (gate)
        {
            status = result.Status;
            if (result.Status == GptQuotaStatus.Ready && result.Limits is not null) limits = result.Limits;
            else if (result.Status is GptQuotaStatus.NoLogin or GptQuotaStatus.Unsupported) limits = null;
        }
        Updated?.Invoke();
    }
}
