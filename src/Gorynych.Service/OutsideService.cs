using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Outside;

namespace Gorynych.Service;

/// <summary>
/// The service's side of the <c>gorynych-outside</c> MCP server: it answers the MCP requests that
/// <c>Gorynych.Outside</c> forwards as <c>_gorynych/mcp_message</c> (the shape of <c>mcp/message</c>
/// in ACP's MCP-over-ACP RFD), in both eras of MCP, and runs the three tools for agents outside
/// Gorynych (docs/service-protocol.md, "Agents outside Gorynych").
/// </summary>
internal sealed class OutsideService(Conductor conductor, TimeSpan progressInterval)
{
    /// <summary>What <c>list_sessions</c> shows instead of an empty list.</summary>
    internal const string NoSession = "There is no session in Gorynych now.";

    /// <summary>About how many characters of text one <c>read_session</c> result shows.</summary>
    public const int ReadBudget = 20_000;

    /// <summary>
    /// <c>_gorynych/mcp_message {serverId, requestId, method, params, _meta}</c> → <c>{result}</c> or
    /// <c>{error}</c>. Errors of the binding itself (a malformed envelope, another server) are
    /// errors of the outer request; a cancelled request fails with <c>-32800</c>.
    /// </summary>
    public async Task<JsonNode?> HandleAsync(ClientConnection client, JsonRpcRequest request, CancellationToken ct)
    {
        var envelope = request.Params as JsonObject ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "params are required");
        if (Text(envelope, "serverId") is not { } serverId || Text(envelope, "requestId") is not { } requestId || Text(envelope, "method") is not { } method)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "serverId, requestId and method are required strings");
        if (serverId != OutsideTools.ServerName)
            throw new JsonRpcException(-33001, $"No MCP server \"{serverId}\" here; Gorynych serves {OutsideTools.ServerName}.");
        var host = envelope["_meta"]?[GorynychProtocol.MetaOutside] as JsonObject ?? [];
        try
        {
            return new JsonObject { ["result"] = await DispatchAsync(client, requestId, method, envelope["params"] as JsonObject, host, ct) };
        }
        catch (JsonRpcException e)
        {
            var error = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
            if (e.ErrorData is not null) error["data"] = e.ErrorData.DeepClone();
            return new JsonObject { ["error"] = error };
        }
    }

    async Task<JsonNode> DispatchAsync(ClientConnection client, string requestId, string method, JsonObject? inner, JsonObject host, CancellationToken ct)
    {
        // A modern request (MCP 2026-07-28) names its revision in params._meta; a legacy one comes after
        // an initialize that the outside server answered itself.
        var version = OutsideTools.ModernRequestVersion(inner);
        var modern = version is not null;
        if (modern && version != OutsideTools.ModernVersion) throw OutsideTools.Unsupported(version!);
        switch (method)
        {
            case "server/discover":
                if (!modern) throw new JsonRpcException(JsonRpcException.InvalidParams, $"server/discover needs {OutsideTools.MetaProtocolVersion} in params._meta");
                return OutsideTools.Discover(AgentHost.Version, inside: false);
            case "initialize":
                return OutsideTools.Initialize(inner, AgentHost.Version, inside: false);
            case "ping":
                return OutsideTools.Complete([], modern);
            case "tools/list":
                return OutsideTools.ToolsList(modern, inside: false);
            case "tools/call":
                return await CallAsync(client, requestId, inner, host, modern, ct);
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {method}");
        }
    }

    async Task<JsonNode> CallAsync(ClientConnection client, string requestId, JsonObject? call, JsonObject host, bool modern, CancellationToken ct)
    {
        var name = (string?)call?["name"];
        var args = call?["arguments"] as JsonObject ?? [];
        if (name is not ("list_sessions" or "read_session" or "ask_agent"))
            throw new JsonRpcException(JsonRpcException.InvalidParams, $"Unknown tool: {name}");
        try
        {
            var text = name switch
            {
                "list_sessions" => ListSessions(args),
                "read_session" => await ReadSessionAsync(args, ct),
                _ => await AskAsync(client, requestId, call!, args, host, ct),
            };
            return OutsideTools.ToolResult(text, isError: false, modern);
        }
        catch (ToolInputException e)
        {
            return OutsideTools.ToolResult(e.Message, isError: true, modern);
        }
        catch (JsonRpcException e)
        {
            return OutsideTools.ToolResult(OutsideTools.CouldNotDo(e.Message), isError: true, modern);
        }
    }

    sealed class ToolInputException(string message) : Exception(message);

    /// <summary><c>list_sessions</c>: every session with history, rendered as <c>list_agents</c> renders its list, with no caller.</summary>
    string ListSessions(JsonObject args)
    {
        var includeArchived = args["include_archived"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return PeersServer.ListAgentsText(conductor.PeerList(self: null, includeArchived), empty: NoSession);
    }

    async Task<string> ReadSessionAsync(JsonObject args, CancellationToken ct)
    {
        var sessionId = Text(args, "session_id") ?? throw new ToolInputException("session_id is required: pass an exact session_id from list_sessions.");
        var before = Text(args, "before");
        var limit = args["limit"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : SessionReader.DefaultLimit;
        var thoughts = args["thoughts"] is JsonValue t && t.TryGetValue<bool>(out var b) && b;
        Conductor.SessionRead read;
        try
        {
            read = await conductor.ReadAsync(sessionId, before, limit, thoughts, ReadBudget, ct);
        }
        catch (JsonRpcException e) when (e.Code == JsonRpcException.ResourceNotFound)
        {
            throw new JsonRpcException(e.Code, conductor.UnknownToOutside(sessionId));
        }
        catch (SessionReader.UnknownCursorException e)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                $"session_id {sessionId} has no message \"{e.Cursor}\". Pass before exactly as a previous read_session result gave it, " +
                "or leave it out to read the latest messages.");
        }
        catch (Conductor.UnreadableException e)
        {
            var reason = e.Message.TrimEnd().TrimEnd('.');
            throw new JsonRpcException(JsonRpcException.InternalError, $"Could not read {Conductor.Who(e.Record)}: {reason}.");
        }
        return ReadSessionText(read, newest: before is null, thoughts);
    }

    /// <summary>How <c>read_session</c> shows a page to an agent.</summary>
    /// <param name="thoughts">The agent's thinking was asked for: the header says when none was recorded (Claude's never is).</param>
    internal static string ReadSessionText(Conductor.SessionRead read, bool newest, bool thoughts = false)
    {
        var page = read.Page;
        var text = new StringBuilder(Conductor.Who(read.Record)).Append(" · status ").Append(read.Live ? "open" : "saved");
        if (page.Updates.Count == 0)
            return text.Append("\nGorynych has no history for this session yet.").ToString();
        if (page.Count == 1) text.Append($" · message {page.First} of {page.Total}");
        else if (page.Count > 1) text.Append($" · messages {page.First}–{page.First + page.Count - 1} of {page.Total}, oldest first");
        if (thoughts && !page.Updates.Any(u => (string?)u["sessionUpdate"] == "agent_thought_chunk")) text.Append(" · no thoughts recorded");
        if (read.Trimmed) text.Append("\n(Older history was trimmed from Gorynych's journal.)");
        // Tool calls and thoughts are the agent's turn: a tool call right after a user message opens
        // the agent's block, which the agent's text then continues.
        var block = Block.None;
        foreach (var u in page.Updates)
        {
            var body = (string?)u["content"]?["text"] ?? "";
            switch ((string?)u["sessionUpdate"])
            {
                case "user_message_chunk":
                    var meta = u["_meta"] as JsonObject;
                    text.Append("\n── user").Append(Label(meta, body)).Append('\n').Append(Unwrapped(meta, body).TrimEnd());
                    block = Block.User;
                    break;
                case "agent_message_chunk":
                    text.Append(block == Block.AgentTools ? "\n" : "\n── agent\n").Append(body.TrimEnd());
                    block = Block.Agent;
                    break;
                case "agent_thought_chunk":
                    text.Append("\n── agent thinking\n").Append(body.TrimEnd());
                    block = Block.Thinking;
                    break;
                case "tool_call":
                    if (block is Block.None or Block.User)
                    {
                        text.Append("\n── agent");
                        block = Block.AgentTools;
                    }
                    var (tool, server) = SessionReader.ToolName((string?)u["title"]);
                    text.Append("\n   · tool: ").Append(server is null ? tool : $"{tool} ({server})")
                        .Append(" · ").Append((string?)u["status"] ?? "pending").Append(PeerDetail(u));
                    break;
            }
        }
        if (read.InProgress && newest) text.Append("\n(turn in progress)");
        if (page.NextCursor is { } next)
            text.Append($"\nOlder messages: call read_session with session_id \"{read.Record.SessionId}\" and before \"{next}\".");
        return text.ToString();
    }

    /// <summary>Where the rendering is: after a user message, in the agent's block opened by tool calls, after its text, in its thinking.</summary>
    enum Block { None, User, AgentTools, Agent, Thinking }

    /// <summary>
    /// What a tool call of the gorynych server says a session decided or asked, from its input:
    /// the question and whom it asked, the answer and to which request, the task and for which
    /// agent, the session archived or restored. Empty for other tools, and without input (Kimi may
    /// give none).
    /// </summary>
    static string PeerDetail(JsonNode u)
    {
        if (u["rawInput"] is not JsonObject input || SessionReader.PeerTool((string?)u["title"]) is not { } tool) return "";
        string? Field(string key) => input[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? OneLine(s) : null;
        var detail = tool switch
        {
            "ask_agent" => Join(Field("session_id") is { } asked ? $"session_id {asked}" : null, Field("question")),
            "reply" => Join(Field("message_id") is { } answered ? $"message_id {answered}" : null, Field("text")),
            "spawn_session" => Join(Field("agent"), Field("prompt")),
            _ => Field("session_id") is { } id ? $"session_id {id}" : null,
        };
        return string.IsNullOrEmpty(detail) ? "" : " · " + detail;

        static string? Join(string? who, string? what) => what is null ? who : who is null ? what : $"{who}: {what}";
    }

    static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A message through Claude's channel without its channel tag: the tag is how it travelled, not what it says.</summary>
    static string Unwrapped(JsonObject? meta, string body)
    {
        if ((string?)meta?[GorynychProtocol.MetaOrigin] != "channel") return body;
        var trimmed = body.Trim();
        if (!trimmed.StartsWith("<channel", StringComparison.Ordinal)) return body;
        var open = trimmed.IndexOf('\n');
        if (open < 0) return body;
        var inner = trimmed[(open + 1)..];
        if (inner.EndsWith("</channel>", StringComparison.Ordinal)) inner = inner[..^"</channel>".Length];
        return inner.Trim();
    }

    /// <summary>
    /// A user message the service put there names where it came from, by its <c>_meta</c>; a prompt
    /// the person typed has no label. A message through Claude's channel carries its request's keys
    /// as one asked in a turn does (<c>gorynych/opinion</c> for a request), and a report is the
    /// channel's only other message. Only a channel message journaled before it carried those keys
    /// is told by the <c>kind</c> in its channel tag, the first line.
    /// </summary>
    static string Label(JsonObject? meta, string body)
    {
        if (meta?[GorynychProtocol.MetaOutside] is not null) return " · opinion request · from outside";
        if (meta?[GorynychProtocol.MetaOpinion] is not null) return " · opinion request";
        var named = meta?[GorynychProtocol.MetaSender] is not null;
        switch ((string?)meta?[GorynychProtocol.MetaOrigin])
        {
            case "peer": return " · opinion request";
            case "report": return " · report";
            case "spawn": return " · spawned task";
            case "channel" when named: return " · report";
            case "channel":
                var tag = body.TrimStart();
                var end = tag.IndexOf('>');
                return end > 0 && tag.StartsWith("<channel", StringComparison.Ordinal) && tag[..end].Contains("kind=\"report\"", StringComparison.Ordinal)
                    ? " · report" : " · opinion request";
            default: return "";
        }
    }

    /// <summary>
    /// <c>ask_agent</c>: asks as a session's question is asked, but without a peer token and with
    /// the outside host in the request. With a <c>progressToken</c> the host gets progress at each
    /// change of state and at least every <see cref="progressInterval"/>, none after the answer.
    /// </summary>
    async Task<string> AskAsync(ClientConnection client, string requestId, JsonObject call, JsonObject args, JsonObject host, CancellationToken ct)
    {
        var sessionId = Text(args, "session_id");
        var question = Text(args, "question");
        if (sessionId is null || question is null)
            throw new ToolInputException("session_id and question are required: pass an exact session_id from list_sessions and the question.");
        var token = call["_meta"]?["progressToken"];
        await using var progress = token is null ? null : new Progress(client.Rpc, requestId, token.DeepClone(), progressInterval);
        var answer = await conductor.AskAsync(sessionId, question, Asker(host), progress is null ? null : progress.Report, ct);
        return (string?)answer?["text"] ?? "(no answer)";
    }

    /// <summary>The outside agent as the asked session sees it: its client's title and its folder.</summary>
    internal static Conductor.Asker Asker(JsonObject host)
    {
        var client = host["client"] as JsonObject;
        var name = (string?)client?["name"];
        var shown = new[] { (string?)client?["title"] ?? name, (string?)host["folder"] }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var who = shown.Count == 0 ? "an unknown agent" : string.Join(", ", shown);
        return new Conductor.Asker(From: null, $"outside_{(string.IsNullOrWhiteSpace(name) ? "unknown" : name)}",
            $"from an agent outside Gorynych ({who})", host.DeepClone().AsObject());
    }

    static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// MCP <c>notifications/progress</c> for one request, sent as request-scoped
    /// <c>_gorynych/mcp_message</c> notifications in order: one per state, and the current state again
    /// every interval. Disposing it stops them and waits for the last, so none follows the result.
    /// </summary>
    sealed class Progress : IAsyncDisposable
    {
        readonly JsonRpcConnection rpc;
        readonly string requestId;
        readonly JsonNode token;
        readonly Lock gate = new();
        readonly Timer timer;
        Task chain = Task.CompletedTask;
        string? current;
        int count;
        bool done;

        public Progress(JsonRpcConnection rpc, string requestId, JsonNode token, TimeSpan interval)
        {
            this.rpc = rpc;
            this.requestId = requestId;
            this.token = token;
            timer = new Timer(_ => Tick(), null, interval, interval);
        }

        public void Report(string message)
        {
            lock (gate)
            {
                if (done || message == current) return;
                current = message;
                Send(message);
            }
        }

        void Tick()
        {
            lock (gate)
                if (!done && current is not null) Send(current);
        }

        void Send(string message) // under the gate
        {
            var notification = new JsonObject
            {
                ["serverId"] = OutsideTools.ServerName,
                ["requestId"] = requestId,
                ["method"] = "notifications/progress",
                ["params"] = new JsonObject { ["progressToken"] = token.DeepClone(), ["progress"] = ++count, ["message"] = message },
            };
            chain = chain.ContinueWith(_ => rpc.SendNotificationAsync(GorynychProtocol.MethodMcpMessage, notification), TaskScheduler.Default).Unwrap();
        }

        public async ValueTask DisposeAsync()
        {
            Task last;
            lock (gate)
            {
                done = true;
                last = chain;
            }
            await timer.DisposeAsync();
            try { await last; }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
}
