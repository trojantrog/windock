using System.Text;
using WinDock.Models;

namespace WinDock.Services;

// Search window captions directly so title changes and tray-hidden windows do
// not depend on the Dock's cached window list.
internal static class SearchWindows
{
    const int Style = -16;
    const long Caption = 0x00C00000, SystemMenu = 0x00080000;
    const uint QueryLimited = 0x1000;

    static readonly HashSet<string> SkipClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow",
        "IME", "MSCTFIME UI"
    ];

    public static List<SearchHit> Find(string query, CancellationToken cancellation)
    {
        var hits = new List<SearchHit>();
        Native.EnumWindows((window, _) =>
        {
            if (cancellation.IsCancellationRequested || hits.Count >= 30) return false;
            var cls = Native.ClassOf(window);
            if (SkipClasses.Contains(cls)) return true;
            var visible = Native.IsWindowVisible(window);
            var ex = (long)Native.GetWindowLongPtr(window, Native.GWL_EXSTYLE);
            var tool = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            var owned = Native.GetWindow(window, Native.GW_OWNER) != 0;
            if (visible)
            {
                if ((ex & Native.WS_EX_APPWINDOW) == 0 && (owned || (ex & tool) != 0)) return true;
            }
            else
            {
                var style = (long)Native.GetWindowLongPtr(window, Style);
                if (owned || (ex & tool) != 0 || (style & Caption) != Caption || (style & SystemMenu) == 0) return true;
            }
            if (Native.DwmGetWindowAttribute(window, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var title = Native.TitleOf(window);
            if (title.Length == 0 || DesktopSearch.MatchScore(query, title) == 0) return true;
            var thread = Native.GetWindowThreadProcessId(window, out var pid);
            if (thread == 0 || pid == 0 || pid == Environment.ProcessId) return true;
            var exe = ProcessPath(pid);
            if (!WindowMotionNative.SameWindow(window, pid) || Native.ClassOf(window) != cls || Native.TitleOf(window) != title) return true;
            var pin = exe is null ? null : new PinItem { Name = title, Target = exe };
            hits.Add(new SearchHit(title, Loc.T("searchRunningWindow"), "app", exe ?? "", pin, window, pid, cls, thread));
            return true;
        }, 0);
        cancellation.ThrowIfCancellationRequested();
        return hits;
    }

    static string? ProcessPath(int pid)
    {
        var process = Native.OpenProcess(QueryLimited, false, pid);
        if (process == 0) return null;
        try
        {
            var length = 32768;
            var value = new StringBuilder(length);
            return Native.QueryFullProcessImageName(process, 0, value, ref length) ? value.ToString() : null;
        }
        finally { Native.CloseHandle(process); }
    }
}
