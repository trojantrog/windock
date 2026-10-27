using System.Windows;
using System.Windows.Controls;
using WinDock.Models;

namespace WinDock.Views;

public sealed class ToolDialog : DesktopToolWindow
{
    public StackPanel Body { get; } = new();
    public TextBlock Message => Status;
    public ToolDialog(Settings settings, string title) : base(settings, title, 580, 290)
    {
        MinHeight = 240; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Layout.Children.Add(Body); Body.Children.Add(Status);
    }
}
