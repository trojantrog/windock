using WinDock.Models;

namespace WinDock.Services;

public sealed record SearchHit(string Name, string Detail, string Kind, string Path, PinItem? Application = null)
{
    public bool Open() => Application is null ? Launcher.OpenFile(Path) : Launcher.Open(Application);
}
public sealed record SearchBatch(IReadOnlyList<SearchHit> Hits, bool EverythingAvailable, bool Limited, string? Error = null);

public sealed class DesktopSearch
{
    readonly object catalogGate = new();
    Task<List<CatalogEntry>>? applications, previousApplications, overdueApplications;
    long catalogStarted;
    public Task WarmAsync() => ApplicationCatalog();

    Task<List<CatalogEntry>> ApplicationCatalog()
    {
        lock (catalogGate)
        {
            if (overdueApplications is { IsCompleted: true } overdue)
            {
                if (overdue.IsCompletedSuccessfully && previousApplications is null) previousApplications = overdue;
                else if (overdue.IsFaulted) _ = overdue.Exception;
                overdueApplications = null;
            }
            if (applications is { IsCompletedSuccessfully: true }) previousApplications = applications;
            var age = Environment.TickCount64 - catalogStarted;
            if (applications is null || applications.IsCompletedSuccessfully && age >= 120_000
                || (applications.IsFaulted || applications.IsCanceled) && age >= 5_000
                || !applications.IsCompleted && age >= 15_000 && overdueApplications is null)
            {
                if (applications is { IsCompleted: false }) overdueApplications = applications;
                else if (applications is { IsFaulted: true }) _ = applications.Exception;
                applications = Task.Run(Catalog.Load);
                catalogStarted = Environment.TickCount64;
            }
            return applications.IsCompletedSuccessfully ? applications : previousApplications ?? applications;
        }
    }

    async Task<(List<SearchHit> Hits, string? Error)> SearchApplicationsAsync(string query, CancellationToken cancellation)
    {
        try
        {
            var catalog = await ApplicationCatalog().WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            var hits = await Task.Run(() => catalog.Where(a => MatchScore(query, a.Name) > 0)
                .OrderByDescending(a => MatchScore(query, a.Name)).Take(30)
                .Select(a => new SearchHit(a.Name, a.Category, "app", a.IconSource ?? "", a.Make())).ToList(), cancellation)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            return (hits, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { return ([], Loc.T("searchAppsUnavailable")); }
    }

    async Task<(EverythingBatch Batch, bool Available, string? Error)> SearchEverythingAsync(
        string query, string? instance, CancellationToken cancellation)
    {
        try { return (await EverythingSearch.QueryAsync(query, instance, cancellation), true, null); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception e) { return (new EverythingBatch([], 0), false, e.Message); }
    }

    public async Task<SearchBatch> SearchAsync(string query, string? instance, CancellationToken cancellation,
        SearchFilter? filter = null, Action<SearchBatch>? onPartial = null)
    {
        query = query.Trim().Replace("\0", "");
        if (query.Length == 0) return new SearchBatch([], true, false);
        if (query.Length > 512) query = query[..512];
        filter ??= new SearchFilter();
        var applicationSearch = filter.IncludeApplications ? SearchApplicationsAsync(query, cancellation)
            : Task.FromResult<(List<SearchHit> Hits, string? Error)>(([], null));
        var includeEverything = filter.Category != SearchCategory.Applications;
        var everythingSearch = includeEverything ? SearchEverythingAsync(filter.Query(query), instance, cancellation)
            : Task.FromResult((new EverythingBatch([], 0), false, (string?)null));

        if (onPartial is not null && filter.IncludeApplications && includeEverything)
        {
            var first = await Task.WhenAny(applicationSearch, everythingSearch);
            cancellation.ThrowIfCancellationRequested();
            if (first == applicationSearch && !everythingSearch.IsCompleted)
            {
                var (partialApps, partialError) = await applicationSearch;
                onPartial(BuildBatch(query, filter, partialApps, new EverythingBatch([], 0), false, partialError));
            }
            else if (first == everythingSearch && !applicationSearch.IsCompleted)
            {
                var (partialIndex, partialAvailable, partialError) = await everythingSearch;
                onPartial(BuildBatch(query, filter, [], partialIndex, partialAvailable, partialError));
            }
        }
        var (apps, applicationError) = await applicationSearch;
        var (indexed, everythingAvailable, error) = await everythingSearch;
        if (applicationError is not null) error = error is null ? applicationError : error + " · " + applicationError;
        cancellation.ThrowIfCancellationRequested();
        return BuildBatch(query, filter, apps, indexed, everythingAvailable, error);
    }

    static SearchBatch BuildBatch(string query, SearchFilter filter, IReadOnlyList<SearchHit> apps,
        EverythingBatch indexed, bool everythingAvailable, string? error)
    {
        var all = apps.Concat(indexed.Hits).Where(filter.Accepts)
            .DistinctBy(h => h.Kind == "app" ? $"app:{h.Application?.AppId ?? h.Application?.Target}|{h.Application?.Args}" : h.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(h => MatchScore(query, h.Name) + (h.Kind == "app" ? 200 : 0))
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).Take(100).ToList();
        var limited = filter.Category == SearchCategory.Applications ? apps.Count >= 30
            : indexed.Total > indexed.Hits.Count || indexed.Hits.Count + apps.Count > 100;
        return new SearchBatch(all, everythingAvailable, limited, error);
    }

    public static int MatchScore(string query, string name)
    {
        if (name.Equals(query, StringComparison.CurrentCultureIgnoreCase)) return 100;
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 80;
        return name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ? 50 : 0;
    }
}
