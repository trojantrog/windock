using WinDock.Services;

namespace WinDock.ViewModels;

public sealed record WidgetPresentation(string Value, string Caption = "", string? Glyph = null, string? Symbol = null,
    string? SecondValue = null, string? SecondCaption = null, double Progress = -1, double SecondProgress = -1,
    string? Paper = null, string? Tip = null, bool Alert = false, bool Dim = false, double MinWidth = 128,
    string Unit = "", string? SecondUnit = null, bool Stacked = false)
{
    public bool Dual => SecondValue is not null;
    public bool HasGlyph => !string.IsNullOrEmpty(Glyph);
    public bool HasSymbol => !string.IsNullOrEmpty(Symbol);
    public bool HasMarker => HasGlyph || HasSymbol;
    public bool HasProgress => Progress >= 0;
    public bool HasSecondProgress => SecondProgress >= 0;
    public bool HasPaper => Paper is not null;

    public static double Height(double iconSize) => Math.Max(64, iconSize);

    public static double ContentHeight(IEnumerable<WidgetTile> widgets, double iconSize, bool compact) =>
        widgets.Any(w => !w.Square(compact)) ? Height(iconSize) : iconSize;

    public static WidgetPresentation From(WidgetTile widget, CompactFace face)
    {
        WidgetPresentation presentation = widget switch
        {
            ClockWidget clock => new(clock.Time, clock.Date, Glyph: "\uE787", MinWidth: 152),
            PomodoroWidget pomodoro => new(pomodoro.Text,
                pomodoro.Done.Length > 0 ? $"{pomodoro.Label} · {pomodoro.Done}" : pomodoro.Label,
                Glyph: pomodoro.IsBreak ? "\uE73E" : "\uE916", Progress: pomodoro.Progress, MinWidth: 160),
            StopwatchWidget stopwatch => new(stopwatch.Text, stopwatch.Label, Glyph: "\uE916", MinWidth: 176),
            TimerWidget timer => new(timer.Text, timer.Label, Glyph: "\uE823", Progress: timer.Progress, MinWidth: 160),
            CountdownWidget countdown => new(countdown.Text, countdown.Label, Glyph: "\uE787", MinWidth: 160),
            ProgressWidget progress => new(progress.Text, progress.Label, Progress: progress.Value),
            ActivityWidget activity => new(activity.CpuText, "CPU", SecondValue: activity.RamText, SecondCaption: "RAM",
                Progress: activity.Cpu, SecondProgress: activity.Ram, MinWidth: 160),
            NetworkWidget network => Network(network),
            GptUsageWidget gpt => gpt.Readable,
            ClaudeWidget claude => new(claude.SessionText, "5h", SecondValue: claude.WeekText, SecondCaption: "7d",
                Progress: claude.Session, SecondProgress: claude.Week, MinWidth: 160),
            WeatherWidget weather => new(weather.Temp, weather.Condition, Symbol: weather.Glyph, MinWidth: 152),
            BatteryWidget battery => new(battery.Text, Glyph: battery.Glyph, MinWidth: 144),
            MicWidget mic => new(Loc.T(mic.Present ? mic.Muted ? "micMuted" : "micOn" : "noMic").Split(',', '，')[0],
                Loc.T("widget_mic"), Glyph: mic.Glyph, MinWidth: 160),
            NoteWidget note => new("", Paper: note.Text, MinWidth: 152),
            TodoWidget todo => new(todo.Count, todo.Next, Glyph: "\uE73E", Progress: todo.Progress, MinWidth: 176),
            _ => new(face.Value, face.Caption, Glyph: face.Glyph, Symbol: face.Symbol, Progress: face.Bar >= 0 ? face.Bar : face.Ring,
                Paper: face.Paper, MinWidth: face.HasGlyph || face.HasSymbol ? 160 : 128)
        };
        return presentation with { Tip = face.Tip, Alert = face.Alert, Dim = face.Dim || widget is MicWidget { Present: false } };
    }

    static WidgetPresentation Network(NetworkWidget network)
    {
        var (down, downUnit) = Rate(network.Down);
        var (up, upUnit) = Rate(network.Up);
        return new(down, SecondValue: up, MinWidth: 144, Unit: downUnit, SecondUnit: upUnit, Stacked: true);
    }

    static (string Value, string Unit) Rate(string text)
    {
        var split = text.LastIndexOf(' ');
        return split > 0 ? (text[..split], text[(split + 1)..]) : (text, "");
    }

    internal static string NetworkLabel(bool upload) => Loc.Instance.Language == Loc.Chinese
        ? upload ? "上传" : "下载"
        : upload ? "Upload" : "Download";
}
