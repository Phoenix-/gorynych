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
/// <item>With a pipe and token it offers <c>list_agents</c>, <c>ask_agent</c>,
/// <c>spawn_session</c>, <c>archive_session</c> and <c>restore_session</c>, which go to the
/// service; the token tells the service which session is asking.</item>
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
            instructions.Append("Messages on this channel come from the user's other coding-agent sessions; their first line names the " +
                "sending session by its session_id. Answer a message with kind opinion with the reply tool, passing its message_id. " +
                "A message with kind report (a session you spawned finished a turn) needs no reply. When this session was not " +
                "running, such a request or report arrives as a prompt instead, with the same first line; treat it the same way. ");
        else
            instructions.Append("An opinion request from another of the user's agents names a message_id in its first line; answer it " +
                "with the reply tool, passing that message_id. ");
        if (peers)
            instructions.Append("list_agents shows the user's other coding-agent sessions and ask_agent asks one for its opinion, by " +
                "its exact session_id; they only give opinions, they do not act on your behalf. spawn_session starts a new child session " +
                "of one of these agents with a task, after the user approves it; each turn the child finishes is reported back to you " +
                "automatically. archive_session hides a session from the lists without deleting it, and restore_session brings it back; " +
                "the user approves each.");
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
                    var delivered = false;
                    string? channelFailure = null;
                    if (channelUrl is not null)
                    {
                        using var response = await http.PostAsync($"{channelUrl}/reply",
                            new StringContent(new JsonObject { ["message_id"] = messageId, ["text"] = text }.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                        if (response.IsSuccessStatusCode)
                            delivered = (bool?)(await response.Content.ReadFromJsonAsync(PeersJsonContext.Default.JsonObject, ct))?["delivered"] ?? true;
                        else
                            channelFailure = "not delivered: the channel did not take it";
                    }
                    // A request the service asked as a turn (Kimi, or Claude while it was not running) waits in the service.
                    if (!delivered && pipe is not null && token is not null)
                    {
                        await using var service = await ConnectAsync(pipe, ct);
                        var result = await service.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
                        {
                            ["messageId"] = messageId,
                            ["text"] = text,
                            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                        }, ct);
                        delivered = (bool?)result?["delivered"] ?? false;
                    }
                    else if (!delivered && channelUrl is null)
                    {
                        return Text("no route for replies", error: true);
                    }
                    if (delivered) return Text("delivered");
                    return Text(channelFailure is not null && (pipe is null || token is null) ? channelFailure : NotDelivered(messageId), error: true);
                }
                case "list_agents" when pipe is not null && token is not null:
                {
                    var query = new JsonObject { ["token"] = token };
                    if (args?["include_archived"] is JsonValue all && all.TryGetValue<bool>(out var include) && include) query["includeArchived"] = true;
                    await using var service = await ConnectAsync(pipe, ct);
                    return Text(ListAgentsText(await service.SendRequestAsync(GorynychProtocol.MethodPeers, query, ct)));
                }
                case "archive_session" or "restore_session" when pipe is not null && token is not null:
                {
                    var target = (string?)args?["session_id"];
                    if (string.IsNullOrWhiteSpace(target)) return Text("session_id is required", error: true);
                    var archive = tool == "archive_session";
                    await using var service = await ConnectAsync(pipe, ct);
                    var result = await service.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject
                    {
                        ["sessionId"] = target,
                        ["archived"] = archive,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                    }, ct);
                    return Text((string?)result?["text"] ?? (archive ? "archived" : "restored"));
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

    /// <summary>
    /// What <c>list_agents</c> shows, from the service's <c>_gorynych/peers</c> result: the caller
    /// (which the list leaves out), the list, and how many archived sessions it leaves out. The
    /// hints are in the result as well as in the schema, since an agent may hold an older schema.
    /// </summary>
    internal static string ListAgentsText(JsonNode? peers)
    {
        var text = new StringBuilder();
        if ((string?)peers?["caller"] is { } caller) text.Append(YouAre(caller)).Append('\n');
        text.Append(peers?["sessions"]?.ToJsonString(new() { WriteIndented = true }) ?? "[]");
        if ((int?)peers?["hiddenArchived"] is int hidden and > 0) text.Append('\n').Append(ArchivedNotShown(hidden));
        return text.ToString();
    }

    /// <summary>The first line of <c>list_agents</c>: the calling session, named as the service names sessions to agents.</summary>
    internal static string YouAre(string caller) => $"You are {caller}.";

    /// <summary>The last line of <c>list_agents</c> when it leaves archived sessions out.</summary>
    internal static string ArchivedNotShown(int count) => count == 1
        ? "1 archived session is not shown; pass include_archived: true to list it."
        : $"{count} archived sessions are not shown; pass include_archived: true to list them.";

    /// <summary>What <c>reply</c> says when nothing waits for its <c>message_id</c>.</summary>
    internal static string NotDelivered(string messageId) =>
        $"No open request with message_id \"{messageId}\" for this session; it may have been answered already or withdrawn. " +
        "reply only answers a request you received, by the message_id in its header. To ask a session something, use ask_agent " +
        "with its session_id.";

    static JsonObject Text(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    internal static JsonNode[] Tools(bool peers)
    {
        var reply = new JsonObject
        {
            ["name"] = "reply",
            ["description"] = "Answer an opinion request you received from another of the user's coding agents, passing the message_id " +
                "from its header. Use it only to answer; to start a question of your own, use ask_agent.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["message_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "the message_id of the request you are answering, from its header (or the channel message's message_id)",
                    },
                    ["text"] = new JsonObject { ["type"] = "string", ["description"] = "the answer" },
                },
                ["required"] = new JsonArray("message_id", "text"),
            },
        };
        if (!peers) return [reply];
        var list = new JsonObject
        {
            ["name"] = "list_agents",
            ["description"] = "List the user's other coding-agent sessions in Gorynych that you can ask with ask_agent. " +
                "The result's first line names this session, which the list leaves out: \"You are session_id ...\", the ID for this " +
                "session itself, for example for archive_session. Each entry has session_id, agent, title, cwd, open, updated_at and " +
                "parent_session_id (set when another session started it with spawn_session). " +
                "Pass a session_id exactly as listed, never adding or stripping a prefix: Claude's are a bare UUID, Kimi's start with session_. " +
                "open is true when the session's agent has it loaded right now; it has nothing to do with archived. A session that " +
                "is not open is started when asked, so it answers more slowly. " +
                "Archived sessions are left out unless include_archived is true, and the result says how many; with include_archived " +
                "every entry says whether it is archived. ask_agent reaches archived sessions too.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["include_archived"] = new JsonObject { ["type"] = "boolean", ["description"] = "also list archived sessions, marked archived: true; default false" },
                },
            },
        };
        var ask = new JsonObject
        {
            ["name"] = "ask_agent",
            ["description"] = "Start a new question to another of the user's coding-agent sessions and wait for its answer. " +
                "The other agent answers from its own session and context; it does not run tools for you. " +
                "To answer a request you received (its header names a message_id), use reply instead.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "the exact session_id of the session to ask, from list_agents or from a header that says " +
                            "\"session_id ...\"; never an agent name, a title or a sender label",
                    },
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
                "the user can open and prompt it directly, and ask_agent asks it questions. Each time the child finishes a turn, Gorynych " +
                "sends this session a short report with the child's final message by itself, also when that turn failed (unless report " +
                "is false), so you need not poll the child, and it need not report. " +
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
        var archive = new JsonObject
        {
            ["name"] = "archive_session",
            ["description"] = "Archive one of the user's coding-agent sessions, this one included: Gorynych hides it from list_agents and " +
                "from the user's session list. Archiving is not deleting: the session keeps all its history, ask_agent still reaches it, " +
                "and the user can still open and prompt it; restore_session brings it back. The user approves each call. " +
                "Use it when the user asks you to archive or tidy up sessions.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "the exact session_id of the session to archive, from list_agents or from a header that says \"session_id ...\"",
                    },
                },
                ["required"] = new JsonArray("session_id"),
            },
        };
        var restore = new JsonObject
        {
            ["name"] = "restore_session",
            ["description"] = "Bring an archived session back into list_agents and the user's session list. It only changes where the " +
                "session is shown and sends it nothing. To ask an archived session something, use ask_agent: it need not be restored " +
                "first. list_agents with include_archived: true shows the archived sessions. The user approves each call.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "the exact session_id of the archived session, from list_agents with include_archived: true",
                    },
                },
                ["required"] = new JsonArray("session_id"),
            },
        };
        return [list, ask, spawn, archive, restore, reply];
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class PeersJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
