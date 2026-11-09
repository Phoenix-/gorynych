using System.Diagnostics;

namespace Gorynych.Adapters.Codex;

/// <summary>
/// Tells when another Codex app takes a thread or lets it go, while the adapter runs
/// (docs/adapters.md, "One writer"). It watches Codex's lock folder: a thread's lock file appears
/// when an app takes it and is usually deleted when it lets go. Once a burst of changes settles,
/// the holders of the threads they touched are found again; the threads last seen held are looked
/// at again on a timer too, for a holder that went away without its file going (it crashed) and
/// for changes the watcher missed. Only a real change of holder is reported. Fail-open: with no
/// folder nothing is watched until it appears, and a watcher that fails leaves the timer alone.
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
    readonly HashSet<string> touched = new(StringComparer.Ordinal);
    readonly SemaphoreSlim signal = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    FileSystemWatcher? watcher;

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
                if (found.TryGetValue(id, out var holder)) told[id] = holder;
                else told.Remove(id);
    }

    async Task RunAsync()
    {
        var due = Stopwatch.GetTimestamp() + (long)(recheck.TotalSeconds * Stopwatch.Frequency);
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
            List<string> ids;
            if (touchedNow)
            {
                while (signal.CurrentCount > 0) signal.Wait(0);
                lock (gate)
                {
                    ids = [.. touched];
                    touched.Clear();
                }
            }
            else
            {
                lock (gate) ids = [.. told.Keys];
                due = Stopwatch.GetTimestamp() + (long)(recheck.TotalSeconds * Stopwatch.Frequency);
            }
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
            return; // not known: nothing to tell
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

    /// <summary>Starts watching once the folder exists; any failure leaves the timer to it.</summary>
    void Watch()
    {
        if (Volatile.Read(ref watcher) is not null || directory is null) return;
        try
        {
            if (!Directory.Exists(directory)) return;
            var w = new FileSystemWatcher(directory, "*.lock") { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            w.Created += (_, e) => Touch(e.Name);
            w.Deleted += (_, e) => Touch(e.Name);
            w.Renamed += (_, e) =>
            {
                Touch(e.OldName);
                Touch(e.Name);
            };
            w.Error += (_, e) => Lost(e.GetException());
            w.EnableRaisingEvents = true;
            Volatile.Write(ref watcher, w);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"codex: not watching {directory}: {e.Message}");
        }
    }

    void Touch(string? name)
    {
        if (Path.GetFileNameWithoutExtension(name) is not { Length: > 0 } id) return;
        lock (gate) touched.Add(id);
        signal.Release();
    }

    /// <summary>The watcher lost events (its buffer overflowed) or failed: every thread told held is looked at now, and the folder watched again.</summary>
    void Lost(Exception e)
    {
        log?.Invoke($"codex: the lock folder's watcher failed, looking again: {e.Message}");
        Interlocked.Exchange(ref watcher, null)?.Dispose();
        lock (gate) touched.UnionWith(told.Keys);
        signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        Interlocked.Exchange(ref watcher, null)?.Dispose();
        try { await loop; }
        catch (OperationCanceledException) { }
        stop.Dispose();
        signal.Dispose();
    }
}
