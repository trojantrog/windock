using System.Text.Json;

namespace WinDock.Services;

public enum GptQuotaStatus { Loading, Ready, NoClient, NoLogin, Unsupported, Timeout, Unavailable }

public sealed record GptQuotaWindow(double UsedPercent, int DurationMinutes, DateTimeOffset? ResetsAt);

public sealed record GptQuotaLimits(GptQuotaWindow? FiveHour, GptQuotaWindow? SevenDay, DateTimeOffset FetchedAt)
{
    public IEnumerable<GptQuotaWindow> Windows => new[] { FiveHour, SevenDay }.OfType<GptQuotaWindow>();

    public static GptQuotaLimits? Parse(JsonElement result, DateTimeOffset fetchedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object
            && map.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
            bucket = codex;
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            bucket = legacy;
        else return null;
        if (bucket.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex") return null;
        var windows = new[] { Window(bucket, "primary"), Window(bucket, "secondary") }.OfType<GptQuotaWindow>().ToArray();
        var five = Unique(windows, 300);
        var seven = Unique(windows, 10080);
        return five is null && seven is null ? null : new(five, seven, fetchedAt);
    }

    static GptQuotaWindow? Unique(GptQuotaWindow[] windows, int minutes)
    {
        var matching = windows.Where(w => w.DurationMinutes == minutes).ToArray();
        return matching.Length == 1 ? matching[0] : null;
    }

    static GptQuotaWindow? Window(JsonElement bucket, string name)
    {
        if (!bucket.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("usedPercent", out var percent) || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var used) || !double.IsFinite(used) || used < 0
            || !window.TryGetProperty("windowDurationMins", out var duration) || duration.ValueKind != JsonValueKind.Number
            || !duration.TryGetInt32(out var minutes) || minutes is not (300 or 10080)) return null;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds))
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); }
            catch (ArgumentOutOfRangeException) { }
        return new(used, minutes, reset);
    }
}

public sealed record GptQuotaResult(GptQuotaStatus Status, GptQuotaLimits? Limits = null);

public sealed record GptQuotaSnapshot(GptQuotaStatus Status, GptQuotaLimits? Limits, bool Refreshing)
{
    public bool Stale(DateTimeOffset now) => Limits is not null && (Status != GptQuotaStatus.Ready
        || now - Limits.FetchedAt >= TimeSpan.FromMinutes(5)
        || Limits.Windows.Any(w => w.ResetsAt is { } reset && reset <= now));
}
