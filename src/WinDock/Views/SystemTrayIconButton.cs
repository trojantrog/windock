using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinDock.Services;

namespace WinDock.Views;

public sealed class SystemTrayIconButton : Border
{
    internal const string DragFormat = "WinDock.TrayIcon";
    readonly SystemTrayService service;
    readonly SystemTrayItem item;
    readonly bool editing;
    readonly DispatcherTimer hover;
    Point pressedAt;
    MouseButton? pressed;
    int clicks;
    bool popupOpen, dragging;
    long moveAt;
    public event Action<SystemTrayItem>? PinRequested;
    public event Action<bool>? DraggingChanged;
    public event Action<SystemTrayItem, int>? MoveRequested;
    public SystemTrayItem Item => item;

    public SystemTrayIconButton(SystemTrayService service, SystemTrayItem item, bool dark, bool showName = false,
        double iconSize = 22, bool editing = false, bool pinned = false)
    {
        this.service = service; this.item = item; this.editing = editing; DataContext = item;
        if (SystemParameters.HighContrast) dark = SystemColors.WindowColor.R + SystemColors.WindowColor.G + SystemColors.WindowColor.B < 384;
        CornerRadius = new CornerRadius(7); Background = Brushes.Transparent;
        Width = showName ? Math.Max(78, iconSize + 44) : Math.Max(36, iconSize + 16);
        Height = showName ? iconSize + 46 : Math.Max(36, iconSize + 16);
        Margin = new Thickness(2); UseLayoutRounding = true; SnapsToDevicePixels = true;
        Focusable = true; Cursor = Cursors.Hand;
        if (!editing) SetBinding(IsEnabledProperty, new Binding(nameof(SystemTrayItem.CanInvoke)));
        SetBinding(ToolTipProperty, new Binding(editing ? nameof(SystemTrayItem.AccessibleName) : nameof(SystemTrayItem.HoverTip)));
        SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SystemTrayItem.AccessibleName)));
        ToolTipService.SetShowOnDisabled(this, true);
        ToolTipService.SetInitialShowDelay(this, Math.Max(250, (int)SystemParameters.MouseHoverTime.TotalMilliseconds));
        ToolTipService.SetShowDuration(this, 15000);
        var grid = new Grid();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var image = new Image { Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(SystemTrayItem.Icon)) { Converter = new TrayIconTint(dark) });
        stack.Children.Add(image);
        if (showName)
        {
            var name = new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(35, 39, 46)), Margin = new Thickness(3, 5, 3, 0) };
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SystemTrayItem.Title))); stack.Children.Add(name);
        }
        grid.Children.Add(stack);
        var hint = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(45, 136, 230)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 4, 0) };
        if (!editing) hint.SetBinding(VisibilityProperty, new Binding(nameof(SystemTrayItem.HasHint)) { Converter = new BooleanToVisibilityConverter() });
        else hint.Visibility = Visibility.Collapsed;
        grid.Children.Add(hint);
        if (editing)
        {
            BorderThickness = new Thickness(1);
            BorderBrush = pinned ? new SolidColorBrush(Color.FromRgb(65, 145, 230)) : Brushes.Transparent;
            var pin = new TextBlock { Text = pinned ? "\uE73E" : "\uE718", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 9, Foreground = dark ? Brushes.WhiteSmoke : Brushes.DimGray, Margin = new Thickness(0, 2, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(pin);
            var menu = new ContextMenu();
            if (!dark)
            {
                menu.Resources["MenuInk"] = new SolidColorBrush(Color.FromRgb(35, 39, 46));
                menu.Resources["MenuSurface"] = new SolidColorBrush(Color.FromRgb(245, 246, 248));
                menu.Resources["MenuLine"] = new SolidColorBrush(Color.FromArgb(28, 0, 0, 0));
                menu.Resources["MenuHover"] = new SolidColorBrush(Color.FromArgb(18, 0, 0, 0));
            }
            AddMenu(menu, Loc.T(pinned ? "trayUnpinIcon" : "trayPinIcon"), () => PinRequested?.Invoke(item), item.StableId.Length > 0);
            AddMenu(menu, Loc.T("trayMoveBefore"), () => MoveRequested?.Invoke(item, -1), item.StableId.Length > 0);
            AddMenu(menu, Loc.T("trayMoveAfter"), () => MoveRequested?.Invoke(item, 1), item.StableId.Length > 0);
            ContextMenu = menu;
        }
        Child = grid;
        var hoverBrush = new SolidColorBrush(dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)); hoverBrush.Freeze();
        hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(250, SystemParameters.MouseHoverTime.TotalMilliseconds)) };
        hover.Tick += (_, _) =>
        {
            hover.Stop();
            if (!IsMouseOver || editing || !IsEnabled) return;
            Send(0x2A1); // WM_MOUSEHOVER
            if (item.Version >= 4 && !item.ShowStandardTip) { Send(0x406); popupOpen = true; }
        };
        MouseEnter += (_, _) => { Background = hoverBrush; if (!editing) { Send(0x200); hover.Start(); } };
        MouseLeave += (_, _) =>
        {
            Background = IsKeyboardFocusWithin ? hoverBrush : Brushes.Transparent; hover.Stop();
            if (!editing) { Send(0x2A3); if (popupOpen) Send(0x407); popupOpen = false; }
        };
        GotKeyboardFocus += (_, _) => Background = hoverBrush;
        LostKeyboardFocus += (_, _) => { if (!IsMouseOver) Background = Brushes.Transparent; };
        Unloaded += (_, _) => { hover.Stop(); if (popupOpen) Send(0x407); popupOpen = false; };
        MouseMove += OnMove;
        MouseDown += OnDown;
        MouseUp += OnUp;
        KeyDown += OnKey;
        LostMouseCapture += (_, _) => { if (!dragging) pressed = null; };
    }

    static void AddMenu(ContextMenu menu, string title, Action action, bool enabled)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (editing && e.ChangedButton != MouseButton.Left) return; // WPF edit menu
        e.Handled = true; Focus(); pressed = e.ChangedButton; clicks = e.ClickCount; pressedAt = e.GetPosition(this);
        CaptureMouse();
        if (editing) return;
        hover.Stop();
        uint message = e.ChangedButton switch
        {
            MouseButton.Left => e.ClickCount == 2 ? 0x203u : 0x201u,
            MouseButton.Right => e.ClickCount == 2 ? 0x206u : 0x204u,
            MouseButton.Middle => e.ClickCount == 2 ? 0x209u : 0x207u, _ => 0
        };
        if (message != 0) Send(message);
    }
    void OnMove(object sender, MouseEventArgs e)
    {
        if (editing)
        {
            var delta = e.GetPosition(this) - pressedAt;
            if (pressed != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed || dragging || item.StableId.Length == 0 ||
                (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)) return;
            dragging = true; pressed = null; DraggingChanged?.Invoke(true); ReleaseMouseCapture();
            try { DragDrop.DoDragDrop(this, new DataObject(DragFormat, item.StableId), DragDropEffects.Move); }
            finally { dragging = false; DraggingChanged?.Invoke(false); }
            return;
        }
        var now = Environment.TickCount64;
        if (now - moveAt > 50) { moveAt = now; Send(0x200); }
    }
    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (pressed != e.ChangedButton) return;
        e.Handled = true; pressed = null;
        if (editing)
        {
            if (!dragging && clicks == 1 && IsMouseOver && item.StableId.Length > 0) PinRequested?.Invoke(item);
        }
        else
        {
            // Explorer, ManagedShell and YASB send both the legacy up and the
            // version-3+ semantic callback. Do not additionally invoke/open.
            switch (e.ChangedButton)
            {
                case MouseButton.Left: Send(0x202); if (item.Version >= 3) Send(0x400); service.MarkSeen(item); break;
                case MouseButton.Right: Send(0x205); if (item.Version >= 3) Send(0x7B); break;
                case MouseButton.Middle: Send(0x208); break;
            }
        }
        ReleaseMouseCapture();
    }
    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;
        if (editing)
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; if (item.StableId.Length > 0) PinRequested?.Invoke(item); }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Left or Key.Right)
            { e.Handled = true; MoveRequested?.Invoke(item, e.Key == Key.Left ? -1 : 1); }
            return;
        }
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            if (item.Version >= 3) Send(0x401, true); else { Send(0x201, true); Send(0x202, true); }
            service.MarkSeen(item);
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            if (item.Version >= 3) Send(0x7B, true); else { Send(0x204, true); Send(0x205, true); }
        }
    }
    void Send(uint message, bool keyboard = false)
    {
        Point point;
        if (keyboard) point = PointToScreen(new Point(0, 0));
        else { Native.GetCursorPos(out var cursor); point = new Point(cursor.X, cursor.Y); }
        service.Invoke(item, message, point);
    }
}
