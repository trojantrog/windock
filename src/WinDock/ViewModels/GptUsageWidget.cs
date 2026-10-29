using System.Globalization;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.ViewModels;

public sealed class GptUsageWidget : WidgetTile
{
    readonly GptQuotaService usage;

    public GptUsageWidget(string key, WidgetSpec spec) : this(key, spec, GptQuotaService.Shared) { }

    public GptUsageWidget(string key, WidgetSpec spec, GptQuotaService usage) : base(key, spec)
    {
        this.usage = usage;
        var weak = new WeakReference<GptUsageWidget>(this);
        Action? changed = null;
        changed = () =>
        {
            if (weak.TryGetTarget(out var widget)) widget.Wake();
            else usage.Updated -= changed;
        };
        usage.Updated += changed;
    }

    void Wake()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(Poke);
        else Poke();
    }

    public WidgetPresentation Readable { get; private set; } = new("—", "GPT");

    protected override CompactFace Compact() => new(Readable.Value, Readable.Caption,
        Bar: Readable.Progress, Tip: Readable.Tip, Dim: Readable.Dim);

    public override DateTime Due(DateTime now) => now.AddSeconds(30);

    public override void Tick(DateTime now)
    {
        var at = new DateTimeOffset(now);
        _ = usage.RefreshAsync(at);
        Readable = Present(usage.Snapshot, at);
    }

    public static WidgetPresentation Present(GptQuotaSnapshot snapshot, DateTimeOffset now)
    {
        var stale = snapshot.Stale(now);
        var status = Loc.T(StatusKey(snapshot.Status));
        var windows = snapshot.Limits?.Windows.ToArray() ?? [];
        var tip = new List<string> { Loc.T("gptQuotaTitle") };
        if (windows.Length == 0)
        {
            tip.Add(status);
            return new("—", "GPT · " + Loc.T(ShortStatusKey(snapshot.Status)), Tip: string.Join("\n", tip), Dim: true, MinWidth: 128);
        }
        foreach (var window in windows)
        {
            var reset = window.ResetsAt is { } date
                ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                : Loc.T("gptResetUnknown");
            tip.Add(Loc.F("gptWindowDetail", WindowName(window), Percent(window), reset));
        }
        var fetched = snapshot.Limits!.FetchedAt;
        var age = Math.Max(0, (int)(now - fetched).TotalMinutes);
        tip.Add(Loc.F("gptUpdated", fetched.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), age));
        if (stale) tip.Add(Loc.T("gptStale"));
        if (snapshot.Status != GptQuotaStatus.Ready) tip.Add(status);
        var first = windows[0];
        var second = windows.ElementAtOrDefault(1);
        var caption = "GPT · " + Loc.F("gptUsedWindow", WindowName(first));
        if (stale) caption += " · " + Loc.T("gptStaleShort");
        return new(Percent(first), caption, SecondValue: second is null ? null : Percent(second),
            SecondCaption: second is null ? null : Loc.F("gptUsedWindow", WindowName(second)),
            Progress: Math.Clamp(first.UsedPercent / 100, 0, 1), SecondProgress: second is null ? -1 : Math.Clamp(second.UsedPercent / 100, 0, 1),
            Tip: string.Join("\n", tip), Dim: stale, MinWidth: second is null ? 128 : stale ? 240 : 192);
    }

    static string Percent(GptQuotaWindow window) => window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    static string WindowName(GptQuotaWindow window) => Loc.T(window.DurationMinutes == 300 ? "gptFiveHour" : "gptSevenDay");

    static string StatusKey(GptQuotaStatus status) => status switch
    {
        GptQuotaStatus.NoClient => "gptNoClient",
        GptQuotaStatus.NoLogin => "gptNoLogin",
        GptQuotaStatus.Unsupported => "gptUnsupported",
        GptQuotaStatus.Timeout => "gptTimeout",
        GptQuotaStatus.Unavailable => "gptUnavailable",
        _ => "gptLoading"
    };

    static string ShortStatusKey(GptQuotaStatus status) => status switch
    {
        GptQuotaStatus.NoLogin => "gptNoLoginShort",
        GptQuotaStatus.NoClient => "gptNoClientShort",
        GptQuotaStatus.Unsupported => "gptUnsupportedShort",
        GptQuotaStatus.Timeout or GptQuotaStatus.Unavailable => "gptUnavailableShort",
        _ => "gptLoadingShort"
    };
}
