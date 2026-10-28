using System.Runtime.InteropServices;
using System.Windows.Media;
using WinDock.Models;

namespace WinDock.Services;

// A Shell extension may block inside icon extraction and cannot be safely
// interrupted. Keep that work off the dispatcher and bound both workers and queue.
internal static class SearchIconLoader
{
    const int MaximumQueued = 128, MaximumCached = 256;
    static readonly object Gate = new();
    static readonly LinkedList<Job> Queue = [];
    static readonly Dictionary<IconKey, Job> Pending = [];
    static readonly Dictionary<IconKey, Cached> Cache = [];

    sealed record Cached(ImageSource? Image, long RetryAt);
    readonly record struct IconKey(string? Target, string? Icon, string? AppId, string? Arguments, bool Raw, int Size)
    {
        public PinItem ToPin() => new()
        {
            Target = Target ?? "", Icon = Icon, AppId = AppId, Args = Arguments,
            Favorites = Raw ? "" : null, Resolve = Raw ? "" : null
        };
    }

    sealed class Job(IconKey key)
    {
        public IconKey Key { get; } = key;
        public HashSet<CancellationToken> Interests { get; } = [];
        public TaskCompletionSource<ImageSource?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static SearchIconLoader()
    {
        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(Work) { IsBackground = true, Name = "Search icons " + (i + 1) };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }
    }

    public static Task<ImageSource?> ForPinAsync(PinItem pin, int size, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Capture values only; resolving file existence and Shell icons belongs to
        // a worker. The original launch target and arguments are never modified.
        var key = new IconKey(pin.ResolvedTarget, pin.ResolvedIcon, pin.AppId, pin.Args, pin.IsRaw, Math.Clamp(size, 16, 256));
        Job job;
        lock (Gate)
        {
            PruneQueued();
            if (Cache.TryGetValue(key, out var cached))
            {
                if (cached.Image is not null || Environment.TickCount64 < cached.RetryAt)
                    return Task.FromResult(cached.Image);
                Cache.Remove(key);
            }
            if (!Pending.TryGetValue(key, out job!))
            {
                if (Queue.Count >= MaximumQueued) return Task.FromResult<ImageSource?>(null);
                job = new Job(key);
                Pending.Add(key, job);
                Queue.AddLast(job);
            }
            job.Interests.RemoveWhere(token => token.IsCancellationRequested);
            job.Interests.Add(cancellation);
            Monitor.PulseAll(Gate);
        }
        // Cancellation detaches the row immediately, even when a Shell call is
        // still completing. Other interested rows can share the same request.
        return job.Completion.Task.WaitAsync(cancellation);
    }

    static void PruneQueued()
    {
        for (var node = Queue.First; node is not null;)
        {
            var next = node.Next;
            node.Value.Interests.RemoveWhere(token => token.IsCancellationRequested);
            if (node.Value.Interests.Count == 0)
            {
                Queue.Remove(node);
                Pending.Remove(node.Value.Key);
                node.Value.Completion.TrySetResult(null);
            }
            node = next;
        }
    }

    static void Work()
    {
        // ShellIcons starts with a native Shell call, which does not initialize
        // COM for us. Balance S_OK and S_FALSE on this same worker thread.
        var initialized = CoInitializeEx(0, 0x2 /* COINIT_APARTMENTTHREADED */) >= 0;
        try
        {
            while (true)
            {
                Job job;
                lock (Gate)
                {
                    PruneQueued();
                    while (Queue.Count == 0) Monitor.Wait(Gate);
                    PruneQueued();
                    if (Queue.First is null) continue;
                    job = Queue.First.Value;
                    Queue.RemoveFirst();
                }
                ImageSource? icon = null;
                try
                {
                    // An unavailable apartment still drains requests; rows keep
                    // their category glyph instead of waiting forever.
                    if (initialized) icon = ShellIcons.ForPin(job.Key.ToPin(), job.Key.Size);
                    if (icon is { IsFrozen: false })
                    {
                        if (icon.CanFreeze) icon.Freeze();
                        else icon = null;
                    }
                }
                catch (Exception e)
                {
                    icon = null;
                    System.Diagnostics.Trace.WriteLine("Search icon unavailable: " + e.GetType().Name);
                }
                lock (Gate)
                {
                    Pending.Remove(job.Key);
                    if (Cache.Count >= MaximumCached && !Cache.ContainsKey(job.Key)) Cache.Remove(Cache.Keys.First());
                    Cache[job.Key] = new Cached(icon, Environment.TickCount64 + 5_000);
                    job.Completion.TrySetResult(icon);
                }
            }
        }
        finally { if (initialized) CoUninitialize(); }
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
}
