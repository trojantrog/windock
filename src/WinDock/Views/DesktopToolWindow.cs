using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public abstract class DesktopToolWindow : Window
{
    protected readonly Settings Settings;
    protected readonly Brush Ink;
    protected readonly Brush Muted;
    protected readonly Brush Plate;
    protected readonly Brush Line;
    protected readonly TextBlock Status;
    protected readonly Grid Layout = new() { Margin = new Thickness(24) };
    protected readonly CancellationTokenSource Lifetime = new();
    protected readonly CancellationToken LifetimeToken;

    protected DesktopToolWindow(Settings settings, string title, double width, double height)
    {
        Settings = settings;
        LifetimeToken = Lifetime.Token;
        Title = title;
        Width = width; Height = height;
        MinWidth = 560; MinHeight = 380;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        var dark = DockTheme.Find(settings.Dock.Theme).Dark;
        Background = Paint(dark ? "#171B24" : "#F6F7FA");
        Ink = Paint(dark ? "#F2F4F8" : "#192230");
        Muted = Paint(dark ? "#A9B4C5" : "#586579");
        Plate = Paint(dark ? "#232A37" : "#FFFFFF");
        Line = Paint(dark ? "#3C4658" : "#CDD4DF");
        Foreground = Ink;
        Status = Text("", 12, muted: true);
        Status.TextWrapping = TextWrapping.Wrap;
        Content = Layout;
        Loaded += (_, _) => CenterOnPointer();
        Closed += (_, _) => { Lifetime.Cancel(); Lifetime.Dispose(); };
    }

    protected static Brush Paint(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    protected TextBlock Text(string value, double size = 13, bool muted = false) => new()
    {
        Text = value, FontSize = size, Foreground = muted ? Muted : Ink,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    protected Button ActionButton(string key, Action action)
    {
        var button = new Button
        {
            Content = Loc.T(key), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 6),
            Background = Plate, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand, MinHeight = 34
        };
        System.Windows.Automation.AutomationProperties.SetName(button, Loc.T(key));
        button.Click += (_, _) => action();
        return button;
    }

    protected TextBox Input(string name, double width = double.NaN) => new()
    {
        Width = width, Padding = new Thickness(10, 7, 10, 7), Background = Plate, Foreground = Ink,
        BorderBrush = Line, CaretBrush = Ink, ToolTip = name, MinHeight = 34,
        Margin = new Thickness(0, 0, 8, 6), VerticalContentAlignment = VerticalAlignment.Center
    };

    protected ListBox List() => new()
    {
        Background = Plate, Foreground = Ink, BorderBrush = Line, BorderThickness = new Thickness(1),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4)
    };

    protected ListBoxItem Row(object value, string title, string detail)
    {
        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        stack.Children.Add(Text(title, 14));
        var sub = Text(detail, 11, muted: true);
        sub.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(sub);
        var row = new ListBoxItem { Content = stack, Tag = value, ToolTip = detail, Foreground = Ink, Padding = new Thickness(0) };
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        return row;
    }

    void CenterOnPointer()
    {
        Native.GetCursorPos(out var pointer);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pointer.X, pointer.Y));
        var monitor = Native.MonitorFromPoint(pointer, 2);
        var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var bounds = screen.WorkingArea;
        var width = Math.Min((int)(Width * scale), bounds.Width);
        var height = Math.Min((int)(Height * scale), bounds.Height);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, 0, bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + Math.Max(0, (bounds.Height - height) / 3), width, height, Native.SWP_NOACTIVATE);
    }
}
