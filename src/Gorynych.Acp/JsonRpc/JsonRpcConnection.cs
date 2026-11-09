using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Acp.JsonRpc;

/// <summary>An incoming JSON-RPC request.</summary>
public sealed record JsonRpcRequest(JsonNode Id, string Method, JsonNode? Params)
{
    /// <summary>
    /// The position of this message among everything received on the connection, so a
    /// handler can tell whether a notification (such as <c>session/cancel</c>) came after it.
    /// </summary>
    public long Sequence { get; init; }
}

/// <summary>A JSON-RPC error, thrown by a request handler or received as a response.</summary>
public sealed class JsonRpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
    public const int AuthRequired = -32000;
    public const int ResourceNotFound = -32002;
}

/// <summary>
/// JSON-RPC 2.0 over a duplex byte stream, one message per line (the ACP stdio framing).
/// Requests from the peer run concurrently; notifications are handled in arrival order,
/// before any later response completes, so a prompt's updates always precede its result.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    static readonly JsonSerializerOptions WireOptions = AcpJson.TextOptions;
    static readonly byte[] NewLine = [(byte)'\n'];

    readonly Stream input;
    readonly Stream output;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> incoming = new();
    readonly CancellationTokenSource lifetime = new();
    // lifetime's token, taken once: a write may still run while DisposeAsync disposes lifetime,
    // whose Token then throws.
    readonly CancellationToken stopping;
    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long nextId;
    long received;
    Task? readLoop;

    public JsonRpcConnection(Stream input, Stream output, string name)
    {
        this.input = input;
        this.output = output;
        Name = name;
        stopping = lifetime.Token;
    }

    /// <summary>A label for logs.</summary>
    public string Name { get; }

    /// <summary>Handles requests from the peer. Return the result, or throw <see cref="JsonRpcException"/>.</summary>
    public Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>Handles notifications from the peer, one at a time in arrival order.</summary>
    public Func<string, JsonNode?, Task>? NotificationHandler { get; set; }

    /// <summary>Receives every line sent ("&gt;") and received ("&lt;"), for logs and fixtures.</summary>
    public Action<string, string>? Trace { get; set; }

    /// <summary>Receives handler failures and malformed input.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// The notification that cancels a request, sent and received with its <c>requestId</c>:
    /// ACP's <c>$/cancel_request</c> by default; MCP calls it <c>notifications/cancelled</c>.
    /// </summary>
    public string CancelNotification { get; init; } = AcpMethods.Protocol.CancelRequest;

    /// <summary>The <see cref="JsonRpcRequest.Sequence"/> position of the notification being handled.</summary>
    public long NotificationSequence { get; private set; }

    /// <summary>Completes when the peer closes the stream or the connection is disposed.</summary>
    public Task Completion => closed.Task;

    public bool IsClosed => closed.Task.IsCompleted;

    public void Start() => readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct = default) =>
        await await StartRequestAsync(method, @params, ct);

    /// <summary>
    /// Sends a request and completes once it is written, returning the task of its result.
    /// Lets a caller order the request against later messages without waiting for the answer.
    /// </summary>
    public async Task<Task<JsonNode?>> StartRequestAsync(string method, JsonNode? @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        if (IsClosed) { pending.TryRemove(id, out _); throw new IOException($"{Name}: connection closed"); }
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) message["params"] = @params;
        var registration = ct.Register(() =>
        {
            if (pending.TryRemove(id, out var p) && p.TrySetCanceled(ct))
                _ = SendNotificationAsync(CancelNotification, new JsonObject { ["requestId"] = id });
        });
        _ = tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        try
        {
            await WriteAsync(message);
        }
        catch (Exception e)
        {
            if (pending.TryRemove(id, out _)) tcs.TrySetException(e);
            throw;
        }
        return tcs.Task;
    }

    public Task SendNotificationAsync(string method, JsonNode? @params)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) message["params"] = @params;
        return WriteAsync(message);
    }

    /// <summary>
    /// Writes one message. On a connection that is closed, or closes meanwhile, it fails with an
    /// <see cref="IOException"/>, never anything else: callers take that as "the peer is gone" (the
    /// service then asks another client), so a write that races <see cref="DisposeAsync"/> must not
    /// surface as a cancellation or a disposed object.
    /// </summary>
    async Task WriteAsync(JsonObject message)
    {
        var text = message.ToJsonString(WireOptions);
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await writeGate.WaitAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"{Name}: connection closed");
        }
        try
        {
            if (IsClosed) throw new IOException($"{Name}: connection closed");
            await output.WriteAsync(bytes, stopping).ConfigureAwait(false);
            await output.WriteAsync(NewLine, stopping).ConfigureAwait(false);
            await output.FlushAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"{Name}: connection closed");
        }
        catch (Exception e) when (e is not IOException)
        {
            throw new IOException($"{Name}: write failed", e);
        }
        finally
        {
            writeGate.Release();
        }
        Trace?.Invoke(">", text);
    }

    async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
            while (!stopping.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(stopping).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) continue;
                Trace?.Invoke("<", line);
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { Log?.Invoke($"{Name}: unparsable line: {Truncate(line)}"); continue; }
                if (message is null) { Log?.Invoke($"{Name}: not a JSON-RPC object: {Truncate(line)}"); continue; }
                await DispatchAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log?.Invoke($"{Name}: read loop failed: {e.Message}"); }
        finally
        {
            Close();
        }
    }

    async Task DispatchAsync(JsonObject message)
    {
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        var id = message["id"];
        var sequence = ++received;
        if (method is null)
        {
            CompleteResponse(id, message);
            return;
        }
        var @params = message["params"];
        @params?.Parent?.AsObject().Remove("params");
        if (id is null)
        {
            if (method == CancelNotification)
            {
                var key = @params?["requestId"]?.ToJsonString();
                if (key is not null && incoming.TryGetValue(key, out var cts))
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
            if (NotificationHandler is null) return;
            NotificationSequence = sequence;
            try { await NotificationHandler(method, @params).ConfigureAwait(false); }
            catch (Exception e) { Log?.Invoke($"{Name}: notification {method} failed: {e}"); }
            return;
        }
        id = id.DeepClone();
        var request = new JsonRpcRequest(id, method, @params) { Sequence = sequence };
        // Registered here, before the next message is read, so a cancel right behind the request finds it.
        var requestKey = id.ToJsonString();
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        incoming[requestKey] = requestCts;
        _ = Task.Run(() => HandleRequestAsync(request, requestKey, requestCts));
    }

    void CompleteResponse(JsonNode? id, JsonObject message)
    {
        long key;
        if (id is JsonValue v && (v.TryGetValue(out key) || (v.TryGetValue<string>(out var str) && long.TryParse(str, out key))))
        {
            if (!pending.TryRemove(key, out var tcs)) return;
            if (message["error"] is JsonObject error)
            {
                var code = error["code"] is JsonValue c && c.TryGetValue<int>(out var ci) ? ci : JsonRpcException.InternalError;
                var text = error["message"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : "error";
                tcs.TrySetException(new JsonRpcException(code, text, error["data"]?.DeepClone()));
            }
            else
            {
                var result = message["result"];
                message.Remove("result");
                tcs.TrySetResult(result);
            }
        }
        else Log?.Invoke($"{Name}: response with unknown id {id?.ToJsonString()}");
    }

    async Task HandleRequestAsync(JsonRpcRequest request, string key, CancellationTokenSource cts)
    {
        using var owned = cts;
        JsonObject response;
        try
        {
            if (RequestHandler is null) throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {request.Method}");
            var result = await RequestHandler(request, cts.Token).ConfigureAwait(false);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id.DeepClone(), ["result"] = result ?? new JsonObject() };
        }
        catch (Exception e)
        {
            var (code, text, data) = e switch
            {
                JsonRpcException j => (j.Code, j.Message, j.ErrorData?.DeepClone()),
                OperationCanceledException => (JsonRpcException.RequestCancelled, "Request cancelled", null),
                JsonException j => (JsonRpcException.InvalidParams, j.Message, null),
                _ => (JsonRpcException.InternalError, e.Message, (JsonNode?)null),
            };
            if (e is not JsonRpcException and not OperationCanceledException)
                Log?.Invoke($"{Name}: request {request.Method} failed: {e}");
            var error = new JsonObject { ["code"] = code, ["message"] = text };
            if (data is not null) error["data"] = data;
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request.Id.DeepClone(), ["error"] = error };
        }
        finally
        {
            incoming.TryRemove(key, out _);
        }
        try { await WriteAsync(response).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    void Close()
    {
        if (!closed.TrySetResult()) return;
        foreach (var (id, tcs) in pending)
            if (pending.TryRemove(id, out _)) tcs.TrySetException(new IOException($"{Name}: connection closed"));
        foreach (var (_, cts) in incoming)
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    int disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Close();
        lifetime.Cancel();
        try { output.Dispose(); } catch { }
        try { input.Dispose(); } catch { }
        if (readLoop is not null)
            try { await readLoop.ConfigureAwait(false); } catch { }
        lifetime.Dispose();
    }

    static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
