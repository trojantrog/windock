using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinDock.Services;
using Forms = System.Windows.Forms;

namespace WinDock.Views;

public sealed class SystemTrayWindow : Window
{
    readonly SystemTrayHub hub;
    readonly StackPanel row = new() { Orientation = Orientation.Horizontal };
    readonly DispatcherTimer position = new() { Interval = TimeSpan.FromSeconds(1) };
    GlassBackdrop? backdrop;
    TrayPanel? panel;
    int capacity = 4;
    readonly Button more;
    public SystemTrayWindow(SystemTrayHub hub)
    {
        this.hub = hub;
        Title = Loc.T("systemTrayTitle"); ShowInTaskbar = false; ShowActivated = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WinDock;component/Themes/DesktopGlass.xaml", UriKind.Relative) });
        var surface = new Border { Padding = new Thickness(3), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("GlassLine"), Child = row };
        Content = surface;
        more = new Button { Content = new TextBlock { Text = "\uE712", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15 },
            ToolTip = Loc.T("systemTrayTitle"), Width = 32, Height = 34, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        more.Click += (_, _) =>
        {
            if (panel is not null) { panel.Close(); panel = null; return; }
            panel = new TrayPanel(true); panel.Closed += (_, _) => panel = null;
            var corner = more.PointToScreen(new Point(more.ActualWidth / 2, 0));
            panel.ShowAt(corner.X, corner.Y, Forms.Screen.FromPoint(new System.Drawing.Point((int)corner.X, (int)corner.Y)));
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(ex | Native.WS_EX_TOOLWINDOW));
            backdrop = new GlassBackdrop(this, surface);
        };
        hub.Service.Icons.CollectionChanged += IconsChanged; hub.Service.PropertyChanged += StatusChanged;
        hub.Service.LayoutChanged += Render;
        Loaded += (_, _) => { Render(); Place(); position.Start(); };
        position.Tick += (_, _) => Place();
        Closed += (_, _) =>
        {
            position.Stop(); panel?.Close(); backdrop?.Dispose();
            hub.Service.Icons.CollectionChanged -= IconsChanged; hub.Service.PropertyChanged -= StatusChanged;
            hub.Service.LayoutChanged -= Render;
        };
    }
    void StatusChanged(object? sender, PropertyChangedEventArgs e) => more.ToolTip = Loc.T("systemTrayTitle") + "\n" + hub.Service.Status;
    void IconsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Render();
    void Render()
    {
        row.Children.Clear();
        foreach (var item in hub.Service.Icons.Where(i => !i.Hidden).Take(capacity)) row.Children.Add(new SystemTrayIconButton(hub.Service, item, true));
        row.Children.Add(more);
    }
    void Place()
    {
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
        if (available < 42 * scale) { if (IsVisible) Hide(); return; }
        var next = Math.Clamp((int)((available / scale - 42) / 38), 0, 6);
        if (capacity != next) { capacity = next; Render(); UpdateLayout(); }
        var foreground = Native.GetForegroundWindow();
        bool fullScreen = foreground != 0 && foreground != hwnd && Native.ClassOf(foreground) is not ("Progman" or "WorkerW" or "Shell_TrayWnd") &&
            Native.GetWindowRect(foreground, out var front) && front.Left <= bounds.Left && front.Top <= bounds.Top && front.Right >= bounds.Right && front.Bottom >= bounds.Bottom;
        if (fullScreen) { if (IsVisible && panel is null) Hide(); return; }
        if (!IsVisible) Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, bottom - (int)Math.Ceiling(ActualHeight * scale) - (int)(10 * scale), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
