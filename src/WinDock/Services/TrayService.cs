using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
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
        icon.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) OpenMenu(); };

        vm.Notice += text => icon.ShowBalloonTip(2500, "WinDock", text, Forms.ToolTipIcon.None);
    }

    void OpenMenu()
    {
        if (contextMenu is not null) contextMenu.IsOpen = false;
        contextMenu = new ContextMenu { Placement = PlacementMode.MousePoint, UseLayoutRounding = true, SnapsToDevicePixels = true };
        var menu = contextMenu.Items;
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
        var opened = contextMenu;
        opened.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(opened) is HwndSource source) Native.SetForegroundWindow(source.Handle);
        };
        opened.Closed += (_, _) => { if (contextMenu == opened) contextMenu = null; };
        PopupInteraction.Open(opened, PopupInteraction.TrayCallbackOwner ?? window);
    }

    static MenuItem Item(string label, Action click, bool check = false, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsChecked = check, IsEnabled = enabled };
        item.Click += (_, _) => click(); return item;
    }

    public void Dispose()
    {
        if (contextMenu is not null) contextMenu.IsOpen = false;
        icon.Visible = false;
        icon.Dispose();
    }
}
