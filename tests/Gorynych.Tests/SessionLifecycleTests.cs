using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Drafts, deletion and the peer list, as an external client like Zed drives them: it creates
/// a session whenever a thread view opens, closes sessions when it drops a thread, quits
/// without closing anything, and deletes from its history only when the agent advertises it.
/// </summary>
[TestClass]
public sealed class SessionLifecycleTests
{
    static readonly string Cwd = Path.GetTempPath();

    static async Task<string> NewAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task CloseAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId });

    static Task DeleteAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = sessionId });

    static async Task<string[]> ListAsync(TestClient client) =>
        (await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest()))
            .Sessions.Select(s => s.SessionId).ToArray();

    // ---------------------------------------------------------------- drafts

    [TestMethod]
    public async Task Draft_IsDeletedNatively_WhenItsOnlyClientLeaves()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);
        string draft;
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            draft = await NewAsync(zed);
            Assert.IsTrue(conductor.Store.Get(draft)!.Draft);
            Assert.AreEqual(0, (await ListAsync(zed)).Length, "a draft is not listed, though its agent lists it");
        }
        // Disposing the client waits for the service side of the connection, which discards the draft.
        CollectionAssert.AreEqual(new[] { "session/new", "session/close", "session/delete" }, kimi.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft));
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(dir, "journal")).Length, "the draft's journal is gone");
    }

    [TestMethod]
    public async Task Close_DiscardsADraft_AndOnlyClosesAPromptedSession()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = await NewAsync(zed);
        var used = await NewAsync(zed);
        await PromptAsync(zed, used, "hello");

        await CloseAsync(zed, draft);
        await CloseAsync(zed, used);

        CollectionAssert.AreEqual(new[] { "session/new", "session/close", "session/delete" }, kimi.CallsFor(draft));
        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close" }, kimi.CallsFor(used));
        Assert.IsNull(conductor.Store.Get(draft));
        Assert.IsFalse(conductor.Store.Get(used)!.Draft);
        CollectionAssert.AreEqual(new[] { used }, await ListAsync(zed));
    }

    [TestMethod]
    public async Task Draft_StaysWhileAnotherClientHasItOpen()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        var first = await TestClient.ConnectAsync(conductor, "first");
        var draft = await NewAsync(first);
        var second = await TestClient.ConnectAsync(conductor, "second");
        await second.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume,
            AcpJson.ToNode(new ResumeSessionRequest { SessionId = draft, Cwd = Cwd }));

        await first.DisposeAsync();
        Assert.IsNotNull(conductor.Store.Get(draft), "the second client still has it open");
        await second.DisposeAsync();
        Assert.IsNull(conductor.Store.Get(draft));
        CollectionAssert.Contains(kimi.CallsFor(draft), "session/delete");
    }

    [TestMethod]
    public async Task Draft_OfAnAgentThatCannotDelete_IsClosedAndDropped()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), claude.Adapter);
        string draft;
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
            draft = await NewAsync(zed);

        CollectionAssert.AreEqual(new[] { "session/new", "session/close" }, claude.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft), "no tombstone: a draft never reached the harness's own list");
    }

    [TestMethod]
    public async Task DraftsLeftByAnEarlierRun_AreSweptAtStart()
    {
        var dir = TestDirs.New();
        var earlier = new SessionStore(Path.Combine(dir, "sessions.json"));
        earlier.Update("kimi-old-draft", "kimi", r => { r.Cwd = Cwd; r.Draft = true; });
        earlier.Update("kimi-old-used", "kimi", r => { r.Cwd = Cwd; r.Title = "Used"; });
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-old-draft", "kimi-old-used"]);
        await using var conductor = TestDirs.Conductor(dir, kimi.Adapter);

        await conductor.SweepDraftsAsync();

        CollectionAssert.AreEqual(new[] { "session/delete" }, kimi.CallsFor("kimi-old-draft"));
        Assert.AreEqual(0, kimi.CallsFor("kimi-old-used").Length);
        Assert.IsNull(conductor.Store.Get("kimi-old-draft"));
        Assert.IsNotNull(conductor.Store.Get("kimi-old-used"));
    }

    // ---------------------------------------------------------------- session/delete

    [TestMethod]
    public async Task Delete_ClosesTheSession_ThenDeletesItInAnAgentThatCan()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var init = await zed.Rpc.RequestAsync<InitializeRequest, InitializeResponse>(AcpMethods.Agent.Initialize, new InitializeRequest { ProtocolVersion = 1 });
        Assert.IsNotNull(init.AgentCapabilities!.SessionCapabilities!.Delete, "Zed sends session/delete only when it is advertised");
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await DeleteAsync(zed, id);

        CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close", "session/delete" }, kimi.CallsFor(id));
        Assert.IsNull(conductor.Store.Get(id));
        Assert.AreEqual(0, (await ListAsync(zed)).Length);
        var load = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            AcpJson.ToNode(new LoadSessionRequest { SessionId = id, Cwd = Cwd, McpServers = [] })));
        Assert.AreEqual(JsonRpcException.ResourceNotFound, load.Code);
        await DeleteAsync(zed, id);        // already deleted
        await DeleteAsync(zed, "unknown"); // never existed
    }

    [TestMethod]
    public async Task Delete_HidesASessionItsAgentKeeps_AcrossARestart()
    {
        var dir = TestDirs.New();
        var claude = new LifecycleAgent("claude", canDelete: false);
        string id;
        await using (var conductor = TestDirs.Conductor(dir, claude.Adapter))
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            id = await NewAsync(zed);
            await PromptAsync(zed, id, "keep my transcript");
            CollectionAssert.AreEqual(new[] { id }, await ListAsync(zed));

            await DeleteAsync(zed, id);

            CollectionAssert.AreEqual(new[] { "session/new", "session/prompt", "session/close" }, claude.CallsFor(id), "closed, never deleted in the harness");
            Assert.IsTrue(conductor.Store.Get(id)!.Deleted);
            Assert.AreEqual(0, (await ListAsync(zed)).Length, "hidden although the agent still lists it");
            Assert.AreEqual(0, (await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject()))!["sessions"]!.AsArray().Count);
            var prompt = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, id, "again"));
            Assert.AreEqual(JsonRpcException.ResourceNotFound, prompt.Code);
        }

        var restarted = new LifecycleAgent("claude", canDelete: false, existing: [id]);
        await using (var conductor = TestDirs.Conductor(dir, restarted.Adapter))
        await using (var zed = await TestClient.ConnectAsync(conductor, "zed"))
        {
            Assert.AreEqual(0, (await ListAsync(zed)).Length);
            var load = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
                AcpJson.ToNode(new LoadSessionRequest { SessionId = id, Cwd = Cwd, McpServers = [] })));
            Assert.AreEqual(JsonRpcException.ResourceNotFound, load.Code);
        }
    }

    [TestMethod]
    public async Task Delete_TakesTheAgentsNotFoundAsSuccess()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true, notFoundOnDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewAsync(zed);
        await PromptAsync(zed, id, "hello");

        await DeleteAsync(zed, id);

        Assert.IsNull(conductor.Store.Get(id));
    }

    // ---------------------------------------------------------------- list_agents

    [TestMethod]
    public async Task Peers_ListOnlySessionsWithHistory_OpenOnesFirst()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = TestDirs.Conductor(TestDirs.New(), kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var older = await NewAsync(zed);
        await PromptAsync(zed, older, "older, closed");
        await Task.Delay(30);
        var open = await NewAsync(zed);
        await PromptAsync(zed, open, "open");
        await Task.Delay(30);
        var newer = await NewAsync(zed);
        await PromptAsync(zed, newer, "newer, closed");
        var deleted = await NewAsync(zed);
        await PromptAsync(zed, deleted, "deleted");
        await NewAsync(zed); // a draft
        await CloseAsync(zed, older);
        await CloseAsync(zed, newer);
        await DeleteAsync(zed, deleted);

        var peers = (await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodPeers, new JsonObject()))!["sessions"]!.AsArray();

        CollectionAssert.AreEqual(new[] { open, newer, older }, peers.Select(p => (string)p!["session_id"]!).ToArray());
        CollectionAssert.AreEqual(new[] { true, false, false }, peers.Select(p => (bool)p!["open"]!).ToArray());
    }

    // ---------------------------------------------------------------- the overlay file

    [TestMethod]
    public void Store_RetriesAWriteWhileTheFileIsHeld()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "first");
        // An antivirus scanner or the indexer opens the file for a moment without delete sharing.
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Delay(120).ContinueWith(_ => held.Dispose(), TaskScheduler.Default);

        store.Update("a", "kimi", r => r.Title = "second");

        released.Wait();
        Assert.AreEqual("second", new SessionStore(path).Get("a")!.Title);
    }

    [TestMethod]
    public void Store_KeepsAChangeItCouldNotWrite_AndWritesItWithTheNext()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "first");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            store.Update("a", "kimi", r => r.Title = "second"); // every retry fails: logged, not thrown
        Assert.AreEqual("first", new SessionStore(path).Get("a")!.Title);

        store.Update("b", "kimi", r => r.Title = "other");

        var reread = new SessionStore(path);
        Assert.AreEqual("second", reread.Get("a")!.Title);
        Assert.AreEqual("other", reread.Get("b")!.Title);
    }

    [TestMethod]
    public void Store_WritesActivityStampsAtMostOnceASecond_AndFlushesTheRest()
    {
        var path = Path.Combine(TestDirs.New(), "sessions.json");
        var store = new SessionStore(path);
        store.Update("a", "kimi", r => r.Title = "t");
        Thread.Sleep(20);
        store.Touch("a", "kimi");
        var stamped = store.Get("a")!.UpdatedAt;
        Assert.AreNotEqual(stamped, new SessionStore(path).Get("a")!.UpdatedAt, "a stamp right after a save waits");

        store.Flush();

        Assert.AreEqual(stamped, new SessionStore(path).Get("a")!.UpdatedAt);
    }

    // ---------------------------------------------------------------- Zed's rules for forms and approvals

    [TestMethod]
    public void ClaudeQuestionsAndApprovals_FollowZedsRules()
    {
        foreach (var input in new[]
        {
            """{"questions":[{"question":"Which color?","header":"Color","multiSelect":false,"options":[{"label":"Red","description":"warm"},{"label":"Blue"}]}]}""",
            """{"questions":[{"question":"Which parts?","header":"Parts","multiSelect":true,"options":[{"label":"A"},{"label":"B"}]}]}""",
            """{"questions":[{"question":"Q1?","header":"One","options":[{"label":"x"},{"label":"y"}]},{"question":"Q2?","multiSelect":true,"options":[{"label":"z"},{"label":"w"}]}]}""",
        })
            ZedRules.CheckForm(ClaudeHooks.QuestionForm("s", "t", JsonNode.Parse(input)!.AsObject()));

        var write = JsonNode.Parse("""{"hook_event_name":"PermissionRequest","tool_name":"Write","tool_input":{"file_path":"C:\\w\\a.txt","content":"hi"}}""")!.AsObject();
        ZedRules.CheckPermission(ClaudeHooks.Permission("s", "t", write));
        ZedRules.CheckPermission(ClaudeHooks.TrustFolder("s", @"C:\w"));
    }

    [TestMethod]
    public async Task FormFallbackApprovals_FollowZedsRules()
    {
        CreateElicitationResponse? answer = null;
        var agent = new ScriptedAdapter("fake", async (a, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionNew: return AcpJson.ToNode(new NewSessionResponse { SessionId = "f1" });
                case AcpMethods.Agent.SessionList: return AcpJson.ToNode(new ListSessionsResponse { Sessions = [] });
                case AcpMethods.Agent.SessionPrompt:
                    answer = await a.Rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate, new ElicitationFormMode
                    {
                        Message = "A few questions",
                        SessionId = "f1",
                        ToolCallId = "t1",
                        RequestedSchema = new ElicitationSchema
                        {
                            Type = ElicitationSchemaType.Object,
                            Properties = new()
                            {
                                ["q1"] = new StringPropertySchema { Title = "Color", OneOf = [new EnumOption { Const = "Red", Title = "Red" }, new EnumOption { Const = "Blue", Title = "Blue" }] },
                                ["q2"] = new StringPropertySchema { Title = "Size", Enum = ["S", "M"] },
                                ["q3"] = new MultiSelectPropertySchema { Title = "Parts", Items = new TitledMultiSelectItems { AnyOf = [new EnumOption { Const = "A", Title = "A" }] } },
                                ["q4"] = new BooleanPropertySchema { Title = "Sure?" },
                            },
                            Required = ["q1", "q2", "q3", "q4"],
                        },
                    }, ct);
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
        await using var conductor = TestDirs.Conductor(TestDirs.New(), agent);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed", forms: false);
        var id = await NewAsync(zed);

        await PromptAsync(zed, id, "ask me");

        Assert.AreEqual(4, zed.Approvals.Count);
        foreach (var approval in zed.Approvals) ZedRules.CheckPermission(approval);
        var content = ((ElicitationAcceptAction)answer!).Content!;
        Assert.AreEqual("Red", (string?)content["q1"]);
        Assert.AreEqual("S", (string?)content["q2"]);
        Assert.AreEqual("A", (string?)content["q3"]![0]);
        Assert.IsTrue((bool)content["q4"]!);
    }
}

