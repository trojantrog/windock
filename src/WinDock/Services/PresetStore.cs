using System.Text.Json;
using WinDock.Models;

namespace WinDock.Services;

public sealed class PresetStore
{
    public string Dir { get; } = Path.Combine(Settings.Dir, "presets");
    public bool Seeded { get; private set; }

    public List<Preset> Load()
    {
        Seed();
        var list = new List<Preset>();
        foreach (var file in Paths().Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var p = JsonFile.Read<Preset>(file);
                if (p is null) continue;
                p.FilePath = file;
                if (p.Id == "") p.Id = Path.GetFileNameWithoutExtension(file);
                if (p.Name == "") p.Name = p.Id;
                list.Add(p);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or FormatException) { }
        }
        return list;
    }

    public void Save(Preset p) => JsonFile.Write(p.FilePath, p);

    public void Delete(Preset p)
    {
        // Remove the recovery copy first: an interrupted delete must not cause
        // a deliberately deleted environment to be restored on the next start.
        File.Delete(p.FilePath + ".bak");
        File.Delete(p.FilePath);
    }

    IEnumerable<string> Paths() => Directory.EnumerateFiles(Dir, "*.json")
        .Concat(Directory.EnumerateFiles(Dir, "*.json.bak").Select(path => path[..^4]))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public string NewPath(string id)
    {
        var next = Paths()
            .Select(f => Path.GetFileName(f).Split('-')[0])
            .Select(n => int.TryParse(n, out var v) ? v : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        return Path.Combine(Dir, $"{next:00}-{id}.json");
    }

    public static IEnumerable<string> BundledNames() =>
        typeof(PresetStore).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Presets/")).Order(StringComparer.Ordinal);

    public static string ReadBundled(string name)
    {
        using var stream = typeof(PresetStore).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    void Seed()
    {
        if (Directory.Exists(Dir) && Paths().Any()) return;
        Directory.CreateDirectory(Dir);
        Seeded = true;
        foreach (var name in BundledNames())
        {
            var preset = JsonSerializer.Deserialize<Preset>(ReadBundled(name), Json.Options);
            if (preset is null) continue;
            preset.Items = preset.Items.Where(i => i.Exists).ToList();
            JsonFile.Write(Path.Combine(Dir, name["Presets/".Length..]), preset);
        }
    }
}
