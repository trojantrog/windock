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
    readonly CancellationTokenSource inputLifetime = new();
    readonly DispatcherTimer notificationAccessCheck = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly bool notificationIdentity = SystemNotificationReader.HasPackageIdentity();
    ContextMenu? menu;
    SystemTrayItem? revealIcon;
    SystemNotificationReader? notificationReader;
    CancellationTokenSource? notificationLifetime;
    IReadOnlyList<SystemNotificationItem> systemNotifications = [];
    bool arranging, dragging, showingHints, showingSystemNotifications, renderQueued, renderPending, closed, inputOpening;
    bool notificationAuthorizing, notificationRefreshing, notificationRefreshAgain;
    int notificationGeneration;
    string? inputStatus, notificationStatus;
    SystemNotificationAccess notificationAccess;
    protected override bool KeepOpenOnDeactivate => dragging || notificationAuthorizing;

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
        notificationAccessCheck.Tick += (_, _) => CheckNotificationAccess();
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
            StopSystemNotifications();
            inputLifetime.Cancel(); inputLifetime.Dispose();
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
        var focusedHint = (Keyboard.FocusedElement as Button)?.Tag as TrayHint;
        var focusedIndex = focusedButton is null ? -1 : previousButtons.IndexOf(focusedButton);
        var focusedLabel = Keyboard.FocusedElement is Button control && Window.GetWindow(control) == this
            ? AutomationProperties.GetName(control) : null;
        var scroll = iconScroll.VerticalOffset;
        var hintOffset = hintScroll.VerticalOffset;
        var options = hub?.Options;
        var tile = (options?.TrayShowNames == true ? Math.Max(78, options.TrayIconSize + 44) : Math.Max(36, (options?.TrayIconSize ?? 22) + 16)) + 4;
        var columns = options?.TrayColumns ?? 5;
        var showingList = showingHints || showingSystemNotifications;
        body.Width = showingList ? Math.Max(280, tile * columns) : Math.Max(190, tile * columns);
        heading.Children.Clear(); heading.Visibility = arranging || showingList ? Visibility.Visible : Visibility.Collapsed;
        if (arranging || showingList)
        {
            var done = SmallButton(showingList ? "\uE72B" : "\uE73E", Loc.T(showingList ? "trayBack" : "trayDone"), () =>
            { StopSystemNotifications(); arranging = false; showingHints = false; Render(); });
            DockPanel.SetDock(done, Dock.Right); heading.Children.Add(done);
            var caption = Text(Loc.T(showingSystemNotifications ? "systemNotificationsTitle" : showingHints ? "trayRecentHints" : "trayArrange"), 12);
            heading.Children.Add(caption);
        }
        iconScroll.Visibility = showingList ? Visibility.Collapsed : Visibility.Visible;
        hintScroll.Visibility = showingList ? Visibility.Visible : Visibility.Collapsed;
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
        var input = SmallButton("\uE765", Loc.T("systemTrayInput"), () => { });
        input.IsEnabled = !inputOpening;
        input.Click += async (_, _) =>
        {
            if (inputOpening) return;
            inputOpening = true; input.IsEnabled = false; inputStatus = null; RenderStatus();
            try
            {
                var outcome = await SystemInputIndicator.OpenAsync(inputLifetime.Token);
                if (closed) return;
                if (outcome == SystemInputRequest.Requested) { SafeClose(); return; }
                var message = outcome switch
                {
                    SystemInputRequest.Busy => "systemTrayInputBusy",
                    SystemInputRequest.TimedOut => "systemTrayInputTimeout",
                    SystemInputRequest.Pending => "systemTrayInputPending",
                    _ => "systemTrayInputUnavailable"
                };
                inputStatus = Loc.T(message);
                RenderStatus();
            }
            catch (Exception)
            {
                if (!closed) { inputStatus = Loc.T("systemTrayInputUnavailable"); RenderStatus(); }
            }
            finally { inputOpening = false; if (!closed) QueueRender(); }
        };
        footer.Children.Add(input);
        if (notificationIdentity)
        {
            var active = showingSystemNotifications && notificationReader?.Enabled == true;
            footer.Children.Add(SmallButton(active ? "\uE72C" : "\uE8FD",
                Loc.T(active ? "systemTrayRefresh" : "systemNotificationsRead"),
                () => { if (active) RequestNotificationRefresh(); else _ = ShowSystemNotificationsAsync(); }));
            if (showingSystemNotifications && notificationAccess == SystemNotificationAccess.Denied)
                footer.Children.Add(SmallButton("\uE713", Loc.T("systemNotificationsPrivacy"),
                    () => Launcher.OpenFile("ms-settings:privacy-notifications")));
        }
        if (hub is not null && !hub.Options.SystemTrayEnabled)
        {
            var enable = new Button { Content = Loc.T("systemTrayEnable"), Padding = new Thickness(10, 6, 10, 6),
                ToolTip = Loc.T("systemTrayConsent"), Foreground = Ink, Background = Hover, BorderThickness = new Thickness(0) };
            enable.Click += (_, _) => { hub.Options.SystemTrayEnabled = true; SafeClose(); };
            body.Width = Math.Max(body.Width, 270); footer.Children.Add(enable);
            if (notificationIdentity)
                footer.Children.Add(SmallButton("\uE7F4", Loc.T("systemTrayWindowsNotifications"), () => { SafeClose(); ShellGuard.Notifications(); }));
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
        RenderStatus();
        if (showingHints) RenderHints();
        else if (showingSystemNotifications) RenderSystemNotifications();
        if (IsActive && showingHints && focusedHint is not null)
        {
            var replacement = hints.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<DockPanel>())
                .SelectMany(header => header.Children.OfType<Button>()).FirstOrDefault(button => ReferenceEquals(button.Tag, focusedHint));
            if (replacement is not null) replacement.Focus();
            else footer.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
        }
        else if (IsActive && focusedIndex >= 0)
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
        if (!showingList && revealIcon is { } requested)
        {
            revealIcon = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (closed || !IsVisible || showingHints || showingSystemNotifications || CurrentIcon(requested) is not { } current) return;
                var button = icons.Children.OfType<SystemTrayIconButton>().FirstOrDefault(entry => ReferenceEquals(entry.Item, current));
                if (button is null) return;
                button.BringIntoView();
                if (IsActive) button.Focus();
            });
        }
    }
    void RenderStatus()
    {
        if (inputStatus is { Length: > 0 }) { status.Text = inputStatus; status.Visibility = Visibility.Visible; return; }
        if (showingSystemNotifications)
        {
            status.Text = notificationStatus ?? "";
            status.Visibility = string.IsNullOrEmpty(notificationStatus) ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
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
            var header = new DockPanel();
            if (CurrentIcon(hint.Icon) is not null)
            {
                var locate = SmallButton("\uE721", Loc.T("trayLocateIcon"), () => ShowHintIcon(hint.Icon));
                locate.Tag = hint;
                DockPanel.SetDock(locate, Dock.Right); header.Children.Add(locate);
            }
            header.Children.Add(Text(hint.Icon.AccessibleName + "  ·  " + hint.Received.ToString("HH:mm"), 10.5, true));
            row.Children.Add(header);
            if (hint.Title.Length > 0) { var title = Text(hint.Title, 12); title.Margin = new Thickness(0, 5, 0, 3); row.Children.Add(title); }
            var message = Text(hint.Text, 11.5, true); message.TextWrapping = TextWrapping.Wrap; message.TextTrimming = TextTrimming.None;
            row.Children.Add(message); hints.Children.Add(row);
        }
    }

    async Task ShowSystemNotificationsAsync()
    {
        if (!notificationIdentity || notificationAuthorizing || closed) return;
        StopSystemNotifications();
        var current = notificationGeneration;
        var source = new SystemNotificationReader(Dispatcher);
        var lifetime = new CancellationTokenSource();
        notificationReader = source; notificationLifetime = lifetime;
        source.Changed += SystemNotificationsChanged;
        showingSystemNotifications = true; showingHints = false; arranging = false; inputStatus = null;
        notificationStatus = Loc.T("systemNotificationsLoading");
        notificationAuthorizing = true;
        QueueRender();
        try
        {
            var result = await source.EnableAsync(lifetime.Token);
            if (closed || current != notificationGeneration || !ReferenceEquals(source, notificationReader)) return;
            notificationAccess = result;
            if (result == SystemNotificationAccess.Enabled)
            {
                notificationStatus = Loc.T("systemNotificationsLoading");
                notificationAccessCheck.Start();
                RequestNotificationRefresh();
            }
            else notificationStatus = NotificationMessage(result);
        }
        catch (Exception)
        {
            if (!closed && current == notificationGeneration)
            {
                notificationAccess = SystemNotificationAccess.Unavailable;
                notificationStatus = Loc.T("systemNotificationsUnavailable");
            }
        }
        finally
        {
            if (current == notificationGeneration)
            {
                notificationAuthorizing = false;
                if (!closed) QueueRender();
            }
        }
    }

    void SystemNotificationsChanged() => RequestNotificationRefresh();

    void CheckNotificationAccess()
    {
        if (closed || !showingSystemNotifications || notificationReader is not { } source) return;
        var access = source.CheckAccess();
        if (access == SystemNotificationAccess.Enabled) return;
        notificationAccessCheck.Stop();
        systemNotifications = []; hints.Children.Clear();
        notificationAccess = access; notificationStatus = NotificationMessage(access);
        RenderStatus(); QueueRender();
    }

    void RequestNotificationRefresh()
    {
        if (closed || !showingSystemNotifications || notificationReader is not { Enabled: true } source || notificationLifetime is null) return;
        if (notificationRefreshing) { notificationRefreshAgain = true; return; }
        _ = RefreshSystemNotificationsAsync(source, notificationGeneration, notificationLifetime.Token);
    }

    async Task RefreshSystemNotificationsAsync(SystemNotificationReader source, int current, CancellationToken token)
    {
        notificationRefreshing = true;
        try
        {
            do
            {
                notificationRefreshAgain = false;
                var result = await source.ReadAsync(token);
                if (closed || current != notificationGeneration || !ReferenceEquals(source, notificationReader) || !showingSystemNotifications) return;
                if (result.Access == SystemNotificationAccess.Cancelled) return;
                notificationAccess = result.Access;
                if (result.Access == SystemNotificationAccess.Enabled)
                {
                    systemNotifications = result.Items;
                    notificationStatus = source.EventsAvailable ? null : Loc.T("systemNotificationsLiveUnavailable");
                }
                else
                {
                    notificationAccessCheck.Stop();
                    systemNotifications = [];
                    notificationStatus = NotificationMessage(result.Access);
                    notificationRefreshAgain = false;
                }
                QueueRender();
            } while (notificationRefreshAgain);
        }
        catch (Exception)
        {
            if (!closed && current == notificationGeneration)
            {
                notificationAccessCheck.Stop();
                systemNotifications = [];
                notificationAccess = SystemNotificationAccess.Unavailable;
                notificationStatus = Loc.T("systemNotificationsUnavailable");
            }
        }
        finally
        {
            if (current == notificationGeneration)
            {
                notificationRefreshing = false;
                if (!closed) QueueRender();
            }
        }
    }

    static string NotificationMessage(SystemNotificationAccess access) => Loc.T(access switch
    {
        SystemNotificationAccess.Denied => "systemNotificationsDenied",
        SystemNotificationAccess.Unspecified => "systemNotificationsUnspecified",
        _ => "systemNotificationsUnavailable"
    });

    void StopSystemNotifications()
    {
        ++notificationGeneration;
        notificationAccessCheck.Stop();
        if (showingSystemNotifications) hints.Children.Clear();
        notificationLifetime?.Cancel(); notificationLifetime?.Dispose(); notificationLifetime = null;
        if (notificationReader is { } source)
        {
            source.Changed -= SystemNotificationsChanged;
            source.Dispose();
        }
        notificationReader = null; systemNotifications = [];
        notificationAuthorizing = notificationRefreshing = notificationRefreshAgain = false;
        notificationStatus = null; notificationAccess = SystemNotificationAccess.Disabled;
        showingSystemNotifications = false;
    }

    void RenderSystemNotifications()
    {
        hints.Children.Clear();
        if (systemNotifications.Count == 0)
        {
            if (notificationAccess == SystemNotificationAccess.Enabled && !notificationRefreshing)
                hints.Children.Add(Text(Loc.T("systemNotificationsNone"), 12, true));
            return;
        }
        foreach (var notification in systemNotifications)
        {
            var row = new StackPanel { Margin = new Thickness(3, 7, 3, 12) };
            var name = !string.IsNullOrWhiteSpace(notification.AppName) ? notification.AppName
                : !string.IsNullOrWhiteSpace(notification.AppId) ? notification.AppId : Loc.T("systemNotificationsUnknown");
            row.Children.Add(Text(name + "  ·  " + notification.Created.ToLocalTime().ToString("MM-dd HH:mm"), 10.5, true));
            foreach (var value in notification.Text.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var message = Text(value, 11.5, true); message.TextWrapping = TextWrapping.Wrap;
                message.TextTrimming = TextTrimming.None; message.Margin = new Thickness(0, 4, 0, 0);
                row.Children.Add(message);
            }
            hints.Children.Add(row);
        }
    }

    SystemTrayItem? CurrentIcon(SystemTrayItem item)
    {
        if (hub?.Service.Ready != true) return null;
        if (!item.Hidden && hub.Service.Icons.Contains(item)) return item;
        if (item.StableId.Length == 0 || item.ProcessStarted == 0) return null;
        var matches = hub.Service.Icons.Where(icon => !icon.Hidden &&
            icon.StableId.Equals(item.StableId, StringComparison.OrdinalIgnoreCase) && icon.ProcessId == item.ProcessId &&
            icon.Window == item.Window && icon.ProcessStarted == item.ProcessStarted).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    void ShowHintIcon(SystemTrayItem item)
    {
        if (CurrentIcon(item) is null) return;
        revealIcon = item; showingHints = false; arranging = false; Render();
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
        Add(menu, Loc.T("trayArrange"), () => { StopSystemNotifications(); arranging = !arranging; showingHints = false; Render(); }, arranging);
        Add(menu, Loc.T("trayShowNames"), () => hub.Options.TrayShowNames = !hub.Options.TrayShowNames, hub.Options.TrayShowNames);
        Add(menu, Loc.T(hub.Options.TrayPinned ? "systemTrayUnpin" : "systemTrayPin"), () => hub.Options.TrayPinned = !hub.Options.TrayPinned);
        menu.Items.Add(new Separator());
        Add(menu, Loc.T("trayRecentHints"), () => { StopSystemNotifications(); showingHints = true; arranging = false; Render(); });
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
        if (e.Key == Key.Escape && (arranging || showingHints || showingSystemNotifications))
        { e.Handled = true; StopSystemNotifications(); arranging = false; showingHints = false; Render(); return; }
        if (Keyboard.Modifiers != ModifierKeys.None || Keyboard.FocusedElement is not SystemTrayIconButton current) return;
        var buttons = icons.Children.OfType<SystemTrayIconButton>().Where(button => button.IsEnabled).ToList();
        var index = buttons.IndexOf(current); if (index < 0) return;
        var columns = hub?.Options.TrayColumns ?? 5;
        var next = e.Key switch { Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - columns,
            Key.Down => index + columns, Key.Home => 0, Key.End => buttons.Count - 1, _ => -1 };
        if (next >= 0 && next < buttons.Count) { e.Handled = true; buttons[next].Focus(); }
    }
}
