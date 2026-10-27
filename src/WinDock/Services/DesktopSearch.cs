using WinDock.Models;

namespace WinDock.Services;

public sealed record SearchHit(string Name, string Detail, string Kind, string Path, PinItem? Application = null)
{
    public bool Open() => Application is null ? Launcher.OpenFile(Path) : Launcher.Open(Application);
}
public sealed record SearchBatch(IReadOnlyList<SearchHit> Hits, bool EverythingAvailable, bool Limited, string? Error = null);

public sealed class DesktopSearch
{
    readonly Lazy<Task<List<CatalogEntry>>> applications = new(() => Task.Run(Catalog.Load));
    public Task WarmAsync() => applications.Value;

    public async Task<SearchBatch> SearchAsync(string query, string? instance, CancellationToken cancellation)
    {
        query = query.Trim().Replace("\0", "");
        if (query.Length == 0) return new SearchBatch([], true, false);
        if (query.Length > 512) query = query[..512];
        var catalog = await applications.Value.WaitAsync(cancellation);
        var apps = await Task.Run(() => catalog.Where(a => MatchScore(query, a.Name) > 0)
            .OrderByDescending(a => MatchScore(query, a.Name)).Take(30)
            .Select(a => new SearchHit(a.Name, a.Category, "app", a.IconSource ?? "", a.Make())).ToList(), cancellation);
        EverythingBatch indexed;
        string? error = null;
        try { indexed = await EverythingSearch.QueryAsync(query, instance, cancellation); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { indexed = new EverythingBatch([], 0); error = e.Message; }
        cancellation.ThrowIfCancellationRequested();
        var all = apps.Concat(indexed.Hits)
            .DistinctBy(h => h.Kind == "app" ? $"app:{h.Application?.AppId ?? h.Application?.Target}|{h.Application?.Args}" : h.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(h => MatchScore(query, h.Name) + (h.Kind == "app" ? 200 : 0))
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).Take(100).ToList();
        return new SearchBatch(all, error is null, indexed.Total > indexed.Hits.Count || indexed.Hits.Count + apps.Count > 100, error);
    }

    public static int MatchScore(string query, string name)
    {
        if (name.Equals(query, StringComparison.CurrentCultureIgnoreCase)) return 100;
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 80;
        return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 50 : 0;
    }
}