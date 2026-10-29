using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WinDock.ViewModels;
using WinDock.Views;
using Forms = System.Windows.Forms;

namespace WinDock.Services;

public sealed class TrayService : IDisposable
{
    readonly MainViewModel vm;
    readonly ConfigWindow window;
    readonly DockViewModel dock;
    readonly Forms.NotifyIcon icon;
    ContextMenu? contextMenu;
    TrayMenuHost? menuHost;
    DispatcherOperation? pendingMenu;
    bool disposed;

    public TrayService(MainViewModel vm, ConfigWindow window, DockViewModel dock)
    {
        this.vm = vm;
        this.window = window;
        this.dock = dock;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        icon = new Forms.NotifyIcon
        {
            Icon = new Icon(stream),
            Text = "WinDock",
            Visible = true
        };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.Toggle(); };
        icon.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) RequestMenu(); };

        vm.Notice += text => icon.ShowBalloonTip(2500, "WinDock", text, Forms.ToolTipIcon.None);
    }

    void RequestMenu()
    {
        if (disposed || pendingMenu is not null || contextMenu?.IsOpen == true || window.Dispatcher.HasShutdownStarted) return;
        var owner = PopupInteraction.TrayCallbackOwner;
        if (owner is null && window.IsVisible && window.WindowState != WindowState.Minimized) owner = window;

        // Finish the NotifyIcon callback and its mouse release before WPF takes
        // capture. Duplicate callbacks must not close and recreate an open menu.
        pendingMenu = window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            pendingMenu = null;
            try
            {
                if (disposed || window.Dispatcher.HasShutdownStarted || contextMenu?.IsOpen == true) return;
                if (owner is not null && (!owner.IsVisible || owner.WindowState == WindowState.Minimized)) return;
                OpenMenu(owner);
            }
            finally { ReleaseMenuHost(); }
        });
    }

    void OpenMenu(Window? owner)
    {
        var opened = new ContextMenu { Placement = PlacementMode.MousePoint, UseLayoutRounding = true, SnapsToDevicePixels = true };
        var menu = opened.Items;
        if (vm.Dock.NewVersion is { } version)
        {
            menu.Add(Item(Loc.F("updateDownload", version), () => Launcher.OpenFile(Updates.Page)));
            menu.Add(new Separator());
        }
        menu.Add(Item(Loc.T("trayOpen"), window.Open));
        if (vm.Dock.SearchEnabled) menu.Add(Item(Loc.T("searchTitle"), DesktopHub.OpenSearch));
        if (vm.Dock.OrganizerEnabled) menu.Add(Item(Loc.T("organizerTitle"), DesktopHub.OpenOrganizer));
        menu.Add(new Separator());

        foreach (var p in vm.Presets)
        {
            var preset = p;
            menu.Add(Item($"{p.Glyph}  {p.Model.Name}", async () => await vm.ApplyAsync(preset, auto: false), p.IsActive));
        }

        menu.Add(new Separator());
        menu.Add(Item(Loc.T("showDock"), () => vm.Dock.Enabled = !vm.Dock.Enabled, vm.Dock.Enabled));
        menu.Add(Item(Loc.T("replaceTaskbar"), () => vm.Dock.ReplaceTaskbar = !vm.Dock.ReplaceTaskbar, vm.Dock.ReplaceTaskbar, vm.Dock.Enabled));
        if (dock.Tracker.HiddenHandles.Any())
            menu.Add(Item(Loc.T("showAllWindows"), () =>
            {
                dock.Tracker.ShowAll();
                dock.Shell.SetHiddenWindows([]);
            }));
        menu.Add(new Separator());
        menu.Add(Item(Loc.T("trayAuto"), () => vm.AutoSwitch = !vm.AutoSwitch, vm.AutoSwitch));
        menu.Add(Item(Loc.T("trayNotify"), () => vm.NotifySwitch = !vm.NotifySwitch, vm.NotifySwitch));
        vm.Dock.RefreshStartup();
        var startup = Item(Loc.T(vm.Dock.StartWithWindows is null ? "startupCancelRegistration" : "trayStartup"),
            () => vm.Dock.StartWithWindows = vm.Dock.StartWithWindows == false,
            vm.Dock.StartWithWindows == true, vm.Dock.CanChangeStartup);
        if (vm.Dock.StartupNeedsAttention) startup.ToolTip = vm.Dock.StartupStatus;
        menu.Add(startup);
        if (vm.Dock.StartupNeedsWindowsSettings)
            menu.Add(Item(Loc.T("startupWindowsSettings"), vm.Dock.OpenStartupSettings));
        var language = new MenuItem { Header = Loc.T("trayLanguage") };
        language.Items.Add(Item("English", () => vm.SetLanguage(Loc.English), Loc.Instance.Language == Loc.English));
        language.Items.Add(Item("Português", () => vm.SetLanguage(Loc.Portuguese), Loc.Instance.Language == Loc.Portuguese));
        language.Items.Add(Item("简体中文", () => vm.SetLanguage(Loc.Chinese), Loc.Instance.Language == Loc.Chinese));
        menu.Add(language);
        menu.Add(new Separator());
        menu.Add(Item(Loc.T("trayExit"), () => Application.Current.Shutdown()));
        // ContextMenu's popup is WS_EX_NOACTIVATE. Activating that HWND after
        // WPF has focused the menu can clear its focus and exit menu mode.
        // Use a real, visible owner instead, including when settings are hidden.
        if (owner is null)
        {
            var host = menuHost ??= CreateMenuHost();
            if (!host.Prepare()) return;
            owner = host;
        }
        else if (!owner.IsActive && !owner.Activate()) return;

        contextMenu = opened;
        opened.Closed += (_, _) =>
        {
            if (contextMenu != opened) return;
            contextMenu = null;
            // WPF closes the menu before dispatching MenuItem.Click at Render
            // priority. Let that command open its window before hiding ours.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, ReleaseMenuHost);
        };
        PopupInteraction.Open(opened, owner);
    }

    TrayMenuHost CreateMenuHost()
    {
        var host = new TrayMenuHost();
        host.Closed += (_, _) =>
        {
            if (menuHost == host) menuHost = null;
            if (contextMenu?.PlacementTarget == host) contextMenu.IsOpen = false;
        };
        return host;
    }

    void ReleaseMenuHost()
    {
        if (!disposed && contextMenu is null && pendingMenu is null) menuHost?.Hide();
    }

    static MenuItem Item(string label, Action click, bool check = false, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsChecked = check, IsEnabled = enabled };
        item.Click += (_, _) => click(); return item;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pendingMenu?.Abort(); pendingMenu = null;
        if (contextMenu is not null) contextMenu.IsOpen = false;
        menuHost?.Close(); menuHost = null;
        icon.Visible = false;
        icon.Dispose();
    }
}
