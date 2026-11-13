using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;
using AcpSessionInfo = Gorynych.Acp.SessionInfo;

namespace Gorynych.Tests;

/// <summary>
/// The AHP face end to end (docs/ahp-face.md, step 1): Microsoft's own <see cref="AhpClient"/> over a real WebSocket
/// against a service with the test agents, next to an ACP client (Zed's part) on the same conductor.
/// </summary>
[TestClass]
public sealed partial class AhpFaceTests
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A service with the face, and Zed on its ACP side.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly LifecycleAgent Claude = new("claude", canDelete: false);
        public LifecycleAgent Kimi = new("kimi", canDelete: true);
        public readonly string Data = TestDirs.New();
        public readonly string Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public Conductor Service = null!;
        public AhpFace Face = null!;
        public AhpEndpoint Endpoint = null!;
        public TestClient Zed = null!;

        bool faceStopped;

        /// <param name="kimi">Kimi with sessions of its own, say: the service did not make them.</param>
        public static async Task<Rig> StartAsync(AhpFaceOptions? options = null, Func<Rig, Task>? before = null, LifecycleAgent? kimi = null)
        {
            var rig = new Rig();
            if (kimi is not null) rig.Kimi = kimi;
            rig.Service = new Conductor(new ConductorOptions { DataDirectory = rig.Data, ServiceId = "ahp-test" }, [rig.Claude.Adapter, rig.Kimi.Adapter]);
            rig.Zed = await TestClient.ConnectAsync(rig.Service, "zed");
            if (before is not null) await before(rig);
            await rig.StartFaceAsync(options);
            return rig;
        }

        /// <summary>The face (again, as after a restart of the service), on a port of its own.</summary>
        public async Task StartFaceAsync(AhpFaceOptions? options = null)
        {
            Face = new AhpFace(Service, options ?? new AhpFaceOptions());
            Endpoint = await AhpEndpoint.StartAsync(Face, 0, Token);
            Face.Port = Endpoint.Port;
            await Face.StartAsync();
            faceStopped = false;
        }

        /// <summary>Stops the face as the service does when it exits.</summary>
        public async Task StopFaceAsync()
        {
            if (faceStopped) return;
            faceStopped = true;
            await new AhpFaceRun(Face, Endpoint).DisposeAsync();
        }

        public Task<Peer> ConnectAsync(string clientId = "vscode", bool initialize = true) => Peer.ConnectAsync(this, clientId, initialize);

        public async Task<string> NewPromptedAsync(string agent, string text)
        {
            var created = await Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
            {
                ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
            }).WaitAsync(Wait);
            var id = (string)created!["sessionId"]!;
            await PromptAsync(id, text);
            return id;
        }

        int prompts;

        /// <summary>A prompt from Zed, with its ID as Zed's bridge gives it.</summary>
        public Task<JsonNode?> PromptAsync(string sessionId, string text) => Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = sessionId, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = $"zed-{Interlocked.Increment(ref prompts)}" },
        }).WaitAsync(Wait);

        public async ValueTask DisposeAsync()
        {
            await StopFaceAsync();
            await Zed.DisposeAsync();
            await Service.DisposeAsync();
        }
    }

    /// <summary>An AHP client, as VS Code's window is one: the package's <see cref="AhpClient"/>, with every event it gets.</summary>
    internal sealed class Peer : IAsyncDisposable
    {
        public AhpClient Client = null!;
        public InitializeResult? Initialized;
        readonly List<ClientEvent> events = [];
        Task pump = Task.CompletedTask;
        public string ClientId = "";

        public static async Task<Peer> ConnectAsync(Rig rig, string clientId, bool initialize)
        {
            var transport = await WebSocketTransport.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Endpoint.Port}/ahp?tkn={rig.Token}"), new WebSocketTransportOptions(), CancellationToken.None);
            var peer = new Peer { ClientId = clientId };
            peer.Client = AhpClient.Connect(transport, new ClientConfig { DefaultRequestTimeout = Wait, KeepAlive = KeepAlivePolicy.Disabled }, SystemTextJsonAhpSerializer.Default);
            var stream = peer.Client.CreateEventStream();
            peer.pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in stream.Events.ReadAllAsync())
                        lock (peer.events) peer.events.Add(e);
                }
                catch (Exception) { }
            });
            if (initialize) peer.Initialized = await peer.Client.InitializeAsync(clientId, ["1.0.0"], [AhpNames.Root], CancellationToken.None).WaitAsync(Wait);
            return peer;
        }

        public ClientEvent[] Events()
        {
            lock (events) return [.. events];
        }

        /// <summary>The actions it got on a channel, in order.</summary>
        public ActionEnvelope[] Actions(string channel) =>
            [.. Events().Where(e => e.Channel == channel).Select(e => e.Event).OfType<SubscriptionEventAction>().Select(a => a.Envelope)];

        public T[] Of<T>(string channel) => [.. Actions(channel).Select(a => a.Action.Value).OfType<T>()];

        public long LastSeq => Events().Select(e => e.Event).OfType<SubscriptionEventAction>().Select(a => a.Envelope.ServerSeq).DefaultIfEmpty(0).Max();

        public async Task<ListSessionsResult> ListAsync(long? limit = null, string? cursor = null) =>
            await Client.RequestAsync<ListSessionsParams, ListSessionsResult>("listSessions", new ListSessionsParams { Channel = AhpNames.Root, Limit = limit, Cursor = cursor }, CancellationToken.None).WaitAsync(Wait) ?? throw new InvalidOperationException("no list");

        public async Task<Snapshot> SubscribeAsync(string channel) => (await Client.SubscribeAsync(channel, null!, CancellationToken.None).WaitAsync(Wait)).Item1.Snapshot!;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            try { await pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFace_ListsTheSessions_WithTheirAgentsAndChats()
    {
        await using var rig = await Rig.StartAsync();
        var claude = await rig.NewPromptedAsync("claude", "Port the parser");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "the face read the list");
        // A session that comes while the client is connected is said on the root channel.
        var kimi = await rig.NewPromptedAsync("kimi", "Review the notes");
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionAdded added && added.Params.Summary.Resource == AhpNames.SessionUri(kimi)),
            "a session that came was said on the root channel");

        var root = vscode.Initialized!.Snapshots.Single().State.Root!;
        CollectionAssert.AreEquivalent(new[] { "claude", "kimi" }, root.Agents.Select(a => a.Provider).ToArray());
        Assert.AreEqual("1.0.0", vscode.Initialized.ProtocolVersion);
        var list = await vscode.ListAsync();
        Assert.AreEqual(2, list.Items.Count, string.Join("\n", list.Items.Select(i => i.Resource)));
        var item = list.Items.Single(i => i.Resource == AhpNames.SessionUri(claude));
        Assert.AreEqual("claude", item.Provider);
        Assert.AreEqual("Port the parser", item.Title);
        Assert.AreEqual(AhpNames.ChatUri(claude), item.DefaultChat);
        Assert.AreEqual(AhpNames.ChatUri(claude), item.Chats!.Single().Resource);
        Assert.AreEqual(ChatInteractivity.Full, item.Chats![0].Interactivity, "a client may prompt a session (step 2)");
        Assert.IsTrue(item.Status.HasFlag(SessionStatus.IsRead));
        Assert.AreEqual(claude, item.Meta![GorynychProtocol.MetaSessionId].GetString());
        Assert.AreEqual(AhpNames.FileUri(Path.GetTempPath().TrimEnd('\\', '/')), item.WorkingDirectories!.Single());

        // Pages: newest first, by a cursor.
        var first = await vscode.ListAsync(limit: 1);
        Assert.AreEqual(1, first.Items.Count);
        Assert.IsNotNull(first.NextCursor);
        var second = await vscode.ListAsync(limit: 1, cursor: first.NextCursor);
        Assert.AreEqual(1, second.Items.Count);
        Assert.IsNull(second.NextCursor);
        CollectionAssert.AreEquivalent(list.Items.Select(i => i.Resource).ToArray(), new[] { first.Items[0].Resource, second.Items[0].Resource });
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AChat_IsBuiltFromItsSessionsHistory_AndReadAsItWas()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        await using var vscode = await rig.ConnectAsync();

        var chat = (await vscode.SubscribeAsync(AhpNames.ChatUri(id))).State.Chat!;

        var turn = chat.Turns.Single();
        Assert.AreEqual("Port the parser", turn.Message.Text);
        Assert.AreEqual(MessageKind.User, turn.Message.Origin.Kind);
        Assert.AreEqual("ok", ((MarkdownResponsePart)turn.ResponseParts.Single().Value!).Content);
        Assert.AreEqual(TurnState.Complete, turn.State);
        Assert.IsTrue(chat.Status.HasFlag(SessionStatus.IsRead), "everything is read when the face starts: the replay does not make it unread");
        Assert.AreEqual(ChatInteractivity.Full, chat.Interactivity);
        Assert.IsTrue(rig.Face.HasOpen(id), "open in the conductor while a client looks at it");
        var session = (await vscode.SubscribeAsync(AhpNames.SessionUri(id))).State.Session!;
        Assert.AreEqual("claude", session.Provider);
        Assert.AreEqual(SessionLifecycle.Ready, session.Lifecycle);
        Assert.AreEqual(AhpNames.ChatUri(id), session.DefaultChat);
        await Assert.ThrowsExactlyAsync<AhpRpcException>(() => vscode.SubscribeAsync(AhpNames.SessionUri(id) + "/annotations"), "a channel the face does not serve");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALiveTurn_StreamsIntoTheChat_AndItsCatalogAndSummaryFollow()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        await using var vscode = await rig.ConnectAsync();
        var chatUri = AhpNames.ChatUri(id);
        var sessionUri = AhpNames.SessionUri(id);
        await vscode.SubscribeAsync(chatUri);
        await vscode.SubscribeAsync(sessionUri);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.BeforePrompt = _ => hold.Task;
        rig.Claude.AnswerUpdates =
        [
            new ToolCall { ToolCallId = "c1", Title = "Run the tests", Kind = ToolKind.Execute, Status = Gorynych.Acp.ToolCallStatus.InProgress },
            new SessionUpdateToolCallUpdate
            {
                ToolCallId = "c1", Status = Gorynych.Acp.ToolCallStatus.Completed,
                Content = [new Content { ContentValue = new TextContent { Text = "12 passed" } }],
            },
            new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "All green." } },
        ];

        var prompting = rig.PromptAsync(id, "Write the tests");
        await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Length == 1, "the turn began in VS Code while it runs");
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionSummaryChanged c
            && c.Params.Changes.Status is { } s && s.HasFlag(SessionStatus.InProgress)), "the list shows it running");
        Assert.AreEqual("Write the tests", vscode.Of<ChatTurnStartedAction>(chatUri)[0].Message.Text);
        Assert.IsTrue(vscode.Of<SessionChatUpdatedAction>(sessionUri).Any(u => u.Changes.Status is { } s && s.HasFlag(SessionStatus.InProgress)), "the catalog too");
        Assert.IsTrue(vscode.Of<SessionIsReadChangedAction>(sessionUri).Any(r => !r.IsRead), "a turn makes its session unread");
        hold.SetResult();
        await prompting;

        await LinkHarness.Until(() => vscode.Of<ChatTurnCompleteAction>(chatUri).Length == 1, "the turn's end");
        var kinds = vscode.Actions(chatUri).Select(a => a.Action.Value!.GetType().Name.Replace("Action", "")).ToArray();
        CollectionAssert.AreEqual(new[] { "ChatTurnStarted", "ChatToolCallStart", "ChatToolCallReady", "ChatToolCallComplete", "ChatResponsePart", "ChatTurnComplete" }, kinds);
        var seqs = vscode.Actions(chatUri).Select(a => a.ServerSeq).ToArray();
        CollectionAssert.AreEqual(seqs.Order().ToArray(), seqs, "numbered in order");
        await LinkHarness.Until(() => vscode.Of<SessionChatUpdatedAction>(sessionUri).LastOrDefault()?.Changes.Status is { } s && !s.HasFlag(SessionStatus.InProgress),
            "the catalog says idle again");
        await LinkHarness.Until(() => vscode.Events().Select(e => e.Event).OfType<SubscriptionEventSessionSummaryChanged>().LastOrDefault(c => c.Params.Changes.Status is not null)
            ?.Params.Changes.Status is { } s && !s.HasFlag(SessionStatus.InProgress), "and so does the list");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnArchive_GoesBothWays()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        await using var vscode = await rig.ConnectAsync();
        var sessionUri = AhpNames.SessionUri(id);
        await vscode.SubscribeAsync(sessionUri);

        // From VS Code: archived in the service, and echoed with its origin.
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionIsArchivedChangedAction { IsArchived = true }), null, CancellationToken.None);
        await LinkHarness.Until(() => rig.Service.Store.Get(id)!.Archived, "archived in the service");
        await LinkHarness.Until(() => vscode.Actions(sessionUri).Any(a => a.Action.Value is SessionIsArchivedChangedAction { IsArchived: true } && a.Origin?.ClientId == "vscode"),
            "echoed with its origin");
        await LinkHarness.Until(() => (vscode.ListAsync().Result.Items.Single().Status & SessionStatus.IsArchived) != 0, "the list says it");

        // From Zed: restored in the service, and said to VS Code.
        await rig.Zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = false }).WaitAsync(Wait);
        await LinkHarness.Until(() => vscode.Actions(sessionUri).Any(a => a.Action.Value is SessionIsArchivedChangedAction { IsArchived: false }), "the session's channel");
        await LinkHarness.Until(() => vscode.Events().Select(e => e.Event).OfType<SubscriptionEventSessionSummaryChanged>()
            .Any(c => c.Params.Changes.Status is { } s && !s.HasFlag(SessionStatus.IsArchived)), "and the root's");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADroppedConnection_IsReplayedOnReconnect_FromWhatTheClientSaw()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        long seen;
        await using (var before = await rig.ConnectAsync())
        {
            await before.SubscribeAsync(chatUri);
            await rig.PromptAsync(id, "One");
            await LinkHarness.Until(() => before.Of<ChatTurnCompleteAction>(chatUri).Length == 1, "the first turn");
            seen = before.LastSeq;
        }
        await rig.PromptAsync(id, "Two");
        await LinkHarness.Until(() => rig.Service.Feed(id).Journal.ReadAll().Count() >= 8, "the second turn in the journal");
        await Task.Delay(300);

        await using var after = await rig.ConnectAsync(initialize: false);
        var result = await after.Client.ReconnectAsync("vscode", seen, [AhpNames.Root, chatUri, "ahp-chat:/gone"], CancellationToken.None).WaitAsync(Wait);

        var replay = (ReconnectReplayResult)result.Value!;
        Assert.AreEqual(ReconnectResultType.Replay, replay.Type);
        CollectionAssert.AreEqual(new[] { "ahp-chat:/gone" }, replay.Missing.ToArray());
        var started = replay.Actions.Select(a => a.Action.Value).OfType<ChatTurnStartedAction>().Single();
        Assert.AreEqual("Two", started.Message.Text, "what came while it was away, and nothing it had");
        Assert.IsTrue(replay.Actions.All(a => a.ServerSeq > seen));
        Assert.IsTrue(replay.Actions.Any(a => a.Action.Value is ChatTurnCompleteAction));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientTheFaceDoesNotKnow_IsToldNotFound_AndInitializesAfresh()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        await using (var before = await rig.ConnectAsync())
            await before.ListAsync();
        await rig.StopFaceAsync();
        await rig.StartFaceAsync(); // the service restarted: the face knows no client

        await using var after = await rig.ConnectAsync(initialize: false);
        var unknown = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => after.Client.ReconnectAsync("vscode", 3, [AhpNames.Root], CancellationToken.None).WaitAsync(Wait));
        Assert.AreEqual(AhpErrorCodes.NotFound, unknown.Code);
        var init = await after.Client.InitializeAsync("vscode", ["1.0.0", "0.9.0"], [AhpNames.Root], CancellationToken.None).WaitAsync(Wait);
        Assert.AreEqual("1.0.0", init.ProtocolVersion);
        Assert.AreEqual(1, (await after.ListAsync()).Items.Count);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheEndpoint_RefusesAWebPage_AndAMissingOrWrongToken()
    {
        await using var rig = await Rig.StartAsync();
        async Task<HttpStatusCode> Try(string query, string? origin = null, string? bearer = null)
        {
            using var ws = new ClientWebSocket();
            ws.Options.CollectHttpResponseDetails = true;
            if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
            if (bearer is not null) ws.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Endpoint.Port}/ahp{query}"), CancellationToken.None).WaitAsync(Wait);
                return ws.HttpStatusCode;
            }
            catch (WebSocketException)
            {
                return ws.HttpStatusCode;
            }
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, await Try(""), "no token");
        Assert.AreEqual(HttpStatusCode.Unauthorized, await Try("?tkn=" + new string('0', 64)), "another token");
        Assert.AreEqual(HttpStatusCode.Forbidden, await Try("?tkn=" + rig.Token, origin: "https://example.com"), "a web page, whatever its token");
        Assert.AreEqual(HttpStatusCode.Forbidden, await Try("?tkn=" + rig.Token, origin: "http://localhost:3000"));
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, await Try("?tkn=" + rig.Token, origin: "vscode-file://vscode-app"), "VS Code's own");
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, await Try("", bearer: rig.Token), "the token as a bearer");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFace_NeverClosesASession_WhileATurnRuns()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { CloseAfter = TimeSpan.Zero, SweepEvery = TimeSpan.FromHours(1) },
            async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        await using var vscode = await rig.ConnectAsync();
        await vscode.SubscribeAsync(chatUri);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.BeforePrompt = _ => hold.Task;
        // Zed prompts and quits: the face is the session's last client, whose close would cancel the turn.
        var zed = await TestClient.ConnectAsync(rig.Service, "zed-2");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        _ = zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = id, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Write the tests" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "zed-2-1" },
        });
        await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Length == 1, "the turn runs");
        await zed.DisposeAsync();
        await vscode.Client.UnsubscribeAsync(chatUri, CancellationToken.None);
        await Task.Delay(200);

        rig.Face.Sweep();
        Assert.IsTrue(rig.Face.HasOpen(id), "kept open while its turn runs");
        hold.SetResult();
        await LinkHarness.Until(() => !rig.Service.Feed(id).TurnRunning, "the turn's end");
        await Task.Delay(200);
        rig.Face.Sweep();

        Assert.IsFalse(rig.Face.HasOpen(id), "closed once no turn runs and nobody looks");
        var mark = rig.Service.Feed(id).Journal.ReadAll().OfType<JsonObject>()
            .Last(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.AreEqual("end_turn", (string?)mark["_meta"]![GorynychProtocol.MetaStopReason], "the turn ran to its end, not cancelled by a close");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CommandsTheFaceDoesNotCarryOut_AreRefused_AndTheRestAnswered()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();

        async Task<int> Code(Func<Task> call) => (await Assert.ThrowsExactlyAsync<AhpRpcException>(call)).Code;
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.RequestAsync<CreateChatParams, ListSessionsResult>("createChat",
            new CreateChatParams { Channel = "ahp-session:/x", Chat = "ahp-chat:/y" }, CancellationToken.None)));
        Assert.AreEqual(JsonRpcErrorCodes.MethodNotFound, await Code(() => vscode.Client.RequestAsync<ListSessionsParams, ListSessionsResult>("terminalCreate",
            new ListSessionsParams { Channel = AhpNames.Root }, CancellationToken.None)));
        // No session of the agent seen yet: no options to describe.
        var config = await vscode.Client.RequestAsync<ResolveSessionConfigParams, ResolveSessionConfigResult>("resolveSessionConfig",
            new ResolveSessionConfigParams { Channel = AhpNames.Root, Provider = "claude" }, CancellationToken.None) ?? throw new InvalidOperationException("no answer");
        Assert.AreEqual(0, config.Schema.Properties.Count);
        Assert.AreEqual(0, (await vscode.Client.RequestAsync<CompletionsParams, CompletionsResult>("completions",
            new CompletionsParams { Channel = AhpNames.Root, Text = "/", Offset = 1 }, CancellationToken.None))!.Items.Count);
        await vscode.Client.PingAsync(CancellationToken.None);

        await using var old = await rig.ConnectAsync(initialize: false);
        var refused = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => old.Client.InitializeAsync("old", ["0.9.0"], [], CancellationToken.None));
        Assert.AreEqual(AhpErrorCodes.UnsupportedProtocolVersion, refused.Code);
        StringAssert.Contains(refused.ErrorData!.Value.GetRawText(), "\"1.0.0\"");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ClientActions_AreAppliedOrRefused_AsTheFaceSays()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        var sessionUri = AhpNames.SessionUri(id);
        await using var vscode = await rig.ConnectAsync();
        await vscode.SubscribeAsync(chatUri);
        await vscode.SubscribeAsync(sessionUri);

        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatIsReadChangedAction { IsRead = false }), null, CancellationToken.None);
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatDraftChangedAction { Draft = new Message { Text = "half", Origin = new MessageOrigin { Kind = MessageKind.User } } }), null, CancellationToken.None);
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnStartedAction
        {
            TurnId = "t1", StartedAt = "2026-11-13T10:00:00.000Z", Message = new Message { Text = "Hi", Origin = new MessageOrigin { Kind = MessageKind.Agent } },
        }), null, CancellationToken.None);
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionTitleChangedAction { Title = "Mine" }), null, CancellationToken.None);
        await vscode.Client.DispatchAsync(AhpNames.Root, new StateAction(new RootConfigChangedAction { Config = [] }), null, CancellationToken.None);

        await LinkHarness.Until(() => vscode.Actions(sessionUri).Any(a => a.RejectionReason is not null), "the title refused");
        var read = vscode.Actions(chatUri).Single(a => a.Action.Value is ChatIsReadChangedAction);
        Assert.IsNull(read.RejectionReason);
        Assert.AreEqual("vscode", read.Origin!.ClientId);
        Assert.IsNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatDraftChangedAction).RejectionReason);
        Assert.AreEqual("A client sends user messages only", vscode.Actions(chatUri).Single(a => a.Action.Value is ChatTurnStartedAction).RejectionReason);
        Assert.AreEqual("Renaming is not supported yet", vscode.Actions(sessionUri).Single(a => a.Action.Value is SessionTitleChangedAction).RejectionReason);
        Assert.AreEqual(1, rig.Claude.Prompts.Count, "nothing reached the agent");
        await LinkHarness.Until(() => vscode.Events().Select(e => e.Event).OfType<SubscriptionEventSessionSummaryChanged>()
            .Any(c => c.Params.Changes.Chats?.Single().Status is { } s && !s.HasFlag(SessionStatus.IsRead)), "the chat's read bit reached the catalog");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AChild_IsAReadOnlyChatInItsRootsSession()
    {
        await using var rig = await Rig.StartAsync(before: async r =>
        {
            await r.NewPromptedAsync("claude", "Coordinate");
        });
        var parent = rig.Service.Store.All().Single().SessionId;
        var child = await rig.NewPromptedAsync("kimi", "Do the part");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 2, "both listed");
        var sessionUri = AhpNames.SessionUri(parent);
        await vscode.SubscribeAsync(sessionUri);

        rig.Service.Store.Update(child, "kimi", r => r.ParentSessionId = parent); // as spawn_session records it

        await LinkHarness.Until(() => vscode.Of<SessionChatAddedAction>(sessionUri).Length == 1, "the child is a chat of its root's session");
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionRemoved r && r.Params.Session == AhpNames.SessionUri(child)),
            "and no session of its own");
        var list = await vscode.ListAsync();
        var item = list.Items.Single();
        CollectionAssert.AreEqual(new[] { AhpNames.ChatUri(parent), AhpNames.ChatUri(child) }, item.Chats!.Select(c => c.Resource).ToArray());
        Assert.AreEqual(AhpNames.ChatUri(parent), item.DefaultChat);
        var chat = (await vscode.SubscribeAsync(AhpNames.ChatUri(child))).State.Chat!;
        Assert.AreEqual("Do the part", chat.Turns.Single().Message.Text);
        Assert.AreEqual(ChatInteractivity.ReadOnly, chat.Interactivity);

        // Archiving the child's chat archives the child.
        await vscode.Client.DispatchAsync(AhpNames.ChatUri(child), new StateAction(new ChatIsArchivedChangedAction { IsArchived = true }), null, CancellationToken.None);
        await LinkHarness.Until(() => rig.Service.Store.Get(child)!.Archived, "the child archived");
        await LinkHarness.Until(() => vscode.Actions(AhpNames.ChatUri(child)).Any(a => a.Action.Value is ChatIsArchivedChangedAction && a.Origin is not null), "echoed");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AServiceTurn_BeginsAndEnds_InTheChat()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Coordinate"));
        var asker = rig.Service.Store.All().Single().SessionId;
        var asked = await rig.NewPromptedAsync("kimi", "Review the notes");
        var chatUri = AhpNames.ChatUri(asked);
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 2, "both listed");
        await vscode.SubscribeAsync(chatUri);
        await using var peers = await TestClient.ConnectAsync(rig.Service, "peers");

        var answer = await peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = asked, ["text"] = "SQLite or Postgres?",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = rig.Service.Store.Get(asker)!.PeerToken },
        }).WaitAsync(Wait);

        Assert.AreEqual("ok", (string?)answer!["text"]);
        await LinkHarness.Until(() => vscode.Of<ChatTurnCompleteAction>(chatUri).Length == 1, "the question's turn ended in VS Code");
        var started = vscode.Of<ChatTurnStartedAction>(chatUri).Single();
        StringAssert.StartsWith(started.TurnId, "service-", "the service's own prompt ID for its turn");
        Assert.AreEqual(MessageKind.Agent, started.Message.Origin.Kind, "another agent wrote it");
        Assert.AreEqual(started.TurnId, vscode.Of<ChatTurnCompleteAction>(chatUri).Single().TurnId);
    }

    /// <summary>
    /// A client that says <c>gorynych/requests: false</c> gets no approval, though it attached last: the one that
    /// takes them does.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientThatTakesNoRequests_GetsNoApproval_ThoughItAttachedLast()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        await using var service = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "requests-test" }, [claude.Adapter]);
        await using var zed = await TestClient.ConnectAsync(service, "zed");
        var created = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        var id = (string)created!["sessionId"]!;
        var faceAttached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        claude.BeforePrompt = _ => faceAttached.Task;
        claude.DuringPrompt = async (agent, sessionId) =>
            await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(new RequestPermissionRequest
            {
                SessionId = sessionId, ToolCall = new ToolCallUpdate { ToolCallId = "c1", Title = "Write parser.rs" },
                Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
            }));
        var prompting = zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = id, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Port the parser" }),
        });
        await LinkHarness.Until(() => claude.Prompts.Count == 1, "the turn runs");

        // A client like the face opens the session during the turn: the one attached last.
        var link = new DuplexLink();
        var (si, so) = link.AgentEnd;
        _ = service.ServeClientAsync(si, so, "face-like");
        var (ci, co) = link.ClientEnd;
        await using var faceLike = new JsonRpcConnection(ci, co, "face-like");
        var asked = 0;
        faceLike.RequestHandler = (_, _) => { Interlocked.Increment(ref asked); return Task.FromResult<JsonNode?>(new JsonObject()); };
        faceLike.Start();
        await faceLike.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject
        {
            ["protocolVersion"] = 1, ["_meta"] = new JsonObject { [GorynychProtocol.MetaRequests] = false },
        }).WaitAsync(Wait);
        await faceLike.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        faceAttached.SetResult();
        await prompting.WaitAsync(Wait);

        Assert.AreEqual("Write parser.rs", zed.Approvals.Single().ToolCall.Title, "the approval went to Zed");
        Assert.AreEqual(0, Volatile.Read(ref asked), "and nothing to the client that takes none");
        _ = typeof(AcpSessionInfo);
    }
}
