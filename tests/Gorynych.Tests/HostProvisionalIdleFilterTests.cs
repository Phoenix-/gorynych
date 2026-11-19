using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Hosts;
using Gorynych.Adapters.Codex;

namespace Gorynych.Tests;

[TestClass]
public sealed class HostProvisionalIdleFilterTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task OnlyMatchingSuccessfulLivePromptIsForwardedWithoutReplay(bool enabled)
    {
        var agentLink = new DuplexLink(); var hostLink = new DuplexLink();
        await using var agent = new JsonRpcConnection(agentLink.AgentEnd.Input, agentLink.AgentEnd.Output, "idle-fake");
        var frames = Channel.CreateUnbounded<JsonNode>(); var processed = Channel.CreateUnbounded<bool>();
        agent.RequestHandler = (request, _) => Task.FromResult<JsonNode?>(request.Method switch
        {
            AcpMethods.Agent.Initialize => new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() },
            AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "session" },
            AcpMethods.Agent.SessionPrompt => new JsonObject { ["stopReason"] = "end_turn",
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = "native-turn" } },
            _ => new JsonObject(),
        });
        agent.Start();
        var instance = new AgentInstance(agentLink.ClientEnd.Input, agentLink.ClientEnd.Output, agent.Completion, () => agent.DisposeAsync());
        var host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((hostLink.ClientEnd.Input, hostLink.ClientEnd.Output)),
            new AdapterHostOptions { Adapter = "codex", Token = "test", FilterCodexIdleForTests = enabled,
                IdleFrameProcessedForTests = () => processed.Writer.TryWrite(true) });
        var run = host.RunAsync();
        await using var conductor = new JsonRpcConnection(hostLink.AgentEnd.Input, hostLink.AgentEnd.Output, "idle-conductor");
        conductor.NotificationHandler = (method, value) => { if (method == GorynychProtocol.MethodHostCodexProvisionalIdle) frames.Writer.TryWrite(value!.DeepClone()); return Task.CompletedTask; };
        conductor.Start();
        try
        {
            await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);
            await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject()).WaitAsync(Wait);
            JsonObject Frame(string prompt = "prompt", string turn = "native-turn", string state = "confirmed") => new() { ["_meta"] = new JsonObject
            { ["sessionId"] = "session", ["promptId"] = prompt, ["nativeTurnId"] = turn, ["turnEpoch"] = 1L, ["ingressRevision"] = 2L, ["state"] = state } };
            async Task Send(JsonObject frame)
            {
                await agent.SendNotificationAsync(GorynychProtocol.MethodCodexProvisionalIdle, frame).WaitAsync(Wait);
                await processed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            }
            await Send(Frame()); Assert.IsFalse(frames.Reader.TryRead(out _), "no successful response yet");
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = "session", ["prompt"] = new JsonArray(),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "prompt" } }).WaitAsync(Wait);
            await Send(Frame(prompt: "other")); await Send(Frame(turn: "other"));
            var malformed = Frame(); malformed["_meta"]!["turnEpoch"] = -1; await Send(malformed);
            Assert.IsFalse(frames.Reader.TryRead(out _), "foreign and malformed identities are not forwarded");
            await Send(Frame());
            if (!enabled) { Assert.IsFalse(frames.Reader.TryRead(out _), "disabled by default"); return; }
            var confirmation = await frames.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.AreEqual("confirmed", (string?)confirmation["_meta"]!["state"]);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "session" }).WaitAsync(Wait);
            var revoked = await frames.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.AreEqual("revoked", (string?)revoked["_meta"]!["state"], "new history admission revokes the published proof first");
            await Send(Frame(state: "revoked"));
            await Send(Frame()); Assert.IsFalse(frames.Reader.TryRead(out _), "old proof cannot revive after history admission");
            await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            Assert.IsFalse(frames.Reader.TryRead(out _), "private evidence is not replayed");
            await Send(Frame()); Assert.IsFalse(frames.Reader.TryRead(out _), "reattach requires a new eligible prompt");
        }
        finally { await conductor.DisposeAsync(); await agent.DisposeAsync(); await run.WaitAsync(Wait); }
    }
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NewerHistoryAdmissionCannotBeOverwrittenByOlderResponse()
    {
        var agentLink = new DuplexLink(); var hostLink = new DuplexLink();
        await using var agent = new JsonRpcConnection(agentLink.AgentEnd.Input, agentLink.AgentEnd.Output, "idle-race-agent");
        var oldResponseHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = Channel.CreateUnbounded<bool>(); var frames = Channel.CreateUnbounded<JsonNode>();
        agent.RequestHandler = (request, _) =>
        {
            if (request.Method == AcpMethods.Agent.SessionResume) historyArrived.TrySetResult();
            return Task.FromResult<JsonNode?>(request.Method switch
            {
                AcpMethods.Agent.Initialize => new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() },
                AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "session" },
                AcpMethods.Agent.SessionPrompt => new JsonObject { ["stopReason"] = "end_turn", ["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = "old-native" } },
                _ => new JsonObject(),
            });
        };
        agent.Start(); var instance = new AgentInstance(agentLink.ClientEnd.Input, agentLink.ClientEnd.Output, agent.Completion, () => agent.DisposeAsync());
        var host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((hostLink.ClientEnd.Input, hostLink.ClientEnd.Output)),
            new AdapterHostOptions { Adapter = "codex", Token = "test", FilterCodexIdleForTests = true,
                IdleFrameProcessedForTests = () => processed.Writer.TryWrite(true),
                BeforeIdleResponseAdmissionForTests = async prompt => { if (prompt == "old") { oldResponseHeld.TrySetResult(); await release.Task.WaitAsync(Wait); } } });
        var run = host.RunAsync();
        await using var conductor = new JsonRpcConnection(hostLink.AgentEnd.Input, hostLink.AgentEnd.Output, "idle-race-conductor");
        conductor.NotificationHandler = (method, value) => { if (method == GorynychProtocol.MethodHostCodexProvisionalIdle) frames.Writer.TryWrite(value!.DeepClone()); return Task.CompletedTask; };
        conductor.Start();
        try
        {
            await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);
            await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject()).WaitAsync(Wait);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = "session", ["prompt"] = new JsonArray(),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "old" } }).WaitAsync(Wait);
            await oldResponseHeld.Task.WaitAsync(Wait);
            var newer = conductor.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject { ["sessionId"] = "session" });
            await historyArrived.Task.WaitAsync(Wait); // admitted and sent before old response installs a slot
            release.TrySetResult(); await newer.WaitAsync(Wait);
            await agent.SendNotificationAsync(GorynychProtocol.MethodCodexProvisionalIdle, new JsonObject { ["_meta"] = new JsonObject
                { ["sessionId"] = "session", ["promptId"] = "old", ["nativeTurnId"] = "old-native", ["turnEpoch"] = 1L, ["ingressRevision"] = 1L, ["state"] = "confirmed" } }).WaitAsync(Wait);
            await processed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.IsFalse(frames.Reader.TryRead(out _), "older response cannot rearm an invalidated admission");
        }
        finally { release.TrySetResult(); await conductor.DisposeAsync(); await agent.DisposeAsync(); await run.WaitAsync(Wait); }
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RealCodexAdapterWitnessPassesHostFilterOnlyForItsResponse()
    {
        var named = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeAppServer { IdleBeforeTurnCompleted = true, BeforeEarlyIdle = () => named.Task.WaitAsync(Wait) };
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test", NativeTurnNamedForTests = (_, _) => named.TrySetResult(),
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = Path.Combine(TestDirs.New(), "no-native-locks"),
            EmitProvisionalIdleForTests = true });
        var instance = await adapter.StartAsync(CancellationToken.None);
        var link = new DuplexLink();
        var host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((link.ClientEnd.Input, link.ClientEnd.Output)),
            new AdapterHostOptions { Adapter = "codex", Token = "test", FilterCodexIdleForTests = true });
        var run = host.RunAsync();
        await using var conductor = new JsonRpcConnection(link.AgentEnd.Input, link.AgentEnd.Output, "idle-conductor");
        var frames = Channel.CreateUnbounded<JsonNode>();
        conductor.NotificationHandler = (method, value) => { if (method == GorynychProtocol.MethodHostCodexProvisionalIdle) frames.Writer.TryWrite(value!.DeepClone()); return Task.CompletedTask; };
        conductor.Start();
        try
        {
            await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);
            await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            var created = await conductor.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
            var session = (string)created!["sessionId"]!;
            var result = await conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = session,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "real adapter fixture" }),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "prompt-identity" } }).WaitAsync(Wait);
            var frame = await frames.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.AreEqual("confirmed", (string?)frame["_meta"]!["state"]);
            Assert.AreEqual("prompt-identity", (string?)frame["_meta"]!["promptId"]);
            Assert.AreEqual((string?)result!["_meta"]![GorynychProtocol.MetaHistoryMark], (string?)frame["_meta"]!["nativeTurnId"]);
            await fake.Rpc!.SendNotificationAsync("thread/status/changed", new JsonObject { ["threadId"] = session,
                ["status"] = new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray() } });
            var revoked = await frames.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            Assert.AreEqual("revoked", (string?)revoked["_meta"]!["state"]);
            Assert.AreEqual(1, fake.All("turn/start").Count(x => (string?)x["threadId"] == session));
        }
        finally
        {
            if (!conductor.IsClosed) await conductor.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject()).WaitAsync(Wait);
            await conductor.DisposeAsync(); await run.WaitAsync(Wait);
        }
    }
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RuntimeHandshakeRequiresBothPeers(bool conductorSupports, bool adapterSupports)
    {
        var agentLink = new DuplexLink(); var hostLink = new DuplexLink();
        await using var agent = new JsonRpcConnection(agentLink.AgentEnd.Input, agentLink.AgentEnd.Output, "idle-mixed-agent");
        var initReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RequestHandler = (request, _) =>
        {
            if (request.Method == AcpMethods.Agent.Initialize)
            {
                initReceived.TrySetResult((bool?)request.Params?["_meta"]?[GorynychProtocol.CapCodexIdleHostReceiver] == true);
                return Task.FromResult<JsonNode?>(new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject(),
                    ["_meta"] = new JsonObject { [GorynychProtocol.CapCodexIdleAdapter] = adapterSupports, [GorynychProtocol.CapHostCodexProvisionalIdle] = true } });
            }
            return Task.FromResult<JsonNode?>(request.Method switch
            {
                AcpMethods.Agent.SessionNew => new JsonObject { ["sessionId"] = "session" },
                AcpMethods.Agent.SessionPrompt => new JsonObject { ["stopReason"] = "end_turn", ["_meta"] = new JsonObject { [GorynychProtocol.MetaHistoryMark] = "native" } },
                _ => new JsonObject(),
            });
        };
        agent.Start();
        var instance = new AgentInstance(agentLink.ClientEnd.Input, agentLink.ClientEnd.Output, agent.Completion, () => agent.DisposeAsync());
        var processed = Channel.CreateUnbounded<bool>(); var frames = Channel.CreateUnbounded<JsonNode>();
        var host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((hostLink.ClientEnd.Input, hostLink.ClientEnd.Output)),
            new AdapterHostOptions { Adapter = "codex", Token = "test", IdleFrameProcessedForTests = () => processed.Writer.TryWrite(true) });
        var run = host.RunAsync();
        await using var conductor = new JsonRpcConnection(hostLink.AgentEnd.Input, hostLink.AgentEnd.Output, "idle-mixed-conductor");
        conductor.NotificationHandler = (method, value) => { if (method == GorynychProtocol.MethodHostCodexProvisionalIdle) frames.Writer.TryWrite(value!.DeepClone()); return Task.CompletedTask; };
        conductor.Start();
        try
        {
            var answer = await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1,
                ["_meta"] = new JsonObject { [GorynychProtocol.CapHostCodexProvisionalIdle] = conductorSupports } }).WaitAsync(Wait);
            Assert.IsTrue(await initReceived.Task.WaitAsync(Wait), "host supplies its own adapter-side capability");
            Assert.AreEqual(conductorSupports && adapterSupports, (bool?)answer?["_meta"]?[GorynychProtocol.CapHostCodexProvisionalIdle] == true);
            Assert.IsNull(answer?["_meta"]?[GorynychProtocol.CapCodexIdleAdapter], "private adapter capability is not forwarded");
            await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() }).WaitAsync(Wait);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject()).WaitAsync(Wait);
            await conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = "session", ["prompt"] = new JsonArray(),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = "prompt" } }).WaitAsync(Wait);
            await agent.SendNotificationAsync(GorynychProtocol.MethodCodexProvisionalIdle, new JsonObject { ["_meta"] = new JsonObject
                { ["sessionId"] = "session", ["promptId"] = "prompt", ["nativeTurnId"] = "native", ["turnEpoch"] = 1L, ["ingressRevision"] = 1L, ["state"] = "confirmed" } }).WaitAsync(Wait);
            await processed.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            if (conductorSupports && adapterSupports)
                Assert.AreEqual("confirmed", (string?)(await frames.Reader.ReadAsync().AsTask().WaitAsync(Wait))["_meta"]!["state"]);
            else Assert.IsFalse(frames.Reader.TryRead(out _));
            var oldConductor = await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 }).WaitAsync(Wait);
            Assert.IsNull(oldConductor?["_meta"]?[GorynychProtocol.CapHostCodexProvisionalIdle], "support is negotiated anew for each initialize");
        }
        finally { await conductor.DisposeAsync(); await agent.DisposeAsync(); await run.WaitAsync(Wait); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task AdapterRequiresHostedConstructionAndHostInitialize(bool hosted, bool offer)
    {
        var fake = new FakeAppServer();
        var adapter = new CodexAdapter(new CodexAdapterOptions { Launch = fake.StartAsync, ClientVersion = "test", HostedIdleEvidence = hosted,
            FindHolders = _ => new Dictionary<string, string>(), LocksDirectory = Path.Combine(TestDirs.New(), "no-native-locks") });
        var instance = await adapter.StartAsync(CancellationToken.None);
        await using var rpc = new JsonRpcConnection(instance.Input, instance.Output, "idle-direct-check");
        rpc.Start();
        try
        {
            var result = await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1,
                ["_meta"] = new JsonObject { [GorynychProtocol.CapCodexIdleHostReceiver] = offer } }).WaitAsync(Wait);
            Assert.AreEqual(hosted && offer, (bool?)result?["_meta"]?[GorynychProtocol.CapCodexIdleAdapter] == true);
        }
        finally { await rpc.DisposeAsync(); await instance.DisposeAsync(); }
    }

}
