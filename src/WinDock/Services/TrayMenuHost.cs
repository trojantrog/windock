using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinDock.Services;

// A notification icon has no visible WPF owner. Keep one activatable window
// available only for its menu; never activate the menu's own popup HWND.
sealed class TrayMenuHost : Window
{
    public TrayMenuHost()
    {
        Width = Height = 1;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = 0;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = Top = 0;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (nint)(style | Native.WS_EX_TOOLWINDOW));
        };
    }

    public bool Prepare()
    {
        Show();
        if (Native.GetCursorPos(out var point))
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, 0, point.X, point.Y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4 /* SWP_NOZORDER */);
        if (IsActive || Activate()) return true;
        Hide();
        return false;
    }
}
