using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace WinDock.Services;

/// <summary>Native desktop acrylic on a non-layered WPF window, with an opaque accessible fallback.</summary>
public sealed class GlassBackdrop : IDisposable
{
    readonly Window window;
    readonly Border surface;
    readonly HwndSource source;
    readonly bool dark;
    bool disposed;
    public bool IsAcrylic { get; private set; }
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    public GlassBackdrop(Window window, Border surface, bool dark = true)
    {
        this.window = window; this.surface = surface; this.dark = dark;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        source.AddHook(Hook); Apply();
    }
    void Apply()
    {
        if (disposed) return;
        var hwnd = source.Handle;
        var darkMode = dark ? 1 : 0; Native.DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
        var round = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        var backdrop = !SystemParameters.HighContrast && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ? 3 : 1;
        IsAcrylic = Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int)) == 0 && backdrop == 3;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (IsAcrylic) IsAcrylic = DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        var fallback = dark ? Color.FromRgb(28, 32, 40) : Color.FromRgb(245, 246, 248);
        if (source.CompositionTarget is { } target) target.BackgroundColor = IsAcrylic ? Colors.Transparent : fallback;
        window.Background = Brushes.Transparent;
        surface.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush :
            new SolidColorBrush(IsAcrylic ? (dark ? Color.FromArgb(38, 18, 24, 34) : Color.FromArgb(72, 250, 251, 253)) : fallback);
    }
    nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is 0x031E or 0x001A or 0x031A) window.Dispatcher.BeginInvoke(Apply);
        return 0;
    }
    public void Dispose() { disposed = true; source.RemoveHook(Hook); }
}
