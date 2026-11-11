using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Remotes;
using static Gorynych.Tests.HubTests;

namespace Gorynych.Tests;

/// <summary>The hub (docs/service-protocol.md, "Remotes") where it can go wrong: a broken file, another machine's ID, a remote that changed, a slow client, a slow remote.</summary>
[TestClass]
public sealed class HubReviewTests
{
    /// <summary>A remote service of the test's own making: it answers as a Gorynych service, and the test can make it send anything.</summary>
    sealed class FakeRemote(string serviceId = "fake-service")
    {
        public readonly List<string> Calls = [];
        public readonly Dictionary<string, Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>>> On = [];
        public JsonArray Sessions { get; set; } = [];
        public volatile JsonRpcConnection? Rpc;
        public volatile bool Attached;

        public int CallsOf(string method)
        {
            lock (Calls) return Calls.Count(c => c.StartsWith(method + " ", StringComparison.Ordinal));
        }

        public Task<(Stream Input, Stream Output)> ConnectAsync(CancellationToken ct)
        {
            var link = new DuplexLink();
            var (si, so) = link.AgentEnd;
            var rpc = new JsonRpcConnection(si, so, "fake-remote");
            rpc.RequestHandler = HandleAsync;
            rpc.Start();
            Rpc = rpc;
            return Task.FromResult(link.ClientEnd);
        }

