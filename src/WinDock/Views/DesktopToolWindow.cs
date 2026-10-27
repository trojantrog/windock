using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using WinDock.Models;
using WinDock.Services;

namespace WinDock.Views;

public abstract class DesktopToolWindow : Window
{
    protected readonly Settings Settings;
    protected readonly Brush Ink, Muted, Plate, Line, Accent;
    protected readonly TextBlock Status;
    protected readonly Grid Layout = new() { Margin = new Thickness(24, 8, 24, 18) };
    protected readonly StackPanel HeaderActions = new() { Orientation = Orientation.Horizontal };
    protected readonly CancellationTokenSource Lifetime = new();
    protected readonly CancellationToken LifetimeToken;
    GlassBackdrop? backdrop;
    public bool CenterOnOpen { get; set; } = true;

    protected DesktopToolWindow(Settings settings, string title, double width, double height)
    {
        Settings = settings; LifetimeToken = Lifetime.Token; Title = title;
        Width = width; Height = height; MinWidth = 580; MinHeight = 360;
        ShowInTaskbar = true; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        FontSize = 13; UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/WinDock;component/Themes/DesktopGlass.xaml", UriKind.Relative) });
        Ink = (Brush)FindResource("GlassInk"); Muted = (Brush)FindResource("GlassMuted");
        Plate = (Brush)FindResource("GlassPlate"); Line = (Brush)FindResource("GlassLine");
        Accent = (Brush)FindResource("GlassAccent");
        if (SystemParameters.HighContrast)
        {
            Ink = SystemColors.WindowTextBrush; Muted = SystemColors.WindowTextBrush;
            Plate = SystemColors.WindowBrush; Line = SystemColors.WindowTextBrush; Accent = SystemColors.HighlightBrush;
            Resources["GlassInk"] = Ink; Resources["GlassMuted"] = Muted; Resources["GlassPlate"] = Plate;
            Resources["GlassLine"] = Line; Resources["GlassAccent"] = Accent;
            Resources["GlassHover"] = SystemColors.ControlBrush; Resources["GlassSelected"] = SystemColors.ControlBrush;
        }
        Foreground = Ink; Status = Text("", 11, true); Status.TextWrapping = TextWrapping.Wrap;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Margin = new Thickness(24, 0, 10, 0) };
        var close = IconButton("\uE8BB", "closeName", Close);
        close.Width = 32; close.Height = 30; close.Margin = new Thickness(12, 0, 0, 0);
        HeaderActions.Children.Add(close);
        WindowChrome.SetIsHitTestVisibleInChrome(HeaderActions, true);
        DockPanel.SetDock(HeaderActions, Dock.Right); header.Children.Add(HeaderActions);
        var caption = Text(title, 12, true); caption.FontWeight = FontWeights.Medium; header.Children.Add(caption);
        root.Children.Add(header); Grid.SetRow(Layout, 1); root.Children.Add(Layout);
        var surface = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = Line, Child = root };
        Content = surface;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(12), UseAeroCaptionButtons = false
        });
        SourceInitialized += (_, _) => backdrop = new GlassBackdrop(this, surface);
        Loaded += (_, _) => { if (CenterOnOpen) CenterOnPointer(); DesktopMotion.Enter(Layout); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) => { backdrop?.Dispose(); Lifetime.Cancel(); Lifetime.Dispose(); };
    }

    protected void AddHeaderAction(Button button) => HeaderActions.Children.Insert(HeaderActions.Children.Count - 1, button);
    protected static Brush Paint(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    protected TextBlock Text(string value, double size = 13, bool muted = false) => new()
    {
        Text = value, FontSize = size, Foreground = muted ? Muted : Ink,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };
    protected TextBlock Glyph(string glyph, double size = 18) => new()
    {
        Text = glyph, FontSize = size, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };
    protected Button ActionButton(string key, Action action)
    {
        var button = new Button { Content = Loc.T(key), Margin = new Thickness(0, 0, 6, 0) };
        AutomationProperties.SetName(button, Loc.T(key)); button.Click += (_, _) => action(); return button;
    }
    protected Button IconButton(string glyph, string key, Action action)
    {
        var button = ActionButton(key, action); button.Content = Glyph(glyph, 15); button.ToolTip = Loc.T(key);
        button.Padding = new Thickness(8); button.MinWidth = 32; button.Background = Brushes.Transparent;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.BorderThickness = new Thickness(0); return button;
    }
    protected TextBox Input(string name, double width = double.NaN)
    {
        var input = new TextBox { Width = width, ToolTip = name, Tag = name };
        AutomationProperties.SetName(input, name); return input;
    }
    protected ListBox List() => new()
    {
        Background = Brushes.Transparent, Foreground = Ink, BorderThickness = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0)
    };
    protected ListBoxItem Row(object value, string title, string detail, string glyph = "\uE8A5")
    {
        var grid = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Glyph(glyph, 20); icon.HorizontalAlignment = HorizontalAlignment.Left; icon.Foreground = Accent; grid.Children.Add(icon);
        var stack = new StackPanel { Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Text(title, 14));
        if (!string.IsNullOrEmpty(detail))
        { var sub = Text(detail, 11, true); sub.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(sub); }
        Grid.SetColumn(stack, 1); grid.Children.Add(stack);
        var row = new ListBoxItem { Content = grid, Tag = value, ToolTip = detail, Padding = new Thickness(0) };
        AutomationProperties.SetName(row, title); return row;
    }
    protected Border Rule() => new() { Background = Line, Height = 1 };
    protected StackPanel EmptyState(string glyph, string title, string detail)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(20) };
        panel.Children.Add(Glyph(glyph, 32));
        var heading = Text(Loc.T(title), 18); heading.HorizontalAlignment = HorizontalAlignment.Center; heading.Margin = new Thickness(0, 18, 0, 8); panel.Children.Add(heading);
        var hint = Text(Loc.T(detail), 12, true); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; panel.Children.Add(hint);
        return panel;
    }
    void CenterOnPointer()
    {
        if (WindowStartupLocation == WindowStartupLocation.CenterOwner && Owner is not null) return;
        Native.GetCursorPos(out var pointer); var monitor = Native.MonitorFromPoint(pointer, 2);
        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var bounds = info.Work;
        var width = Math.Min((int)(Width * scale), bounds.Right - bounds.Left);
        var height = Math.Min((int)(Height * scale), bounds.Bottom - bounds.Top);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, 0, bounds.Left + (bounds.Right - bounds.Left - width) / 2,
            bounds.Top + Math.Max(0, (bounds.Bottom - bounds.Top - height) / 3), width, height, Native.SWP_NOACTIVATE);
    }
}
