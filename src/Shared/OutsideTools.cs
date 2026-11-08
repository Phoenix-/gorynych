// Compiled into both Gorynych.Service and Gorynych.Outside: the outside server answers from this
// copy when the service cannot be reached, and the service answers from it otherwise.
using System.Text.Json.Nodes;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Outside;

/// <summary>
/// The <c>gorynych-outside</c> MCP server's definitions: its tools and their texts, the
/// instructions, and the answers that need no service (<c>server/discover</c>, <c>initialize</c>,
/// <c>tools/list</c>) in both eras of MCP. See docs/service-protocol.md, "Agents outside Gorynych".
/// </summary>
internal static class OutsideTools
{
    /// <summary>The server's name, its <c>serverId</c> in <c>_gorynych/mcp_message</c>, and the name the guide registers.</summary>
    public const string ServerName = "gorynych-outside";

    /// <summary>The stateless MCP revision, whose requests carry their version in <c>params._meta</c>.</summary>
    public const string ModernVersion = "2026-07-28";

    /// <summary>The revisions with an <c>initialize</c> handshake, newest first.</summary>
    public static readonly string[] LegacyVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion";
    public const string MetaClientInfo = "io.modelcontextprotocol/clientInfo";
    public const string MetaServerInfo = "io.modelcontextprotocol/serverInfo";

    /// <summary>MCP's <c>UnsupportedProtocolVersionError</c>.</summary>
    public const int UnsupportedVersion = -32022;

    public const string Instructions =
        "Gorynych runs the user's Claude, Codex, Kimi and DeepSeek coding sessions on this machine. list_sessions lists them, read_session shows " +
        "what one said, and ask_agent asks one for its opinion. They cannot ask you anything back.";

    /// <summary>What the server says, and every tool call returns, when it runs under a Gorynych service.</summary>
    public const string InsideGorynych =
        "This is the gorynych-outside server, for agents outside Gorynych. This session runs inside Gorynych: use list_agents " +
        "and ask_agent of its gorynych server instead.";

    /// <summary>A tool call's result while the service cannot be reached.</summary>
    public static string ServiceDown(string reason) =>
        $"Gorynych is not running and could not be started: {Sentence(reason)} Start Gorynych, then call the tool again.";

    /// <summary>The prefix of a tool's error that comes from Gorynych.</summary>
    public static string CouldNotDo(string message) => $"Gorynych could not do that: {message}";

    /// <summary>The version a modern request declares in <c>params._meta</c>; null for a legacy request.</summary>
    public static string? ModernRequestVersion(JsonNode? @params) =>
        @params?["_meta"]?[MetaProtocolVersion] is JsonValue v && v.TryGetValue<string>(out var version) ? version : null;

    /// <summary>MCP's error for a revision this server does not serve over modern requests.</summary>
    public static JsonRpcException Unsupported(string requested) =>
        new(UnsupportedVersion, "Unsupported protocol version",
            new JsonObject { ["supported"] = new JsonArray(ModernVersion), ["requested"] = requested });

    /// <summary>The answer to <c>server/discover</c>.</summary>
    public static JsonObject Discover(string serverVersion, bool inside) => new()
    {
        ["resultType"] = "complete",
        ["supportedVersions"] = new JsonArray(ModernVersion),
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["_meta"] = new JsonObject { [MetaServerInfo] = ServerInfo(serverVersion) },
        ["instructions"] = inside ? InsideGorynych : Instructions,
    };

    /// <summary>The answer to a legacy host's <c>initialize</c>: its own revision when known, else the newest legacy one.</summary>
    public static JsonObject Initialize(JsonNode? @params, string serverVersion, bool inside)
    {
        var requested = (string?)@params?["protocolVersion"];
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && LegacyVersions.Contains(requested) ? requested : LegacyVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = ServerInfo(serverVersion),
            ["instructions"] = inside ? InsideGorynych : Instructions,
        };
    }

    static JsonObject ServerInfo(string version) => new() { ["name"] = ServerName, ["title"] = "Gorynych", ["version"] = version };

    /// <summary>The answer to <c>tools/list</c>; none inside Gorynych.</summary>
    public static JsonObject ToolsList(bool modern, bool inside) =>
        Complete(new JsonObject { ["tools"] = inside ? new JsonArray() : new JsonArray(Tools()) }, modern);

    /// <summary>A tool call's result: one text block.</summary>
    public static JsonObject ToolResult(string text, bool isError, bool modern) => Complete(new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    }, modern);

    /// <summary>A modern result names its type (MCP 2026-07-28); a legacy one has no such field.</summary>
    public static JsonObject Complete(JsonObject result, bool modern)
    {
        if (modern) result["resultType"] = "complete";
        return result;
    }

    public static JsonNode[] Tools() =>
    [
        new JsonObject
        {
            ["name"] = "list_sessions",
            ["description"] = "List the user's coding-agent sessions in Gorynych, which runs Claude, Codex, Kimi and DeepSeek sessions on this machine. " +
                "Pass a session_id to read_session to see what that session said, or to ask_agent to ask it something. Each entry has " +
                "session_id, agent, title, cwd, status, updated_at and parent_session_id (set when another session started it with " +
                "spawn_session). Pass a session_id exactly as listed, never adding or stripping a prefix: Claude's, Codex's " +
                "and DeepSeek's are a bare UUID, Kimi's start with session_. status open means the agent has the session loaded now; a saved one is started when " +
                "asked, so it answers more slowly, and is unloaded again once it has been idle with no client for some minutes; " +
                "read_session reads it without starting it. Archived sessions are left out " +
                "unless include_archived is true; when some are left out, the result's last line says how many.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["include_archived"] = new JsonObject { ["type"] = "boolean", ["description"] = "also list archived sessions, marked archived: true; default false" },
                },
            },
        },
        new JsonObject
        {
            ["name"] = "read_session",
            ["description"] = "Read what was said in one of the user's Gorynych sessions: its latest messages, oldest first, with each " +
                "tool call on its own line. Use it to see what a session reported, decided or asked. It does not disturb the session and " +
                "does not start its agent. When older messages exist, the result's last line says how to read them: pass its before value.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "the exact session_id, from list_sessions" },
                    ["before"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "read the messages before this point; pass the value a previous read_session result gave, or " +
                            "leave it out for the latest messages",
                    },
                    ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "how many messages to show, 1 to 50; default 10" },
                    ["thoughts"] = new JsonObject { ["type"] = "boolean", ["description"] = "also show the agent's thinking; default false" },
                },
                ["required"] = new JsonArray("session_id"),
            },
        },
        new JsonObject
        {
            ["name"] = "ask_agent",
            ["description"] = "Ask one of the user's Gorynych sessions for its opinion and wait for the answer. It answers from its own " +
                "session and context; it does not run tools or act for you, and it cannot ask you back, so put everything it needs " +
                "into the question. A session that is not loaded is started to answer, and a session in the middle of a turn answers " +
                "after it, so this can take minutes. To see what a session already said, use read_session: it costs that session nothing.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["session_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "the exact session_id of the session to ask, from list_sessions; never an agent name or a title",
                    },
                    ["question"] = new JsonObject { ["type"] = "string", ["description"] = "the question, with all the context the other agent needs" },
                },
                ["required"] = new JsonArray("session_id", "question"),
            },
        },
    ];

    static string Sentence(string text)
    {
        text = text.Trim();
        return text.Length == 0 ? "no reason given." : text.EndsWith('.') ? text : text + ".";
    }
}
