namespace Gorynych.Acp.Hosting;

/// <summary>
/// An adapter is an ACP agent. The service starts it and talks ACP to it as a client over
/// the returned streams, whether the agent is an external process (<c>kimi acp</c>) or an
/// in-process implementation joined by a <see cref="JsonRpc.DuplexLink"/>.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>A stable short name, used as <c>_meta["gorynych/agent"]</c> and for routing.</summary>
    string Name { get; }

    /// <summary>A human-readable name for lists.</summary>
    string DisplayName { get; }

    Task<AgentInstance> StartAsync(CancellationToken ct);
}

/// <summary>A running agent: the ACP byte streams and a way to stop it.</summary>
public sealed class AgentInstance(Stream input, Stream output, Task exited, Func<ValueTask> stop) : IAsyncDisposable
{
    /// <summary>Messages from the agent.</summary>
    public Stream Input { get; } = input;

    /// <summary>Messages to the agent.</summary>
    public Stream Output { get; } = output;

    /// <summary>Completes when the agent exits.</summary>
    public Task Exited { get; } = exited;

    public ValueTask DisposeAsync() => stop();
}

/// <summary>Gorynych's own ACP extensions: <c>_meta</c> keys and <c>_</c>-prefixed methods.</summary>
public static class GorynychProtocol
{
    /// <summary>Which adapter owns a session; in <c>session/new</c> params it selects one.</summary>
    public const string MetaAgent = "gorynych/agent";

    /// <summary>The persistent ID of the service, in the <c>initialize</c> result.</summary>
    public const string MetaServiceId = "gorynych/serviceId";

    /// <summary>The adapters a service offers, in the <c>initialize</c> result.</summary>
    public const string MetaAgents = "gorynych/agents";

    /// <summary>Where a user message came from when it was not typed by the user (for example <c>channel</c>).</summary>
    public const string MetaOrigin = "gorynych/origin";

    /// <summary>Who sent an agent-to-agent message.</summary>
    public const string MetaSender = "gorynych/sender";

    /// <summary>Identifies the asking session in calls from the peers MCP server the service gave it.</summary>
    public const string MetaPeerToken = "gorynych/peerToken";

    /// <summary>Asks a session's agent for an opinion: <c>{sessionId, text, sender?}</c> → <c>{text}</c>.</summary>
    public const string MethodOpinion = "_gorynych/opinion";

    /// <summary>An answer to an opinion request that was delivered as a turn: <c>{messageId, text}</c>.</summary>
    public const string MethodReply = "_gorynych/reply";

    /// <summary>The opinion request a message or update belongs to (its <c>message_id</c>).</summary>
    public const string MetaOpinion = "gorynych/opinion";

    /// <summary>The sessions a peer can ask: <c>{token}</c> → <c>{sessions: [...]}</c>.</summary>
    public const string MethodPeers = "_gorynych/peers";

    /// <summary>
    /// The session that started this one with <c>spawn_session</c>: in every <c>SessionInfo._meta</c>
    /// of <c>session/list</c>, and in a <c>session_info_update._meta</c> when it is set.
    /// </summary>
    public const string MetaParentSessionId = "gorynych/parentSessionId";

    /// <summary>
    /// Starts a child of the calling session, after asking the person:
    /// <c>{agent, prompt, cwd?, title?, report?}</c> with the caller's peer token → <c>{sessionId}</c>.
    /// </summary>
    public const string MethodSpawn = "_gorynych/spawn";

    /// <summary>
    /// Tells a session's agent something that needs no answer, such as a child's report:
    /// <c>{sessionId, text, sender, fromSessionId?}</c> → <c>{}</c>. An agent without a way in
    /// (no channel) answers method not found, and the service delivers it as a turn.
    /// </summary>
    public const string MethodNote = "_gorynych/note";

    /// <summary>The session a peer request or a report came from, in the <c>_meta</c> of the message it became.</summary>
    public const string MetaFromSessionId = "gorynych/fromSessionId";

    /// <summary>A notification to clients: <c>{sessionId, change}</c>, change <c>added</c>, <c>updated</c> or <c>removed</c>.</summary>
    public const string MethodSessionsChanged = "_gorynych/sessions_changed";

    /// <summary>
    /// In <c>agentCapabilities._meta</c> of the <c>initialize</c> result: the service sends
    /// <see cref="MethodSessionsChanged"/>. (Also in the result's own <c>_meta</c>, deprecated.)
    /// </summary>
    public const string MetaSessionsChanged = "gorynych/sessionsChanged";

    /// <summary>
    /// In <c>agentCapabilities._meta</c> of the <c>initialize</c> result: the service keeps an archive
    /// flag per session (<see cref="MethodArchive"/>). (Also in the result's own <c>_meta</c>, deprecated.)
    /// </summary>
    public const string MetaArchive = "gorynych/archive";

    /// <summary>
    /// <c>true</c> in <c>SessionInfo._meta</c> for an archived session (absent otherwise), and an
    /// explicit <c>true</c> or <c>false</c> in a <c>session_info_update._meta</c> when the flag changes.
    /// </summary>
    public const string MetaArchived = "gorynych/archived";