/// <summary>
/// What Zed (main b5cd310) accepts, from its ACP client code: permission options of the four
/// standard kinds with distinct IDs (<c>permission_option_from_v1</c>, <c>request_permission</c>),
/// and forms whose required properties are defined and renderable, with no string that has both
/// <c>enum</c> and <c>oneOf</c> (<c>validate_form_schema</c>). Anything else fails the whole
/// request with -32602.
/// </summary>
static class ZedRules
{
    static readonly HashSet<string> Kinds = ["allow_once", "allow_always", "reject_once", "reject_always"];

    public static void CheckPermission(RequestPermissionRequest request)
    {
        Assert.IsTrue(request.Options.Count > 0, "Zed rejects an approval without options");
        Assert.AreEqual(request.Options.Count, request.Options.Select(o => o.OptionId).Distinct().Count(), "Zed rejects duplicate option IDs");
        foreach (var option in request.Options)
            Assert.IsTrue(Kinds.Contains(option.Kind.Value), $"Zed rejects the option kind '{option.Kind.Value}'");
    }

    public static void CheckForm(ElicitationFormMode form)
    {
        var properties = form.RequestedSchema.Properties ?? [];
        foreach (var name in form.RequestedSchema.Required ?? [])
        {
            Assert.IsTrue(properties.TryGetValue(name, out var property), $"required property '{name}' is not defined");
            Assert.IsTrue(Renderable(property), $"required property '{name}' has a type Zed cannot render");
        }
        foreach (var (name, property) in properties)
            Assert.IsFalse(property is StringPropertySchema { Enum: not null, OneOf: not null }, $"'{name}' sets both enum and oneOf");
    }

