using WinDock.ViewModels;
using WinDock.Views;

namespace WinDock.Services;

public sealed class SystemTrayHub : IDisposable
{
    public static SystemTrayHub? Current { get; private set; }
    public SystemTrayService Service { get; } = new();
    public DockOptions Options { get; }
    SystemTrayWindow? strip;
    public SystemTrayHub(DockOptions options)
    {
        Options = options; Current = this;
        options.Changed += Changed; Apply();
    }
    void Changed(string name)
    {
        if (name is nameof(DockOptions.SystemTrayEnabled) or nameof(DockOptions.TrayPinned) or nameof(DockOptions.Enabled)) Apply();
    }
    void Apply()
    {
        if (Options.SystemTrayEnabled && Options.Enabled) Service.Start(); else Service.Stop();
        if (Options.SystemTrayEnabled && Options.TrayPinned && Options.Enabled)
        {
            strip ??= new SystemTrayWindow(this);
            if (!strip.IsVisible) strip.Show();
        }
        else { strip?.Close(); strip = null; }
    }
    public void Retry() { Service.Stop(); if (Options.SystemTrayEnabled && Options.Enabled) Service.Start(); }
    public void Dispose()
    {
        Options.Changed -= Changed; strip?.Close(); strip = null; Service.Dispose();
        if (Current == this) Current = null;
    }
}
