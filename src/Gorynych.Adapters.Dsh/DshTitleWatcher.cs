namespace Gorynych.Adapters.Dsh;

/// <summary>
/// Follows DSH's projection cache, read only, for the native titles its own title service writes
/// there (docs/adapters.md, "DeepSeek Harness", "Titles"). ACP does not carry DSH titles, so the
/// adapter notices a title written after a turn and sends the service a standard
/// <c>session_info_update</c>. Polling the cache records is small (a handful of files), survives a
/// cache folder that appears late, and sees the atomic replacement DSH writes without ever touching
/// the cache itself. One bounded loop, stopped with the adapter.
/// </summary>
internal sealed class DshTitleWatcher(DshPins pins, Func<string[]> sessionIds, Func<string, string, Task<bool>> onTitle, TimeSpan interval, Action<string>? log)
{
    readonly CancellationTokenSource stop = new();
    Task? loop;
    int stopped;

    public void Start()
    {
        if (loop is not null) return;
        loop = Task.Run(() => RunAsync(stop.Token));
    }

    /// <summary>Stops the loop and waits for its last scan to finish; later calls do nothing.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
        {
            if (loop is not null)
                try { await loop; }
                catch (OperationCanceledException) { }
            return;
        }
        stop.Cancel();
        if (loop is not null)
            try { await loop; }
            catch (OperationCanceledException) { }
        stop.Dispose();
    }

    async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            await ScanAsync(ct);
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    async Task ScanAsync(CancellationToken ct)
    {
        // Follow only sessions the router has seen. The user's cache can contain years of
        // unrelated sessions; reading that entire directory every two seconds is unbounded.
        foreach (var id in sessionIds())
        {
            ct.ThrowIfCancellationRequested();
            var title = pins.ReadTitle(id);
            if (title is not { Length: > 0 }) continue;
            try
            {
                // The router owns deduplication, including titles returned by session/list.
                await onTitle(id, title).WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Text.Json.JsonException or InvalidOperationException)
            {
                log?.Invoke($"dsh {id}: the native title could not be announced: {e.Message}");
            }
        }
    }
}