    static bool Renderable(ElicitationPropertySchema? property) => property switch
    {
        StringPropertySchema or NumberPropertySchema or IntegerPropertySchema or BooleanPropertySchema => true,
        MultiSelectPropertySchema m => m.Items is StringMultiSelectItems or TitledMultiSelectItems,
        _ => false,
    };
}

/// <summary>
/// A scripted agent with numbered sessions that records every call by session. Like Kimi it
/// announces its commands as soon as a session exists. One that cannot delete keeps listing a
/// session after a delete, as the Claude adapter keeps listing the sessions it started.
/// </summary>
sealed class LifecycleAgent
{
    readonly List<(string Method, string? SessionId)> calls = [];
    readonly List<string> sessions;
    int next;

    public LifecycleAgent(string name, bool canDelete, bool notFoundOnDelete = false, string[]? existing = null)
    {
        sessions = [.. existing ?? []];
        Adapter = new ScriptedAdapter(name, async (agent, req, ct) =>
        {
            var sessionId = (string?)req.Params?["sessionId"];
            if (req.Method is not (AcpMethods.Agent.Initialize or AcpMethods.Agent.SessionList or AcpMethods.Agent.SessionNew))
                lock (calls) calls.Add((req.Method, sessionId));
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return AcpJson.ToNode(new InitializeResponse
                    {
                        ProtocolVersion = 1,
                        AgentCapabilities = new AgentCapabilities
                        {
                            SessionCapabilities = new SessionCapabilities
                            {
                                List = new SessionListCapabilities(),
                                Resume = new SessionResumeCapabilities(),
                                Close = new SessionCloseCapabilities(),
                                Delete = canDelete ? new SessionDeleteCapabilities() : null,
                            },
                        },
                        AgentInfo = new Implementation { Name = name, Version = "1" },
                    });
                case AcpMethods.Agent.SessionNew:
                    var id = $"{name}-{Interlocked.Increment(ref next)}";
                    lock (sessions) sessions.Add(id);
                    lock (calls) calls.Add((req.Method, id));
                    await agent.Update(id, new AvailableCommandsUpdate { AvailableCommands = [] });
                    return AcpJson.ToNode(new NewSessionResponse { SessionId = id });
                case AcpMethods.Agent.SessionList:
                    lock (sessions)
                        return AcpJson.ToNode(new ListSessionsResponse { Sessions = sessions.Select(s => new SessionInfo { SessionId = s, Cwd = Path.GetTempPath() }).ToList() });
                case AcpMethods.Agent.SessionPrompt:
                    await agent.Update(sessionId!, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "ok" } });
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                case AcpMethods.Agent.SessionResume:
                case AcpMethods.Agent.SessionClose:
                    return new JsonObject();
                case AcpMethods.Agent.SessionDelete when canDelete:
                    if (notFoundOnDelete) throw new JsonRpcException(JsonRpcException.ResourceNotFound, "no such session");
                    lock (sessions) sessions.Remove(sessionId!);
                    return new JsonObject();
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
    }

    public ScriptedAdapter Adapter { get; }

    /// <summary>The methods called for <paramref name="sessionId"/>, in order (session/new counted under the ID it returned).</summary>
    public string[] CallsFor(string sessionId)
    {
        lock (calls) return calls.Where(c => c.SessionId == sessionId).Select(c => c.Method).ToArray();
    }
}
