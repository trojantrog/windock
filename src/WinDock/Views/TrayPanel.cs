using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinDock.Services;
using WinDock.ViewModels;

namespace WinDock.Views;

public sealed class TrayPanel : Flyout
{
    readonly SystemTrayHub? hub = SystemTrayHub.Current;
    readonly StackPanel body = new();
    readonly WrapPanel icons = new();
    readonly StackPanel hints = new();
    readonly TextBlock status;
    readonly DockPanel heading = new();
    readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    readonly ScrollViewer iconScroll, hintScroll;
    ContextMenu? menu;
    bool arranging, dragging, showingHints, renderQueued, renderPending, closed;
    protected override bool KeepOpenOnDeactivate => dragging;

    public TrayPanel(bool dark) : base(dark, glass: true)
    {
        Title = Loc.T("systemTrayTitle");
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SetBody(body);
        heading.Margin = new Thickness(0, 0, 0, 8); body.Children.Add(heading);
        status = Text("", 11, muted: true); status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.None; status.Margin = new Thickness(3, 4, 3, 10); body.Children.Add(status);
        iconScroll = new ScrollViewer { Content = icons, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        hintScroll = new ScrollViewer { Content = hints, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        body.Children.Add(iconScroll); body.Children.Add(hintScroll);
        footer.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(footer);
        KeyboardNavigation.SetTabNavigation(icons, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetDirectionalNavigation(icons, KeyboardNavigationMode.Contained);
        icons.AllowDrop = true; icons.DragOver += OnDragOver; icons.Drop += OnDrop;
        PreviewKeyDown += Navigate;
        Interaction.Changed += () => { if (renderPending && !dragging && !Interaction.IsBusy) QueueRender(); };
        Interaction.TrayFocusRestored += item => icons.Children.OfType<SystemTrayIconButton>()
            .FirstOrDefault(button => ReferenceEquals(button.Item, item))?.Focus();
        if (hub is not null)
        {
            hub.Service.Icons.CollectionChanged += RefreshIcons; hub.Service.Hints.CollectionChanged += RefreshHints;
            hub.Service.PropertyChanged += RefreshStatus; hub.Service.LayoutChanged += QueueRender;
            hub.Service.FocusRequested += Interaction.RequestTrayFocus;
            hub.PresentationChanged += QueueRender;
        }
        Loaded += (_, _) => icons.Children.OfType<SystemTrayIconButton>().FirstOrDefault()?.Focus();
        Closed += (_, _) =>
        {
            closed = true;
            if (menu is not null) menu.IsOpen = false;
            if (hub is null) return;
            hub.Service.Icons.CollectionChanged -= RefreshIcons; hub.Service.Hints.CollectionChanged -= RefreshHints;
            hub.Service.PropertyChanged -= RefreshStatus; hub.Service.LayoutChanged -= QueueRender;
            hub.Service.FocusRequested -= Interaction.RequestTrayFocus;
            hub.PresentationChanged -= QueueRender;
        };
        Render();
    }

    void RefreshStatus(object? sender, PropertyChangedEventArgs e)
    {
        if (closed) return;
        if (dragging || Interaction.IsBusy) { renderPending = true; return; }
        RenderStatus();
    }
    void RefreshIcons(object? sender, NotifyCollectionChangedEventArgs e) => QueueRender();
    void RefreshHints(object? sender, NotifyCollectionChangedEventArgs e) { if (showingHints) QueueRender(); }
    void QueueRender()
    {
        if (closed || renderQueued) return;
        renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { renderQueued = false; if (!closed && IsLoaded) Render(); });
    }
    void Render()
    {
        if (closed) return;
        if (dragging || Interaction.IsBusy) { renderPending = true; return; }
        renderPending = false;
        var previousButtons = icons.Children.OfType<SystemTrayIconButton>().ToList();
        var focusedButton = Keyboard.FocusedElement as SystemTrayIconButton;
        var focusedIndex = focusedButton is null ? -1 : previousButtons.IndexOf(focusedButton);
        var focusedLabel = Keyboard.FocusedElement is Button control && Window.GetWindow(control) == this
            ? AutomationProperties.GetName(control) : null;
        var scroll = iconScroll.VerticalOffset;
        var hintOffset = hintScroll.VerticalOffset;
        var options = hub?.Options;
        var tile = (options?.TrayShowNames == true ? Math.Max(78, options.TrayIconSize + 44) : Math.Max(36, (options?.TrayIconSize ?? 22) + 16)) + 4;
        var columns = options?.TrayColumns ?? 5;
        body.Width = showingHints ? Math.Max(280, tile * columns) : Math.Max(190, tile * columns);
        heading.Children.Clear(); heading.Visibility = arranging || showingHints ? Visibility.Visible : Visibility.Collapsed;
        if (arranging || showingHints)
        {
            var done = SmallButton(showingHints ? "\uE72B" : "\uE73E", Loc.T(showingHints ? "trayBack" : "trayDone"), () =>
            { arranging = false; showingHints = false; Render(); });
            DockPanel.SetDock(done, Dock.Right); heading.Children.Add(done);
            var caption = Text(Loc.T(showingHints ? "trayRecentHints" : "trayArrange"), 12); heading.Children.Add(caption);
        }
        iconScroll.Visibility = showingHints ? Visibility.Collapsed : Visibility.Visible;
        hintScroll.Visibility = showingHints ? Visibility.Visible : Visibility.Collapsed;
        icons.Children.Clear();
        if (hub is not null)
        foreach (var icon in hub.OrderedIcons())
        {
            var button = new SystemTrayIconButton(hub.Service, icon, Dark, options!.TrayShowNames,
                options.TrayIconSize, arranging, hub.IsPinned(icon));
            button.PinRequested += entry => { if (entry.StableId.Length > 0) hub.Options.SetTrayPinned(entry.StableId, !hub.IsPinned(entry)); };
            button.MoveRequested += Move;
            button.DraggingChanged += active => { dragging = active; if (!active) QueueRender(); };
            if (button.ContextMenu is { } editMenu) editMenu.Closed += (_, _) => MenuClosed(button);
            icons.Children.Add(button);
        }
        footer.Children.Clear();
        if (hub is not null && !hub.Options.SystemTrayEnabled)
        {
            var enable = new Button { Content = Loc.T("systemTrayEnable"), Padding = new Thickness(10, 6, 10, 6),
                ToolTip = Loc.T("systemTrayConsent"), Foreground = Ink, Background = Hover, BorderThickness = new Thickness(0) };
            enable.Click += (_, _) => { hub.Options.SystemTrayEnabled = true; SafeClose(); };
            body.Width = Math.Max(body.Width, 270); footer.Children.Add(enable);
        }
        else
        {
            if (showingHints && hub?.Service.Hints.Count > 0)
                footer.Children.Add(SmallButton("\uE74D", Loc.T("systemTrayClearHints"), () => hub?.Service.ClearHints()));
            footer.Children.Add(SmallButton("\uE7F4", Loc.T("systemTrayWindowsNotifications"), () => { SafeClose(); ShellGuard.Notifications(); }));
            var more = SmallButton("\uE712", Loc.T("trayMore"), () => { });
            more.Click += (_, _) => OpenMenu(more);
            footer.Children.Add(more);
        }
        RenderStatus(); if (showingHints) RenderHints();
        if (IsActive && focusedIndex >= 0)
        {
            var buttons = icons.Children.OfType<SystemTrayIconButton>().Where(button => button.IsEnabled).ToList();
            var replacement = buttons.FirstOrDefault(button => button.Item == focusedButton!.Item)
                ?? buttons.ElementAtOrDefault(Math.Min(focusedIndex, buttons.Count - 1));
            if (replacement is not null) replacement.Focus();
            else footer.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
        }
        else if (IsActive && !string.IsNullOrEmpty(focusedLabel))
            heading.Children.OfType<Button>().Concat(footer.Children.OfType<Button>())
                .FirstOrDefault(button => button.IsEnabled && AutomationProperties.GetName(button) == focusedLabel)?.Focus();
        iconScroll.ScrollToVerticalOffset(scroll);
        hintScroll.ScrollToVerticalOffset(hintOffset);
    }
    void RenderStatus()
    {
        if (showingHints) { status.Visibility = Visibility.Collapsed; return; }
        var service = hub?.Service;
        var content = service?.Status ?? Loc.T("systemTrayOff");
        if (service?.Ready == true && !service.HasStatusNotice)
            content = service.Icons.Any(icon => !icon.Hidden) ? "" : Loc.T("systemTrayWaiting");
        status.Text = content;
        status.Visibility = string.IsNullOrEmpty(content) ? Visibility.Collapsed : Visibility.Visible;
    }
    void RenderHints()
    {
        hints.Children.Clear();
        if (hub is null) return;
        if (hub.Service.Hints.Count == 0) { hints.Children.Add(Text(Loc.T("trayNoHints"), 12, true)); return; }
        foreach (var hint in hub.Service.Hints.Take(50))
        {
            var row = new StackPanel { Margin = new Thickness(3, 7, 3, 12) };
            row.Children.Add(Text(hint.Icon.AccessibleName + "  ·  " + hint.Received.ToString("HH:mm"), 10.5, true));
            if (hint.Title.Length > 0) { var title = Text(hint.Title, 12); title.Margin = new Thickness(0, 5, 0, 3); row.Children.Add(title); }
            var message = Text(hint.Text, 11.5, true); message.TextWrapping = TextWrapping.Wrap; message.TextTrimming = TextTrimming.None;
            row.Children.Add(message); hints.Children.Add(row);
        }
    }
    Button SmallButton(string glyph, string label, Action action)
    {
        var button = new Button { Width = 30, Height = 28, Margin = new Thickness(2, 0, 0, 0), ToolTip = label,
            Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ink,
            Content = Glyph(glyph, 14), Style = null };
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Plate";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content); template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "Plate")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "Plate")); template.Triggers.Add(focus);
        button.Template = template;
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); return button;
    }
    void OpenMenu(Button target)
    {
        if (hub is null) return;
        menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        if (!Dark)
        {
            menu.Resources["MenuInk"] = Ink; menu.Resources["MenuSurface"] = Paint("#FAF5F6F8");
            menu.Resources["MenuLine"] = Track; menu.Resources["MenuHover"] = Hover;
        }
        Add(menu, Loc.T("trayArrange"), () => { arranging = !arranging; showingHints = false; Render(); }, arranging);
        Add(menu, Loc.T("trayShowNames"), () => hub.Options.TrayShowNames = !hub.Options.TrayShowNames, hub.Options.TrayShowNames);
        Add(menu, Loc.T(hub.Options.TrayPinned ? "systemTrayUnpin" : "systemTrayPin"), () => hub.Options.TrayPinned = !hub.Options.TrayPinned);
        menu.Items.Add(new Separator());
        Add(menu, Loc.T("trayRecentHints"), () => { showingHints = true; arranging = false; Render(); });
        Add(menu, Loc.T("systemTrayRefresh"), () => { if (hub.Service.Ready) hub.Service.RequestRefresh(); else hub.Retry(); });
        menu.Closed += (_, _) => MenuClosed(target);
        PopupInteraction.Open(menu, target);
    }
    void MenuClosed(FrameworkElement target) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!IsVisible) return;
        if (!IsActive || Interaction.IsBusy) return;
        if (target.IsLoaded) target.Focus(); else icons.Children.OfType<SystemTrayIconButton>().FirstOrDefault()?.Focus();
    });
    static void Add(ContextMenu menu, string title, Action action, bool? value = null)
    {
        var item = new MenuItem { Header = title, IsCheckable = value.HasValue, IsChecked = value == true };
        item.Click += (_, _) => action(); menu.Items.Add(item);
    }
    void Move(SystemTrayItem item, int direction)
    {
        if (hub is null) return;
        var list = hub.OrderedIcons().ToList(); var index = list.IndexOf(item);
        var next = index + direction;
        if (index < 0 || next < 0 || next >= list.Count) return;
        var before = direction < 0 ? list[next].StableId : next + 1 < list.Count ? list[next + 1].StableId : null;
        hub.Move(item, before);
    }
    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = arranging && e.Data.GetDataPresent(SystemTrayIconButton.DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
    }
    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!arranging || hub is null || e.Data.GetData(SystemTrayIconButton.DragFormat) is not string id) return;
        var item = hub.Service.Icons.FirstOrDefault(icon => icon.StableId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        var point = e.GetPosition(icons);
        var target = icons.Children.OfType<SystemTrayIconButton>().FirstOrDefault(button =>
        {
            var bounds = button.TransformToAncestor(icons).TransformBounds(new Rect(button.RenderSize));
            return bounds.Contains(point);
        });
        hub.Move(item, target?.Item.StableId);
    }
    void Navigate(object sender, KeyEventArgs e)
    {
        if (PopupInteraction.HasChildPopup(this)) return;
        if (e.Key == Key.Escape && (arranging || showingHints)) { e.Handled = true; arranging = false; showingHints = false; Render(); return; }
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not SystemTrayIconButton current) return;
        var buttons = icons.Children.OfType<SystemTrayIconButton>().Where(button => button.IsEnabled).ToList();
        var index = buttons.IndexOf(current); if (index < 0) return;
        var columns = hub?.Options.TrayColumns ?? 5;
        var next = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - columns,
            Key.Down => index + columns, Key.Home => 0, Key.End => buttons.Count - 1, _ => -1 };
        if (next >= 0 && next < buttons.Count) { e.Handled = true; buttons[next].Focus(); }
    }
}
