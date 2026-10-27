using WinDock.Services;

namespace WinDock;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--preview-search"] or ["--preview-organizer"])
        {
            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            var settings = Models.Settings.Load();
            Loc.Instance.Set(settings.Language);
            var preview = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnLastWindowClose };
            System.Windows.Window window = args[0] == "--preview-search"
                ? new Views.SearchWindow(settings, new DesktopSearch()) : new Views.OrganizerWindow(settings);
            preview.Run(window);
            return;
        }
        if (args is ["--watchdog", var pid] && int.TryParse(pid, out var id))
        {
            ShellGuard.Watch(id);
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
