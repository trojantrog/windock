using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinDock.Services;

internal sealed record TrayProcessIdentity(string Path, string Name, long Started)
{
    public static readonly TrayProcessIdentity Unknown = new("", "", 0);
}

internal sealed class TrayIdentity
{
    readonly Dictionary<uint, Task<TrayProcessIdentity>> processes = [];
    readonly SemaphoreSlim reads = new(4);

    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);

    public Task<TrayProcessIdentity> Read(uint pid)
    {
        if (!processes.TryGetValue(pid, out var task)) processes[pid] = task = ReadAsync(pid);
        return task;
    }

    async Task<TrayProcessIdentity> ReadAsync(uint pid)
    {
        await reads.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => ReadProcess(pid)).ConfigureAwait(false); }
        finally { reads.Release(); }
    }

    static TrayProcessIdentity ReadProcess(uint pid)
    {
        var handle = Native.OpenProcess(0x1000, false, (int)pid);
        if (handle == 0) return ReadRestrictedProcess(pid);
        string path; long started;
        try
        {
            var buffer = new StringBuilder(32768); var length = buffer.Capacity;
            if (!Native.QueryFullProcessImageName(handle, 0, buffer, ref length)) return ReadRestrictedProcess(pid);
            path = buffer.ToString();
            if (!GetProcessTimes(handle, out started, out _, out _, out _)) started = 0;
        }
        finally { Native.CloseHandle(handle); }
        return new(path, DisplayName(path), started);
    }

    static TrayProcessIdentity ReadRestrictedProcess(uint pid)
    {
        // A restricted process can still own a live, observed tray window.
        // Do not infer an executable path from historical tray registrations.
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var basename = process.ProcessName;
            if (string.IsNullOrWhiteSpace(basename)) return TrayProcessIdentity.Unknown;
            return new("", basename, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return TrayProcessIdentity.Unknown; }
    }

    static string DisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        try
        {
            // Do not turn a tray refresh into a synchronous network metadata read.
            if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                var description = version.FileDescription?.Trim();
                var product = version.ProductName?.Trim();
                // When both names identify the executable, prefer its shorter
                // product label over a longer functional description.
                if (!string.IsNullOrWhiteSpace(product) && !Generic(product)
                    && !string.IsNullOrWhiteSpace(description) && !Generic(description)
                    && product.Length < description.Length
                    && product.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && description.Contains(name, StringComparison.OrdinalIgnoreCase)) name = product;
                else if (!string.IsNullOrWhiteSpace(description) && !Generic(description)) name = description;
                else if (!string.IsNullOrWhiteSpace(product) && !Generic(product)) name = product;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return name;
    }

    static bool Generic(string value) => value.Equals("Electron", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Application", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Windows Application", StringComparison.OrdinalIgnoreCase);

    public void Keep(IEnumerable<uint> alive)
    {
        var present = alive.ToHashSet();
        foreach (var pid in processes.Keys.Where(pid => !present.Contains(pid)).ToArray()) processes.Remove(pid);
    }
    public void Clear() => processes.Clear();

    public static string Name(SystemTrayItem item)
    {
        var executable = Path.GetFileNameWithoutExtension(item.ProcessPath).ToLowerInvariant();
        if (executable == "securityhealthsystray") return Loc.T("traySecurity");
        if (executable == "bthudtask") return Loc.T("trayBluetooth");
        if (executable is "explorer" or "rundll32" or "svchost" || item.ApplicationName.Length == 0)
        {
            var tip = item.Tip;
            if (Contains(tip, "音量", "扬声器", "耳机", "volume", "speaker", "headphone")) return Loc.T("trayAudio");
            if (Contains(tip, "安全删除", "safely remove")) return Loc.T("trayDevices");
            if (Contains(tip, "蓝牙", "bluetooth")) return Loc.T("trayBluetooth");
            if (Contains(tip, "电池", "battery")) return Loc.T("trayBattery");
            if (Contains(tip, "网络", "network", "internet access")) return Loc.T("trayNetwork");
        }
        if (item.ApplicationName.Length > 0) return item.ApplicationName;
        var first = item.Tip.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        // Status-only tips (rates, counts, percentages) are not application names.
        return first.Length > 0 && !char.IsDigit(first[0]) && !Contains(first, "kb/s", "mb/s", "gb/s", "kib/s") ? first : "";
    }

    static bool Contains(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
