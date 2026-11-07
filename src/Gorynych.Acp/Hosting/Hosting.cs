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

    /// <summary>Returns service status: ID, process, adapters, sessions.</summary>
    public const string MethodStatus = "_gorynych/status";

    /// <summary>Asks the service to exit.</summary>
    public const string MethodShutdown = "_gorynych/shutdown";

    /// <summary>The default local endpoint: one named pipe per user.</summary>
    public static string DefaultPipeName => "gorynych-acp-" + Sanitize(Environment.UserName);

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}