        async Task<JsonNode?> HandleAsync(JsonRpcRequest req, CancellationToken ct)
        {
            lock (Calls) Calls.Add($"{req.Method} {(string?)req.Params?["sessionId"]}");
            if (On.TryGetValue(req.Method, out var handle)) return await handle(req, ct);
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return new JsonObject
                    {
                        ["protocolVersion"] = 1,
                        ["agentCapabilities"] = new JsonObject
                        {
                            ["loadSession"] = true,
                            ["_meta"] = new JsonObject { [GorynychProtocol.CapLink] = 1, [GorynychProtocol.CapDetach] = true, [GorynychProtocol.MetaArchive] = true },
                        },
                        ["_meta"] = new JsonObject
                        {
                            [GorynychProtocol.MetaServiceId] = serviceId,
                            [GorynychProtocol.MetaAgents] = new JsonArray(new JsonObject { ["name"] = "claude", ["displayName"] = "Claude" }),
                        },
                    };
                case AcpMethods.Agent.SessionList:
                    return new JsonObject { ["sessions"] = Sessions.DeepClone() };
                case AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad:
                    Attached = true;
                    return new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaPosition] = "0" } };
                case AcpMethods.Agent.SessionClose or GorynychProtocol.MethodDetach:
                    Attached = false;
                    return new JsonObject();
                case AcpMethods.Agent.SessionPrompt:
                    return new JsonObject { ["stopReason"] = "end_turn" };
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        }

        public Task SayAsync(string sessionId, string text) => Rpc!.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["update"] = new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } },
        });
    }

    static JsonObject Info(string sessionId, string agent = "claude") => new()
    {
        ["sessionId"] = sessionId, ["cwd"] = @"C:\work", ["title"] = sessionId, ["updatedAt"] = "2026-11-12T10:00:00Z",
        ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
    };

    static JsonObject Resume(string sessionId, string? after = null)
    {
        var r = new JsonObject { ["sessionId"] = sessionId, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() };
        if (after is not null)
            r["_meta"] = new JsonObject { [GorynychProtocol.MetaReplayFrom] = new JsonObject { ["type"] = GorynychProtocol.ReplayAfterMark, ["mark"] = after } };
        return r;
    }

    /// <summary>
    /// A remotes.json that is no list of remotes at all (an array, a number, a broken one) leaves the remotes as they
    /// were, and the watcher reads on: a good file later is taken, also when the first one it read was broken.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ABrokenRemotesJson_LeavesTheRemotesAsTheyWere_AndALaterGoodOneIsTaken()
    {
        var fake = new FakeRemote();
        var data = TestDirs.New();
        var path = RemotesFile.PathIn(data);
        File.WriteAllText(path, "[]");
        await using var hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("codex", canDelete: true).Adapter], _ => fake.ConnectAsync);
        await Task.Delay(400);
        Assert.AreEqual(0, hub.Remotes.Status().Count);

        RemotesFile.Write(path, [new RemoteEntry("box", "ssh", ["box"], [])]);
        await LinkHarness.Until(() => Connected(hub), "the good file was taken after a broken one");
        foreach (var broken in new[] { "5", "[]", "{\"remotes\": 3}", "{\"remotes\": [{\"name\": \"a@b\", \"command\": \"x\"}]}", "{" })
        {
            AtomicFile.WriteAllText(path, broken);
            await Task.Delay(400);
            Assert.IsTrue(Connected(hub) && hub.Remotes.Status().Count == 1, $"the remotes stay as they were after: {broken}");
        }
        RemotesFile.Write(path, [new RemoteEntry("box", "ssh", ["box"], []), new RemoteEntry("box2", "ssh", ["box2"], [])]);
        await LinkHarness.Until(() => hub.Remotes.Status().Count == 2, "and the watcher still reads");
    }

    /// <summary>
    /// The same ID on two machines (a copied Claude transcript keeps its UUID): what the machine that does not own the
    /// session here sends about it (an update, a question, a removal) is dropped, and the owner's goes on.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WhatAMachineSendsAboutAnotherMachinesSession_IsDropped()
    {
        var box = new FakeRemote("box-service") { Sessions = [Info("same-1")] };
        var rogue = new FakeRemote("rogue-service");
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("box", "ssh", ["box"], []), new RemoteEntry("rogue", "ssh", ["rogue"], [])]);
        await using var hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("codex", canDelete: true).Adapter],
            entry => entry.Name == "box" ? box.ConnectAsync : rogue.ConnectAsync);
        await LinkHarness.Until(() => Connected(hub) && hub.Remotes.Status().Count == 2, "both connected");
        await using var zed = await TestClient.ConnectAsync(hub, "zed");
        Assert.AreEqual("claude@box", Agent((await ListAllAsync(zed)).Single()));
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("same-1")).WaitAsync(Wait);

        await rogue.SayAsync("same-1", "from rogue");
        var asked = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => rogue.Rpc!.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(new RequestPermissionRequest
        {
            SessionId = "same-1", ToolCall = new ToolCallUpdate { ToolCallId = "c1", Title = "rm -rf" },
            Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
        })).WaitAsync(Wait));
        await rogue.Rpc!.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject
        {
            ["sessionId"] = "same-1", ["change"] = "removed", ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
        });
        await box.SayAsync("same-1", "from box");

        await LinkHarness.Until(() => zed.Text().Contains("from box"), "the owner's update arrives");
        Assert.AreEqual("from box", zed.Text(), "none of the other machine's");
        Assert.AreEqual(0, zed.Approvals.Count, "nor its question");
        StringAssert.Contains(asked.Message, "another session's");
        Assert.AreEqual("claude@box", hub.Remotes.AgentOf("same-1"), "and the route stays the owner's");
        Assert.IsFalse(zed.Changes.Any(c => (string?)c["change"] == "removed"), "no removal said for it");
    }

    /// <summary>
    /// The remote comes back as another build (an update there): its agents and extensions are taken again, and the
    /// clients told; one that comes back as this service itself is refused.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ARemoteThatComesBack_IsTakenAgain_ItsNewAgentsAnnounced_AndItselfRefused()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        await using var updated = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "remote-service" },
            [new LifecycleAgent("claude", canDelete: false).Adapter, new LifecycleAgent("kimi", canDelete: true).Adapter, new LifecycleAgent("dsh", canDelete: false).Adapter]);

        rig.Target = updated;
        rig.Cut();

        await LinkHarness.Until(() => rig.Hub.Remotes.Agents.Any(a => a.Name == "dsh@box"), "its new agent is offered");
        await LinkHarness.Until(() => zed.Notices.Contains(GorynychProtocol.MethodAgentsChanged), "and the clients told");
        Assert.AreEqual(1, rig.Hub.Remotes.Status().Count);

        rig.Target = rig.Hub;
        rig.Cut();
        await LinkHarness.Until(() => (string?)rig.Box?["lastError"] is { } e && e.Contains("is this service itself"), "refused when it came back as this service");
        Assert.IsFalse(rig.Connected);
    }

    /// <summary>A client's own re-open that fails keeps it as it had it; a first one that fails leaves nothing open there.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnOpenThatFails_KeepsWhatTheClientHad_OrLeavesNothingOpen()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var zed = await TestClient.ConnectAsync(rig.Hub, "zed");
        var id = await NewPromptedAsync(zed, "claude@box", "Port the parser");

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(id, after: "not-a-position")).WaitAsync(Wait));

        Assert.AreEqual(1, rig.Hub.Remotes.ClientsOf(id), "it still has it");
        await using (var there = await TestClient.ConnectAsync(rig.Remote, "remote-zed"))
        {
            await there.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(id)).WaitAsync(Wait);
            rig.RemoteClaude.Answer = "and still gets it";
            await PromptAsync(there, id, "Anything new?");
        }
        await LinkHarness.Until(() => zed.Text().Contains("and still gets it"), "its updates still come");
        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id }).WaitAsync(Wait);
        await LinkHarness.Until(() => rig.Remote.Feed(id).Clients().Length == 0 && !rig.Hub.Remotes.Follows(id), "let go there and here");

        await using var other = await TestClient.ConnectAsync(rig.Hub, "zed-2");
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume(id, after: "not-a-position")).WaitAsync(Wait));
        await Task.Delay(300);
        Assert.AreEqual(0, rig.Remote.Feed(id).Clients().Length, "a first open that failed leaves nothing open there");
        Assert.IsFalse(rig.Hub.Remotes.Follows(id));
    }

    /// <summary>A close for the hub's last client, and a new client's open right behind it, reach the remote in that order.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnOpenRightAfterTheLastClose_WaitsForTheCloseThere()
    {
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeRemote { Sessions = [Info("s-1")] };
        fake.On[AcpMethods.Agent.SessionClose] = async (_, _) =>
        {
            await closing.Task;
            fake.Attached = false;
            return new JsonObject();
        };
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("box", "ssh", ["box"], [])]);
        await using var hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("codex", canDelete: true).Adapter], _ => fake.ConnectAsync);
        await LinkHarness.Until(() => Connected(hub), "connected");
        await using var first = await TestClient.ConnectAsync(hub, "zed-1");
        await using var second = await TestClient.ConnectAsync(hub, "zed-2");
        await ListAllAsync(first);
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("s-1")).WaitAsync(Wait);
        await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = "s-1" }).WaitAsync(Wait);
        await LinkHarness.Until(() => fake.CallsOf(AcpMethods.Agent.SessionClose) == 1, "the close is on its way there");

        var reopening = second.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("s-1"));
        await Task.Delay(300);
        Assert.IsFalse(reopening.IsCompleted, "it waits for the close");
        closing.SetResult();
        await reopening.WaitAsync(Wait);

        Assert.IsTrue(fake.Attached, "open there, as the hub thinks");
        Assert.AreEqual(1, hub.Remotes.ClientsOf("s-1"));
    }

    /// <summary>A client that stops reading holds up nobody: another client of the same remote session gets its turn at once.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientThatStopsReading_HoldsUpNoOtherClient()
    {
        await using var rig = new Rig();
        await LinkHarness.Until(() => rig.Connected, "the hub connected to box");
        await using var reader = await TestClient.ConnectAsync(rig.Hub, "zed-reads");
        var id = await NewPromptedAsync(reader, "claude@box", "Port the parser");
        // A client that opens the session, then reads nothing more: its pipe fills.
        var stuck = new DuplexLink();
        var (si, so) = stuck.AgentEnd;
        _ = rig.Hub.ServeClientAsync(si, so, "zed-stuck");
        var (_, toHub) = stuck.ClientEnd;
        foreach (var line in new[]
        {
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = 1 } },
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = AcpMethods.Agent.SessionResume, ["params"] = Resume(id) },
        })
            await toHub.WriteAsync(Encoding.UTF8.GetBytes(line.ToJsonString() + "\n"));
        await toHub.FlushAsync();
        await LinkHarness.Until(() => rig.Hub.Remotes.ClientsOf(id) == 2, "both have it");
        rig.RemoteClaude.Answer = new string('x', 300_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await PromptAsync(reader, id, "Write a lot");

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5), $"the reader waited {watch.ElapsedMilliseconds} ms");
        Assert.AreEqual(300_000 + 2, reader.Text().Length, "and got all of it");
    }

    /// <summary>A remote that answers but is slow holds up no list for long; an ID no remote has is not looked for again at once.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASlowRemote_HoldsUpAListBriefly_AndAnUnknownIdIsNotLookedForAgainAtOnce()
    {
        var slow = new FakeRemote("slow-service");
        slow.On[AcpMethods.Agent.SessionList] = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        var fast = new FakeRemote("fast-service") { Sessions = [Info("s-1")] };
        var data = TestDirs.New();
        RemotesFile.Write(RemotesFile.PathIn(data), [new RemoteEntry("slow", "ssh", ["slow"], []), new RemoteEntry("fast", "ssh", ["fast"], [])]);
        await using var hub = Rig.HubOver(data, "hub-service", [new LifecycleAgent("codex", canDelete: true).Adapter],
            entry => entry.Name == "slow" ? slow.ConnectAsync : fast.ConnectAsync, listWait: TimeSpan.FromMilliseconds(300));
        await LinkHarness.Until(() => Connected(hub) && hub.Remotes.Status().Count == 2, "both connected");
        await using var zed = await TestClient.ConnectAsync(hub, "zed");
        await NewPromptedAsync(zed, "codex", "Plan the week");

        var listed = await ListAllAsync(zed).WaitAsync(TimeSpan.FromSeconds(3));
        CollectionAssert.AreEquivalent(new[] { "codex", "claude@fast" }, listed.Select(Agent).ToArray(), "the slow one's sessions are left out this time");

        var before = fast.CallsOf(AcpMethods.Agent.SessionList);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("nope")).WaitAsync(Wait));
        Assert.AreEqual(before + 1, fast.CallsOf(AcpMethods.Agent.SessionList), "looked for once: " + string.Join(" | ", fast.Calls));

        await fast.Rpc!.SendNotificationAsync(GorynychProtocol.MethodSessionsChanged, new JsonObject
        {
            ["sessionId"] = "s-2", ["change"] = "added", ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = "claude" },
        });
        await Task.Delay(100);
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, Resume("nope")).WaitAsync(Wait));
        Assert.AreEqual(before + 2, fast.CallsOf(AcpMethods.Agent.SessionList), "and again once a list changed");
    }
}
