using System.ComponentModel;
using System.Diagnostics;
using WinDock.Models;

namespace WinDock.Services;

public sealed record SearchHit(string Name, string Detail, string Kind, string Path, PinItem? Application = null,
    nint Window = 0, int ProcessId = 0, string? WindowClass = null, uint WindowThreadId = 0)
{
    public async Task<bool> OpenAsync(CancellationToken cancellation = default)
    {
        if (Window == 0) return Application is null ? Launcher.OpenFile(Path) : Launcher.Open(Application);
        bool SameWindow() => WindowMotionNative.SameWindow(Window, ProcessId)
            && Native.ClassOf(Window) == WindowClass
            && (WindowThreadId == 0 || Native.GetWindowThreadProcessId(Window, out _) == WindowThreadId);
        if (!SameWindow()) return false;
        if (!Native.IsWindowVisible(Window))
        {
            if (!Native.ShowWindowAsync(Window, Native.SW_SHOWNA)) return false;
            for (var i = 0; i < 10 && !Native.IsWindowVisible(Window); i++)
            {
                await Task.Delay(60, cancellation);
                if (!SameWindow()) return false;
            }
        }
        if (!SameWindow() || !Native.IsWindowVisible(Window)) return false;
        WindowTracker.Activate(Window);
        for (var i = 0; i < 10; i++)
        {
            if (!SameWindow() || !Native.IsWindowVisible(Window)) return false;
            var foreground = Native.GetForegroundWindow();
            if (!Native.IsIconic(Window) && (foreground == Window || Native.GetAncestor(foreground, Native.GA_ROOTOWNER) == Window))
                return true;
            await Task.Delay(60, cancellation);
        }
        return false;
    }
}
public sealed record SearchBatch(IReadOnlyList<SearchHit> Hits, bool EverythingAvailable, bool Limited, string? Error = null);

public sealed class DesktopSearch
{
    sealed record SearchEntry(CatalogEntry Entry, IReadOnlyList<string> Aliases);
    readonly object catalogGate = new();
    Task<List<SearchEntry>>? applications, previousApplications, overdueApplications;
    long catalogStarted;
    public Task WarmAsync() => ApplicationCatalog();

    Task<List<SearchEntry>> ApplicationCatalog()
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
                applications = Task.Run(LoadApplications);
                catalogStarted = Environment.TickCount64;
            }
            return applications.IsCompletedSuccessfully ? applications : previousApplications ?? applications;
        }
    }

    static List<SearchEntry> LoadApplications()
    {
        var entries = Catalog.Load().Select(entry => new SearchEntry(entry, [entry.Name])).ToList();
        try
        {
            foreach (var exe in TrayApps.Registered())
            {
                if (!File.Exists(exe)) continue;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(exe);
                    var aliases = new[] { info.ProductName, info.FileDescription, Path.GetFileNameWithoutExtension(exe) }
                        .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
                        .Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
                    if (aliases.Length == 0) continue;
                    var name = aliases[0];
                    var entry = new CatalogEntry(name, Loc.T("catPrograms"), exe, () => new PinItem { Name = name, Target = exe });
                    entries.Add(new SearchEntry(entry, aliases));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or System.Security.SecurityException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return entries;
    }

    async Task<(List<SearchHit> Hits, string? Error)> SearchApplicationsAsync(string query, CancellationToken cancellation)
    {
        var windows = Task.Run(() => SearchWindows.Find(query, cancellation), cancellation);
        List<SearchHit> catalogHits = [];
        string? error = null;
        try
        {
            var catalog = await ApplicationCatalog().WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            catalogHits = await Task.Run(() => catalog.Select(a => (a.Entry, Score: a.Aliases.Max(alias => MatchScore(query, alias))))
                .Where(a => a.Score > 0).OrderByDescending(a => a.Score).Take(30)
                .Select(a => new SearchHit(a.Entry.Name, a.Entry.Category, "app", a.Entry.IconSource ?? "", a.Entry.Make())).ToList(), cancellation)
                .WaitAsync(TimeSpan.FromSeconds(2), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception) { error = Loc.T("searchAppsUnavailable"); }
        return ((await windows).Concat(catalogHits).ToList(), error);
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
        var runningPaths = apps.Where(h => h.Window != 0 && !string.IsNullOrEmpty(h.Path))
            .Select(h => h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var all = apps.Concat(indexed.Hits).Where(filter.Accepts)
            .Where(h => h.Window != 0 || h.Kind != "app" || h.Application is not { Args: null, ResolvedTarget: { } target } || !runningPaths.Contains(target))
            .DistinctBy(h => h.Window != 0 ? $"window:{h.Window}:{h.ProcessId}"
                : h.Kind == "app" ? $"app:{h.Application?.AppId ?? h.Application?.Target}|{h.Application?.Args}" : h.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(h => MatchScore(query, h.Name) + (h.Kind == "app" ? 200 : 0) + (h.Window != 0 ? 10 : 0))
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
