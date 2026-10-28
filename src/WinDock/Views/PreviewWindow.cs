using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinDock.Services;
using WinDock.ViewModels;
using Forms = System.Windows.Forms;

namespace WinDock.Views;

public sealed class PreviewWindow : Window
{
    double boxWidth = 320, boxHeight = 200;
    const int DwmCorner = 33, DwmRound = 2;
    const uint ThumbDestination = 0x1, ThumbVisible = 0x8, ThumbOpacity = 0x4, ThumbClientOnly = 0x10;

    readonly StackPanel cards = new() { Orientation = Orientation.Horizontal };
    readonly List<Thumbnail> thumbs = [];
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(300) };
    nint hwnd;
    int generation;
    bool dark = true;
    (double Center, double Top, Forms.Screen Screen)? anchor;

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        Content = new Border { Padding = new Thickness(6), Child = cards };

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
            var round = DwmRound;
            Native.DwmSetWindowAttribute(hwnd, DwmCorner, ref round, sizeof(int));
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { generation++; refresh.Stop(); Release(); WindowCloseRequest.Prune(); }
        };
        MouseEnter += (_, _) => PointerEntered?.Invoke();
        MouseLeave += (_, _) => PointerLeft?.Invoke();
        refresh.Tick += (_, _) => { RefreshCards(); Reposition(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) return;
            SizeCards(VisualTreeHelper.GetDpi(this).DpiScaleX);
            UpdateLayout(); Position(); Reposition();
        }, DispatcherPriority.Loaded);
    }

    public AppTile? Tile { get; private set; }

    public event Action? PointerEntered;
    public event Action? PointerLeft;
    public event Action<nint>? Picked;

    Brush Ink => dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x24));

    public void ShowFor(AppTile tile, double centerX, double top, Forms.Screen screen, bool darkTheme)
    {
        dark = darkTheme;
        Tile = tile;
        var requestedGeneration = ++generation;
        refresh.Stop();
        Release();
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x1C, 0x1E, 0x26) : Color.FromRgb(0xF6, 0xF6, 0xF8));
        cards.Children.Clear();
        anchor = (centerX, top, screen);
        var monitor = Native.MonitorFromPoint(new Native.Point { X = (int)centerX, Y = (int)top }, 2);
        var dpiScale = Native.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
        var windows = tile.Windows.Where(window => window.IsCurrent).Take(6).ToArray();
        SetBoxSize(screen, dpiScale, windows.Length);
        foreach (var window in windows) cards.Children.Add(Card(tile, window));
        if (cards.Children.Count == 0)
        {
            Hide();
            return;
        }

        if (!IsVisible) Show();
        UpdateLayout();
        Position();
        Dispatcher.BeginInvoke(() => { if (requestedGeneration == generation) Register(); }, DispatcherPriority.Loaded);
    }

    void SetBoxSize(Forms.Screen screen, double dpiScale, int count)
    {
        var available = (screen.Bounds.Width / Math.Max(dpiScale, 0.1) - 28) / Math.Max(1, count) - 16;
        boxWidth = Math.Clamp(available, 1, 420);
        boxHeight = Math.Round(boxWidth * 0.625);
    }

    void SizeCards(double dpiScale)
    {
        if (anchor is not { } location) return;
        SetBoxSize(location.Screen, dpiScale, cards.Children.Count);
        foreach (var card in cards.Children.OfType<Border>())
        {
            if (card.Child is not StackPanel { Children.Count: 2 } panel
                || panel.Children[0] is not DockPanel header || panel.Children[1] is not Border box) continue;
            header.Width = boxWidth; box.Width = boxWidth; box.Height = boxHeight;
        }
    }

    void Position()
    {
        if (anchor is not { } location || hwnd == 0) return;
        var (centerX, top, screen) = location;
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
        var marginX = (int)Math.Round(8 * dpi.DpiScaleX);
        var marginY = (int)Math.Round(8 * dpi.DpiScaleY);
        var bounds = screen.Bounds;
        var x = (int)Math.Round(Math.Clamp(centerX - w / 2.0, bounds.Left + marginX, Math.Max(bounds.Left + marginX, bounds.Right - w - marginX)));
        var y = (int)Math.Round(Math.Max(bounds.Top + marginY, top - h - 10 * dpi.DpiScaleY));
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    Border Card(AppTile tile, AppWindow window)
    {
        var title = new TextBlock
        {
            Text = Native.TitleOf(window.Handle) is { Length: > 0 } live ? live : window.Title,
            FontSize = 12,
            Foreground = Ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "\uE8BB",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;

        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6), Width = boxWidth };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        if (tile.Icon is { } icon)
        {
            var small = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) };
            RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(small, Dock.Left);
            header.Children.Add(small);
        }
        header.Children.Add(title);

        var box = new Border
        {
            Width = boxWidth,
            Height = boxHeight,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0)),
            Tag = window.Handle,
            Child = tile.Icon is { } big ? new Image { Source = big, Width = 40, Height = 40, Opacity = 0.85 } : null
        };

        var card = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new StackPanel { Children = { header, box } }
        };
        var preview = new PreviewCard(window, title, box, close);
        card.Tag = preview;
        UpdateCloseButton(preview);
        var hoverBrush = new SolidColorBrush(dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0, 0, 0));
        card.MouseEnter += (_, _) => card.Background = hoverBrush;
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // A disabled close control must not become a click on the preview.
            var atClose = e.GetPosition(close);
            if (atClose.X >= 0 && atClose.Y >= 0 && atClose.X < close.ActualWidth && atClose.Y < close.ActualHeight) return;
            if (window.IsCurrent) Picked?.Invoke(window.Handle);
            else RefreshCards();
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            // A close request may open a save prompt or be refused. Keep the
            // live preview until the source window actually leaves the screen.
            WindowCloseRequest.TryClose(window);
            UpdateCloseButton(preview);
            RefreshCards();
        };
        return card;
    }

    void Register()
    {
        if (!IsVisible || hwnd == 0) return;
        Release();
        foreach (var card in cards.Children.OfType<Border>())
        {
            if (card.Tag is not PreviewCard preview || !preview.Window.IsCurrent) continue;
            var box = preview.Box;
            var source = preview.Window.Handle;
            if (Native.DwmRegisterThumbnail(hwnd, source, out var thumb) != 0) continue;
            thumbs.Add(new Thumbnail(thumb, box));
        }
        Reposition();
        if (cards.Children.Count > 0) refresh.Start();
    }

    void RefreshCards()
    {
        if (!IsVisible) return;
        WindowCloseRequest.Prune();
        var removed = false;
        foreach (var card in cards.Children.OfType<Border>().ToArray())
        {
            if (card.Tag is not PreviewCard preview) continue;
            if (!preview.Window.IsCurrent || !Native.IsWindowVisible(preview.Window.Handle))
            {
                Unregister(preview.Box);
                cards.Children.Remove(card);
                removed = true;
                continue;
            }
            var title = Native.TitleOf(preview.Window.Handle);
            if (title.Length > 0 && title != preview.Title.Text) preview.Title.Text = title;
            UpdateCloseButton(preview);
        }
        if (cards.Children.Count == 0) { Hide(); return; }
        if (!removed) return;
        SizeCards(VisualTreeHelper.GetDpi(this).DpiScaleX);
        UpdateLayout();
        Position();
    }

    static void UpdateCloseButton(PreviewCard preview)
    {
        var enabled = WindowCloseRequest.CanClose(preview.Window);
        preview.Close.IsEnabled = enabled;
        preview.Close.Opacity = enabled ? 1 : 0.4;
        if (!enabled) preview.Close.Background = Brushes.Transparent;
    }

    void Reposition()
    {
        if (!IsVisible || hwnd == 0) return;
        var client = PointToScreen(new Point());
        foreach (var item in thumbs)
        {
            var box = item.Box;
            if (!box.IsLoaded) continue;
            var origin = box.PointToScreen(new Point());
            var end = box.PointToScreen(new Point(box.ActualWidth, box.ActualHeight));
            var x1 = (int)Math.Round(origin.X - client.X);
            var y1 = (int)Math.Round(origin.Y - client.Y);
            var width = (int)Math.Round(end.X - client.X) - x1;
            var height = (int)Math.Round(end.Y - client.Y) - y1;
            var valid = Native.DwmQueryThumbnailSourceSize(item.Handle, out var source) == 0
                && source.Width > 0 && source.Height > 0 && width > 0 && height > 0;
            if (!valid)
            {
                if (item.Visible)
                {
                    var hidden = new Native.ThumbnailProperties { Flags = ThumbVisible, Visible = 0 };
                    if (Native.DwmUpdateThumbnailProperties(item.Handle, ref hidden) == 0) item.Visible = false;
                }
                continue;
            }
            var fit = Math.Min((double)width / source.Width, (double)height / source.Height);
            var fw = Math.Clamp((int)Math.Round(source.Width * fit), 1, width);
            var fh = Math.Clamp((int)Math.Round(source.Height * fit), 1, height);
            var left = x1 + (width - fw) / 2;
            var topPx = y1 + (height - fh) / 2;
            var destination = new Native.Rect { Left = left, Top = topPx, Right = left + fw, Bottom = topPx + fh };
            if (item.Visible && SameRect(item.Destination, destination)) continue;
            var props = new Native.ThumbnailProperties
            {
                Flags = ThumbDestination | ThumbVisible | ThumbOpacity | ThumbClientOnly,
                Destination = destination,
                Opacity = 255,
                Visible = 1,
                SourceClientAreaOnly = 0
            };
            if (Native.DwmUpdateThumbnailProperties(item.Handle, ref props) == 0)
            {
                item.Destination = destination;
                item.Visible = true;
            }
        }
    }

    static bool SameRect(Native.Rect left, Native.Rect right) => left.Left == right.Left && left.Top == right.Top
        && left.Right == right.Right && left.Bottom == right.Bottom;

    void Unregister(Border box)
    {
        var index = thumbs.FindIndex(t => t.Box == box);
        if (index < 0) return;
        Native.DwmUnregisterThumbnail(thumbs[index].Handle);
        thumbs.RemoveAt(index);
    }

    void Release()
    {
        foreach (var item in thumbs) Native.DwmUnregisterThumbnail(item.Handle);
        thumbs.Clear();
    }

    sealed class Thumbnail(nint handle, Border box)
    {
        public nint Handle { get; } = handle;
        public Border Box { get; } = box;
        public Native.Rect Destination { get; set; }
        public bool Visible { get; set; }
    }

    sealed record PreviewCard(AppWindow Window, TextBlock Title, Border Box, Border Close);
}
