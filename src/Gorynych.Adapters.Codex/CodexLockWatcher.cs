using System.Diagnostics;

namespace Gorynych.Adapters.Codex;

/// <summary>
/// Tells when another Codex app takes a thread or lets it go, while the adapter runs
/// (docs/adapters.md, "One writer"). It watches Codex's lock folder: a thread's lock file appears
/// when an app takes it and is usually deleted when it lets go. Once a burst of changes settles,
/// the holders of the threads they touched are found again; the threads last seen held are looked
/// at again on a timer too, for a holder that went away without its file going (it crashed). When
/// the watcher may have missed something (its buffer overflowed, it was made again), every thread
/// seen is looked at. Only a real change of holder is reported. Fail-open: with no folder nothing
/// is watched until it appears, a watcher that fails leaves the timer alone, and threads whose
/// holders could not be found are looked at again on the next tick.
/// </summary>
internal sealed class CodexLockWatcher : IAsyncDisposable
{
    readonly string? directory;
    readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, string>> holders;
    readonly Func<string, Task> report;
    readonly TimeSpan settle;
    readonly TimeSpan recheck;
    readonly Action<string>? log;
    readonly Lock gate = new();
    readonly Dictionary<string, string> told = new(StringComparer.Ordinal); // the holder clients were last told, per thread
    readonly HashSet<string> seen = new(StringComparer.Ordinal); // every thread a list or an open showed
    readonly HashSet<string> touched = new(StringComparer.Ordinal);
    // Never disposed: a callback of the watcher that is already queued may still release it.
    readonly SemaphoreSlim signal = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    FileSystemWatcher? watcher; // under gate
    bool disposed; // under gate
    int disposing;

    /// <param name="report">Says that a thread's holder changed (the service then tells its clients).</param>
    public CodexLockWatcher(string? directory, Func<IEnumerable<string>, IReadOnlyDictionary<string, string>> holders,
        Func<string, Task> report, TimeSpan settle, TimeSpan recheck, Action<string>? log)
    {
        this.directory = directory;
        this.holders = holders;
        this.report = report;
        this.settle = settle;
        this.recheck = recheck;
        this.log = log;
        loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// What a list or an open just found for these threads, as the client now sees it: a later
    /// change is told against it, and a thread seen held is looked at again on the timer.
    /// </summary>
    public void Saw(IEnumerable<string> threadIds, IReadOnlyDictionary<string, string> found)
    {
        lock (gate)
            foreach (var id in threadIds)
            {
                seen.Add(id);
                if (found.TryGetValue(id, out var holder)) told[id] = holder;
                else told.Remove(id);
            }
    }

    long Due() => Stopwatch.GetTimestamp() + (long)(recheck.TotalSeconds * Stopwatch.Frequency);

    async Task RunAsync()
    {
        var due = Due();
        while (!stop.IsCancellationRequested)
        {
            Watch();
            var left = TimeSpan.FromSeconds(Math.Max(0, due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            bool touchedNow;
            try
            {
                touchedNow = await signal.WaitAsync(left, stop.Token);
                if (touchedNow) await Task.Delay(settle, stop.Token); // the rest of the burst
            }
            catch (OperationCanceledException)
            {
                return;
            }
            while (signal.CurrentCount > 0) signal.Wait(0);
            List<string> ids;
            lock (gate)
            {
                // A tick also takes up what is still to do: threads whose holders could not be found before.
                ids = touchedNow ? [.. touched] : [.. touched.Union(told.Keys)];
                touched.Clear();
            }
            if (!touchedNow) due = Due();
            if (ids.Count > 0) await ReportAsync(ids);
        }
    }

    async Task ReportAsync(List<string> ids)
    {
        IReadOnlyDictionary<string, string> found;
        try
        {
            found = holders(ids);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Not known now: looked at again on the next tick.
            lock (gate) touched.UnionWith(ids);
            return;
        }
        var changed = new List<string>();
        lock (gate)
            foreach (var id in ids)
            {
                var before = told.GetValueOrDefault(id);
                var after = found.GetValueOrDefault(id);
                if (before == after) continue;
                if (after is null) told.Remove(id);
                else told[id] = after;
                changed.Add(id);
            }
        foreach (var id in changed)
        {
            try
            {
                await report(id);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log?.Invoke($"codex: could not tell that thread {id} changed hands: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Keeps a working watcher, and makes one when there is none or the one there stopped (its
    /// folder went, which it may not report); after making one, every thread seen is looked at,
    /// since changes before it were not watched. Any failure leaves the timer to it.
    /// </summary>
    void Watch()
    {
        if (directory is null) return;
        FileSystemWatcher? current;
        lock (gate) current = watcher;
        try
        {
            var there = Directory.Exists(directory);
            if (current is not null)
            {
                if (there && current.EnableRaisingEvents) return;
                lock (gate) if (watcher == current) watcher = null;
                current.Dispose();
            }
            if (!there) return;
            var w = new FileSystemWatcher(directory, "*.lock")
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Touch(e.Name);
            w.Deleted += (_, e) => Touch(e.Name);
            w.Renamed += (_, e) =>
            {
                Touch(e.OldName);
                Touch(e.Name);
            };
            w.Error += (_, e) => Missed(e.GetException());
            w.EnableRaisingEvents = true;
            lock (gate)
            {
                if (disposed)
                {
                    w.Dispose();
                    return;
                }
                watcher = w;
                touched.UnionWith(seen);
                touched.UnionWith(told.Keys);
            }
            signal.Release();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"codex: not watching {directory}: {e.Message}");
        }
    }

    void Touch(string? name)
    {
        if (Path.GetFileNameWithoutExtension(name) is not { Length: > 0 } id) return;
        lock (gate)
        {
            if (disposed) return;
            touched.Add(id);
        }
        signal.Release();
    }

    /// <summary>
    /// The watcher lost events (its buffer overflowed) or failed: every thread seen is looked at
    /// now. A watcher that still runs is kept; the next round makes a new one if it stopped.
    /// </summary>
    void Missed(Exception e)
    {
        lock (gate)
        {
            if (disposed) return;
            touched.UnionWith(seen);
            touched.UnionWith(told.Keys);
        }
        log?.Invoke($"codex: the lock folder's watcher missed changes, looking at every thread: {e.Message}");
        signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0) return;
        FileSystemWatcher? w;
        lock (gate)
        {
            disposed = true;
            w = watcher;
            watcher = null;
        }
        w?.Dispose();
        stop.Cancel();
        try
        {
            await loop;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"codex: the lock folder's watcher had failed: {e.Message}");
        }
        stop.Dispose();
    }
}
