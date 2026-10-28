using System.Reflection;
using System.Text.Json;

namespace WinDock.Services;

internal sealed class TrayDiagnostics
{
    Task? write;
    string? last;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly string Build = typeof(TrayDiagnostics).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public void Publish(object state)
    {
        if (write is { IsCompleted: false }) return;
        var content = JsonSerializer.Serialize(state, Json);
        if (last == content) return;
        var entry = JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            processId = Environment.ProcessId,
            build = Build,
            state
        }, Json);
        write = Task.Run(() =>
        {
            // One local current-state file, never a history of notification text.
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock");
            var path = Path.Combine(directory, "tray-status.json");
            var temporary = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, entry);
                File.Move(temporary, path, overwrite: true);
                last = content;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A diagnostic write must never interrupt tray handling.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        });
    }
}
