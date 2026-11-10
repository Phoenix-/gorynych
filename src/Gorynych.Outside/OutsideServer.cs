using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Outside;

/// <summary>
/// The stdio MCP server for agents outside Gorynych. It keeps no state of its own beyond a legacy
/// host's <c>clientInfo</c>: every request goes to the service as <c>_gorynych/mcp_message</c> on a
/// new pipe connection, and the service's answer goes back unchanged. It answers by itself only
/// <c>ping</c> and a legacy <c>initialize</c>; inside Gorynych it offers no tools; and while the
/// service cannot be reached it answers from its own copy of the tools.
/// </summary>
internal sealed class OutsideServer(string pipe, string serviceExe, bool inside, string version)
{
    static readonly TimeSpan StartRetry = TimeSpan.FromMinutes(1);
    readonly string folder = Environment.CurrentDirectory;
    readonly string? hostSessionId = Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID");
    readonly SemaphoreSlim startGate = new(1, 1);
    long lastStart = long.MinValue / 2;
    string? lastStartFailure;
    JsonObject? legacyClient;

    /// <summary>Serves the host until it closes stdin. Never exits on an error: a host does not restart a server that died.</summary>
    public async Task RunAsync(Stream input, Stream output)
    {
        var host = new JsonRpcConnection(input, output, "outside-host")
        {
            // MCP's cancellation; the forwarded request is then cancelled with ACP's $/cancel_request.
            CancelNotification = "notifications/cancelled",
            Log = OutsideLog.Warn,
        };
        host.RequestHandler = (request, ct) => HandleAsync(host, request, ct);
        host.NotificationHandler = (_, _) => Task.CompletedTask; // notifications/initialized and the like
        host.Start();
        await host.Completion;
    }

    async Task<JsonNode?> HandleAsync(JsonRpcConnection host, JsonRpcRequest request, CancellationToken ct)
    {
        var @params = request.Params as JsonObject;
        var modern = OutsideTools.ModernRequestVersion(@params) is not null;
        switch (request.Method)
        {
            case "ping":
                return OutsideTools.Complete([], modern);
            case "initialize":
                // A legacy host (MCP 2025-11-25 and earlier); a modern one never sends it.
                legacyClient = @params?["clientInfo"]?.DeepClone() as JsonObject;
                return OutsideTools.Initialize(@params, version, inside);
        }
        if (inside) return Local(request.Method, @params, inside: true, serviceDown: null);
        try
        {
            return await ForwardAsync(host, request, @params, ct);
        }
        catch (ServiceUnreachableException e)
        {
            return Local(request.Method, @params, inside: false, serviceDown: e.Message);
        }
        catch (ServiceLostException)
        {
            // The service went once the call was on its way (a restart): it may have been done, or not.
            var target = (string?)@params?["arguments"]?["session_id"];
            var text = (string?)@params?["name"] == "ask_agent"
                ? $"Gorynych restarted while session_id {target} was answering, so its answer was lost. Ask again in a moment: " +
                  $"ask_agent with session_id {target} and the same question."
                : "Gorynych restarted while it handled this call, so its answer was lost. Call the tool again in a moment.";
            if (request.Method == "tools/call") return OutsideTools.ToolResult(text, isError: true, modern);
            throw new JsonRpcException(JsonRpcException.InternalError, text);
        }
    }

