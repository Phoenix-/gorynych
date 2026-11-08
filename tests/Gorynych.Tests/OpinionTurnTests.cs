using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Asking an agent that has no channel (Kimi): the request becomes an ordinary turn, marked
/// with the sender and message_id, queued behind the session's running turn, and answered
/// with the peers server's reply tool or, failing that, by the turn's text.
/// </summary>
[TestClass]
public sealed partial class OpinionTurnTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A turn-based agent like Kimi: no <c>_gorynych/opinion</c>; it records its MCP servers
    /// and lets each test decide what a prompt does.
    /// </summary>
    sealed class TurnAgent
    {
        public required string Name { get; init; }
        public required string SessionId { get; init; }
        public string? Token { get; private set; }
        public List<string> Prompts { get; } = [];
        public int Running, MaxRunning;
        public Func<ScriptedAgent, PromptRequest, string, Task> OnPrompt { get; set; } = (_, _, _) => Task.CompletedTask;

        public ScriptedAdapter Adapter() => new(Name, async (agent, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionNew:
                    var args = req.Params!["mcpServers"]!.AsArray().First(s => (string?)s!["name"] == "gorynych")!["args"]!.AsArray();
                    Token = (string?)args[args.Count - 1];
                    return AcpJson.ToNode(new NewSessionResponse { SessionId = SessionId });
                case AcpMethods.Agent.SessionPrompt:
                    var p = req.ParamsAs<PromptRequest>();
                    var text = ((TextContent)p.Prompt[0]).Text;
                    lock (Prompts) Prompts.Add(text);
                    var now = Interlocked.Increment(ref Running);
                    lock (Prompts) MaxRunning = Math.Max(MaxRunning, now);
                    try { await OnPrompt(agent, p, text); }
                    finally { Interlocked.Decrement(ref Running); }
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
    }

    static NewSessionRequest New(string agent) => new()
    {
        Cwd = Path.GetTempPath(),
        McpServers = [],
        Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
    };

    static Conductor Conductor(params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
    }, adapters);

    static string MessageId(string text) => MessageIdPattern().Match(text).Groups[1].Value;

    [GeneratedRegex(@"message_id (m\d+)\]")]
    private static partial Regex MessageIdPattern();

    static Task<JsonNode?> Ask(TestClient client, string token, string target, string question) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target,
            ["text"] = question,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });

    [TestMethod]
    public async Task Request_IsAMarkedTurn_AndTheReplyToolAnswersIt()
    {
        var asker = new TurnAgent { Name = "claude", SessionId = "asking" };
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        await using var conductor = Conductor(asker.Adapter(), kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        await using var kimiTools = await TestClient.ConnectAsync(conductor, "kimi-peers-server");
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("claude"));
        kimi.OnPrompt = async (agent, p, text) =>
        {
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Thinking it over." } });
            // What Kimi's gorynych MCP server does when Kimi calls reply.
            var delivered = await kimiTools.Rpc.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
            {
                ["messageId"] = MessageId(text),
                ["text"] = "Spaces.",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Token },
            });
            Assert.IsTrue((bool)delivered!["delivered"]!);
        };

        var reply = await Ask(client, asker.Token!, "kimi-1", "Tabs or spaces for C#?");
        Assert.AreEqual("Spaces.", (string?)reply!["text"]);

        var prompt = kimi.Prompts.Single();
        StringAssert.StartsWith(prompt, "[Opinion request · from session_id asking (claude) · message_id m1]");
        Assert.IsFalse(prompt.Contains("peer_claude", StringComparison.Ordinal), "no label that looks like a session ID");
        StringAssert.Contains(prompt, "Tabs or spaces for C#?");
        StringAssert.Contains(prompt, "reply tool of the gorynych MCP server");

        // In the feed the exchange is kept apart from the user's own turns. (The reply travels on
        // another connection, so it can overtake the turn's last update.)
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline && !client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "agent_message_chunk")) await Task.Delay(20);
        var asked = client.Snapshot().Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual("peer", (string?)asked["_meta"]!["gorynych/origin"]);
        Assert.AreEqual("peer_claude", (string?)asked["_meta"]!["gorynych/sender"]);
        Assert.AreEqual("m1", (string?)asked["_meta"]!["gorynych/opinion"]);
        Assert.IsFalse(string.IsNullOrEmpty((string?)asked["messageId"]), "the service names the message it writes");
        var answer = client.Snapshot().Single(u => (string?)u["sessionUpdate"] == "agent_message_chunk");
        Assert.AreEqual("m1", (string?)answer["_meta"]!["gorynych/opinion"]);
    }

    [TestMethod]
    public async Task WithoutAReply_TheTurnsTextIsTheAnswer()
    {
        var asker = new TurnAgent { Name = "claude", SessionId = "asking" };
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        kimi.OnPrompt = async (agent, p, _) =>
        {
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Spaces, " } });
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "four of them." } });
        };
        await using var conductor = Conductor(asker.Adapter(), kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("claude"));

        var reply = await Ask(client, asker.Token!, "kimi-1", "Tabs or spaces?");
        Assert.AreEqual("Spaces, four of them.", (string?)reply!["text"]);
    }

    [TestMethod]
    public async Task Request_WaitsForTheRunningTurn()
    {
        var asker = new TurnAgent { Name = "claude", SessionId = "asking" };
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        var userTurn = new TaskCompletionSource();
        kimi.OnPrompt = async (agent, p, text) =>
        {
            if (text == "long user task") await userTurn.Task;
            else await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "answer" } });
        };
        await using var conductor = Conductor(asker.Adapter(), kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("claude"));

        var user = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "kimi-1", Prompt = [new TextContent { Text = "long user task" }] });
        while (kimi.Prompts.Count == 0) await Task.Delay(20);
        var opinion = Ask(client, asker.Token!, "kimi-1", "quick question");
        await Task.Delay(300);
        Assert.AreEqual(1, kimi.Prompts.Count, "the request waits; ACP cannot inject into a running turn");
        Assert.IsFalse(opinion.IsCompleted);

        userTurn.SetResult();
        await user.WaitAsync(Wait);
        Assert.AreEqual("answer", (string?)(await opinion.WaitAsync(Wait))!["text"]);
        Assert.AreEqual(1, kimi.MaxRunning, "never two turns at once");
        StringAssert.StartsWith(kimi.Prompts[1], "[Opinion request · from session_id asking (claude)");
    }

    [TestMethod]
    public async Task ListAgentsAndReply_AreAllowedWithoutAsking_AskAgentArchiveAndRestoreAreNot()
    {
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        var outcomes = new List<string>();
        var asked = new[] { "mcp__gorynych__ask_agent", "mcp__gorynych__archive_session", "mcp__gorynych__restore_session" };
        kimi.OnPrompt = async (agent, p, _) =>
        {
            foreach (var tool in new[] { "mcp__gorynych__list_agents", "mcp__gorynych__reply" }.Concat(asked))
            {
                var r = await agent.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission,
                    new RequestPermissionRequest
                    {
                        SessionId = p.SessionId,
                        ToolCall = new ToolCallUpdate { ToolCallId = tool, Title = tool },
                        Options =
                        [
                            new PermissionOption { OptionId = "approve", Name = "Approve once", Kind = PermissionOptionKind.AllowOnce },
                            new PermissionOption { OptionId = "approve_for_session", Name = "Approve for this session", Kind = PermissionOptionKind.AllowAlways },
                            new PermissionOption { OptionId = "reject", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
                        ],
                    });
                outcomes.Add(((SelectedPermissionOutcome)r.Outcome).OptionId);
            }
        };
        await using var conductor = Conductor(kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "reject" });
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = "kimi-1", Prompt = [new TextContent { Text = "go" }] });

        CollectionAssert.AreEqual(new[] { "approve", "approve", "reject", "reject", "reject" }, outcomes);
        CollectionAssert.AreEqual(asked, client.Approvals.Select(a => a.ToolCall.Title).ToArray(), "the user approves these");
    }

    [TestMethod]
    public async Task Reply_FromTheWrongSession_IsNotDelivered()
    {
        var asker = new TurnAgent { Name = "claude", SessionId = "asking" };
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        await using var conductor = Conductor(asker.Adapter(), kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        var outcome = new TaskCompletionSource<bool>();
        kimi.OnPrompt = async (agent, p, text) =>
        {
            // The asker tries to answer its own question in Kimi's name.
            var r = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
            {
                ["messageId"] = MessageId(text),
                ["text"] = "forged",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = asker.Token },
            });
            outcome.SetResult((bool)r!["delivered"]!);
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "real" } });
        };
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("claude"));

        var reply = await Ask(client, asker.Token!, "kimi-1", "q");
        Assert.IsFalse(await outcome.Task.WaitAsync(Wait));
        Assert.AreEqual("real", (string?)reply!["text"]);
    }

    [TestMethod]
    public async Task AskingWhileAnswering_FailsWithHowToAnswer()
    {
        var asker = new TurnAgent { Name = "claude", SessionId = "asking" };
        var kimi = new TurnAgent { Name = "kimi", SessionId = "kimi-1" };
        await using var conductor = Conductor(asker.Adapter(), kimi.Adapter());
        await using var client = await TestClient.ConnectAsync(conductor);
        var refused = new TaskCompletionSource<JsonRpcException>();
        kimi.OnPrompt = async (agent, p, text) =>
        {
            // Kimi, asked, tries to ask the asker back instead of answering.
            try { await Ask(client, kimi.Token!, "asking", "What do you mean?"); }
            catch (JsonRpcException e) { refused.SetResult(e); }
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Spaces." } });
        };
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("claude"));

        await Ask(client, asker.Token!, "kimi-1", "Tabs or spaces?");
        var e = await refused.Task.WaitAsync(Wait);
        Assert.AreEqual(JsonRpcException.InvalidRequest, e.Code);
        Assert.AreEqual("This session is answering opinion request m1, so it cannot ask another session during this turn. " +
            "Send your answer with the reply tool, passing message_id \"m1\"; if another opinion would help, say so in the answer.", e.Message);
    }
}
