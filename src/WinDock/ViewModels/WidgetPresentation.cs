using WinDock.Services;

namespace WinDock.ViewModels;

public sealed record WidgetPresentation(string Value, string Caption = "", string? Glyph = null, string? Symbol = null,
    string? SecondValue = null, string? SecondCaption = null, double Progress = -1, double SecondProgress = -1,
    string? Paper = null, string? Tip = null, bool Alert = false, bool Dim = false, double MinWidth = 104)
{
    public bool Dual => SecondValue is not null;
    public bool HasGlyph => !string.IsNullOrEmpty(Glyph);
    public bool HasSymbol => !string.IsNullOrEmpty(Symbol);
    public bool HasMarker => HasGlyph || HasSymbol;
    public bool HasProgress => Progress >= 0;
    public bool HasSecondProgress => SecondProgress >= 0;
    public bool HasPaper => Paper is not null;

    public static double Height(double iconSize) => Math.Max(42, iconSize);

    public static double ContentHeight(IEnumerable<WidgetTile> widgets, double iconSize, bool compact) =>
        widgets.Any(w => !w.Square(compact)) ? Height(iconSize) : iconSize;

    public static WidgetPresentation From(WidgetTile widget, CompactFace face)
    {
        WidgetPresentation presentation = widget switch
        {
            ClockWidget clock => new(clock.Time, clock.Date, Glyph: "\uE787", MinWidth: 128),
            PomodoroWidget pomodoro => new(pomodoro.Text,
                pomodoro.Done.Length > 0 ? $"{pomodoro.Label} · {pomodoro.Done}" : pomodoro.Label,
                Glyph: pomodoro.IsBreak ? "\uE73E" : "\uE916", Progress: pomodoro.Progress, MinWidth: 128),
            StopwatchWidget stopwatch => new(stopwatch.Text, stopwatch.Label, Glyph: "\uE916", MinWidth: 144),
            TimerWidget timer => new(timer.Text, timer.Label, Glyph: "\uE823", Progress: timer.Progress, MinWidth: 128),
            CountdownWidget countdown => new(countdown.Text, countdown.Label, Glyph: "\uE787", MinWidth: 136),
            ProgressWidget progress => new(progress.Text, progress.Label, Progress: progress.Value),
            ActivityWidget activity => new(activity.CpuText, "CPU", SecondValue: activity.RamText, SecondCaption: "RAM",
                Progress: activity.Cpu, SecondProgress: activity.Ram, MinWidth: 116),
            NetworkWidget network => new(network.Down, NetworkLabel(false), SecondValue: network.Up, SecondCaption: NetworkLabel(true), MinWidth: 172),
            ClaudeWidget claude => new(claude.SessionText, "5h", SecondValue: claude.WeekText, SecondCaption: "7d",
                Progress: claude.Session, SecondProgress: claude.Week, MinWidth: 124),
            WeatherWidget weather => new(weather.Temp, weather.Condition, Symbol: weather.Glyph, MinWidth: 124),
            BatteryWidget battery => new(battery.Text, Glyph: battery.Glyph, MinWidth: 104),
            MicWidget mic => new(Loc.T(mic.Present ? mic.Muted ? "micMuted" : "micOn" : "noMic").Split(',', '，')[0],
                Loc.T("widget_mic"), Glyph: mic.Glyph, MinWidth: 144),
            NoteWidget note => new("", Paper: note.Text, MinWidth: 152),
            TodoWidget todo => new(todo.Count, todo.Next, Glyph: "\uE73E", Progress: todo.Progress, MinWidth: 160),
            _ => new(face.Value, face.Caption, Glyph: face.Glyph, Symbol: face.Symbol, Progress: face.Bar >= 0 ? face.Bar : face.Ring,
                Paper: face.Paper, MinWidth: face.HasGlyph || face.HasSymbol ? 128 : 104)
        };
        return presentation with { Tip = face.Tip, Alert = face.Alert, Dim = face.Dim || widget is MicWidget { Present: false } };
    }

    internal static string NetworkLabel(bool upload) => Loc.Instance.Language == Loc.Chinese
        ? upload ? "上传" : "下载"
        : upload ? "Upload" : "Download";
}
