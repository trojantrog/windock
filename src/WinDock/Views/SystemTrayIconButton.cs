using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WinDock.Services;

namespace WinDock.Views;

public sealed class SystemTrayIconButton : Border
{
    readonly SystemTrayService service;
    readonly SystemTrayItem item;
    long moveAt;
    public SystemTrayIconButton(SystemTrayService service, SystemTrayItem item, bool dark, bool showName = false)
    {
        this.service = service; this.item = item; DataContext = item;
        CornerRadius = new CornerRadius(8); Background = Brushes.Transparent;
        Width = showName ? 78 : 34; Height = showName ? 72 : 34; Margin = new Thickness(2);
        UseLayoutRounding = true; SnapsToDevicePixels = true; Focusable = true; Cursor = Cursors.Hand;
        SetBinding(ToolTipProperty, new Binding(nameof(SystemTrayItem.HoverTip)));
        SetBinding(AutomationProperties.NameProperty, new Binding(nameof(SystemTrayItem.AccessibleName)));
        SetBinding(IsEnabledProperty, new Binding(nameof(SystemTrayItem.CanInvoke)));
        ToolTipService.SetShowOnDisabled(this, true);
        var grid = new Grid();
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var image = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.SetBinding(Image.SourceProperty, new Binding(nameof(SystemTrayItem.Icon)));
        stack.Children.Add(image);
        if (showName)
        {
            var name = new TextBlock { FontSize = 10.5, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = dark ? Brushes.WhiteSmoke : Brushes.Black, Margin = new Thickness(3, 7, 3, 0) };
            name.SetBinding(TextBlock.TextProperty, new Binding(nameof(SystemTrayItem.AccessibleName))); stack.Children.Add(name);
        }
        grid.Children.Add(stack);
        var hint = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(125, 186, 255)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) };
        hint.SetBinding(VisibilityProperty, new Binding(nameof(SystemTrayItem.HasHint)) { Converter = new BooleanToVisibilityConverter() });
        grid.Children.Add(hint); Child = grid;
        var hover = new SolidColorBrush(dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)); hover.Freeze();
        MouseEnter += (_, _) => { Background = hover; Send(0x200); if (item.Version >= 4) Send(0x406); };
        MouseLeave += (_, _) => { Background = Brushes.Transparent; if (item.Version >= 4) Send(0x407); };
        MouseMove += (_, _) => { var now = Environment.TickCount64; if (now - moveAt > 50) { moveAt = now; Send(0x200); } };
        MouseDown += (_, e) =>
        {
            e.Handled = true; Focus();
            uint message = e.ChangedButton switch { MouseButton.Left => e.ClickCount == 2 ? 0x203u : 0x201u,
                MouseButton.Right => e.ClickCount == 2 ? 0x206u : 0x204u,
                MouseButton.Middle => e.ClickCount == 2 ? 0x209u : 0x207u, _ => 0 };
            if (message != 0) Send(message);
        };
        MouseUp += (_, e) =>
        {
            e.Handled = true;
            switch (e.ChangedButton)
            {
                case MouseButton.Left: Send(0x202); if (item.Version >= 3) Send(0x400); service.MarkSeen(item); break;
                case MouseButton.Right: Send(0x205); if (item.Version >= 3) Send(0x7B); break;
                case MouseButton.Middle: Send(0x208); break;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                if (item.Version >= 3) Send(0x401, keyboard: true); else { Send(0x201, true); Send(0x202, true); }
                service.MarkSeen(item);
            }
            else if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                e.Handled = true;
                if (item.Version >= 3) Send(0x7B, true); else { Send(0x204, true); Send(0x205, true); }
            }
        };
    }
    void Send(uint message, bool keyboard = false)
    {
        Point point;
        if (keyboard) point = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        else { Native.GetCursorPos(out var cursor); point = new Point(cursor.X, cursor.Y); }
        service.Invoke(item, message, point);
    }
}
