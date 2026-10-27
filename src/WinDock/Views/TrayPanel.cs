using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinDock.Services;

namespace WinDock.Views;

public sealed class TrayPanel : Flyout
{
    readonly SystemTrayHub? hub = SystemTrayHub.Current;
    readonly StackPanel body = new() { Width = 342 };
    readonly WrapPanel icons = new();
    readonly StackPanel hints = new();
    readonly TextBlock status;
    public TrayPanel(bool dark) : base(dark)
    {
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        SetBody(body);
        body.Children.Add(Text(Loc.T("systemTrayTitle"), 14, font: FontFamily));
        status = Text(hub?.Service.Status ?? Loc.T("systemTrayOff"), 11, muted: true);
        status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 8, 0, 12); body.Children.Add(status);
        if (hub is null) return;
        if (!hub.Options.SystemTrayEnabled)
        {
            var explanation = Text(Loc.T("systemTrayConsent"), 11, muted: true); explanation.TextWrapping = TextWrapping.Wrap;
            explanation.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(explanation);
            body.Children.Add(Action(Loc.T("systemTrayEnable"), () => { hub.Options.SystemTrayEnabled = true; SafeClose(); }));
        }
        else
        {
            body.Children.Add(new ScrollViewer { Content = icons, MaxHeight = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var controls = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
            controls.Children.Add(Action(Loc.T("systemTrayRefresh"), () => { if (hub.Service.Ready) hub.Service.RequestRefresh(); else hub.Retry(); }));
            var pin = Action(Loc.T(hub.Options.TrayPinned ? "systemTrayUnpin" : "systemTrayPin"), () => { hub.Options.TrayPinned = !hub.Options.TrayPinned; SafeClose(); });
            pin.Margin = new Thickness(6, 0, 0, 0); controls.Children.Add(pin); body.Children.Add(controls);
            var detail = Text(Loc.T("systemTrayHintsDescription"), 10.5, muted: true); detail.TextWrapping = TextWrapping.Wrap;
            detail.Margin = new Thickness(0, 6, 0, 9); body.Children.Add(detail);
            body.Children.Add(new ScrollViewer { Content = hints, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            body.Children.Add(Action(Loc.T("systemTrayClearHints"), hub.Service.ClearHints));
            hub.Service.Icons.CollectionChanged += RefreshIcons; hub.Service.Hints.CollectionChanged += RefreshHints;
            hub.Service.PropertyChanged += RefreshStatus; RenderIcons(); RenderHints();
        }
        var notifications = Action(Loc.T("systemTrayWindowsNotifications"), () => { SafeClose(); ShellGuard.Notifications(); });
        notifications.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(notifications);
        Closed += (_, _) =>
        {
            hub.Service.Icons.CollectionChanged -= RefreshIcons; hub.Service.Hints.CollectionChanged -= RefreshHints;
            hub.Service.PropertyChanged -= RefreshStatus;
        };
    }
    void RefreshStatus(object? sender, PropertyChangedEventArgs e) => status.Text = hub!.Service.Status;
    void RefreshIcons(object? sender, NotifyCollectionChangedEventArgs e) => RenderIcons();
    void RefreshHints(object? sender, NotifyCollectionChangedEventArgs e) => RenderHints();
    void RenderIcons()
    {
        icons.Children.Clear();
        foreach (var icon in hub!.Service.Icons)
        {
            var button = new SystemTrayIconButton(hub.Service, icon, Dark, showName: true);
            button.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(SystemTrayItem.Hidden)) { Converter = new HiddenTrayIconConverter() });
            icons.Children.Add(button);
        }
        if (icons.Children.Count == 0)
        {
            var empty = Text(Loc.T("systemTrayWaiting"), 11, true); empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, 8, 0, 8); icons.Children.Add(empty);
        }
    }
    void RenderHints()
    {
        hints.Children.Clear();
        foreach (var hint in hub!.Service.Hints.Take(8))
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 9) };
            row.Children.Add(Text($"{hint.Received:HH:mm}  {hint.Icon.AccessibleName}", 10, true));
            var title = Text(hint.Title, 11.5); title.Margin = new Thickness(0, 4, 0, 2); row.Children.Add(title);
            var message = Text(hint.Text, 11, true); message.TextWrapping = TextWrapping.Wrap; row.Children.Add(message);
            hints.Children.Add(row);
        }
    }
}

internal sealed class HiddenTrayIconConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
