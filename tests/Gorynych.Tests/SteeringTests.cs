using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The conductor's shared ACP v1 steering extension (docs/steering.md): capability, validation,
/// forwarding, journaling, the legacy idle fallback and uncertain outcomes. Adapter-specific
/// behaviour is in ClaudeHeadlessTests and CodexAdapterTests.
/// </summary>
[TestClass]
public sealed class SteeringTests
{
    static JsonNode? NewSession(string id) => AcpJson.ToNode(new NewSessionResponse { SessionId = id, Meta = null });

    static JsonNode? Init(bool steering = true) => ScriptedAgent.Init(steering: steering);

    static Task<JsonNode?> Steer(TestClient client, string sessionId, string text, bool promptRequired = true, string? promptId = null,
        JsonNode? content = null)
    {
        var meta = new JsonObject();
        if (promptRequired) meta[GorynychProtocol.MetaSteering] = new JsonObject { ["idleBehavior"] = "promptRequired" };
        if (promptId is not null) meta[GorynychProtocol.MetaPromptId] = promptId;
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = content ?? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["_meta"] = meta,
        });
    }

    static Task<PromptResponse> Prompt(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static async Task<string> NewAsync(TestClient client, string agent = "a")
    {
        var response = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Path.GetTempPath(), Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } });
        return response.SessionId;
    }

    static string? Outcome(JsonNode? result) => (string?)result?["outcome"];

    [TestMethod]
    public async Task CapabilityAndSessionFlag_AreAdvertisedOnlyWhenTheAgentSteers()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir,
            new ScriptedAdapter("yes", (_, request, _) => Task.FromResult(request.Method == AcpMethods.Agent.Initialize ? Init() : NewSession("s1"))),
            new ScriptedAdapter("no", (_, request, _) => Task.FromResult(request.Method == AcpMethods.Agent.Initialize ? Init(steering: false) : NewSession("s2"))));
        await using var client = await TestClient.ConnectAsync(conductor);
        Assert.IsTrue((bool?)client.Initialized?.Meta?[GorynychProtocol.MetaSteering]?["supported"]);

        var yes = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "yes" } });
        var no = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = dir, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "no" } });
        Assert.AreEqual(true, (bool?)yes.Meta?[GorynychProtocol.MetaSessionSteering]);
        Assert.AreEqual(false, (bool?)no.Meta?[GorynychProtocol.MetaSessionSteering]);

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Steer(client, "s2", "late"));
        Assert.AreEqual(JsonRpcException.InvalidRequest, refused.Code);
        Assert.AreEqual("notSupported", (string?)refused.ErrorData?[GorynychProtocol.MetaSteerRefused]);
    }

    [TestMethod]
    public async Task InvalidSteeringParams_AreRefusedBeforeTheAdapter()
    {
        var calls = 0;
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ScriptedAdapter("a", (_, request, _) =>
        {
            if (request.Method == AcpMethods.Agent.Initialize) return Task.FromResult(Init());
            if (request.Method == GorynychProtocol.MethodSteering) Interlocked.Increment(ref calls);
            return Task.FromResult(NewSession("s"));
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = await NewAsync(client);

        static JsonObject Params(string sessionId, JsonNode prompt, JsonNode? idle = null) => new()
        {
            ["sessionId"] = sessionId, ["prompt"] = prompt,
            ["_meta"] = idle is null ? new JsonObject() : new JsonObject { [GorynychProtocol.MetaSteering] = idle },
        };

        var empty = await Assert.ThrowsExactlyAsync<JsonRpcException>(() =>
            client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering, Params(session, new JsonArray())));
        Assert.AreEqual(JsonRpcException.InvalidParams, empty.Code);

        var image = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering,
            Params(session, new JsonArray(new JsonObject { ["type"] = "image", ["data"] = "aGk=", ["mimeType"] = "image/png" }))));
        Assert.AreEqual(JsonRpcException.InvalidParams, image.Code);

        var unknown = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering,
            Params(session, new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }), new JsonObject { ["idleBehavior"] = "now" })));
        Assert.AreEqual(JsonRpcException.InvalidParams, unknown.Code);

        var noPrompt = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => client.Rpc.SendRequestAsync(GorynychProtocol.MethodSteering,
            new JsonObject { ["sessionId"] = session, ["_meta"] = new JsonObject() }));
        Assert.AreEqual(JsonRpcException.InvalidParams, noPrompt.Code);
        Assert.AreEqual(0, calls, "nothing malformed reached the adapter");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task InjectedSteer_IsJournaledOnceBeforeTheTurnEnd_AndNotEchoedToItsSender()
    {
        var dir = TestDirs.New();
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ScriptedAdapter("a", (_, request, _) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return Task.FromResult(Init());
                case AcpMethods.Agent.SessionNew: return Task.FromResult(NewSession("s"));
                case AcpMethods.Agent.SessionPrompt:
                    promptStarted.TrySetResult();
                    return release.Task;
                case GorynychProtocol.MethodSteering:
                    return Task.FromResult<JsonNode?>(AcpSteering.Result(AcpSteering.Injected));
                default: return Task.FromResult<JsonNode?>(new JsonObject());
            }
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var sender = await TestClient.ConnectAsync(conductor, "sender");
        var session = await NewAsync(sender);
        await using var watcher = await TestClient.ConnectAsync(conductor, "watcher");
        await watcher.Rpc.RequestAsync<ResumeSessionRequest, ResumeSessionResponse>(AcpMethods.Agent.SessionResume,
            new ResumeSessionRequest { SessionId = session, Cwd = dir, McpServers = [] });

        var prompt = Prompt(sender, session, "work");
        await promptStarted.Task;
        var steered = await Steer(sender, session, "turn left", promptId: "client-p1");
        Assert.AreEqual(AcpSteering.Injected, Outcome(steered));

        await Task.Delay(100); // live publishing is asynchronous to the answer's writer
        var chunks = conductor.Feed(session).Journal.ReadAll().OfType<JsonObject>()
            .Where(u => (string?)u["sessionUpdate"] == "user_message_chunk").ToList();
        var steerChunk = chunks.Single(u => (string?)u["content"]?["text"] == "turn left");
        Assert.AreEqual(true, (bool?)steerChunk["_meta"]?[GorynychProtocol.MetaSteered]);
        Assert.AreEqual("client-p1", (string?)steerChunk["_meta"]?[GorynychProtocol.MetaPromptId]);
        Assert.IsNull(sender.Snapshot().FirstOrDefault(u => (string?)u["content"]?["text"] == "turn left"), "the sender already has its text");
        Assert.IsTrue(watcher.Snapshot().Any(u => (string?)u["content"]?["text"] == "turn left"), "the other client sees the steered message live");

        release.TrySetResult(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
        Assert.AreEqual(StopReason.EndTurn, (await prompt).StopReason);
        var journal = conductor.Feed(session).Journal.ReadAll().OfType<JsonObject>().ToList();
        var steerAt = journal.FindIndex(u => (string?)u["content"]?["text"] == "turn left");
        var endAt = journal.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.IsTrue(steerAt >= 0 && endAt > steerAt, $"the steered message is before its turn's end ({steerAt} < {endAt})");
        Assert.AreEqual(1, journal.Count(u => (string?)u["content"]?["text"] == "turn left"), "journaled once");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OptedInIdleSteer_AnswersPromptRequired_AndJournalsNothing()
    {
        var dir = TestDirs.New();
        await using var conductor = TestDirs.Conductor(dir, new ScriptedAdapter("a", (_, request, _) =>
        {
            return Task.FromResult(request.Method switch
            {
                AcpMethods.Agent.Initialize => Init(),
                AcpMethods.Agent.SessionNew => NewSession("s"),
                GorynychProtocol.MethodSteering => AcpSteering.PromptRequiredResult(),
                _ => new JsonObject(),
            } as JsonNode);
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = await NewAsync(client);
        var result = await Steer(client, session, "too late");
        Assert.AreEqual(AcpSteering.PromptRequired, Outcome(result));
        Assert.AreEqual(AcpSteering.NoRunningTurn, (string?)result?["reason"]);
        Assert.AreEqual(0, conductor.Feed(session).Journal.ReadAll().Count);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task LegacyIdleSteer_StartsTheTurn_WithItsClientPromptIdAndAnswersOnceHanded()
    {
        var dir = TestDirs.New();
        var prompt = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = TestDirs.Conductor(dir, new ScriptedAdapter("a", (_, request, _) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return Task.FromResult(Init());
                case AcpMethods.Agent.SessionNew: return Task.FromResult(NewSession("s"));
                case AcpMethods.Agent.SessionPrompt:
                    prompt.TrySetResult((JsonObject)request.Params!.DeepClone());
                    return Task.FromResult<JsonNode?>(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
                case GorynychProtocol.MethodSteering:
                    return Task.FromResult<JsonNode?>(AcpSteering.PromptRequiredResult());
                default: return Task.FromResult<JsonNode?>(new JsonObject());
            }
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = await NewAsync(client);
        var result = await Steer(client, session, "start after all", promptRequired: false);
        Assert.AreEqual(AcpSteering.StartedNewTurn, Outcome(result));
        var got = await prompt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var messageId = (string?)got["_meta"]?[GorynychProtocol.MetaMessageId];
        var promptId = (string?)got["_meta"]?[GorynychProtocol.MetaPromptId];
        Assert.AreEqual("service-" + messageId, promptId);
        var journal = conductor.Feed(session).Journal.ReadAll().OfType<JsonObject>().ToList();
        var chunk = journal.Single(u => (string?)u["sessionUpdate"] == "user_message_chunk");
        Assert.AreEqual(messageId, (string?)chunk["messageId"]);
        Assert.AreEqual(promptId, (string?)chunk["_meta"]?[GorynychProtocol.MetaPromptId]);
        Assert.IsNull(chunk["_meta"]?[GorynychProtocol.MetaSteered], "the fallback is an ordinary prompt, not a steered message");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAcceptanceKeptByTheHost_IsJournaledByTheNextConductor_BeforeTheTurnEnd(bool nativeEndFirst)
    {
        var data = TestDirs.New();
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerRelease = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new ScriptedAdapter("a", (_, request, _) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return Task.FromResult(Init());
                case AcpMethods.Agent.SessionNew: return Task.FromResult(NewSession("s-host"));
                case AcpMethods.Agent.SessionPrompt:
                    started.TrySetResult();
                    return release.Task;
                case GorynychProtocol.MethodSteering:
                    steerEntered.TrySetResult();
                    return steerRelease.Task;
                default: return Task.FromResult<JsonNode?>(new JsonObject());
            }
        });
        var hosts = new InProcessHosts(_ => agent);
        var service = hosts.Conductor(data, agent);
        hosts.Service = service;
        await using var client = await TestClient.ConnectAsync(service);
        var sid = await NewAsync(client);
        var prompt = Prompt(client, sid, "work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        hosts.WireOf(hosts["a"]).HoldBackFromService(_ => true);
        var steering = Steer(client, sid, "kept by the host", promptId: "recover-p1");
        await steerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (nativeEndFirst)
        {
            release.TrySetResult(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
            await Task.Delay(150); // let the host read the terminal response before the steering response
        }
        steerRelease.TrySetResult(AcpSteering.Result(AcpSteering.Injected));
        await Task.Delay(300); // the host accepted it, and its conductor has not read the record yet
        Assert.IsTrue((hosts["a"].Host.Report()["buffered"] as JsonArray)?.Any(n => (string?)n == sid) == true,
            "the host keeps the acceptance in its numbered buffer");
        var restart = await HostTests.RestartAsync(service);
        Assert.AreEqual(true, (bool?)restart["restarted"]);
        await HostTests.GoneAsync(service, null, hosts);

        await using var next = HostTests.Next(data, hosts, null, agent);
        try
        {
            await LinkHarness.Until(() => next.Feed(sid).Journal.SteeredMessageIds().Count > 0, "the replay of the host's acceptance", 5000);
        }
        catch (Exception e)
        {
            var lines = string.Join("\n", next.Feed(sid).Journal.ReadAll().Select(u => u.ToJsonString()));
            Assert.Fail($"{e.Message}\nhosts:\n{hosts.Log()}\njournal:\n{lines}\nbuffered: {hosts["a"].Host.Report()["buffered"]?.ToJsonString()}");
        }
        release.TrySetResult(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
        await LinkHarness.Until(() => next.Feed(sid).Journal.ReadAll().OfType<JsonObject>()
            .Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"), "the turn end");
        var journal = next.Feed(sid).Journal.ReadAll().OfType<JsonObject>().ToList();
        Assert.AreEqual(1, journal.Count(u => (bool?)u["_meta"]?[GorynychProtocol.MetaSteered] == true), "the record and its response journal one message");
        var steeredAt = journal.FindIndex(u => (bool?)u["_meta"]?[GorynychProtocol.MetaSteered] == true);
        var endAt = journal.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.IsTrue(steeredAt >= 0 && endAt > steeredAt, $"the recovered message is before the end marker ({steeredAt} < {endAt})");
        _ = steering; // the old service's answer went with it; the record is what recovers acceptance
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ANativeTurnEnd_ThatBeatsTheSteerAnswer_StillJournalsTheSteerFirst()
    {
        var dir = TestDirs.New();
        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptRelease = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerRelease = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ScriptedAdapter("a", (_, request, _) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return Task.FromResult(Init());
                case AcpMethods.Agent.SessionNew: return Task.FromResult(NewSession("s-race"));
                case AcpMethods.Agent.SessionPrompt:
                    promptStarted.TrySetResult();
                    return promptRelease.Task;
                case GorynychProtocol.MethodSteering:
                    return steerRelease.Task;
                default: return Task.FromResult<JsonNode?>(new JsonObject());
            }
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = await NewAsync(client);
        var prompt = Prompt(client, session, "work");
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var steering = Steer(client, session, "accepted but late", promptId: "race-p1");
        await Task.Delay(100); // the steer is in flight in the conductor
        promptRelease.TrySetResult(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
        await Task.Delay(200); // the native turn end is read before the steer's answer
        steerRelease.TrySetResult(AcpSteering.Result(AcpSteering.Injected));
        Assert.AreEqual(AcpSteering.Injected, Outcome(await steering));
        Assert.AreEqual(StopReason.EndTurn, (await prompt).StopReason);
        var journal = conductor.Feed(session).Journal.ReadAll().OfType<JsonObject>().ToList();
        var steeredAt = journal.FindIndex(u => (bool?)u["_meta"]?[GorynychProtocol.MetaSteered] == true);
        var endAt = journal.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended");
        Assert.IsTrue(steeredAt >= 0 && endAt > steeredAt, $"the steer acceptance is journaled before the end ({steeredAt} < {endAt})");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CancelledLegacyFallback_DoesNotStartAfterThePreviousTurnEnds()
    {
        var dir = TestDirs.New();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompts = 0;
        var adapter = new ScriptedAdapter("a", (_, request, _) =>
        {
            if (request.Method == AcpMethods.Agent.Initialize) return Task.FromResult(Init());
            if (request.Method == AcpMethods.Agent.SessionNew) return Task.FromResult(NewSession("fallback"));
            if (request.Method == GorynychProtocol.MethodSteering) return Task.FromResult<JsonNode?>(AcpSteering.PromptRequiredResult());
            if (request.Method == AcpMethods.Agent.SessionPrompt)
            {
                Interlocked.Increment(ref prompts);
                started.TrySetResult();
                return release.Task;
            }
            return Task.FromResult<JsonNode?>(new JsonObject());
        });
        await using var conductor = TestDirs.Conductor(dir, adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var sid = await NewAsync(client);
        var first = Prompt(client, sid, "existing work");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancel = new CancellationTokenSource();
        var fallback = conductor.SteeringAsync(conductor.Feed(sid).Clients().Single(), new JsonRpcRequest(JsonValue.Create(999)!, GorynychProtocol.MethodSteering,
            new JsonObject { ["sessionId"] = sid, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "withdraw me" }) }), cancel.Token);
        await LinkHarness.Until(() => conductor.Feed(sid).PromptsInProgress().Count() == 2, "the fallback waiting for the first turn");
        cancel.Cancel();
        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => fallback);
        Assert.AreEqual(JsonRpcException.RequestCancelled, error.Code);
        release.TrySetResult(AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn }));
        await first;
        await Task.Delay(150);
        Assert.AreEqual(1, prompts, "a withdrawn fallback must never reach the agent");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OwnTurnEnd_WaitsForTheSteeringAcceptance(bool hosted)
    {
        var dir = TestDirs.New();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonNode?> WaitForAcceptance() { entered.TrySetResult(); return accepted.Task; }
        var adapter = new ScriptedAdapter("a", (_, request, _) => request.Method switch
        {
            AcpMethods.Agent.Initialize => Task.FromResult(Init()),
            AcpMethods.Agent.SessionNew => Task.FromResult(NewSession("own")),
            GorynychProtocol.MethodSteering => WaitForAcceptance(),
            _ => Task.FromResult<JsonNode?>(new JsonObject()),
        });
        var hosts = new InProcessHosts(_ => adapter);
        await using var conductor = hosted ? hosts.Conductor(dir, adapter) : TestDirs.Conductor(dir, adapter);
        hosts.Service = conductor;
        await using var client = await TestClient.ConnectAsync(conductor);
        var sid = await NewAsync(client);
        await adapter.Current!.Update(sid, new SessionInfoUpdate
        {
            Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "started" },
        });
        var steer = Steer(client, sid, "own-turn input");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.Current.Update(sid, new SessionInfoUpdate
        {
            Meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "ended" },
        });
        await Task.Delay(150);
        accepted.SetResult(AcpSteering.Result(AcpSteering.Injected));
        await steer;
        await LinkHarness.Until(() => conductor.Feed(sid).Journal.ReadAll().OfType<JsonObject>()
            .Any(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"), "the own turn end");
        var journal = conductor.Feed(sid).Journal.ReadAll().OfType<JsonObject>().ToList();
        Assert.IsTrue(journal.FindIndex(u => (bool?)u["_meta"]?[GorynychProtocol.MetaSteered] == true)
            < journal.FindIndex(u => (string?)u["_meta"]?[GorynychProtocol.MetaAgentTurn] == "ended"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DefiniteRefusals_AndUncertainFailures_AreNotJournaledOrRetried()
    {
        var dir = TestDirs.New();
        var steeringCalls = 0;
        var fail = true;
        await using var conductor = TestDirs.Conductor(dir, new ScriptedAdapter("a", (_, request, _) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize: return Task.FromResult(Init());
                case AcpMethods.Agent.SessionNew: return Task.FromResult(NewSession("s"));
                case GorynychProtocol.MethodSteering:
                    Interlocked.Increment(ref steeringCalls);
                    if (fail) throw new JsonRpcException(JsonRpcException.InternalError, "the native side went away");
                    throw AcpSteering.Refused("turnNotSteerable", "this turn cannot take a steer");
                default: return Task.FromResult<JsonNode?>(new JsonObject());
            }
        }));
        await using var client = await TestClient.ConnectAsync(conductor);
        var session = await NewAsync(client);

        var uncertain = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Steer(client, session, "maybe"));
        StringAssert.Contains(uncertain.Message, "unknown", uncertain.Message);
        Assert.AreEqual(1, steeringCalls);
        Assert.AreEqual(0, conductor.Feed(session).Journal.ReadAll().Count);

        fail = false;
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => Steer(client, session, "no"));
        Assert.AreEqual(JsonRpcException.InvalidRequest, refused.Code);
        Assert.AreEqual("turnNotSteerable", (string?)refused.ErrorData?[GorynychProtocol.MetaSteerRefused]);
        Assert.AreEqual(2, steeringCalls);
        Assert.AreEqual(0, conductor.Feed(session).Journal.ReadAll().Count);
    }
}
