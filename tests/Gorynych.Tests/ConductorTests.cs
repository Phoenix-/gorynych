using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class ConductorTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static NewSessionRequest New(string agent) => new()
    {
        Cwd = Path.GetTempPath(),
        McpServers = [],
        Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
    };

    static PromptRequest Prompt(string sessionId, string text) => new() { SessionId = sessionId, Prompt = [new TextContent { Text = text }] };

    /// <summary>A fake agent with one session that answers each prompt with a chunk.</summary>
    static ScriptedAdapter Echo(string name = "fake", Func<ScriptedAgent, PromptRequest, CancellationToken, Task>? onPrompt = null) =>
        new(name, async (agent, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionNew: return AcpJson.ToNode(new NewSessionResponse { SessionId = "s1" });
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionResume: return new JsonObject();
                case AcpMethods.Agent.SessionPrompt:
                    var p = req.ParamsAs<PromptRequest>();
                    if (onPrompt is not null) await onPrompt(agent, p, ct);
                    else await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "echo:" + ((TextContent)p.Prompt[0]).Text } });
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });

    [TestMethod]
    public async Task KimiRecording_RelaysTitleAndTheSubscriptionError()
    {
        var replay = KimiReplay.FromFixture();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), replay.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);

        var empty = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        Assert.AreEqual(0, empty.Sessions.Count);

        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("kimi"));
        Assert.AreEqual("session_b95c363b-74e1-4634-8ff1-1d65d76b0f53", created.SessionId);
        Assert.AreEqual("kimi", (string?)created.Meta![GorynychProtocol.MetaAgent]);
        Assert.AreEqual(3, created.ConfigOptions!.Count, "model, thinking and mode, as Kimi 2.4.1 reports them");

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt,
            AcpJson.ToNode(Prompt(created.SessionId, "Create a file named hello.txt in the current directory containing the word hi, using your file writing tool. Then reply with exactly DONE."))));
        Assert.AreEqual(JsonRpcException.AuthRequired, error.Code);
        StringAssert.Contains(error.Message, "403");
        // The service shows the shortened first prompt at once; Kimi's own title then replaces it.
        var titles = client.Snapshot().Where(u => (string?)u["sessionUpdate"] == "session_info_update").Select(u => (string?)u["title"]).ToList();
        Assert.AreEqual(2, titles.Count);
        StringAssert.EndsWith(titles[0], "…");
        Assert.AreEqual("Create a file named hello.txt in the current directory containing the word hi, using your file writing tool. Then reply with exactly DONE.", titles[1]);

        var listed = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest());
        var only = listed.Sessions.Single();
        Assert.AreEqual(created.SessionId, only.SessionId);
        Assert.AreEqual("kimi", (string?)only.Meta![GorynychProtocol.MetaAgent]);
        StringAssert.StartsWith(only.Title, "Create a file named hello.txt");
    }

    [TestMethod]
    public async Task Approval_RoundTripsThroughTheService()
    {
        var outcomes = new List<string>();
        var adapter = Echo(onPrompt: async (agent, p, ct) =>
        {
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "before;" } });
            var r = await agent.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission,
                new RequestPermissionRequest
                {
                    SessionId = p.SessionId,
                    ToolCall = new ToolCallUpdate { ToolCallId = "t1", Title = "Write hello.txt" },
                    Options =
                    [
                        new PermissionOption { OptionId = "yes", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                        new PermissionOption { OptionId = "no", Name = "Deny", Kind = PermissionOptionKind.RejectOnce },
                    ],
                }, ct);
            outcomes.Add(((SelectedPermissionOutcome)r.Outcome).OptionId);
            await agent.Update(p.SessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "after" } });
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "no" });

        var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("fake"));
        var result = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, Prompt(created.SessionId, "go"));

        Assert.AreEqual(StopReason.EndTurn, result.StopReason);
        Assert.AreEqual("Write hello.txt", client.Approvals.Single().ToolCall.Title);
        CollectionAssert.AreEqual(new[] { "no" }, outcomes);
        Assert.AreEqual("before;after", client.Text());
    }

    [TestMethod]
    public async Task Load_ReplaysTheJournal_InANewConnection()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, Echo());
        string sessionId;
        await using (var first = await TestClient.ConnectAsync(conductor, "first"))
        {
            sessionId = (await first.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("fake"))).SessionId;
            await first.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "hello"));
            CollectionAssert.AreEqual(new[] { "session_info_update", "agent_message_chunk" }, first.Kinds(), "the sender does not get its own prompt back");
        }

        await using var second = await TestClient.ConnectAsync(conductor, "second");
        await second.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = sessionId, Cwd = Path.GetTempPath(), McpServers = [] }));
        CollectionAssert.AreEqual(new[] { "user_message_chunk", "session_info_update", "agent_message_chunk" }, second.Kinds());
        Assert.AreEqual("hello", second.Text("user_message_chunk"));
        Assert.AreEqual("echo:hello", second.Text());

        // The loading client is attached: it sees the next turn live.
        await second.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "again"));
        Assert.AreEqual("echo:helloecho:again", second.Text());

        // The journal and the session list survive a restart of the service.
        await conductor.DisposeAsync();
        await using var restarted = TestDirs.Conductor(dir, Echo());
        await using var third = await TestClient.ConnectAsync(restarted, "third");
        await third.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = sessionId, Cwd = Path.GetTempPath(), McpServers = [] }));
        Assert.AreEqual("echo:helloecho:again", third.Text());
    }

    [TestMethod]
    public async Task PendingApproval_WaitsForTheNextClient()
    {
        var asked = new TaskCompletionSource();
        var answered = new TaskCompletionSource<string>();
        var adapter = Echo(onPrompt: async (agent, p, ct) =>
        {
            var request = new RequestPermissionRequest
            {
                SessionId = p.SessionId,
                ToolCall = new ToolCallUpdate { ToolCallId = "t1", Title = "Run tests" },
                Options = [new PermissionOption { OptionId = "yes", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
            };
            var pending = agent.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, request, ct);
            asked.TrySetResult();
            var r = await pending;
            answered.TrySetResult(((SelectedPermissionOutcome)r.Outcome).OptionId);
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), adapter);

        var first = await TestClient.ConnectAsync(conductor, "first");
        var never = new TaskCompletionSource<RequestPermissionOutcome>();
        first.Approve = _ => never.Task; // the person closes the GUI without answering
        var sessionId = (await first.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("fake"))).SessionId;
        _ = first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, AcpJson.ToNode(Prompt(sessionId, "test")));
        await asked.Task.WaitAsync(Wait);
        await first.DisposeAsync();

        await using var second = await TestClient.ConnectAsync(conductor, "second");
        await second.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = sessionId, Cwd = Path.GetTempPath(), McpServers = [] }));
        Assert.AreEqual("yes", await answered.Task.WaitAsync(Wait));
        Assert.AreEqual("Run tests", second.Approvals.Single().ToolCall.Title);
    }

    [TestMethod]
    public async Task RestartedAgent_ResumesTheSessionBeforeTheNextPrompt()
    {
        var adapter = Echo();
        await using var conductor = TestDirs.Conductor(TestDirs.New(), adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var sessionId = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, New("fake"))).SessionId;
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "one"));

        await adapter.Current!.Rpc.DisposeAsync(); // the agent process dies
        await Task.Delay(100);
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt, Prompt(sessionId, "two"));

        Assert.AreEqual(2, adapter.Starts);
        var methods = adapter.Methods.Where(m => m.StartsWith("session/", StringComparison.Ordinal)).ToArray();
        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/resume", "session/prompt" }, methods);
        Assert.AreEqual("echo:oneecho:two", client.Text());
    }

    [TestMethod]
    public async Task List_MergesAgents_NewestFirst_InPages()
    {
        ScriptedAdapter Lister(string name, int count, int minuteOffset) => new(name, (agent, req, ct) => Task.FromResult(req.Method switch
        {
            AcpMethods.Agent.Initialize => ScriptedAgent.Init(),
            AcpMethods.Agent.SessionList => AcpJson.ToNode(new ListSessionsResponse
            {
                Sessions = Enumerable.Range(0, count).Select(i => new SessionInfo
                {
                    SessionId = $"{name}-{i}",
                    Cwd = @"C:\w",
                    Title = $"{name} {i}",
                    UpdatedAt = new DateTimeOffset(2026, 11, 7, 10, minuteOffset + i * 2, 0, TimeSpan.Zero).ToString("O"),
                }).ToList(),
            }),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method),
        }));
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "t", ListPageSize = 4 },
            [Lister("a", 3, 0), Lister("b", 3, 1)]);
        await using var client = await TestClient.ConnectAsync(conductor);

        var seen = new List<SessionInfo>();
        string? cursor = null;
        do
        {
            var page = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest { Cursor = cursor });
            seen.AddRange(page.Sessions);
            cursor = page.NextCursor;
        } while (cursor is not null);

        CollectionAssert.AreEqual(new[] { "b-2", "a-2", "b-1", "a-1", "b-0", "a-0" }, seen.Select(s => s.SessionId).ToArray());
        Assert.AreEqual("b", (string?)seen[0].Meta![GorynychProtocol.MetaAgent]);
    }

    [TestMethod]
    public void Journal_MergesStreamedChunks_AndStaysBounded()
    {
        var path = Path.Combine(TestDirs.New(), "j.jsonl");
        var journal = new SessionJournal(path, maxEntries: 16);
        journal.Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = "question" } })!);
        for (var i = 0; i < 40; i++)
            journal.Append(AcpJson.ToNode<SessionUpdate>(new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"{i} " }, MessageId = "m" })!);
        journal.Append(AcpJson.ToNode<SessionUpdate>(new ToolCall { ToolCallId = "t", Title = "Read" })!);

        var entries = new SessionJournal(path, maxEntries: 16).ReadAll();
        Assert.IsTrue(entries.Count <= 16, $"{entries.Count} entries");
        var text = string.Concat(entries.Where(e => (string?)e["sessionUpdate"] == "agent_message_chunk").Select(e => (string?)e["content"]!["text"]));
        Assert.AreEqual(string.Concat(Enumerable.Range(0, 40).Select(i => $"{i} ")), text, "no streamed text is lost");
        Assert.AreEqual("tool_call", (string?)entries[^1]["sessionUpdate"]);
    }
}

