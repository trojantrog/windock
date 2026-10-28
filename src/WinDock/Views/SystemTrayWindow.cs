using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using WinDock.Models;
using WinDock.Services;
using Forms = System.Windows.Forms;

namespace WinDock.Views;

public sealed class SystemTrayWindow : Window
{
    readonly SystemTrayHub hub;
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    readonly DispatcherTimer position = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Border surface;
    GlassBackdrop? backdrop;
    TrayPanel? panel;
    int capacity = 4;
    bool dark;
    readonly Button more;
    readonly PopupInteraction interaction;
    bool renderPending, renderQueued, closed;
    public SystemTrayWindow(SystemTrayHub hub)
    {
        this.hub = hub; dark = DockTheme.Find(hub.Options.Theme).Dark;
        interaction = PopupInteraction.For(this);
        interaction.Changed += () => { if (renderPending && !interaction.IsBusy) QueueRender(); };
        Title = Loc.T("systemTrayTitle"); ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000; Top = -32000; UseLayoutRounding = true; SnapsToDevicePixels = true;
        surface = new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = row };
        Content = surface;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(-1), UseAeroCaptionButtons = false });
        more = new Button { Content = new TextBlock { Text = "\uE70E", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
            ToolTip = Loc.T("systemTrayTitle"), Width = 32, Height = 38, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var template = new ControlTemplate(typeof(Button));
        var plate = new FrameworkElementFactory(typeof(Border)) { Name = "Plate" };
        plate.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        plate.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        plate.AppendChild(content); template.VisualTree = plate;
        foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(22, 128, 128, 128)), "Plate"));
            template.Triggers.Add(trigger);
        }
        more.Template = template;
        AutomationProperties.SetName(more, Loc.T("systemTrayTitle"));
        more.Click += (_, _) =>
        {
            if (PopupInteraction.WasAnchorDismissed(more)) return;
            if (panel is not null) { panel.Close(); panel = null; return; }
            var opened = new TrayPanel(dark) { Owner = this };
            panel = opened;
            opened.Interaction.Anchor = more;
            opened.Closed += (_, _) => { if (panel == opened) panel = null; };
            var corner = more.PointToScreen(new Point(more.ActualWidth / 2, 0));
            panel.ShowAt(corner.X, corner.Y, Forms.Screen.FromPoint(new System.Drawing.Point((int)corner.X, (int)corner.Y)));
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(ex | Native.WS_EX_TOOLWINDOW));
            ApplyTheme();
        };
        hub.Service.Icons.CollectionChanged += IconsChanged; hub.Service.PropertyChanged += StatusChanged;
        hub.Service.LayoutChanged += Render; hub.PresentationChanged += OptionsChanged;
        Loaded += (_, _) => { Render(); Place(); position.Start(); };
        position.Tick += (_, _) => Place();
        Closed += (_, _) =>
        {
            closed = true;
            position.Stop(); panel?.Close(); backdrop?.Dispose();
            hub.Service.Icons.CollectionChanged -= IconsChanged; hub.Service.PropertyChanged -= StatusChanged;
            hub.Service.LayoutChanged -= Render; hub.PresentationChanged -= OptionsChanged;
        };
    }
    void ApplyTheme()
    {
        dark = DockTheme.Find(hub.Options.Theme).Dark;
        surface.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
        more.Foreground = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 39, 46));
        backdrop?.Dispose(); backdrop = new GlassBackdrop(this, surface, dark);
    }
    void OptionsChanged()
    {
        if (new WindowInteropHelper(this).Handle != 0 && dark != DockTheme.Find(hub.Options.Theme).Dark)
        { panel?.Close(); ApplyTheme(); }
        Render(); Place();
    }
    void StatusChanged(object? sender, PropertyChangedEventArgs e) =>
        more.ToolTip = hub.Service.Ready ? Loc.T("systemTrayTitle") : Loc.T("systemTrayTitle") + "\n" + hub.Service.Status;
    void IconsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Render();
    void QueueRender()
    {
        if (closed || renderQueued) return;
        renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { renderQueued = false; Render(); });
    }
    void Render()
    {
        if (closed) return;
        if (interaction.IsBusy) { renderPending = true; return; }
        renderPending = false;
        var oldButtons = row.Children.OfType<SystemTrayIconButton>().ToList();
        var focusedButton = Keyboard.FocusedElement as SystemTrayIconButton;
        var focusedIndex = focusedButton is null ? -1 : oldButtons.IndexOf(focusedButton);
        var moreFocused = more.IsKeyboardFocusWithin;
        row.Children.Clear();
        var buttonSize = Math.Max(36, hub.Options.TrayIconSize + 16);
        more.Height = buttonSize + 4;
        foreach (var item in hub.OrderedIcons().Where(hub.IsPinned).Take(capacity))
            row.Children.Add(new SystemTrayIconButton(hub.Service, item, dark, iconSize: hub.Options.TrayIconSize));
        row.Children.Add(more);
        if (!IsActive) return;
        if (moreFocused) more.Focus();
        else if (focusedIndex >= 0)
        {
            var buttons = row.Children.OfType<SystemTrayIconButton>().Where(button => button.IsEnabled).ToList();
            var replacement = buttons.FirstOrDefault(button => button.Item == focusedButton!.Item)
                ?? buttons.ElementAtOrDefault(Math.Min(focusedIndex, buttons.Count - 1));
            if (replacement is not null) replacement.Focus(); else more.Focus();
        }
    }
    void Place()
    {
        if (closed || interaction.IsBusy || panel?.Interaction.IsBusy == true) return;
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var bounds = screen.Bounds;
        var monitor = Native.MonitorFromPoint(new Native.Point { X = bounds.Left + 1, Y = bounds.Top + 1 }, 2);
        double scale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        var left = bounds.Left + (int)(12 * scale);
        var bottom = hub.Options.ReplaceTaskbar ? bounds.Bottom : screen.WorkingArea.Bottom;
        double available = bounds.Width * 0.22;
        foreach (var dock in Application.Current.Windows.OfType<DockWindow>().Where(w => w.IsVisible))
            if (Native.GetWindowRect(new WindowInteropHelper(dock).Handle, out var rect) && rect.Right > left && rect.Left < bounds.Right && rect.Top < bottom && rect.Bottom > bottom - 160 * scale)
                available = Math.Min(available, rect.Left - left - 12 * scale);
        if (available < 42 * scale) { if (IsVisible && panel is null) Hide(); return; }
        var slot = Math.Max(36, hub.Options.TrayIconSize + 16) + 4;
        var next = Math.Clamp((int)((available / scale - 42) / slot), 0, 12);
        if (capacity != next) { capacity = next; Render(); UpdateLayout(); }
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
        bool fullScreen = foreground != 0 && foregroundPid != Environment.ProcessId &&
            Native.ClassOf(foreground) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") &&
            Native.GetWindowRect(foreground, out var front) && front.Left <= bounds.Left && front.Top <= bounds.Top &&
            front.Right >= bounds.Right && front.Bottom >= bounds.Bottom && WindowMotionNative.IsFullscreen(foreground);
        if (fullScreen) { if (IsVisible && panel is null) Hide(); return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, bottom - (int)Math.Ceiling(ActualHeight * scale) - (int)(10 * scale), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
