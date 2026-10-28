using Microsoft.Win32;

namespace WinDock.Services;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "WinDock";

    public static string Command(string exe) => $"\"{exe}\" --tray";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return Environment.ProcessPath is { } exe && key?.GetValue(Name) is string command
                && command.Equals(Command(exe), StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe) key.SetValue(Name, Command(exe));
            else if (!value) key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}
