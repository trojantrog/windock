using System.Text.Json;

namespace WinDock.Models;

internal static class JsonFile
{
    static readonly object Gate = new();

    public static T? Read<T>(string path) where T : class => ReadOne<T>(path) ?? ReadOne<T>(path + ".bak");

    static T? ReadOne<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write<T>(string path, T value) where T : class
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(content);
                    file.Flush(flushToDisk: true);
                }
                if (!File.Exists(path)) { File.Move(temporary, path); return; }

                // An unreadable original must not be replaced. Preserve malformed
                // data separately so it cannot displace the last usable backup.
                var original = File.ReadAllText(path);
                bool valid;
                try { valid = JsonSerializer.Deserialize<T>(original, Json.Options) is not null; }
                catch (JsonException) { valid = false; }
                var backup = valid ? path + ".bak"
                    : path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                File.Replace(temporary, path, backup);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
