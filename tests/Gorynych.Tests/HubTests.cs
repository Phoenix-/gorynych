using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Remotes;

namespace Gorynych.Tests;

/// <summary>
/// The hub (docs/design.md, "Several machines"): a service that shows the sessions of another machine's
/// service as its own agents' (<c>claude@box</c>), reached here through an in-memory link the test can cut,
/// in place of <c>ssh -T box Gorynych.Service.exe acp</c>.
/// </summary>
[TestClass]
public sealed class HubTests
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>A remote service and a hub that lists it as <c>box</c>.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly LifecycleAgent RemoteClaude = new("claude", canDelete: false);
        public readonly LifecycleAgent RemoteKimi = new("kimi", canDelete: true);
        public readonly LifecycleAgent LocalCodex = new("codex", canDelete: true);
        public readonly Conductor Remote;
        public readonly Conductor Hub;
        public readonly string HubData = TestDirs.New();
        readonly List<(Stream A, Stream B)> links = [];
        public volatile bool Reachable = true;
        public int Connects;

        /// <summary>The service the next connection reaches: the remote, or another in its place (an update there).</summary>
        public Conductor? Target;

        public Rig(bool remotesFile = true, int pageSize = 50)
        {
            Remote = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "remote-service" }, [RemoteClaude.Adapter, RemoteKimi.Adapter]);
            if (remotesFile) RemotesFile.Write(RemotesFile.PathIn(HubData), [new RemoteEntry("box", "ssh", ["-T", "box"], [])]);
            Hub = HubOver(HubData, "hub-service", [LocalCodex.Adapter], _ => ConnectAsync, pageSize);
        }

        public static Conductor HubOver(string data, string serviceId, IAgentAdapter[] adapters,
            Func<RemoteEntry, Func<CancellationToken, Task<(Stream Input, Stream Output)>>?> connect, int pageSize = 50, TimeSpan? listWait = null) =>
            new(new ConductorOptions
            {
                DataDirectory = data, ServiceId = serviceId, Remotes = true, RemoteConnect = connect, ListPageSize = pageSize,
                RemotesWatchEvery = TimeSpan.FromMilliseconds(100),
                RemoteTimings = new RemoteOptions
                {
                    Backoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], Handshake = TimeSpan.FromSeconds(10),
                    ListWait = listWait ?? TimeSpan.FromSeconds(5),
                },
            }, adapters);

        async Task<(Stream Input, Stream Output)> ConnectAsync(CancellationToken ct)
        {
            await Task.Yield();
            if (!Reachable) throw new IOException("box: no route to host (test)");
            Interlocked.Increment(ref Connects);
            return Serve(Target ?? Remote, "hub", links);
        }

        /// <summary>The remote's ACP end for a hub: a client connection of the remote service.</summary>
        public static (Stream Input, Stream Output) Serve(Conductor remote, string name, List<(Stream A, Stream B)> links)
        {
            var link = new DuplexLink();
            var (si, so) = link.AgentEnd;
            _ = remote.ServeClientAsync(si, so, name);
            var ends = link.ClientEnd;
            lock (links) links.Add((ends.Input, ends.Output));
            return ends;
        }

        /// <summary>The ssh connection drops: both ends see it closed.</summary>
        public void Cut()
        {
            lock (links)
            {
                foreach (var (a, b) in links)
                {
                    try { b.Dispose(); } catch { }
                    try { a.Dispose(); } catch { }
                }
                links.Clear();
            }
        }

        public JsonObject? Box => Hub.Remotes.Status().SingleOrDefault(r => (string?)r!["name"] == "box") as JsonObject;

        public bool Connected => Hub.Remotes.Status().Any(r => (string?)r!["name"] == "box" && (bool)r["connected"]!);

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            Cut();
            await Remote.DisposeAsync();
        }
    }

    internal static async Task<string> NewPromptedAsync(TestClient client, string agent, string text, string? cwd = null)
    {
        var created = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
        {
            ["cwd"] = cwd ?? Path.GetTempPath(), ["mcpServers"] = new JsonArray(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        }).WaitAsync(Wait);
        var id = (string)created!["sessionId"]!;
        await PromptAsync(client, id, text);
        return id;
    }

    internal static Task<JsonNode?> PromptAsync(TestClient client, string sessionId, string text, string? promptId = null)
    {
        var @params = new JsonObject { ["sessionId"] = sessionId, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (promptId is not null) @params["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = promptId };
        return client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, @params).WaitAsync(Wait);
    }

    internal static async Task<List<JsonObject>> ListAllAsync(TestClient client, bool archived = false)
    {
        var all = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var @params = new JsonObject();
            if (cursor is not null) @params["cursor"] = cursor;
            if (archived) @params["_meta"] = new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true };
            var page = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, @params).WaitAsync(Wait);
            all.AddRange((page!["sessions"] as JsonArray ?? []).OfType<JsonObject>());
            cursor = (string?)page["nextCursor"];
        } while (cursor is not null);
        return all;
    }

    internal static string? Agent(JsonObject info) => (string?)info["_meta"]?[GorynychProtocol.MetaAgent];

    /// <summary>Whether the hub has its remotes, and each answers: not true of none yet, before it read remotes.json.</summary>
    internal static bool Connected(Conductor hub) => hub.Remotes.Status() is { Count: > 0 } all && all.All(r => (bool)r!["connected"]!);

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheHub_ListsItsOwnSessionsAndTheRemotes_AsOneList_PagedAcrossBoth()
    {
        await using var rig = new Rig(pageSize: 2);
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
        {
            await NewPromptedAsync(there, "claude", "Port the parser");
            await NewPromptedAsync(there, "kimi", "Review the notes");
            await NewPromptedAsync(there, "claude", "Fix the build");
        }
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        await NewPromptedAsync(zed, "codex", "Plan the week");
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");

        var all = await ListAllAsync(zed);

        Assert.AreEqual(4, all.Count, string.Join("\n", all.Select(s => s.ToJsonString())));
        CollectionAssert.AreEquivalent(new[] { "claude@box", "claude@box", "kimi@box", "codex" }, all.Select(Agent).ToArray());
        Assert.IsTrue(all.Where(s => Agent(s)!.EndsWith("@box")).All(s => (string?)s["_meta"]![GorynychProtocol.MetaMachine] == "box"), "each remote one says its machine");
        Assert.IsNull(all.Single(s => Agent(s) == "codex")["_meta"]![GorynychProtocol.MetaMachine], "the local one has none");
        var times = all.Select(s => DateTimeOffset.Parse((string)s["updatedAt"]!)).ToList();
        CollectionAssert.AreEqual(times.OrderByDescending(t => t).ToList(), times, "one order across both, newest first, over pages of 2");

        await using var scoped = await TestClient.ConnectAsync(rig.Hub, "zed-claude@box", agent: "claude@box");
        Assert.AreEqual("Gorynych: claude@box", scoped.Initialized!.AgentInfo!.Title);
        var mine = await ListAllAsync(scoped);
        Assert.AreEqual(2, mine.Count);
        Assert.IsTrue(mine.All(s => Agent(s) == "claude@box"));
        var agents = (zed.Initialized!.Meta![GorynychProtocol.MetaAgents] as JsonArray)!.Select(a => (string?)a!["name"]).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "codex" }, agents);
        // The agents of a remote are offered once it answered; this client came before or after.
        var later = await TestClient.ConnectAsync(rig.Hub, "zed-again");
        CollectionAssert.AreEquivalent(new[] { "codex", "claude@box", "kimi@box" },
            (later.Initialized!.Meta![GorynychProtocol.MetaAgents] as JsonArray)!.Select(a => (string?)a!["name"]).ToArray());
        await later.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARemoteSession_IsCreatedPromptedAndApproved_ThroughTheHub_WithItsPromptIdAndPositions()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        RequestPermissionResponse? decided = null;
        rig.RemoteClaude.DuringPrompt = async (agent, sessionId) =>
        {
            var answer = await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(new RequestPermissionRequest
            {
                SessionId = sessionId,
                ToolCall = new ToolCallUpdate { ToolCallId = "call-1", Title = "Write parser.rs" },
                Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                    new PermissionOption { OptionId = "deny", Name = "Deny", Kind = PermissionOptionKind.RejectOnce }],
            }));
            decided = AcpJson.FromNode<RequestPermissionResponse>(answer);
        };
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed", agent: "claude@box");
        zed.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = "deny" });

        var created = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
        {
            ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(new JsonObject { ["name"] = "zeds-own", ["command"] = "x", ["args"] = new JsonArray(), ["env"] = new JsonArray() }),
        }).WaitAsync(Wait);
        var id = (string)created!["sessionId"]!;
        Assert.AreEqual("box", (string?)created["_meta"]![GorynychProtocol.MetaMachine]);
        Assert.AreEqual("claude@box", (string?)created["_meta"]![GorynychProtocol.MetaAgent]);
        Assert.IsTrue(rig.Remote.IsLocalSession(id), "created there, kept there");
        Assert.IsFalse(rig.Hub.IsLocalSession(id), "and not here");

        var answer = await PromptAsync(zed, id, "Port the parser", promptId: "zed-p1");

        Assert.AreEqual("end_turn", (string?)answer!["stopReason"]);
        Assert.AreEqual("Port the parser", rig.RemoteClaude.Prompts.Single(p => p.SessionId == id).Text);
        Assert.AreEqual("deny", ((SelectedPermissionOutcome)decided!.Outcome).OptionId, "the approval went to the hub's client and back");
        Assert.AreEqual("Write parser.rs", zed.Approvals.Single().ToolCall.Title);
        Assert.AreEqual("ok", zed.Text());
        var updates = zed.SessionUpdates.Where(u => u.SessionId == id).Select(u => u.Update).ToList();
        Assert.IsTrue(updates.Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").All(u => u["_meta"]?[GorynychProtocol.MetaPosition] is not null), "the remote's positions pass through");
        Assert.IsTrue(updates.Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaPromptId] == "zed-p1" && (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"),
            "the mark of the prompt's end, under the client's own prompt ID");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheHub_HasARemoteSessionOpenThere_OnlyWhileOneOfItsClientsHasIt()
    {
        await using var rig = new Rig();
        string id;
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
        {
            id = await NewPromptedAsync(there, "claude", "Port the parser");
            await there.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id }).WaitAsync(Wait);
        }
        await LinkHarness.Until(() => rig.Remote.Feed(id).Clients().Length == 0 && rig.Connected, "nobody has it open there");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        await ListAllAsync(zed);
        Assert.AreEqual(0, rig.Remote.Feed(id).Clients().Length, "a list attaches to nothing");

        var loaded = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        Assert.AreEqual("box", (string?)loaded!["_meta"]![GorynychProtocol.MetaMachine]);
        Assert.AreEqual(1, rig.Remote.Feed(id).Clients().Length, "open there while the client has it");
        Assert.AreEqual("Port the parser", zed.Text("user_message_chunk"), "the load's replay passed through");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id }).WaitAsync(Wait);
        await LinkHarness.Until(() => rig.Remote.Feed(id).Clients().Length == 0, "let go there when its last client here closed it");

        // A client that goes without closing lets go there too, but cancels nothing: the turn runs on.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.RemoteClaude.BeforePrompt = _ => hold.Task;
        var gone = await TestClient.ConnectAsync(rig.Hub, "zed-gone");
        await gone.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        _ = PromptAsync(gone, id, "Write the tests");
        await LinkHarness.Until(() => rig.RemoteClaude.Prompts.Count(p => p.SessionId == id) == 2, "the turn runs there");
        await gone.DisposeAsync();
        await LinkHarness.Until(() => rig.Remote.Feed(id).Clients().Length == 0, "let go when the client went");
        Assert.IsTrue(rig.Remote.Feed(id).TurnRunning, "nothing cancelled its turn");
        hold.SetResult();
        await LinkHarness.Until(() => !rig.Remote.Feed(id).TurnRunning, "the turn ends on its own");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALinkThatDrops_MakesItsSessionsReadOnly_AndTheirClientsGetWhatCameMeanwhile_WhenItIsBack()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed", agent: "claude@box", notices: true);
        var id = await NewPromptedAsync(zed, "claude@box", "Port the parser");

        rig.Reachable = false;
        rig.Cut();
        await LinkHarness.Until(() => !rig.Connected, "the hub saw the link drop");
        await LinkHarness.Until(() => zed.SessionUpdates.Any(u => u.SessionId == id && (string?)u.Update["_meta"]?[GorynychProtocol.MetaInteractivity] == GorynychProtocol.InteractivityReadOnly),
            "the client learned it is read-only");
        var readOnly = zed.SessionUpdates.Last(u => u.SessionId == id && u.Update["_meta"]?[GorynychProtocol.MetaInteractivity] is not null).Update;
        StringAssert.StartsWith((string?)readOnly["_meta"]![GorynychProtocol.MetaInteractivityReason], "box is not connected");
        Assert.AreEqual("box", (string?)readOnly["_meta"]![GorynychProtocol.MetaMachine]);
        await LinkHarness.Until(() => zed.Snapshot().Any(u => (string?)u["sessionUpdate"] == "notice"), "a client that shows notices is told");

        // Meanwhile: unreachable is an error at once, and its sessions are not listed.
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(zed, id, "Anything new?"));
        StringAssert.StartsWith(refused.Message, "box is not connected");
        Assert.AreEqual(0, (await ListAllAsync(zed)).Count);
        // And on the remote machine, work goes on: another client there prompts the session.
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
        {
            await there.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
            rig.RemoteClaude.Answer = "done meanwhile";
            await PromptAsync(there, id, "Write the tests");
        }

        rig.Reachable = true;
        await LinkHarness.Until(() => rig.Connected, "the hub is back");
        await LinkHarness.Until(() => zed.Text().Contains("done meanwhile"), "what came meanwhile reached the client");
        await LinkHarness.Until(() => (string?)zed.SessionUpdates.Last(u => u.SessionId == id && u.Update["_meta"]?[GorynychProtocol.MetaInteractivity] is not null)
            .Update["_meta"]![GorynychProtocol.MetaInteractivity] == GorynychProtocol.InteractivityFull, "and it may write again");
        Assert.AreEqual("Write the tests", zed.Text("user_message_chunk"), "the other client's prompt, from the replay after the client's position");
        var answer = await PromptAsync(zed, id, "Thanks");
        Assert.AreEqual("end_turn", (string?)answer!["stopReason"]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnUnreachableRemote_IsAnErrorAtOnce_AndItsAgentsScopeIsStillTaken()
    {
        await using var rig = new Rig();
        rig.Reachable = false;
        await LinkHarness.Until(() => (string?)rig.Box?["lastError"] is { } e && e.Contains("no route to host"), "the hub says why");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed", agent: "claude@box");

        var started = DateTime.UtcNow;
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
            new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait));

        StringAssert.StartsWith(refused.Message, "box is not connected");
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "at once, not a wait");
        Assert.AreEqual(0, (await ListAllAsync(zed)).Count);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => TestClient.ConnectAsync(rig.Hub, "zed-typo", agent: "claude@nowhere"), "an unknown machine is refused");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TwoServicesThatListEachOther_ShowEachSessionOnce()
    {
        var aKimi = new LifecycleAgent("kimi", canDelete: true);
        var bClaude = new LifecycleAgent("claude", canDelete: false);
        var links = new List<(Stream, Stream)>();
        Conductor? a = null, b = null;
        string aData = TestDirs.New(), bData = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(aData), [new RemoteEntry("b", "ssh", ["b"], [])]);
        RemotesFile.Write(RemotesFile.PathIn(bData), [new RemoteEntry("a", "ssh", ["a"], [])]);
        a = Rig.HubOver(aData, "service-a", [aKimi.Adapter], _ => _ => Task.FromResult(Rig.Serve(b!, "hub-a", links)));
        b = Rig.HubOver(bData, "service-b", [bClaude.Adapter], _ => _ => Task.FromResult(Rig.Serve(a!, "hub-b", links)));
        try
        {
            await LinkHarness.Until(() => Connected(a) && Connected(b), "each connected to the other");
            await using var onA = await TestClient.ConnectAsync(a, "zed-a");
            await using var onB = await TestClient.ConnectAsync(b, "zed-b");
            var aSession = await NewPromptedAsync(onA, "kimi", "On A");
            var bSession = await NewPromptedAsync(onB, "claude", "On B");

            var listA = await ListAllAsync(onA);
            var listB = await ListAllAsync(onB);

            CollectionAssert.AreEquivalent(new[] { $"{aSession} kimi", $"{bSession} claude@b" }, listA.Select(s => $"{s["sessionId"]} {Agent(s)}").ToArray());
            CollectionAssert.AreEquivalent(new[] { $"{bSession} claude", $"{aSession} kimi@a" }, listB.Select(s => $"{s["sessionId"]} {Agent(s)}").ToArray());
            var agentsOfA = await TestClient.ConnectAsync(a, "zed-a2");
            CollectionAssert.AreEquivalent(new[] { "kimi", "claude@b" }, (agentsOfA.Initialized!.Meta![GorynychProtocol.MetaAgents] as JsonArray)!.Select(x => (string?)x!["name"]).ToArray(),
                "never an agent the other shows from its own remotes");
            await agentsOfA.DisposeAsync();
            // A new session on A is said to B's clients once, under A's name, and never comes back to A.
            var added = await NewPromptedAsync(onA, "kimi", "Another on A");
            await LinkHarness.Until(() => onB.Changes.Any(c => (string?)c["sessionId"] == added && (string?)c["change"] == "added"), "B's client learned of it");
            Assert.AreEqual("kimi@a", (string?)onB.Changes.First(c => (string?)c["sessionId"] == added)["_meta"]![GorynychProtocol.MetaAgent]);
            await Task.Delay(300);
            Assert.AreEqual(1, onA.Changes.Count(c => (string?)c["sessionId"] == added && (string?)c["change"] == "added"), "A's own client, once");
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task RemotesJson_IsTakenWithoutARestart()
    {
        await using var rig = new Rig(remotesFile: false);
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
            await NewPromptedAsync(there, "claude", "Port the parser");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        Assert.AreEqual(0, rig.Hub.Remotes.Status().Count);

        RemotesFile.Write(RemotesFile.PathIn(rig.HubData), [new RemoteEntry("box", "ssh", ["-T", "box"], [])]);
        await LinkHarness.Until(() => rig.Connected, "the new remote connected");
        Assert.AreEqual("claude@box", Agent((await ListAllAsync(zed)).Single()));

        RemotesFile.Write(RemotesFile.PathIn(rig.HubData), []);
        await LinkHarness.Until(() => rig.Hub.Remotes.Status().Count == 0, "the removed one went");
        Assert.AreEqual(0, (await ListAllAsync(zed)).Count);
    }

    /// <summary>Two clients of the hub on one remote session, over its one connection: the second one's load replays to it alone.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TwoClientsOfTheHub_OnOneRemoteSession_EachGetEachUpdateOnce()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var first = await TestClient.ConnectAsync(rig.Hub, "zed-1");
        var id = await NewPromptedAsync(first, "claude@box", "Port the parser");
        await using var second = await TestClient.ConnectAsync(rig.Hub, "zed-2");

        await second.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        await PromptAsync(second, id, "Write the tests");

        Assert.AreEqual("Port the parser", second.Text("user_message_chunk").Replace("Write the tests", ""), "the second one got the history");
        Assert.AreEqual("okok", second.Text(), "and both answers once");
        await LinkHarness.Until(() => first.Text() == "okok", "the first one got the second answer live");
        Assert.AreEqual(0, first.SessionUpdates.Count(u => u.SessionId == id && (string?)u.Update["sessionUpdate"] == "user_message_chunk" && (string?)u.Update["content"]?["text"] == "Port the parser"),
            "and not its own prompt again from the other one's replay");
    }

    /// <summary>
    /// The hub restarts: it keeps nothing of the remote session, yet a client that comes back resumes it after
    /// its position (the remote's), and gets what came meanwhile, through the hub that starts afresh.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AfterARestartOfTheHub_AClientResumesARemoteSession_FromItsPosition()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        string id;
        string position;
        await using (var zed = await TestClient.ConnectAsync(rig.Hub, "zed"))
        {
            id = await NewPromptedAsync(zed, "claude@box", "Port the parser");
            position = (string)zed.SessionUpdates.Last(u => u.SessionId == id && u.Update["_meta"]?[GorynychProtocol.MetaPosition] is not null).Update["_meta"]![GorynychProtocol.MetaPosition]!;
        }
        await rig.Hub.DisposeAsync();
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
        {
            await there.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
            rig.RemoteClaude.Answer = "done while the hub was away";
            await PromptAsync(there, id, "Write the tests");
        }
        await using var next = Rig.HubOver(rig.HubData, "hub-service", [new LifecycleAgent("codex", canDelete: true).Adapter], _ => _ => Task.FromResult(Rig.Serve(rig.Remote, "hub-2", [])));
        await LinkHarness.Until(() => Connected(next), "the next hub connected");
        await using var back = await TestClient.ConnectAsync(next, "zed-back");

        var resumed = await back.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
        {
            ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(),
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = position } },
        }).WaitAsync(Wait);

        Assert.AreEqual("box", (string?)resumed!["_meta"]![GorynychProtocol.MetaMachine]);
        Assert.AreEqual("Write the tests", back.Text("user_message_chunk"), "only what came after its position");
        Assert.AreEqual("done while the hub was away", back.Text());
        Assert.IsTrue(long.Parse((string)resumed["_meta"]![GorynychProtocol.MetaPosition]!) > long.Parse(position));
    }

    /// <summary>A remote session's archive flag and its settings stay on its machine: the hub passes them on.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARemoteSessionsArchiveAndMode_AreKeptOnItsMachine()
    {
        await using var rig = new Rig();
        rig.RemoteClaude.ReportsModes = true;
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        var id = await NewPromptedAsync(zed, "claude@box", "Port the parser");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject { ["sessionId"] = id, ["configId"] = "mode", ["value"] = "plan" }).WaitAsync(Wait);
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = id, ["archived"] = true }).WaitAsync(Wait);

        Assert.AreEqual("plan", rig.RemoteClaude.Modes[id]);
        Assert.IsTrue(rig.Remote.Store.Get(id)!.Archived, "archived there");
        Assert.AreEqual(0, (await ListAllAsync(zed)).Count, "and so left out of a list");
        var archived = (await ListAllAsync(zed, archived: true)).Single();
        Assert.IsTrue((bool)archived["_meta"]![GorynychProtocol.MetaArchived]!);
        Assert.AreEqual("claude@box", Agent(archived));
    }

    /// <summary>An agent outside Gorynych sees and reads the remotes' sessions through the hub, and is told it cannot ask them yet.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAgentOutsideGorynych_SeesAndReadsRemoteSessions_ButCannotAskThemYet()
    {
        await using var rig = new Rig();
        string id;
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
            id = await NewPromptedAsync(there, "claude", "Port the parser");
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");

        var listed = await rig.Hub.PeerListAsync(self: null, includeArchived: false, CancellationToken.None);
        var entry = (listed["sessions"] as JsonArray)!.OfType<JsonObject>().Single(s => (string?)s["session_id"] == id);
        Assert.AreEqual("claude@box", (string?)entry["agent"]);
        Assert.AreEqual("box", (string?)entry["machine"]);
        var peers = await rig.Hub.PeerListAsync(self: "anyone", includeArchived: false, CancellationToken.None);
        Assert.IsFalse((peers["sessions"] as JsonArray)!.OfType<JsonObject>().Any(s => (string?)s["session_id"] == id), "a session here asks none on another machine yet");

        var read = await rig.Hub.Remotes.ReadAsync(id, cursor: null, limit: 20, thoughts: false, budget: null, CancellationToken.None);
        var text = OutsideService.ReadSessionText(read!, newest: true);
        StringAssert.Contains(text, $"session_id {id} (claude@box");
        StringAssert.Contains(text, "Port the parser");
        StringAssert.Contains(text, "ok");

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rig.Hub.AskAsync(id, "SQLite or Postgres?",
            OutsideService.Asker(new JsonObject { ["client"] = new JsonObject { ["name"] = "claude-code" } }), onState: null, CancellationToken.None));
        StringAssert.Contains(refused.Message, "is on another machine, and asking or starting a session on another machine is not supported yet (step 7");
    }

    /// <summary>A remote that turns out to be this service itself is refused, and not tried again until remotes.json changes.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARemoteThatIsThisServiceItself_IsRefused_AndNotTriedAgain()
    {
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("me", "ssh", ["localhost"], [])]);
        var connects = 0;
        Conductor? hub = null;
        hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("kimi", canDelete: true).Adapter], _ => _ =>
        {
            Interlocked.Increment(ref connects);
            return Task.FromResult(Rig.Serve(hub!, "itself", []));
        });
        await using var _ = hub;
        await LinkHarness.Until(() => (string?)hub.Remotes.Status().SingleOrDefault()?["lastError"] is { } e && e.Contains("is this service itself"), "refused");
        await Task.Delay(500); // several backoffs of this rig
        Assert.AreEqual(1, Volatile.Read(ref connects), "not tried again");
        await using var zed = await TestClient.ConnectAsync(hub, "zed");
        CollectionAssert.AreEqual(new[] { "kimi" }, (zed.Initialized!.Meta![GorynychProtocol.MetaAgents] as JsonArray)!.Select(a => (string?)a!["name"]).ToArray());
    }

    /// <summary>A remote session with the ID of a local one is left out: the local one keeps its ID.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARemoteSessionWithALocalSessionsId_IsLeftOut_AndTheLocalOneKept()
    {
        var remoteKimi = new LifecycleAgent("kimi", canDelete: true);
        var localKimi = new LifecycleAgent("kimi", canDelete: true);
        var links = new List<(Stream, Stream)>();
        await using var remote = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "remote-service" }, [remoteKimi.Adapter]);
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("box", "ssh", ["box"], [])]);
        await using var hub = Rig.HubOver(data, "hub-service", [localKimi.Adapter], _ => _ => Task.FromResult(Rig.Serve(remote, "hub", links)));
        await using (var there = await TestClient.ConnectAsync(remote, "remote-zed"))
            Assert.AreEqual("kimi-1", await NewPromptedAsync(there, "kimi", "On the other machine"));
        await using var zed = await TestClient.ConnectAsync(hub, "zed");
        Assert.AreEqual("kimi-1", await NewPromptedAsync(zed, "kimi", "On this one"));
        await LinkHarness.Until(() => Connected(hub), "the hub connected to box");

        var listed = (await ListAllAsync(zed)).Single();

        Assert.AreEqual("kimi", Agent(listed));
        Assert.AreEqual("On this one", (string?)listed["title"]);
    }

    /// <summary>
    /// The remote reached by a command, as over ssh: here the bridge itself, unscoped, on the remote service's pipe;
    /// <c>remote add</c>, <c>list</c> and <c>remove</c> change what a running hub has, with no restart.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task TheHub_ReachesARemoteThroughItsCommand_TheUnscopedBridge_AsTheRemoteCommandSaysIt()
    {
        var remoteClaude = new LifecycleAgent("claude", canDelete: false);
        var remotePipe = "gorynych-hubtest-r-" + Guid.NewGuid().ToString("N")[..8];
        var hubPipe = "gorynych-hubtest-h-" + Guid.NewGuid().ToString("N")[..8];
        await using var remote = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "remote-service" }, [remoteClaude.Adapter]);
        var hubData = TestDirs.New();
        DataFolder.Claim(hubData); // as the service does at its first start there
        await using var hub = new Conductor(new ConductorOptions
        {
            DataDirectory = hubData, ServiceId = "hub-service", Remotes = true, RemotesWatchEvery = TimeSpan.FromMilliseconds(200),
        }, [new LifecycleAgent("codex", canDelete: true).Adapter]);
        using var stop = new CancellationTokenSource();
        var remoteListening = PipeListener.RunAsync(remote, remotePipe, stop.Token);
        var hubListening = PipeListener.RunAsync(hub, hubPipe, stop.Token);
        try
        {
            string id;
            await using (var there = await TestClient.ConnectAsync(remote, "remote-zed"))
                id = await NewPromptedAsync(there, "claude", "Port the parser");

            var (added, addOut) = await RunAsync("remote", "add", "box", "--data", hubData, "--", BridgeTests.ServiceExe(), "acp", "--pipe", remotePipe);
            Assert.AreEqual(0, added, addOut);
            StringAssert.Contains(addOut, "added box");
            await LinkHarness.Until(() => Connected(hub), "the hub connected through the bridge", 30_000);
            await using var zed = await TestClient.ConnectAsync(hub, "zed", agent: "claude@box");
            var listed = (await ListAllAsync(zed)).Single();
            Assert.AreEqual(id, (string?)listed["sessionId"]);
            await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = id, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
            await PromptAsync(zed, id, "And now the tests");
            Assert.AreEqual("ok", zed.Text());

            var (listedCode, listOut) = await RunAsync("remote", "list", "--data", hubData, "--pipe", hubPipe);
            Assert.AreEqual(0, listedCode, listOut);
            StringAssert.Contains(listOut, "box: ");
            StringAssert.Contains(listOut, "connected since");
            StringAssert.Contains(listOut, "agents: claude@box");

            var (removed, removeOut) = await RunAsync("remote", "remove", "box", "--data", hubData);
            Assert.AreEqual(0, removed, removeOut);
            await LinkHarness.Until(() => hub.Remotes.Status().Count == 0, "the running hub let it go");
            var bad = await RunAsync("remote", "add", "a@b", "--data", hubData, "--", "ssh", "b");
            Assert.AreEqual(CommandLine.UsageExitCode, bad.Code);
            StringAssert.Contains(bad.Output, "has no @");
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(remoteListening, hubListening).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(BridgeTests.ServiceExe()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, await output + await error);
    }

    /// <summary>
    /// A remote of another version announces fewer extensions: no link, no detach, no list changes. What it
    /// has works, fields the hub does not know pass on, and the hub uses nothing it did not announce.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(false, DisplayName = "no detach: the hub stays attached rather than close")]
    [DataRow(true, DisplayName = "with detach: a client that went is detached, one that closed is closed")]
    public async Task ARemoteWithFewerExtensions_WorksWithWhatItAnnounced(bool detaches)
    {
        var detached = 0;
        var closed = 0;
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("old", "ssh", ["old"], [])]);
        await using var hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("kimi", canDelete: true).Adapter], _ => _ =>
        {
            var link = new DuplexLink();
            var (si, so) = link.AgentEnd;
            var rpc = new JsonRpcConnection(si, so, "old-remote");
            rpc.RequestHandler = async (req, ct) =>
            {
                await Task.Yield();
                switch (req.Method)
                {
                    case AcpMethods.Agent.Initialize:
                        return new JsonObject
                        {
                            ["protocolVersion"] = 1,
                            ["agentCapabilities"] = new JsonObject { ["loadSession"] = true, ["_meta"] = new JsonObject { [GorynychProtocol.MetaArchive] = true, [GorynychProtocol.CapDetach] = detaches } },
                            ["_meta"] = new JsonObject
                            {
                                [GorynychProtocol.MetaServiceId] = "old-service",
                                [GorynychProtocol.MetaAgents] = new JsonArray(new JsonObject { ["name"] = "claude", ["displayName"] = "Claude" }),
                            },
                        };
                    case AcpMethods.Agent.SessionList:
                        return new JsonObject
                        {
                            ["sessions"] = new JsonArray(new JsonObject
                            {
                                ["sessionId"] = "old-1", ["cwd"] = @"C:\work", ["title"] = "From an older one", ["updatedAt"] = "2026-11-01T10:00:00Z",
                                ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = "claude", ["x-later/field"] = 7 },
                            }, new JsonObject
                            {
                                // What it shows from its own remote: one hop, never listed here.
                                ["sessionId"] = "far-1", ["cwd"] = @"C:\far", ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = "claude@far", [GorynychProtocol.MetaMachine] = "far" },
                            }),
                        };
                    case AcpMethods.Agent.SessionResume:
                        return new JsonObject();
                    case AcpMethods.Agent.SessionPrompt:
                        await rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
                        {
                            ["sessionId"] = "old-1", ["update"] = new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "still here" } },
                        });
                        return new JsonObject { ["stopReason"] = "end_turn" };
                    case GorynychProtocol.MethodDetach:
                        Interlocked.Increment(ref detached);
                        return new JsonObject();
                    case AcpMethods.Agent.SessionClose:
                        Interlocked.Increment(ref closed);
                        return new JsonObject();
                    default:
                        throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
                }
            };
            rpc.Start();
            return Task.FromResult(link.ClientEnd);
        });
        await LinkHarness.Until(() => Connected(hub), "the hub connected to old");
        var zed = await TestClient.ConnectAsync(hub, "zed");

        var listed = (await ListAllAsync(zed)).Single();
        Assert.AreEqual("claude@old", Agent(listed));
        Assert.AreEqual(7, (int)listed["_meta"]!["x-later/field"]!, "a field the hub does not know passes on");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "old-1", ["cwd"] = @"C:\work", ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        await PromptAsync(zed, "old-1", "Still there?");
        Assert.AreEqual("still here", zed.Text());

        await zed.DisposeAsync();
        await Task.Delay(300);
        Assert.AreEqual(detaches ? 1 : 0, detached, "detached only if it announced it");
        Assert.AreEqual(0, closed, "never a close for a client that went: it would cancel a turn there");
        if (!detaches) return;
        await using var again = await TestClient.ConnectAsync(hub, "zed-again");
        await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "old-1", ["cwd"] = @"C:\work", ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
        await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = "old-1" }).WaitAsync(Wait);
        await LinkHarness.Until(() => Volatile.Read(ref closed) == 1, "a client's close is a close there");
    }
}
