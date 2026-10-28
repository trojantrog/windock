using System.Windows.Threading;

namespace WinDock.Services;

// A running process that registered a notification icon remains an application
// when its main window is hidden. These entries are not synthetic tray icons.
public sealed class BackgroundApps
{
    public static BackgroundApps Shared { get; } = new();
    readonly Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    int consumers, generation;
    bool refreshing;
    public IReadOnlyList<TrayApp> Apps { get; private set; } = [];
    public event Action? Changed;
    BackgroundApps() => timer.Tick += (_, _) => Refresh();
    public void Acquire() { if (++consumers != 1) return; generation++; timer.Start(); Refresh(); }
    public void Release() { if (--consumers > 0) return; consumers = 0; generation++; timer.Stop(); Apps = []; }
    public async void Refresh()
    {
        if (consumers == 0 || refreshing) return;
        refreshing = true; var stamp = generation;
        try
        {
            var apps = await Task.Run(() => TrayApps.Running().Take(512).ToArray());
            if (consumers == 0 || stamp != generation || dispatcher.HasShutdownStarted) return;
            static string Identity(TrayApp app) => app.Exe + "|" + app.AppId + "|" + string.Join(',', app.Pids.Order());
            if (Apps.Select(Identity).SequenceEqual(apps.Select(Identity), StringComparer.OrdinalIgnoreCase)) return;
            Apps = apps; Changed?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or System.Security.SecurityException or InvalidOperationException)
        {
            if (consumers > 0 && stamp == generation && Apps.Count > 0) { Apps = []; Changed?.Invoke(); }
        }
        finally { refreshing = false; }
    }
}
