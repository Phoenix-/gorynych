using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// The <c>gorynych</c> stdio MCP server agents start from their <c>mcpServers</c>:
/// <c>Gorynych.Service peers [--pipe P --token T] [--channel-url U]</c>.
/// <list type="bullet">
/// <item>With a pipe and token it offers <c>list_agents</c>, <c>ask_agent</c> and
/// <c>spawn_session</c>, which go to the service; the token tells the service which session is
/// asking.</item>
/// <item>With a channel URL (Claude Code) it is also a development channel: it declares
/// <c>claude/channel</c>, long-polls the Claude adapter and pushes
/// <c>notifications/claude/channel</c>.</item>
/// <item><c>reply</c> answers an opinion request: into the channel when there is one,
/// otherwise to the service (<c>_gorynych/reply</c>).</item>
/// </list>
/// </summary>
public static class PeersServer
{
    public const string ServerName = "gorynych";

    public static async Task<int> RunAsync(string? pipe, string? token, string? channelUrl)
    {
        var peers = pipe is not null && token is not null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // A cancelled tool call (the agent's turn was stopped) cancels the request to the service,
        // which withdraws what it waits for, such as the person's answer to a spawn.
        var mcp = new JsonRpcConnection(Console.OpenStandardInput(), Console.OpenStandardOutput(), "gorynych-mcp")
        {
            CancelNotification = "notifications/cancelled",
        };
        mcp.RequestHandler = async (req, ct) => req.Method switch
        {
            "initialize" => Initialize(req, peers, channelUrl is not null),
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = new JsonArray(Tools(peers)) },
            "tools/call" => await CallAsync(http, pipe, token, channelUrl, (string?)req.Params?["name"], req.Params?["arguments"] as JsonObject, ct),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {req.Method}"),
        };
        mcp.Start();
        using var stop = new CancellationTokenSource();
        _ = mcp.Completion.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
        if (channelUrl is not null) await PollChannelAsync(http, mcp, channelUrl, stop.Token);
        else await mcp.Completion;
        await mcp.DisposeAsync();
        return 0;
    }

    static JsonObject Initialize(JsonRpcRequest req, bool peers, bool channel)
    {
        var capabilities = new JsonObject { ["tools"] = new JsonObject() };
        if (channel) capabilities["experimental"] = new JsonObject { ["claude/channel"] = new JsonObject() };
        var instructions = new StringBuilder();
        if (channel)
            instructions.Append("Messages on this channel come from the user's other coding agents; the sender attribute names the agent. " +
                "Send answers back with the reply tool, passing the message_id. A message with kind report (a session you spawned " +
                "finished a turn) needs no reply. ");
        else
            instructions.Append("An opinion request from another of the user's agents names a message_id; answer it with the reply tool. ");
        if (peers)
            instructions.Append("list_agents shows the user's other coding-agent sessions and ask_agent asks one for its opinion; " +
                "they only give opinions, they do not act on your behalf. spawn_session starts a new child session of one of these agents " +
                "with a task, after the user approves it.");
        return new JsonObject
        {
            ["protocolVersion"] = req.Params?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = AgentHost.Version },
            ["instructions"] = instructions.ToString().Trim(),
        };
    }

    static async Task PollChannelAsync(HttpClient http, JsonRpcConnection mcp, string url, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var batch = await http.GetFromJsonAsync($"{url}/next", PeersJsonContext.Default.JsonObject, stop);
                foreach (var m in batch?["messages"] as JsonArray ?? [])
                    if (m is not null)
                        await mcp.SendNotificationAsync("notifications/claude/channel", m.DeepClone());
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                try { await Task.Delay(2000, stop); } catch (OperationCanceledException) { break; }
            }
        }
    }

    static async Task<JsonNode> CallAsync(HttpClient http, string? pipe, string? token, string? channelUrl, string? tool, JsonObject? args, CancellationToken ct)
    {
        try
        {
            switch (tool)
            {
                case "reply":
                {
                    var messageId = (string?)args?["message_id"];
                    var text = (string?)args?["text"];
                    if (string.IsNullOrWhiteSpace(messageId) || text is null) return Text("message_id and text are required", error: true);
                    if (channelUrl is not null)
                    {
                        using var response = await http.PostAsync($"{channelUrl}/reply",
                            new StringContent(new JsonObject { ["message_id"] = messageId, ["text"] = text }.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                        return response.IsSuccessStatusCode ? Text("delivered") : Text("not delivered", error: true);
                    }
                    if (pipe is null || token is null) return Text("no route for replies", error: true);
                    await using var service = await ConnectAsync(pipe, ct);
                    await service.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
                    {
                        ["messageId"] = messageId,
                        ["text"] = text,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                    }, ct);
                    return Text("delivered");
                }
                case "list_agents" when pipe is not null && token is not null:
                {
                    await using var service = await ConnectAsync(pipe, ct);
                    var peers = await service.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = token }, ct);
                    return Text(peers?["sessions"]?.ToJsonString(new() { WriteIndented = true }) ?? "[]");
                }
                case "ask_agent" when pipe is not null && token is not null:
                {
                    var target = (string?)args?["session_id"];
                    var question = (string?)args?["question"];
                    if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(question))
                        return Text("session_id and question are required", error: true);
                    await using var service = await ConnectAsync(pipe, ct);
                    var reply = await service.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
                    {
                        ["sessionId"] = target,
                        ["text"] = question,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                    }, ct);
                    return Text((string?)reply?["text"] ?? "(no answer)");
                }
                case "spawn_session" when pipe is not null && token is not null:
                {
                    var agent = (string?)args?["agent"];
                    var prompt = (string?)args?["prompt"];
                    if (string.IsNullOrWhiteSpace(agent) || string.IsNullOrWhiteSpace(prompt))
                        return Text("agent and prompt are required", error: true);
                    var spawn = new JsonObject
                    {
                        ["agent"] = agent,
                        ["prompt"] = prompt,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                    };
                    if (args?["cwd"] is JsonValue cwd && cwd.TryGetValue<string>(out var folder) && folder.Length > 0) spawn["cwd"] = folder;
                    if (args?["title"] is JsonValue title && title.TryGetValue<string>(out var name) && name.Length > 0) spawn["title"] = name;
                    if (args?["report"] is JsonValue report && report.TryGetValue<bool>(out var reports)) spawn["report"] = reports;
                    await using var service = await ConnectAsync(pipe, ct);
                    var started = await service.SendRequestAsync(GorynychProtocol.MethodSpawn, spawn, ct);
                    return Text(new JsonObject { ["session_id"] = (string?)started?["sessionId"] }.ToJsonString());
                }
                default:
                    return Text($"unknown tool {tool}", error: true);
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or TimeoutException or HttpRequestException)
        {
            return Text($"Gorynych could not do that: {e.Message}", error: true);
        }
    }

    static async Task<JsonRpcConnection> ConnectAsync(string pipe, CancellationToken ct)
    {
        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await stream.ConnectAsync(5000, ct);
        var rpc = new JsonRpcConnection(stream, stream, "peers->service");
        rpc.Start();
        await rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest
        {
            ProtocolVersion = AcpMethods.ProtocolVersion,
            ClientInfo = new Implementation { Name = "gorynych-peers", Version = AgentHost.Version },
        }, ct);
        return rpc;
    }

    static JsonObject Text(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    static JsonNode[] Tools(bool peers)
    {
        var reply = new JsonObject
        {
            ["name"] = "reply",
            ["description"] = "Send your answer to an opinion request from another of the user's coding agents, passing its message_id.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["message_id"] = new JsonObject { ["type"] = "string", ["description"] = "message_id of the request being answered" },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "the answer" },
                },
                ["required"] = new JsonArray("message_id", "text"),
            },
        };
        if (!peers) return [reply];
        var list = new JsonObject
        {
            ["name"] = "list_agents",
            ["description"] = "List the user's other coding-agent sessions in Gorynych that you can ask for an opinion: session ID, agent, title, " +
                "folder, whether it is open, and parent_session_id for a session another session started with spawn_session.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        };
        var ask = new JsonObject
        {
            ["name"] = "ask_agent",
            ["description"] = "Ask another of the user's coding agents for its opinion and wait for the answer. " +
                "The other agent answers from its own session and context; it does not run tools for you.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "the session to ask, from list_agents" },
                    ["question"] = new JsonObject { ["type"] = "string", ["description"] = "the question, with the context the other agent needs" },
                },
                ["required"] = new JsonArray("session_id", "question"),
            },
        };
        var spawn = new JsonObject
        {
            ["name"] = "spawn_session",
            ["description"] = "Start a new session of one of the user's coding agents as a child of this session, with a first prompt it works on. " +
                "The user approves every spawn; if they decline, nothing starts and the call fails. " +
                "Returns {\"session_id\": ...} at once, without waiting for the work; the child then runs on its own, " +
                "the user can open and prompt it directly, and ask_agent asks it questions. Each time the child finishes a turn, this session gets " +
                "a short report with its final message, also when that turn failed (unless report is false). " +
                "Use it when the user asks you to hand work to another agent.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["agent"] = new JsonObject { ["type"] = "string", ["description"] = "the agent to run: claude or kimi" },
                    ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "the first prompt: the task, with the context the agent needs" },
                    ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "the folder to work in; default: this session's folder; a relative path is taken from it" },
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "a short name for the session in the user's list" },
                    ["report"] = new JsonObject { ["type"] = "boolean", ["description"] = "report each finished turn of the child to this session; default true" },
                },
                ["required"] = new JsonArray("agent", "prompt"),
            },
        };
        return [list, ask, spawn, reply];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class PeersJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
