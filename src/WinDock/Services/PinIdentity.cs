using WinDock.Models;

namespace WinDock.Services;

public static class PinIdentity
{
    public static bool TryAdd(HashSet<string> seen, PinItem item, string? target = null, string? arguments = null, string? appId = null)
    {
        return seen.Add(Key(item, target, arguments, appId));
    }

    static string Key(PinItem item, string? target, string? arguments, string? appId)
    {
        if (item.IsRaw) return Identifier(item.AppId is { Length: > 0 } id ? id : item.Name);
        if (item.IsLauncher) return Identifier(item.AppId!);
        if (target is null) return Identifier(item.Name);

        var identity = Identifier(item.AppId ?? appId ?? target);
        return string.IsNullOrEmpty(arguments) ? identity : $"args:{identity.Length}:{identity}{arguments}";
    }

    static string Identifier(string value) => "app:" + value.ToLowerInvariant();
}
