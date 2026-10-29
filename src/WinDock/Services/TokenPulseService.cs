namespace WinDock.Services;

public sealed class TokenPulseService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    public static TokenPulseService Shared { get; } = new();

    readonly object gate = new();
    readonly Func<DateTimeOffset, TokenPulseSpeed> read;
    volatile TokenPulseSpeed snapshot = new(TokenPulseStatus.Loading, DateTimeOffset.MinValue);
    DateTimeOffset nextRead = DateTimeOffset.MinValue;
    bool reading;

    public TokenPulseService(Func<DateTimeOffset, TokenPulseSpeed>? read = null)
        => this.read = read ?? new TokenPulseReader().Read;

    public TokenPulseSpeed Snapshot => snapshot;

    public async Task Refresh(DateTimeOffset now)
    {
        lock (gate)
        {
            if (reading || now < nextRead) return;
            reading = true;
            nextRead = now + RefreshInterval;
        }
        try
        {
            snapshot = await Task.Run(() => read(now)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            snapshot = new(TokenPulseStatus.Unavailable, now);
        }
        finally
        {
            lock (gate) reading = false;
        }
    }
}
