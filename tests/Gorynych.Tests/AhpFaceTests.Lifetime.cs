using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Tests;

/// <summary>
/// The AHP face over time: the order of what a client gets, clients that go or stop reading, sessions that open
/// and close under turns, the first list, names and times, and the face's own stop.
/// </summary>
public sealed partial class AhpFaceTests
{
    /// <summary>A client on a bare WebSocket, which sees each message in the order it came.</summary>
    sealed class RawPeer : IDisposable
    {
        public readonly ClientWebSocket Socket = new();
        long nextId;

        public static async Task<RawPeer> ConnectAsync(Rig rig)
        {
            var peer = new RawPeer();
            await peer.Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Endpoint.Port}/ahp?tkn={rig.Token}"), CancellationToken.None).WaitAsync(Wait);
            return peer;
        }

        public async Task<long> RequestAsync(string method, JsonObject @params)
        {
            var id = ++nextId;
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
            await Socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message.ToJsonString())), WebSocketMessageType.Text, true, CancellationToken.None).WaitAsync(Wait);
            return id;
        }

        public async Task<JsonObject?> ReceiveAsync(TimeSpan within)
        {
            var buffer = new byte[64 * 1024];
            using var text = new MemoryStream();
            using var timeout = new CancellationTokenSource(within);
            try
            {
                while (true)
                {
                    var result = await Socket.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    text.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) return JsonNode.Parse(text.ToArray())!.AsObject();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>What came before the answer to <paramref name="id"/>, in order, and the answer.</summary>
        public async Task<(List<JsonObject> Before, JsonObject Answer)> AnswerAsync(long id)
        {
            var before = new List<JsonObject>();
            while (true)
            {
                var message = await ReceiveAsync(Wait) ?? throw new InvalidOperationException($"no answer to {id}");
                if (message["id"] is JsonValue v && v.GetValue<long>() == id) return (before, message);
                before.Add(message);
            }
        }

        public void Dispose() => Socket.Dispose();
    }

    static bool IsAction(JsonObject message, string channel) =>
        (string?)message["method"] == "action" && (string?)message["params"]?["channel"] == channel;

    static long SeqOf(JsonObject action) => (long)action["params"]!["serverSeq"]!;

    /// <summary>
    /// An answer that carries a channel's state is queued with that state: no action of the channel comes before it,
    /// and every one after it is newer than its snapshot. The agent streams all the while, so the face is busy.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task AnAnswerWithState_ComesBeforeEveryActionNewerThanIt()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        var sessionUri = AhpNames.SessionUri(id);
        await using (var opener = await rig.ConnectAsync())
            await opener.SubscribeAsync(chatUri); // the chat is open, as for a client already on it
        using var stop = new CancellationTokenSource();
        rig.Claude.DuringPrompt = async (agent, sessionId) =>
        {
            for (var i = 0; i < 200_000 && !stop.IsCancellationRequested; i++)
                await agent.Update(sessionId, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "x" } });
        };
        var prompting = rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = id, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Stream" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "zed-stream" },
        });

        for (var round = 0; round < 25; round++)
        {
            using var raw = await RawPeer.ConnectAsync(rig);
            var init = await raw.RequestAsync("initialize", new JsonObject
            {
                ["channel"] = AhpNames.Root, ["protocolVersions"] = new JsonArray("1.0.0"), ["clientId"] = $"raw-{round}",
                ["initialSubscriptions"] = new JsonArray(sessionUri),
            });
            var (beforeInit, initialized) = await raw.AnswerAsync(init);
            Assert.IsFalse(beforeInit.Any(m => IsAction(m, sessionUri)), "nothing of the session before the answer with its state");
            var sessionFrom = (long)initialized["result"]!["snapshots"]![0]!["fromSeq"]!;

            var subscribe = await raw.RequestAsync("subscribe", new JsonObject { ["channel"] = chatUri });
            var (beforeSubscribed, subscribed) = await raw.AnswerAsync(subscribe);
            Assert.IsFalse(beforeSubscribed.Any(m => IsAction(m, chatUri)), $"round {round}: no action of the chat before the answer with its state");
            Assert.IsTrue(beforeSubscribed.Where(m => IsAction(m, sessionUri)).All(m => SeqOf(m) > sessionFrom), "the session's actions are newer than its snapshot");
            var chatFrom = (long)subscribed["result"]!["snapshot"]!["fromSeq"]!;
            for (var more = 0; more < 20 && await raw.ReceiveAsync(TimeSpan.FromSeconds(2)) is { } next; more++)
                if (IsAction(next, chatUri)) Assert.IsTrue(SeqOf(next) > chatFrom, "the chat's actions after it are newer than its snapshot");
        }
        stop.Cancel();
        await prompting.WaitAsync(Wait);
    }

    /// <summary>An unsubscribe that comes while its session opens takes the subscription back, so the session closes when nobody looks.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnUnsubscribe_WhileItsSessionOpens_TakesTheSubscriptionBack()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { CloseAfter = TimeSpan.Zero, SweepEvery = TimeSpan.FromHours(1) },
            async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        await using var vscode = await rig.ConnectAsync();
        var life = rig.Service.Feed(id).LifeGate;
        await life.WaitAsync(); // the session cannot open yet

        var subscribing = vscode.SubscribeAsync(chatUri);
        await Task.Delay(200);
        await vscode.Client.UnsubscribeAsync(chatUri, CancellationToken.None);
        await Task.Delay(200);
        life.Release();
        await subscribing; // answered all the same
        await LinkHarness.Until(() => rig.Face.HasOpen(id), "the session opened");

        rig.Face.Sweep();
        Assert.IsFalse(rig.Face.HasOpen(id), "nobody looks at it: closed");
    }

    /// <summary>A transport whose sends never end once it is stuck: a client that reads nothing more.</summary>
    sealed class StuckTransport : ITransport
    {
        readonly System.Threading.Channels.Channel<TransportMessage> incoming = System.Threading.Channels.Channel.CreateUnbounded<TransportMessage>();
        public int Sent;
        public volatile bool Stuck;

        public void Feed(JsonObject message) => incoming.Writer.TryWrite(TransportMessage.FromText(message.ToJsonString()));

        public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken)
        {
            if (Stuck) await Task.Delay(Timeout.Infinite, cancellationToken);
            Interlocked.Increment(ref Sent);
        }

        public async ValueTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken) => await incoming.Reader.ReadAsync(cancellationToken);

        public ValueTask CloseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A client that takes nothing more is let go of: no longer counted, so nothing is queued for it, and its reading ended.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientThatTakesNothing_IsLetGoOf()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { SendTimeout = TimeSpan.FromMilliseconds(300) },
            async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var stuck = new StuckTransport();
        var serving = rig.Face.ServeAsync(stuck, CancellationToken.None);
        stuck.Feed(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["channel"] = AhpNames.Root, ["protocolVersions"] = new JsonArray("1.0.0"), ["clientId"] = "stuck",
                ["initialSubscriptions"] = new JsonArray(AhpNames.Root),
            },
        });
        await LinkHarness.Until(() => Volatile.Read(ref stuck.Sent) >= 1, "its initialize answered"); // and maybe the sessions after it
        Assert.AreEqual(1, (int)rig.Face.Status()["clients"]!);

        // It takes nothing more, and a session comes: the face has something to send it.
        stuck.Stuck = true;
        await rig.NewPromptedAsync("kimi", "Review the notes");

        await LinkHarness.Until(() => (int)rig.Face.Status()["clients"]! == 0, "the client that takes nothing is let go of");
        await serving.WaitAsync(Wait); // its reading ended too
    }

    /// <summary>A prompt that came without an ID gets one of the service's own: its turn ends in VS Code too.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task APromptWithoutAnId_EndsItsTurnInTheChatToo()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        await using var vscode = await rig.ConnectAsync();
        await vscode.SubscribeAsync(chatUri);

        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = id, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "No ID" }),
        }).WaitAsync(Wait);

        await LinkHarness.Until(() => vscode.Of<ChatTurnCompleteAction>(chatUri).Length == 1, "its turn ended in VS Code");
        var started = vscode.Of<ChatTurnStartedAction>(chatUri).Single();
        StringAssert.StartsWith(started.TurnId, "prompt-");
        Assert.AreEqual(started.TurnId, vscode.Of<ChatTurnCompleteAction>(chatUri).Single().TurnId);
    }

    /// <summary>A close and the load after it reach the conductor in that order: the chat opened again sees the next turn.</summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ASessionClosedAndOpenedAgainAtOnce_IsOpen()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { CloseAfter = TimeSpan.Zero, SweepEvery = TimeSpan.FromHours(1) },
            async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        await using var vscode = await rig.ConnectAsync();

        for (var round = 1; round <= 5; round++)
        {
            await vscode.SubscribeAsync(chatUri);
            await vscode.Client.UnsubscribeAsync(chatUri, CancellationToken.None);
            await LinkHarness.Until(() =>
            {
                rig.Face.Sweep();
                return !rig.Face.HasOpen(id);
            }, "closed: nobody looks");
            await vscode.SubscribeAsync(chatUri); // at once, while the close may still be on its way

            var text = $"Round {round}";
            await rig.PromptAsync(id, text);
            await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Any(t => t.Message.Text == text), $"round {round}'s turn reached VS Code");
        }
    }

    /// <summary>
    /// A session that becomes a child while its turn runs leaves the list as a session of its own, but the face lets
    /// go of it only when the turn is over: its close, as the session's last client, would cancel the turn.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionThatGoesWhileItsTurnRuns_IsClosedOnlyWhenTheTurnIsOver()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Coordinate"));
        var parent = rig.Service.Store.All().Single().SessionId;
        var child = await rig.NewPromptedAsync("kimi", "Do the part");
        var chatUri = AhpNames.ChatUri(child);
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 2, "both listed");
        await vscode.SubscribeAsync(chatUri);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Kimi.BeforePrompt = _ => hold.Task;
        // Zed prompts and quits: the face is the session's last client.
        var zed = await TestClient.ConnectAsync(rig.Service, "zed-2");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = child, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        _ = zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
        {
            ["sessionId"] = child, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Go on" }),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "zed-2-1" },
        });
        await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Length == 1, "the turn runs");
        await zed.DisposeAsync();

        rig.Service.Store.Update(child, "kimi", r => r.ParentSessionId = parent); // as spawn_session records it
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionRemoved r && r.Params.Session == AhpNames.SessionUri(child)),
            "no session of its own any more");
        Assert.IsTrue(rig.Face.HasOpen(child), "kept open while its turn runs");

        hold.SetResult();
        await LinkHarness.Until(() => !rig.Face.HasOpen(child), "let go of once its turn is over");
        var mark = rig.Service.Feed(child).Journal.ReadAll().OfType<JsonObject>()
            .Last(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.AreEqual("end_turn", (string?)mark["_meta"]![GorynychProtocol.MetaStopReason], "the turn ran to its end, not cancelled by a close");
    }

    /// <summary>The root's notifications carry no number: a reconnect's replay sends again those that came after what the client saw.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AReconnect_GetsTheSessionsThatCameWhileTheClientWasAway()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        long seen;
        await using (var before = await rig.ConnectAsync())
        {
            await before.ListAsync();
            seen = Math.Max(before.Initialized!.ServerSeq, before.LastSeq);
        }
        var kimi = await rig.NewPromptedAsync("kimi", "Review the notes");
        await LinkHarness.Until(() => (int)rig.Face.Status()["sessions"]! == 2, "the face has the session that came");

        await using var after = await rig.ConnectAsync(initialize: false);
        var result = await after.Client.ReconnectAsync("vscode", seen, [AhpNames.Root], CancellationToken.None).WaitAsync(Wait);

        Assert.AreEqual(ReconnectResultType.Replay, ((ReconnectReplayResult)result.Value!).Type);
        await LinkHarness.Until(() => after.Events().Any(e => e.Event is SubscriptionEventSessionAdded a && a.Params.Summary.Resource == AhpNames.SessionUri(kimi)),
            "the session that came while it was away");
    }

    /// <summary>An update that comes outside any turn, unmarked (a late chunk), begins a turn that ends once the session goes quiet.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnUpdateOutsideAnyTurn_BeginsATurnThatEndsWhenTheSessionGoesQuiet()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { QuietFor = TimeSpan.FromMilliseconds(300) },
            async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var id = rig.Service.Store.All().Single().SessionId;
        var chatUri = AhpNames.ChatUri(id);
        await using var vscode = await rig.ConnectAsync();
        await vscode.SubscribeAsync(chatUri);

        await rig.Claude.Adapter.Current!.Update(id, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "One more thing." } });

        await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Length == 1, "a turn of the agent's own");
        await LinkHarness.Until(() => vscode.Of<ChatTurnCompleteAction>(chatUri).Length == 1, "ended once the session went quiet");
        Assert.IsFalse(rig.Service.Feed(id).TurnRunning);
    }

    /// <summary>The face stops at once with clients on it, one that answers no close too: the endpoint has no socket left to wait for.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFace_StopsAtOnce_WithClientsConnected()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"));
        var vscode = await rig.ConnectAsync();
        using var silent = await RawPeer.ConnectAsync(rig);
        await LinkHarness.Until(() => (int)rig.Face.Status()["clients"]! == 2, "both connected");

        var watch = Stopwatch.StartNew();
        await rig.StopFaceAsync();

        // Well under the endpoint's own 2 s for the requests still running: it waited for none.
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"stopped in {watch.Elapsed.TotalSeconds:0.0} s");
        await vscode.DisposeAsync();
    }

    /// <summary>A session the agent lists with no title and no time: named for its agent and ID, the same each time, and old, not new.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionWithNoTitleOrTime_IsNamedForItsAgentAndId_AndIsOld()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["session_0a1b2c3d4e5f", "session_99aa"], titles: new() { ["session_99aa"] = "Old work" });
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("claude", "Port the parser"), kimi: kimi);
        var claude = rig.Service.Store.All().Single(r => r.Agent == "claude").SessionId;
        await using var vscode = await rig.ConnectAsync();

        var list = await vscode.ListAsync();
        var untitled = list.Items.Single(i => i.Resource == AhpNames.SessionUri("session_0a1b2c3d4e5f"));
        Assert.AreEqual("kimi session 0a1b2c3d", untitled.Title);
        Assert.AreEqual("kimi session 0a1b2c3d", untitled.Chats!.Single().Title, "its chat too, which VS Code may show for it");
        Assert.AreEqual("Old work", list.Items.Single(i => i.Resource == AhpNames.SessionUri("session_99aa")).Title);
        Assert.AreEqual(AhpNames.Time(DateTimeOffset.UnixEpoch), untitled.ModifiedAt, "no time said: the oldest, never now");
        Assert.AreEqual(AhpNames.SessionUri(claude), list.Items[0].Resource, "a session in use comes before the old ones");

        // Later changes keep its name.
        await rig.Zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = "session_0a1b2c3d4e5f", ["archived"] = true }).WaitAsync(Wait);
        await LinkHarness.Until(() => (vscode.ListAsync().Result.Items.Single(i => i.Resource == untitled.Resource).Status & SessionStatus.IsArchived) != 0, "archived");
        Assert.IsFalse(vscode.Events().Select(e => e.Event).OfType<SubscriptionEventSessionSummaryChanged>().Any(c => c.Params.Changes.Title is "" || c.Params.Changes.Chats?.Any(ch => ch.Title is "") == true),
            "never an empty title");
        Assert.AreEqual("kimi session 0a1b2c3d", (await vscode.SubscribeAsync(untitled.Resource)).State.Session!.Title);
        Assert.AreEqual("0a1b2c3d", AhpFace.ShortId("0a1b2c3d-1111-2222-3333-444455556666"));
        Assert.AreEqual("kimi-1", AhpFace.ShortId("kimi-1"));
        Assert.AreEqual("_tail", AhpFace.ShortId("_tail"));
    }

    /// <summary>The first list waits for the slowest agent: initialize and ping do not, and listSessions answers once it is read.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheFirstList_HoldsUpNeitherInitializeNorPing_AndListSessionsWaitsForIt()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-old"]) { BeforeList = () => slow.Task };
        try
        {
            await using var rig = await Rig.StartAsync(kimi: kimi);
            await using var vscode = await rig.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var listing = vscode.ListAsync();
            await vscode.Client.PingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(listing.IsCompleted, "the list waits for the agents'");
            slow.SetResult();

            Assert.AreEqual(AhpNames.SessionUri("kimi-old"), (await listing).Items.Single().Resource);
            await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionAdded a && a.Params.Summary.Resource == AhpNames.SessionUri("kimi-old")),
                "said on the root channel as it came");
        }
        finally
        {
            slow.TrySetResult();
        }
    }
}