/// <summary>
/// Replays the ACP traffic recorded from <c>kimi acp</c> (Kimi Code 2.4.1): each request gets
/// the recorded notifications and then the recorded result or error.
/// </summary>
sealed class KimiReplay
{
    readonly List<(string Method, List<JsonObject> Notifications, JsonObject Response)> exchanges = [];

    public ScriptedAdapter Adapter { get; }

    KimiReplay()
    {
        Adapter = new ScriptedAdapter("kimi", async (agent, req, ct) =>
        {
            var index = exchanges.FindIndex(e => e.Method == req.Method);
            if (index < 0) throw new JsonRpcException(JsonRpcException.MethodNotFound, $"not in the recording: {req.Method}");
            var (_, notifications, response) = exchanges[index];
            exchanges.RemoveAt(index);
            foreach (var n in notifications)
                await agent.Rpc.SendNotificationAsync((string)n["method"]!, n["params"]!.DeepClone());
            if (response["error"] is JsonObject error)
                throw new JsonRpcException((int)error["code"]!, (string)error["message"]!);
            return response["result"]!.DeepClone();
        });
    }

    public static KimiReplay FromFixture()
    {
        var replay = new KimiReplay();
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kimi", "kimi-acp-2.4.1.jsonl"))
            .Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        (string Method, List<JsonObject> Notes)? open = null;
        foreach (var line in lines)
        {
            var m = line["message"]!.AsObject();
            if ((string)line["from"]! == "client")
            {
                open = ((string)m["method"]!, []);
                continue;
            }
            if (m["method"] is not null) { open?.Notes.Add(m); continue; }
            if (open is { } o) replay.exchanges.Add((o.Method, o.Notes, m));
            open = null;
        }
        return replay;
    }
}
