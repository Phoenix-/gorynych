using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// One <c>claude -p</c> process in stream-json mode (see docs/adapters.md): JSON lines in on stdin,
/// JSON lines out on stdout. The adapter's own control requests are matched with their responses
/// here; every other line goes to the message handler, one at a time, in the order Claude wrote
/// them.
/// </summary>
internal sealed class ClaudeProcess : IAsyncDisposable
{
    readonly Process process;
    readonly Stream stdin;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> pending = new(StringComparer.Ordinal);
    readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Func<JsonObject, Task> onMessage;
    readonly Action<string> log;
    readonly StringBuilder stderrTail = new();
    readonly Lock stderrGate = new();
    readonly string? stderrLog;
    long counter;
    bool inputClosed;

    const int StderrTailChars = 2000;
    const long StderrLogLimit = 1024 * 1024;

    ClaudeProcess(Process process, Func<JsonObject, Task> onMessage, Action<string> log, string? stderrLog)
    {
        this.process = process;
        this.onMessage = onMessage;
        this.log = log;
        this.stderrLog = stderrLog;
        stdin = process.StandardInput.BaseStream;
    }

    /// <summary>Starts <paramref name="exe"/> with <paramref name="args"/> in <paramref name="cwd"/>, with exactly <paramref name="environment"/>.</summary>
    /// <param name="stderrLog">A file for Claude's stderr (kept up to 1 MB), or null.</param>
    public static ClaudeProcess Start(string exe, IEnumerable<string> args, string cwd, IReadOnlyDictionary<string, string> environment,
        string? stderrLog, Func<JsonObject, Task> onMessage, Action<string> log)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment.Clear();
        foreach (var (k, v) in environment) info.Environment[k] = v;
        if (stderrLog is not null)
            try { File.WriteAllText(stderrLog, ""); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { stderrLog = null; }
        var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} did not start");
        var claude = new ClaudeProcess(process, onMessage, log, stderrLog);
        // Blocking reads for the life of the process: threads of their own, so sessions never starve the pool.
        var reading = Task.Factory.StartNew(claude.ReadOutputAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _ = Task.Factory.StartNew(claude.ReadErrors, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = claude.WatchExitAsync(reading);
        return claude;
    }

    public int Id => process.Id;

    /// <summary>Completes with the exit code once the process has exited and its output is read.</summary>
    public Task<int> Exited => exited.Task;

    /// <summary>The end of what Claude wrote to stderr, for an error message.</summary>
    public string StderrTail
    {
        get { lock (stderrGate) return stderrTail.ToString().Trim(); }
    }

    async Task ReadOutputAsync()
    {
        try
        {
            using var reader = process.StandardOutput;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException)
                {
                    log($"claude {process.Id}: not JSON: {Shorten(line)}");
                    continue;
                }
                if (message is null) continue;
                if ((string?)message["type"] == "control_response" && message["response"] is JsonObject response
                    && (string?)response["request_id"] is { } id && pending.TryRemove(id, out var waiting))
                {
                    waiting.TrySetResult(response);
                    continue;
                }
                try { await onMessage(message); }
                catch (Exception e) { log($"claude {process.Id}: handling {(string?)message["type"]} failed: {e.Message}"); }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>
    /// Completes <see cref="Exited"/> when the process has exited and its output is read, or 2 s
    /// after the exit: a grandchild that inherited stdout must not keep the session "running".
    /// </summary>
    async Task WatchExitAsync(Task reading)
    {
        int code;
        try
        {
            await process.WaitForExitAsync();
            code = process.ExitCode;
        }
        catch (InvalidOperationException) { code = -1; }
        await Task.WhenAny(reading, Task.Delay(2000));
        foreach (var id in pending.Keys.ToList())
            if (pending.TryRemove(id, out var waiting))
                waiting.TrySetException(new IOException($"Claude exited with code {code}"));
        exited.TrySetResult(code);
    }

    void ReadErrors()
    {
        try
        {
            var buffer = new char[4096];
            int n;
            using var reader = process.StandardError;
            while ((n = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var text = new string(buffer, 0, n);
                lock (stderrGate)
                {
                    stderrTail.Append(text);
                    if (stderrTail.Length > StderrTailChars) stderrTail.Remove(0, stderrTail.Length - StderrTailChars);
                }
                if (stderrLog is not null)
                    try
                    {
                        if (new FileInfo(stderrLog).Length < StderrLogLimit) File.AppendAllText(stderrLog, text);
                    }
                    catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Writes one JSON line to Claude's stdin.</summary>
    public async Task WriteAsync(JsonObject message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeGate.WaitAsync(ct);
        try
        {
            if (inputClosed) throw new IOException("Claude's input is closed");
            await stdin.WriteAsync(bytes, ct);
            await stdin.FlushAsync(ct);
        }
        finally { writeGate.Release(); }
    }

    /// <summary>
    /// Sends a control request (<c>{type: "control_request", request_id, request}</c>) and returns the
    /// <c>response</c> of its success; an error response throws <see cref="ClaudeControlException"/>.
    /// </summary>
    public async Task<JsonObject> ControlAsync(JsonObject request, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = "g" + Interlocked.Increment(ref counter);
        var waiting = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiting;
        try
        {
            await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }, ct);
            var response = await waiting.Task.WaitAsync(timeout, ct);
            if ((string?)response["subtype"] == "error")
                throw new ClaudeControlException($"{(string?)request["subtype"]}: {(string?)response["error"] ?? "failed"}");
            return response["response"] as JsonObject ?? [];
        }
        finally { pending.TryRemove(id, out _); }
    }

    /// <summary>Answers a control request Claude sent (<c>can_use_tool</c>, …).</summary>
    public Task RespondAsync(string requestId, JsonObject response) =>
        WriteAsync(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
        });

    /// <summary>Refuses a control request Claude sent.</summary>
    public Task RespondErrorAsync(string requestId, string error) =>
        WriteAsync(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error },
        });

    /// <summary>Closes stdin, which ends Claude once its turn is over.</summary>
    public async Task CloseInputAsync()
    {
        await writeGate.WaitAsync();
        try
        {
            if (inputClosed) return;
            inputClosed = true;
            try { stdin.Close(); }
            catch (IOException) { }
        }
        finally { writeGate.Release(); }
    }

    public void Kill()
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Closes stdin, gives Claude <paramref name="grace"/> to exit, then kills its process tree.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        await CloseInputAsync();
        if (await Task.WhenAny(exited.Task, Task.Delay(grace)) != exited.Task)
        {
            Kill();
            await Task.WhenAny(exited.Task, Task.Delay(5000));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!exited.Task.IsCompleted) await StopAsync(TimeSpan.FromSeconds(5));
        process.Dispose();
    }

    static string Shorten(string s) => s.Length <= 200 ? s : s[..200] + "…";
}

/// <summary>Claude answered a control request with an error.</summary>
internal sealed class ClaudeControlException(string message) : Exception(message);
