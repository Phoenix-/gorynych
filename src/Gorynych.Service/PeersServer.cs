using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

/// <summary>
/// The stdio MCP server the service hands to an agent in <c>mcpServers</c>
/// (<c>Gorynych.Service peers --pipe … --token …</c>). Its tools let the agent see the user's
/// other agent sessions and ask one of them for an opinion; both go to the service over its
/// pipe, and the token tells the service which session is asking.
/// </summary>
public static class PeersServer
{
    public const string ServerName = "gorynych";

    const string Instructions =
        "Tools for consulting the user's other coding agents (Claude Code, Kimi Code, …) that run in Gorynych. " +
        "Use list_agents to see their sessions and ask_agent to ask one for its opinion. They only give opinions; " +
        "they do not act on your behalf.";

    public static async Task<int> RunAsync(string pipe, string token)
    {
        var mcp = new JsonRpcConnection(Console.OpenStandardInput(), Console.OpenStandardOutput(), "peers");
        mcp.RequestHandler = async (req, ct) => req.Method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = req.Params?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = AgentHost.Version },
                ["instructions"] = Instructions,
            },
            "ping" => new JsonObject(),
            "tools/list" => new JsonObject { ["tools"] = new JsonArray(ListTool(), AskTool()) },
            "tools/call" => await CallAsync(pipe, token, (string?)req.Params?["name"], req.Params?["arguments"] as JsonObject, ct),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {req.Method}"),
        };
        mcp.Start();
        await mcp.Completion;
        await mcp.DisposeAsync();
        return 0;
    }

    static async Task<JsonNode> CallAsync(string pipe, string token, string? tool, JsonObject? args, CancellationToken ct)
    {
        try
        {
            await using var service = await ConnectAsync(pipe, ct);
            switch (tool)
            {
                case "list_agents":
                {
                    var peers = await service.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject { ["token"] = token }, ct);
                    return Text(peers?["sessions"]?.ToJsonString(new() { WriteIndented = true }) ?? "[]");
                }
                case "ask_agent":
                {
                    var target = (string?)args?["session_id"];
                    var question = (string?)args?["question"];
                    if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(question))
                        return Text("session_id and question are required", error: true);
                    var reply = await service.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
                    {
                        ["sessionId"] = target,
                        ["text"] = question,
                        ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
                    }, ct);
                    return Text((string?)reply?["text"] ?? "(no answer)");
                }
                default:
                    return Text($"unknown tool {tool}", error: true);
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or TimeoutException)
        {
            return Text($"Gorynych could not ask: {e.Message}", error: true);
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

    static JsonObject ListTool() => new()
    {
        ["name"] = "list_agents",
        ["description"] = "List the user's other coding-agent sessions in Gorynych that you can ask for an opinion: session ID, agent, title, folder.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    static JsonObject AskTool() => new()
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
}
