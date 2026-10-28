using System.ComponentModel;
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
    bool popupOpen, dragging, nativePress;
    CancellationTokenSource? nativeActivation;
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
        Focusable = true; Cursor = editing || item.CanInvoke || TrayActionRouter.CanAttempt(item) ? Cursors.Hand : Cursors.Arrow;
        if (!editing) PropertyChangedEventManager.AddHandler(item, ItemChanged, "");
        SetBinding(ToolTipProperty, new Binding(editing ? nameof(SystemTrayItem.AccessibleName) : nameof(SystemTrayItem.HoverTip)));
        SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SystemTrayItem.AccessibleName)));
        SetBinding(AutomationProperties.HelpTextProperty, new Binding(nameof(SystemTrayItem.Tip)));
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
            if (!IsMouseOver || editing || !IsEnabled || pressed is not null || Interaction?.IsBusy == true) return;
            Send(0x2A1); // WM_MOUSEHOVER
            if (item.Version >= 4 && item.CanInvoke && !item.ShowStandardTip)
            {
                Interaction?.BeginTrayHover(item);
                popupOpen = Send(0x406);
                if (!popupOpen) Interaction?.CompleteTrayHover(item);
            }
        };
        MouseEnter += (_, _) => { Background = hoverBrush; if (!editing) { Send(0x200); hover.Start(); } };
        MouseLeave += (_, _) =>
        {
            Background = IsKeyboardFocusWithin ? hoverBrush : Brushes.Transparent;
            if (!editing) Send(0x2A3);
            EndHover();
        };
        GotKeyboardFocus += (_, _) => Background = hoverBrush;
        LostKeyboardFocus += (_, _) => { if (!IsMouseOver) Background = Brushes.Transparent; };
        Unloaded += (_, _) =>
        {
            pressed = null;
            nativePress = false;
            nativeActivation?.Cancel();
            if (IsMouseCaptured) ReleaseMouseCapture();
            EndHover();
        };
        MouseMove += OnMove;
        MouseDown += OnDown;
        MouseUp += OnUp;
        KeyDown += OnKey;
        LostMouseCapture += (_, _) =>
        {
            if (dragging || pressed is null) return;
            pressed = null;
            if (!editing && !nativePress) Interaction?.CompleteTrayMenu(true);
            nativePress = false;
        };
    }

    static void AddMenu(ContextMenu menu, string title, Action action, bool enabled)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }

    void ItemChanged(object? sender, PropertyChangedEventArgs e) =>
        Cursor = item.CanInvoke || TrayActionRouter.CanAttempt(item) ? Cursors.Hand : Cursors.Arrow;

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle) || pressed is not null) return;
        if (editing && e.ChangedButton != MouseButton.Left) return; // WPF edit menu
        if (!editing && (nativeActivation is not null || !item.CanInvoke
            && (e.ChangedButton != MouseButton.Left || !TrayActionRouter.CanAttempt(item))))
        { e.Handled = true; Focus(); return; }
        e.Handled = true; Focus(); pressed = e.ChangedButton; clicks = e.ClickCount; pressedAt = e.GetPosition(this);
        nativePress = !editing && !item.CanInvoke;
        if (!CaptureMouse()) { pressed = null; nativePress = false; EndHover(); return; }
        if (editing) return;
        if (nativePress) { EndHover(); return; }
        Interaction?.BeginTrayMenu(item, this);
        EndHover();
        uint message = e.ChangedButton switch
        {
            MouseButton.Left => e.ClickCount % 2 == 0 ? 0x203u : 0x201u,
            MouseButton.Right => e.ClickCount % 2 == 0 ? 0x206u : 0x204u,
            MouseButton.Middle => e.ClickCount % 2 == 0 ? 0x209u : 0x207u, _ => 0
        };
        if (message != 0 && !Send(message)) Interaction?.CompleteTrayMenu(false);
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
        if (!nativePress && nativeActivation is null && now - moveAt > 50) { moveAt = now; Send(0x200); }
    }
    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (pressed != e.ChangedButton)
        {
            if (!editing && (nativeActivation is not null || !item.CanInvoke)
                && e.ChangedButton is MouseButton.Left or MouseButton.Right or MouseButton.Middle) e.Handled = true;
            return;
        }
        e.Handled = true; pressed = null;
        var useNativeAction = nativePress;
        nativePress = false;
        var releasedInside = new Rect(RenderSize).Contains(e.GetPosition(this));
        ReleaseMouseCapture();
        if (!releasedInside)
        {
            if (!editing && !useNativeAction) Interaction?.CompleteTrayMenu(true);
            return;
        }
        if (editing)
        {
            if (!dragging && clicks == 1 && item.StableId.Length > 0) PinRequested?.Invoke(item);
        }
        else if (useNativeAction)
        {
            // Invoke has one default-action meaning. Do not turn the second
            // half of a double click into another toggle/open request.
            if (clicks % 2 != 0) ActivateNative();
        }
        else
        {
            // Explorer, ManagedShell and YASB send both the legacy up and the
            // version-3+ semantic callback. Do not additionally invoke/open.
            var sent = false;
            switch (e.ChangedButton)
            {
                case MouseButton.Left: sent = Send(0x202); if (item.Version >= 3) sent |= Send(0x400); break;
                case MouseButton.Right: sent = Send(0x205); if (item.Version >= 3) sent |= Send(0x7B); break;
                case MouseButton.Middle: sent = Send(0x208); break;
            }
            Interaction?.CompleteTrayMenu(sent);
        }
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
        if (!item.CanInvoke || nativeActivation is not null)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                if (TrayActionRouter.CanAttempt(item)) ActivateNative();
            }
            else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))) e.Handled = true;
            return;
        }
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            Interaction?.BeginTrayMenu(item, this);
            EndHover();
            var sent = item.Version >= 3 ? Send(0x401, true) : Send(0x201, true) & Send(0x202, true);
            Interaction?.CompleteTrayMenu(sent);
        }
        else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            Interaction?.BeginTrayMenu(item, this);
            EndHover();
            var sent = item.Version >= 3 ? Send(0x7B, true) : Send(0x204, true) & Send(0x205, true);
            Interaction?.CompleteTrayMenu(sent);
        }
    }
    PopupInteraction? Interaction => Window.GetWindow(this) is { } window ? PopupInteraction.For(window) : null;

    async void ActivateNative()
    {
        if (nativeActivation is not null || !TrayActionRouter.CanAttempt(item)) return;
        EndHover();
        var cancellation = new CancellationTokenSource();
        nativeActivation = cancellation;
        try { await TrayActionRouter.ActivateAsync(service, item, this, cancellation.Token); }
        finally
        {
            if (ReferenceEquals(nativeActivation, cancellation)) nativeActivation = null;
            cancellation.Dispose();
        }
    }

    void EndHover()
    {
        hover.Stop();
        if (!popupOpen) return;
        popupOpen = false;
        Send(0x407);
        Interaction?.CompleteTrayHover(item);
    }

    bool Send(uint message, bool keyboard = false)
    {
        Point point;
        if (keyboard) point = PointToScreen(new Point(0, 0));
        else
        {
            if (!Native.GetCursorPos(out var cursor)) return false;
            point = new Point(cursor.X, cursor.Y);
        }
        return service.Invoke(item, message, point);
    }
}