    /// <summary>The answers from the server's own copy: inside Gorynych, and while the service cannot be reached.</summary>
    JsonNode Local(string method, JsonObject? @params, bool inside, string? serviceDown)
    {
        var requested = OutsideTools.ModernRequestVersion(@params);
        var modern = requested is not null;
        if (modern && requested != OutsideTools.ModernVersion) throw OutsideTools.Unsupported(requested!);
        return method switch
        {
            "server/discover" => OutsideTools.Discover(version, inside),
            "tools/list" => OutsideTools.ToolsList(modern, inside),
            "tools/call" => OutsideTools.ToolResult(inside ? OutsideTools.InsideGorynych : OutsideTools.ServiceDown(serviceDown!), isError: true, modern),
            _ when inside => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}"),
            _ => throw new JsonRpcException(JsonRpcException.InternalError, OutsideTools.ServiceDown(serviceDown!)),
        };
    }

    async Task<JsonNode?> ForwardAsync(JsonRpcConnection host, JsonRpcRequest request, JsonObject? @params, CancellationToken ct)
    {
        // The host's id as a string; the answer goes back under the host's own id.
        var requestId = request.Id is JsonValue v && v.TryGetValue<string>(out var text) ? text : request.Id.ToJsonString();
        var active = 1;
        // A notification of this request (progress) goes to the host while the request is active, and never after.
        async Task Notified(string method, JsonNode? n)
        {
            if (method != GorynychProtocol.MethodMcpMessage || Volatile.Read(ref active) == 0) return;
            if ((string?)n?["serverId"] != OutsideTools.ServerName || (string?)n?["requestId"] != requestId || (string?)n?["method"] is not { } inner) return;
            await host.SendNotificationAsync(inner, n?["params"]?.DeepClone());
        }
        await using var service = await ConnectAsync(Notified, ct);
        var envelope = new JsonObject
        {
            ["serverId"] = OutsideTools.ServerName,
            ["requestId"] = requestId,
            ["method"] = request.Method,
            ["params"] = @params?.DeepClone(),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaOutside] = HostObject(@params) },
        };
        JsonNode? answer;
        try
        {
            Task<JsonNode?> sent;
            try
            {
                sent = await service.StartRequestAsync(GorynychProtocol.MethodMcpMessage, envelope, ct);
            }
            catch (IOException e)
            {
                throw new ServiceUnreachableException($"the connection to it broke ({e.Message})");
            }
            try
            {
                answer = await sent;
            }
            catch (IOException e)
            {
                throw new ServiceLostException(e.Message);
            }
        }
        finally
        {
            Volatile.Write(ref active, 0);
        }
        if (answer?["error"] is JsonObject error)
            throw new JsonRpcException((int?)error["code"] ?? JsonRpcException.InternalError, (string?)error["message"] ?? "error", error["data"]?.DeepClone());
        return answer?["result"]?.DeepClone();
    }

    /// <summary>The host as the service sees it: its MCP client, the folder it runs in, and Claude Code's session ID.</summary>
    JsonObject HostObject(JsonObject? @params)
    {
        var host = new JsonObject();
        if ((@params?["_meta"]?[OutsideTools.MetaClientInfo] ?? legacyClient)?.DeepClone() is { } client) host["client"] = client;
        host["folder"] = folder;
        if (!string.IsNullOrEmpty(hostSessionId)) host["hostSessionId"] = hostSessionId;
        return host;
    }

    async Task<JsonRpcConnection> ConnectAsync(Func<string, JsonNode?, Task> notifications, CancellationToken ct)
    {
        var stream = await TryConnectAsync(TimeSpan.FromMilliseconds(300), ct) ?? await StartServiceAsync(ct);
        var rpc = new JsonRpcConnection(stream, stream, "outside->service") { NotificationHandler = notifications, Log = OutsideLog.Warn };
        rpc.Start();
        try
        {
            await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
            {
                ProtocolVersion = AcpMethods.ProtocolVersion,
                ClientInfo = new Implementation { Name = OutsideTools.ServerName, Version = version },
            }, ct);
            return rpc;
        }
        catch (Exception e)
        {
            // Whatever ended it (the host's cancellation too), the pipe and its read loop go with it.
            await rpc.DisposeAsync();
            if (e is IOException or JsonRpcException or System.Text.Json.JsonException)
                throw new ServiceUnreachableException($"it did not answer ({e.Message})");
            throw;
        }
    }

    /// <summary>
    /// Starts the service as the stdio bridge does (<see cref="ServiceLaunch.Decide"/>), at most once
    /// a minute, and waits for its pipe. A start that cannot break away from the host's job is
    /// logged: closing the host may then stop the service with every session.
    /// </summary>
    async Task<NamedPipeClientStream> StartServiceAsync(CancellationToken ct)
    {
        await startGate.WaitAsync(ct);
        try
        {
            if (await TryConnectAsync(TimeSpan.FromMilliseconds(300), ct) is { } ready) return ready; // another request started it
            if (Environment.TickCount64 - lastStart < (long)StartRetry.TotalMilliseconds)
                throw new ServiceUnreachableException(lastStartFailure ?? "it was started less than a minute ago and has not opened its pipe");
            lastStart = Environment.TickCount64;
            try
            {
                NamedPipeClientStream stream;
                switch (ServiceLaunch.Decide(LogonTask.Exists(pipe), ServiceLaunch.IsRemoteLogon()))
                {
                    case ServiceLaunch.Way.Refuse:
                        throw new InvalidOperationException(ServiceLaunch.Refusal(pipe, pipe == GorynychProtocol.DefaultPipeName));
                    case ServiceLaunch.Way.RunTask:
                        OutsideLog.Info($"starting the service with the logon task {LogonTask.Name(pipe)}");
                        if (LogonTask.Run(pipe) is { } failure)
                            throw new InvalidOperationException($"the logon task {LogonTask.Name(pipe)} did not start: {failure}");
                        stream = await TryConnectAsync(TimeSpan.FromSeconds(20), ct) ?? throw new TimeoutException(ServiceLaunch.TaskTimeout(pipe, 20));
                        break;
                    default:
                        if (!File.Exists(serviceExe)) throw new FileNotFoundException($"{serviceExe} does not exist");
                        OutsideLog.Info($"starting the service: {serviceExe} --pipe {pipe}");
                        if (!DetachedProcess.Start(serviceExe, ["--pipe", pipe]))
                            OutsideLog.Warn("the host's job object forbids breaking away, so the service started inside it: " +
                                "closing the host may stop the service and every session");
                        stream = await TryConnectAsync(TimeSpan.FromSeconds(20), ct) ?? throw new TimeoutException("it did not open its pipe within 20 seconds");
                        break;
                }
                lastStartFailure = null;
                return stream;
            }
            catch (OperationCanceledException)
            {
                // The host gave up on this request while the service was starting: the next request
                // connects to it, or starts it again, instead of waiting out the minute.
                lastStart = long.MinValue / 2;
                throw;
            }
            catch (Exception e)
            {
                lastStartFailure = e.Message;
                OutsideLog.Warn($"cannot start the service: {e.Message}");
                throw new ServiceUnreachableException(e.Message);
            }
        }
        finally
        {
            startGate.Release();
        }
    }

    Task<NamedPipeClientStream?> TryConnectAsync(TimeSpan timeout, CancellationToken ct) => ServicePipe.ConnectAsync(pipe, timeout, ct);

    sealed class ServiceUnreachableException(string reason) : Exception(reason);

    /// <summary>The connection to the service went once the request was on its way: a restart, most likely.</summary>
    sealed class ServiceLostException(string reason) : Exception(reason);
}
