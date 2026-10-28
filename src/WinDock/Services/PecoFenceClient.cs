using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinDock.Models;

namespace WinDock.Services;

public sealed record FenceInfo(string Id, string Title, string Kind, int ItemCount, string? TabHost, bool RolledUp)
{
    public bool IsVirtual => Kind is "virtual" or "inbox";
    public override string ToString() => Title;
}

public sealed record FenceItem(string Id, string FileName, string Path, string Fence, string FenceTitle, bool IsFolder)
{
    public override string ToString() => FileName;
}

public sealed record FenceSnapshot(string Id, string Name, long Ts)
{
    public override string ToString() => Name;
}

public sealed class PecoFenceException(string message, bool uncertain = false) : Exception(message)
{
    public bool MayHaveCompleted { get; } = uncertain;
}

public interface IPecoCommandRunner
{
    Task<JsonElement> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation);
}

public sealed class PecoProcessRunner(string executable) : IPecoCommandRunner
{
    public async Task<JsonElement> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--timeout");
        start.ArgumentList.Add("10000");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new PecoFenceException(Loc.T("pecoUnavailable"));
        }
        catch (Win32Exception) { throw new PecoFenceException(Loc.T("pecoUnavailable")); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(13));
        var readOnly = arguments.Count >= 2 && arguments[1] == "list" && arguments[0] is "fence" or "item" or "snapshot";
        PecoFenceException TimeoutFailure() => new(Loc.T(readOnly ? "pecoReadTimeout" : "pecoTimeout"), uncertain: !readOnly);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        var finished = Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
        try { await finished.WaitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            // Observe reader failures even if a child kept a redirected pipe open
            // after the CLI exited and the overall timeout won the race.
            _ = finished.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (cancellation.IsCancellationRequested) throw;
            throw TimeoutFailure();
        }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode == 3) throw new PecoFenceException(Loc.T("pecoNotRunning"));
        if (process.ExitCode == 4) throw TimeoutFailure();
        if (process.ExitCode != 0)
        {
            var message = Loc.T("pecoOperationFailed");
            try
            {
                using var failure = JsonDocument.Parse(stderr);
                message = failure.RootElement.GetProperty("error").GetProperty("message").GetString() ?? message;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new PecoFenceException(message);
        }
        try { using var document = JsonDocument.Parse(stdout); return document.RootElement.Clone(); }
        catch (JsonException) { throw new PecoFenceException(Loc.T("pecoInvalidReply"), uncertain: !readOnly); }
    }
}

public sealed class PecoFenceClient(IPecoCommandRunner runner)
{
    public static string? FindExecutable(string? configured)
    {
        var candidates = new List<string?> { configured };
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (run?.GetValue("PecoFence") is string command)
        {
            var trimmed = command.Trim();
            var suffix = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var end = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : suffix < 0 ? -1 : suffix + 4;
            if (end > 0) candidates.Add(trimmed.StartsWith('"') ? trimmed[1..end] : trimmed[..Math.Min(end, trimmed.Length)]);
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PecoFence", "pecofence.exe"));
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var subkey in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subkey);
                if (entry?.GetValue("DisplayName") is not string name || !name.StartsWith("PecoFence", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.GetValue("InstallLocation") is string folder) candidates.Add(Path.Combine(folder, "pecofence-cli.exe"));
            }
        }
        foreach (var candidate in candidates.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate!);
                var cli = Directory.Exists(expanded) ? Path.Combine(expanded, "pecofence-cli.exe")
                    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(expanded))!, "pecofence-cli.exe");
                if (File.Exists(cli)) return cli;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public async Task<List<FenceInfo>> FencesAsync(CancellationToken cancellation = default) =>
        Read<List<FenceInfo>>(await runner.RunAsync(["fence", "list", "--fields", "id,title,kind,itemCount,tabHost,rolledUp"], cancellation)) ?? [];

    public async Task<List<FenceItem>> ItemsAsync(string fence, CancellationToken cancellation = default) =>
        Read<List<FenceItem>>(await runner.RunAsync(["item", "list", "--fence", Id(fence), "--fields", "id,fileName,path,fence,fenceTitle,isFolder"], cancellation)) ?? [];

    public async Task<List<FenceItem>> AllItemsAsync(CancellationToken cancellation = default) =>
        Read<List<FenceItem>>(await runner.RunAsync(["item", "list", "--fields", "id,fileName,path,fence,fenceTitle,isFolder"], cancellation)) ?? [];

    public async Task<List<FenceSnapshot>> SnapshotsAsync(CancellationToken cancellation = default) =>
        Read<List<FenceSnapshot>>(await runner.RunAsync(["snapshot", "list"], cancellation)) ?? [];

    public Task<JsonElement> SaveLayoutAsync(string name, CancellationToken cancellation = default) =>
        runner.RunAsync(["snapshot", "save", Name(name)], cancellation);

    Task<JsonElement> BeforeChange(CancellationToken cancellation) => SaveLayoutAsync($"WinDock {DateTime.Now:yyyy-MM-dd HH:mm:ss}", cancellation);

    public async Task CreateAsync(string title, CancellationToken cancellation = default)
    {
        title = Name(title);
        await BeforeChange(cancellation);
        await runner.RunAsync(["fence", "create", "--title", title], cancellation);
    }

    public async Task RenameAsync(string fence, string title, CancellationToken cancellation = default)
    {
        fence = Id(fence); title = Name(title);
        await BeforeChange(cancellation);
        await runner.RunAsync(["fence", "rename", fence, title], cancellation);
    }

    public async Task MoveAsync(IEnumerable<FenceItem> selected, string destination, CancellationToken cancellation = default)
    {
        destination = Id(destination);
        var items = selected.ToList();
        if (items.Count == 0) return;
        var fences = await FencesAsync(cancellation);
        var allowed = fences.Where(f => f.IsVirtual).Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = items.Select(i => Id(i.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var current = (await AllItemsAsync(cancellation)).Where(i => ids.Contains(i.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (current.Count != ids.Length) throw new PecoFenceException(Loc.T("pecoInvalidSelection"));
        if (!allowed.Contains(destination) || current.Any(i => !allowed.Contains(i.Fence)))
            throw new PecoFenceException(Loc.T("pecoVirtualOnly"));
        var arguments = new List<string> { "item", "move" };
        arguments.AddRange(ids);
        arguments.AddRange(["--to", destination]);
        await BeforeChange(cancellation);
        var result = await runner.RunAsync(arguments, cancellation);
        if (result.TryGetProperty("skipped", out var skipped) && skipped.GetArrayLength() != 0)
            throw new PecoFenceException(Loc.T("pecoPartialMove"), uncertain: true);
    }

    public async Task RestoreAsync(string snapshot, CancellationToken cancellation = default)
    {
        snapshot = Id(snapshot);
        await BeforeChange(cancellation);
        await runner.RunAsync(["snapshot", "restore", snapshot], cancellation);
    }

    public Task<JsonElement> ShowAsync(bool shown, CancellationToken cancellation = default) =>
        runner.RunAsync(["fence", shown ? "show-all" : "hide-all"], cancellation);

    public Task<JsonElement> OpenOptionsAsync(string fence, CancellationToken cancellation = default) =>
        runner.RunAsync(["fence", "open-options", Id(fence)], cancellation);

    static T? Read<T>(JsonElement json) => json.Deserialize<T>(Json.Options);
    static string Id(string value) => Guid.TryParse(value, out var id) ? id.ToString() : throw new PecoFenceException(Loc.T("pecoInvalidSelection"));
    static string Name(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new PecoFenceException(Loc.T("pecoNameRequired"));
        return name;
    }
}
