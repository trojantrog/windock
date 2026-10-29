using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.ViewModels;

public sealed partial class TokenSpeedWidget : WidgetTile
{
    readonly TokenPulseService service;
    TokenPulseSpeed current = new(TokenPulseStatus.Loading, DateTimeOffset.MinValue);

    public TokenSpeedWidget(string key, WidgetSpec spec) : this(key, spec, TokenPulseService.Shared) { }

    public TokenSpeedWidget(string key, WidgetSpec spec, TokenPulseService service) : base(key, spec)
        => this.service = service;

    [ObservableProperty] string text = "—";
    [ObservableProperty] string caption = "";
    [ObservableProperty] string tip = "";

    public TokenPulseStatus Status => current.Status;
    public override DateTime Due(DateTime now) => now + TokenPulseService.RefreshInterval;

    protected override CompactFace Compact() => new(Text, Caption, Tip: Tip, Dim: Status != TokenPulseStatus.Ready);

    public override void Tick(DateTime now)
    {
        var at = new DateTimeOffset(now).ToUniversalTime();
        _ = service.Refresh(at);
        current = service.Snapshot.At(at);
        Text = Status == TokenPulseStatus.Ready && current.Latest is { } sample
            ? sample.TokensPerSecond.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        Caption = Loc.T(Status switch
        {
            TokenPulseStatus.Ready => "tokenSpeedUnit",
            TokenPulseStatus.Stale => "tokenSpeedWaiting",
            TokenPulseStatus.NoSamples => "tokenSpeedNoSamples",
            TokenPulseStatus.Missing => "tokenSpeedMissing",
            TokenPulseStatus.Busy => "tokenSpeedBusy",
            TokenPulseStatus.TimedOut => "tokenSpeedTimedOut",
            TokenPulseStatus.InvalidData => "tokenSpeedInvalidData",
            TokenPulseStatus.Unsupported => "tokenSpeedUnsupported",
            TokenPulseStatus.Unavailable => "tokenSpeedUnavailable",
            _ => "tokenSpeedLoading"
        });
        Tip = Describe(at);
        OnPropertyChanged(nameof(Status));
    }

    string Describe(DateTimeOffset now)
    {
        var lines = new List<string> { Loc.T("tokenSpeedEstimate") };
        if (Status != TokenPulseStatus.Ready) lines.Add(Caption);
        if (current.Latest is { } sample)
        {
            var culture = Loc.Instance.Culture;
            var model = string.IsNullOrWhiteSpace(sample.Model) ? Loc.T("tokenSpeedUnknownModel")
                : string.Concat(sample.Model.Take(160).Select(c => char.IsControl(c) ? ' ' : c)).Trim();
            var completed = DateTimeOffset.FromUnixTimeMilliseconds((long)(sample.EndedAt * 1000)).ToLocalTime();
            var seconds = sample.Seconds.ToString(sample.Seconds < 10000 ? "0.##" : "G4", culture);
            var age = Math.Max(0, now.ToUnixTimeMilliseconds() / 1000.0 - sample.EndedAt).ToString("0", culture);
            lines.Add(Loc.F("tokenSpeedSample", model, completed.ToString("HH:mm:ss", culture),
                sample.OutputTokens.ToString("N0", culture), seconds, age));
            if (current.ModelAverage is { } average)
                lines.Add(Loc.F("tokenSpeedAverage", average.ToString("0.0", CultureInfo.InvariantCulture), current.ModelSamples));
            if (current.WindowLimited) lines.Add(Loc.T("tokenSpeedLimited"));
        }
        lines.Add(Loc.T("tokenSpeedMethod"));
        return string.Join("\n", lines);
    }
}
