using System.IO.Pipelines;
using System.Text.Json.Nodes;

namespace Gorynych.Acp.JsonRpc;

/// <summary>Typed ACP calls over a <see cref="JsonRpcConnection"/>.</summary>
public static class AcpConnectionExtensions
{
    public static async Task<TResponse> RequestAsync<TRequest, TResponse>(
        this JsonRpcConnection connection, string method, TRequest request, CancellationToken ct = default)
    {
        var result = await connection.SendRequestAsync(method, AcpJson.ToNode(request), ct).ConfigureAwait(false);
        return AcpJson.FromNode<TResponse>(result ?? new JsonObject())
            ?? throw new JsonRpcException(JsonRpcException.InternalError, $"{method}: empty result");
    }

    public static Task NotifyAsync<T>(this JsonRpcConnection connection, string method, T notification) =>
        connection.SendNotificationAsync(method, AcpJson.ToNode(notification));

    /// <summary>Sends one <c>session/update</c> notification.</summary>
    public static Task SendUpdateAsync(this JsonRpcConnection connection, string sessionId, SessionUpdate update) =>
        connection.NotifyAsync(AcpMethods.Client.SessionUpdate, new SessionNotification { SessionId = sessionId, Update = update });

    /// <summary>Parses request parameters or fails with <c>-32602 Invalid params</c>.</summary>
    public static T ParamsAs<T>(this JsonRpcRequest request) =>
        AcpJson.FromNode<T>(request.Params ?? new JsonObject())
            ?? throw new JsonRpcException(JsonRpcException.InvalidParams, $"{request.Method}: missing params");
}

/// <summary>Two in-memory byte pipes forming a duplex link between an in-process ACP client and agent.</summary>
public sealed class DuplexLink
{
    readonly Pipe toAgent = new();
    readonly Pipe toClient = new();

    /// <summary>The client end: write requests to the agent, read the agent's messages.</summary>
    public (Stream Input, Stream Output) ClientEnd => (toClient.Reader.AsStream(), toAgent.Writer.AsStream());

    /// <summary>The agent end: read the client's messages, write to the client.</summary>
    public (Stream Input, Stream Output) AgentEnd => (toAgent.Reader.AsStream(), toClient.Writer.AsStream());
}
