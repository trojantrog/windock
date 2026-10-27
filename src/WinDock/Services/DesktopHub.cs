using WinDock.Models;
using WinDock.Views;

namespace WinDock.Services;

public static class DesktopHub
{
    static Settings? settings;
    static readonly DesktopSearch Search = new();
    static SearchWindow? search;
    static OrganizerWindow? organizer;
    public static event Action? ShortcutChanged;
    public static void Configure(Settings value) => settings = value;
    public static void NotifyShortcutChanged() => ShortcutChanged?.Invoke();

    public static void OpenSearch()
    {
        if (settings is null) return;
        if (search is null)
        {
            search = new SearchWindow(settings, Search);
            search.Closed += (_, _) => search = null;
            search.Show();
        }
        search.FocusSearch();
    }

    public static void ToggleSearch()
    {
        if (search is not null) search.Close();
        else OpenSearch();
    }

    public static void OpenOrganizer()
    {
        if (settings is null) return;
        if (organizer is null)
        {
            organizer = new OrganizerWindow(settings);
            organizer.Closed += (_, _) => organizer = null;
            organizer.Show();
        }
        organizer.Activate();
    }

    public static void Close()
    {
        search?.Close();
        organizer?.Close();
    }

    public static (uint Modifiers, uint Key) Shortcut(string shortcut) => shortcut switch
    {
        "Ctrl+Shift+Space" => (0x2 | 0x4 | 0x4000, 0x20),
        "Win+Shift+Space" => (0x8 | 0x4 | 0x4000, 0x20),
        _ => (0x1 | 0x4000, 0x20)
    };
}