    /// <summary>In <c>session/list</c> params <c>_meta</c>: <c>true</c> lists archived sessions too.</summary>
    public const string MetaIncludeArchived = "gorynych/includeArchived";

    /// <summary>
    /// An MCP request of the <c>gorynych-outside</c> server for agents outside Gorynych, in the
    /// shape of <c>mcp/message</c> from ACP's draft MCP-over-ACP RFD: <c>{serverId, requestId,
    /// method, params}</c> → <c>{result}</c> or <c>{error}</c>; as a notification from the service,
    /// a notification that belongs to that request (progress).
    /// </summary>
    public const string MethodMcpMessage = "_gorynych/mcp_message";

    /// <summary>Reads a session's history from the journal: <c>{sessionId, cursor?, limit?, thoughts?}</c>.</summary>
    public const string MethodRead = "_gorynych/read";

    /// <summary>
    /// The agent outside Gorynych behind a <c>_gorynych/mcp_message</c>: <c>{client, folder,
    /// hostSessionId}</c>; also in the <c>_meta</c> of the opinion request it asked.
    /// </summary>
    public const string MetaOutside = "gorynych/outside";

    /// <summary>
    /// On a <c>session/update</c> from an adapter: <c>"started"</c> on the first update of a turn
    /// the agent runs that the service did not start (a message through the agent's own channel,
    /// the person typing in its terminal), <c>"ended"</c> on an update when it ends. The service
    /// keeps the session loaded in between (docs/adapter-contract.md, §7).
    /// </summary>
    public const string MetaAgentTurn = "gorynych/agentTurn";

    /// <summary>
    /// A short random tag for one run, in the message IDs a component counts in memory (the
    /// service's <c>m…</c>, Claude's channel <c>c…</c>): after a restart the count starts again, and the
    /// tag keeps a late reply to a request of an earlier run from answering a new one. Four letters
    /// or digits from <see cref="RunTagAlphabet"/>, so the IDs stay easy for an agent to copy.
    /// </summary>
    public static string NewRunTag() => System.Security.Cryptography.RandomNumberGenerator.GetString(RunTagAlphabet, 4);

    /// <summary>The characters of a run tag: no look-alikes (0 1 i l o), since Kimi once copied an l as a 1.</summary>
    public const string RunTagAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Sets a session's archive flag: <c>{sessionId, archived}</c> → <c>{}</c>. Only a flag: it
    /// closes, cancels and unloads nothing. A call with a peer token also gets <c>text</c>, what the agent is told.
    /// </summary>
    public const string MethodArchive = "_gorynych/archive";

    /// <summary>
    /// Sets a session's native title in its agent: <c>{sessionId, title}</c> → <c>{}</c>. The
    /// service calls it only on an adapter that advertises <see cref="CapSetTitle"/>
    /// (docs/adapter-contract.md, §9).
    /// </summary>
    public const string MethodSetTitle = "_gorynych/set_title";

    /// <summary>In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MethodSetTitle"/>.</summary>
    public const string CapSetTitle = "gorynych/setTitle";

    /// <summary>
    /// In <c>agentCapabilities._meta</c>: seconds; the agent's sessions are unloaded after this
    /// idle time when it is shorter than the service's own (docs/adapter-contract.md, §7).
    /// </summary>
    public const string CapUnloadAfter = "gorynych/unloadAfter";

    /// <summary>
    /// In <c>agentCapabilities._meta</c>: <c>true</c> when the agent keeps nothing durable before a
    /// session's first prompt, so a draft it does not list was never durable (docs/adapter-contract.md, §3).
    /// </summary>
    public const string CapDraftsNotDurable = "gorynych/draftsNotDurable";

    /// <summary>
    /// <c>true</c> in the <c>_meta</c> of a mode's value (<c>SessionConfigSelectOption</c> of the
    /// option with category <c>mode</c>, or a <c>SessionMode</c>): in that mode the agent runs
    /// anything without asking the person. The service asks the person before a session that is
    /// not in such a mode asks one that is (docs/adapter-contract.md, §6.2).
    /// </summary>
    public const string MetaFullAccess = "gorynych/fullAccess";

    /// <summary>
    /// On a <c>session/prompt</c> the service sends: the <c>messageId</c> under which the service
    /// journals the prompt's text, for an agent that can keep it with the message (docs/adapter-contract.md, §5).
    /// </summary>
    public const string MetaMessageId = "gorynych/messageId";

    /// <summary>Returns service status: ID, process, adapters, sessions.</summary>
    public const string MethodStatus = "_gorynych/status";

    /// <summary>Asks the service to exit.</summary>
    public const string MethodShutdown = "_gorynych/shutdown";

    /// <summary>The default local endpoint: one named pipe per user.</summary>
    public static string DefaultPipeName => "gorynych-acp-" + Sanitize(Environment.UserName);

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}
