namespace WinDock.Services;

public enum TokenPulseStatus { Loading, Ready, Stale, NoSamples, Missing, Busy, TimedOut, Unavailable, InvalidData, Unsupported }

public sealed record TokenPulseSample(double EndedAt, double StartedAt, long OutputTokens, string? Model)
{
    public double Seconds => EndedAt - StartedAt;
    public double TokensPerSecond => OutputTokens / Seconds;
}

public sealed record TokenPulseSpeed(TokenPulseStatus Status, DateTimeOffset CheckedAt, TokenPulseSample? Latest = null,
    double? ModelAverage = null, int ModelSamples = 0, bool WindowLimited = false)
{
    public static readonly TimeSpan Recent = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public TokenPulseSpeed At(DateTimeOffset now)
    {
        if (Latest is null || Status is not (TokenPulseStatus.Ready or TokenPulseStatus.Stale)) return this;
        var age = now.ToUnixTimeMilliseconds() / 1000.0 - Latest.EndedAt;
        return this with { Status = age >= 0 && age <= Recent.TotalSeconds ? TokenPulseStatus.Ready : TokenPulseStatus.Stale };
    }
}
