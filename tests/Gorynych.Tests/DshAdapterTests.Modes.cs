using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;

namespace Gorynych.Tests;

/// <summary>Launches a fake DSH per process the adapter starts, all on one store, as DSH's processes share <c>~/.dsh</c>.</summary>
sealed class FakeDshHost
{
    public FakeDshStore Store { get; } = new();
    public List<FakeDsh> Launched { get; } = [];

    /// <summary>Resumes fail in the full-access processes launched from now on.</summary>
    public bool FullAccessResumeFails { get; set; }

    /// <summary>The full-access processes launched from now on never answer `initialize`.</summary>
    public bool FullAccessHangs { get; set; }

    /// <summary>How many full-access processes start; launching more fails, as a `dsh` that cannot start.</summary>
    public int FullAccessStarts { get; set; } = int.MaxValue;

    public async Task<AgentInstance> LaunchAsync(string patch, CancellationToken ct)
    {
        var full = patch.Contains("full-access", StringComparison.Ordinal);
        if (full && FullAccessLaunches >= FullAccessStarts) throw new InvalidOperationException("dsh --profile acp did not start");
        var dsh = new FakeDsh(Store) { ResumeFails = FullAccessResumeFails && full, HangInitialize = FullAccessHangs && full };
        var instance = await dsh.LaunchAsync(patch, ct);
        lock (Launched) Launched.Add(dsh);
        return instance;
    }

    public FakeDsh ReadOnly
    {
        get { lock (Launched) return Launched.First(d => !d.FullAccess); }
    }

    public FakeDsh FullAccess
    {
        get { lock (Launched) return Launched.Last(d => d.FullAccess); }
    }

    public int FullAccessLaunches
    {
        get { lock (Launched) return Launched.Count(d => d.FullAccess); }
    }
}

/// <summary>The DSH adapter's permission modes: one DSH process per sandbox, behind a router (docs/adapters.md, "DeepSeek Harness").</summary>
public sealed partial class DshAdapterTests
{
    static async Task<(Service Service, FakeDshHost Host, string Data)> StartModesAsync(string? data = null, string? dshHome = null, FakeDshHost? host = null,
        List<string>? log = null, TimeSpan? startTimeout = null)
    {
        host ??= new FakeDshHost();
        data ??= TestDirs.New();
        var adapter = new DshAdapter(new DshAdapterOptions
        {
            DataDirectory = data,
            Instructions = "Peer instructions.",
            Launch = host.LaunchAsync,
            DshHome = dshHome ?? TestDirs.New(),
            StartTimeout = startTimeout ?? TimeSpan.FromSeconds(30),
            Log = log is null ? null : m => { lock (log) log.Add(m); },
        });
        var instance = await adapter.StartAsync(CancellationToken.None);
        var rpc = new JsonRpcConnection(instance.Input, instance.Output, "service");
        var service = new Service { Instance = instance, Rpc = rpc };
        rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u) lock (service.Updates) service.Updates.Add(u);
            return Task.CompletedTask;
        };
        rpc.RequestHandler = async (req, ct) =>
        {
            lock (service.Questions) service.Questions.Add(req.Params!.DeepClone().AsObject());
            await using var _ = ct.Register(() => Interlocked.Increment(ref service.Withdrawn));
            return await service.Answer(req.Params!.AsObject(), ct);
        };
        rpc.Start();
        await rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientInfo"] = new JsonObject { ["name"] = "service" } });
        return (service, host, data);
    }

    static Task<JsonNode?> SetModeAsync(Service service, string sessionId, string mode) =>
        service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject
        {
            ["sessionId"] = sessionId, ["configId"] = "mode", ["value"] = mode,
        }).WaitAsync(Bound);

    static Task<JsonNode?> ResumeAsync(Service service, string sessionId, string cwd) =>
        service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
        {
            ["sessionId"] = sessionId, ["cwd"] = cwd, ["mcpServers"] = new JsonArray(),
        }).WaitAsync(Bound);

    /// <summary>The mode option's current value, after checking it comes first, as a select of category mode.</summary>
    static string ModeIn(JsonNode? holder)
    {
        var option = holder!["configOptions"]![0]!;
        Assert.AreEqual("mode", (string?)option["id"]);
        Assert.AreEqual("mode", (string?)option["category"]);
        Assert.AreEqual("select", (string?)option["type"]);
        Assert.IsTrue(holder["configOptions"]!.AsArray().Any(o => (string?)o!["id"] == "model"), "DSH's own options stay");
        return (string)option["currentValue"]!;
    }

    static int CallsTo(FakeDsh dsh, string method, string? sessionId = null)
    {
        lock (dsh.Calls) return dsh.Calls.Count(c => c.Method == method && (sessionId is null || (string?)c.Params?["sessionId"] == sessionId));
    }

    [TestMethod]
    public async Task ANewSession_IsReadOnly_AndOffersFullAccess_AsCodexsModesDo()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;

        var created = await service.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Work, ["mcpServers"] = new JsonArray() });

        Assert.AreEqual("read-only", ModeIn(created));
        var values = created!["configOptions"]![0]!["options"]!.AsArray().Select(o => ((string)o!["value"]!, (string)o["name"]!)).ToArray();
        CollectionAssert.AreEqual(new[] { ("read-only", "Read only"), ("full-access", "Full access") }, values);
        var marked = created["configOptions"]![0]!["options"]!.AsArray()
            .Where(o => o!["_meta"]?[GorynychProtocol.MetaFullAccess] is JsonValue v && v.GetValue<bool>()).Select(o => (string?)o!["value"]).ToArray();
        CollectionAssert.AreEqual(new[] { "full-access" }, marked, "the service asks before a session outside full access asks this one");
        Assert.AreEqual(0, host.FullAccessLaunches, "the full-access process starts only when needed");
    }

    [TestMethod]
    public async Task Switching_MovesTheSessionToTheOtherProcess_AndBack_StoppingTheIdleOne()
    {
        var (service, host, data) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);

        var switched = await SetModeAsync(service, id, "full-access");

        Assert.AreEqual("full-access", ModeIn(switched));
        var full = host.FullAccess;
        var patch = File.ReadAllText(full.PatchPath!);
        StringAssert.Contains(patch, "mode: danger-full-access");
        StringAssert.Contains(patch, "policy: ask");
        StringAssert.Contains(patch, "- id: permission\n  disabled: true");
        StringAssert.Contains(patch, DshPatch.FullAccessShell);
        Assert.IsFalse(patch.Contains(DshPatch.ReadOnlyShell, StringComparison.Ordinal));
        Assert.AreEqual("service", (string?)full.Calls.First(c => c.Method == AcpMethods.Agent.Initialize).Params?["clientInfo"]?["name"],
            "the second process is initialized as the service initialized the first");
        Assert.AreEqual(1, CallsTo(host.ReadOnly, AcpMethods.Agent.SessionClose, id));
        Assert.IsFalse(host.ReadOnly.IsOpen(id));
        Assert.IsTrue(full.IsOpen(id));
        Assert.AreEqual(Work, (string?)full.Calls.Single(c => c.Method == AcpMethods.Agent.SessionResume).Params?["cwd"]);
        StringAssert.Contains(File.ReadAllText(Path.Combine(data, DshAdapter.ModesFile)), id);

        var ran = new List<string>();
        full.OnPrompt = s => { lock (ran) ran.Add("full:" + s); return Task.CompletedTask; };
        host.ReadOnly.OnPrompt = s => { lock (ran) ran.Add("read-only:" + s); return Task.CompletedTask; };
        await PromptAsync(service, id);
        CollectionAssert.AreEqual(new[] { "full:" + id }, ran);

        var back = await SetModeAsync(service, id, "read-only");

        Assert.AreEqual("read-only", ModeIn(back));
        Assert.IsTrue(host.ReadOnly.IsOpen(id));
        await full.Rpc.Completion.WaitAsync(Bound); // the full-access process held nothing more, so it was stopped
        Assert.IsFalse(File.ReadAllText(Path.Combine(data, DshAdapter.ModesFile)).Contains(id, StringComparison.Ordinal));
        await PromptAsync(service, id);
        CollectionAssert.AreEqual(new[] { "full:" + id, "read-only:" + id }, ran);
    }

    [TestMethod]
    public async Task SwitchingToTheModeASessionHas_ChangesNothing()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);

        Assert.AreEqual("read-only", ModeIn(await SetModeAsync(service, id, "read-only")));

        Assert.AreEqual(0, host.FullAccessLaunches);
        Assert.AreEqual(0, CallsTo(host.ReadOnly, AcpMethods.Agent.SessionClose));
        var unknown = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "auto"));
        Assert.AreEqual(JsonRpcException.InvalidParams, unknown.Code);
    }

    [TestMethod]
    public async Task AFullAccessSession_IsResumedInTheFullAccessProcess_AfterARestart()
    {
        var data = TestDirs.New();
        var host = new FakeDshHost();
        string id;
        var (first, _, _) = await StartModesAsync(data, host: host);
        await using (first)
        {
            id = await NewAsync(first, Work);
            await SetModeAsync(first, id, "full-access");
            var other = await NewAsync(first, Work);
            await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
            await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = other });
        }

        var again = new FakeDshHost();
        foreach (var (s, cwd) in host.Store.Cwds) again.Store.Cwds[s] = cwd;
        var (service, _, _) = await StartModesAsync(data, host: again);
        await using var _ = service;

        Assert.AreEqual("full-access", ModeIn(await ResumeAsync(service, id, Work)));
        Assert.IsTrue(again.FullAccess.IsOpen(id));
        Assert.AreEqual(0, CallsTo(again.ReadOnly, AcpMethods.Agent.SessionResume), "the read-only process never saw it");
    }

    [TestMethod]
    public async Task TheList_HasEverySessionOnce_WhicheverProcessHasItOpen()
    {
        var (service, _, _) = await StartModesAsync();
        await using var _ = service;
        var a = await NewAsync(service, Work);
        var b = await NewAsync(service, Other);
        await SetModeAsync(service, b, "full-access");

        CollectionAssert.AreEquivalent(new[] { a, b }, await ListAsync(service));
        CollectionAssert.AreEqual(new[] { b }, await ListAsync(service, new JsonObject { ["cwd"] = Other }));
    }

    [TestMethod]
    public async Task TheList_AddsTheOtherProcesssOpenSessions_WhenDshLeavesThemOut()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var a = await NewAsync(service, Work);
        await SetModeAsync(service, a, "full-access");
        host.Store.Cwds.Remove(a); // as if the read-only process had not seen it yet

        CollectionAssert.AreEqual(new[] { a }, await ListAsync(service));
    }

    [TestMethod]
    public async Task ARunningTurn_HoldsTheSwitchBack()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        var release = new TaskCompletionSource();
        host.ReadOnly.OnPrompt = _ => release.Task;
        var prompt = PromptAsync(service, id);
        await Until(() => CallsTo(host.ReadOnly, AcpMethods.Agent.SessionPrompt) == 1, "the prompt");

        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "full-access"));

        StringAssert.Contains(refused.Message, "turn");
        release.SetResult();
        await prompt;
        Assert.AreEqual("full-access", ModeIn(await SetModeAsync(service, id, "full-access")));
    }

    [TestMethod]
    public async Task ASwitchTheOtherProcessRefuses_LeavesTheSessionWhereItWas()
    {
        var host = new FakeDshHost { FullAccessResumeFails = true };
        var (service, _, data) = await StartModesAsync(host: host);
        await using var _ = service;
        var id = await NewAsync(service, Work);

        var failed = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, id, "full-access"));

        StringAssert.Contains(failed.Message, "full access mode");
        Assert.IsTrue(host.ReadOnly.IsOpen(id), "resumed back in the read-only process");
        Assert.IsFalse(File.Exists(Path.Combine(data, DshAdapter.ModesFile)) && File.ReadAllText(Path.Combine(data, DshAdapter.ModesFile)).Contains(id, StringComparison.Ordinal));
        await host.FullAccess.Rpc.Completion.WaitAsync(Bound);
        var ran = false;
        host.ReadOnly.OnPrompt = _ => { ran = true; return Task.CompletedTask; };
        await PromptAsync(service, id);
        Assert.IsTrue(ran);
    }

    [TestMethod]
    [DataRow("workspace-write", "ask", "dsh", false)]
    [DataRow("workspace-write", "never", "dsh", false)]
    [DataRow("read-only", "ask", "read-only", false)]
    [DataRow("read-only", "never", "dsh", false)]
    [DataRow("danger-full-access", "never", "full-access", true)]
    [DataRow("danger-full-access", "ask", "full-access", true)]
    public async Task ASessionDshPinned_ShowsItsPin_GoesToItsProcess_AndCannotSwitch(string sandbox, string approval, string shown, bool inFullAccess)
    {
        var home = TestDirs.New();
        var host = new FakeDshHost();
        host.Store.Cwds["pinned"] = Work;
        var cache = Path.Combine(home, "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "pinned.json"), """
            {"version":7,"record":{"identity":{},"rows":{
              "sandboxMode":{"ver":1,"seq":5,"val":"SANDBOX"},
              "approvalState":{"ver":1,"seq":5,"val":{"policy":"APPROVAL","turnOpen":false}}}}}
            """.Replace("SANDBOX", sandbox).Replace("APPROVAL", approval));
        var (service, _, _) = await StartModesAsync(dshHome: home, host: host);
        await using var _ = service;

        var resumed = await ResumeAsync(service, "pinned", Work);

        Assert.AreEqual(shown, ModeIn(resumed));
        Assert.AreEqual(inFullAccess, host.FullAccessLaunches == 1 && host.FullAccess.IsOpen("pinned"));
        // Marked only for no sandbox, which is what Gorynych's own full-access runs (with approval ask, nothing
        // escalates): workspace-write that never asks is still confined.
        Assert.AreEqual(inFullAccess, Gorynych.Service.Conductor.IsFullAccess(resumed!.AsObject()));
        if (shown == "dsh")
            StringAssert.Contains((string?)resumed!["configOptions"]![0]!["options"]!.AsArray().Last()!["description"], sandbox);
        var other = shown == "full-access" ? "read-only" : "full-access";
        var refused = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SetModeAsync(service, "pinned", other));
        StringAssert.Contains(refused.Message, "/permission");
    }

    [TestMethod]
    public async Task QuestionsOfBothProcesses_ReachTheService_AndTheirAnswersGoBack()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var a = await NewAsync(service, Work);
        var b = await NewAsync(service, Work);
        await SetModeAsync(service, b, "full-access");
        service.Answer = (q, _) => Task.FromResult<JsonNode?>(JsonNode.Parse($$$"""
            {"outcome":{"outcome":"selected","optionId":"{{{((string)q["sessionId"]! == a ? "allow-once" : "reject-once")}}}"}}
            """));
        JsonNode? answerA = null, answerB = null;
        const string write = """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"write","kind":"other","status":"in_progress","rawInput":{"file_path":"a.txt","content":"x","sandbox_permissions":"workspace-write"}}""";
        host.ReadOnly.OnPrompt = async s => { await host.ReadOnly.Update(s, write); answerA = await (await host.ReadOnly.AskAsync(s, "c1")).WaitAsync(Bound); };
        host.FullAccess.OnPrompt = async s => { await host.FullAccess.Update(s, write); answerB = await (await host.FullAccess.AskAsync(s, "c1")).WaitAsync(Bound); };

        await Task.WhenAll(PromptAsync(service, a), PromptAsync(service, b));

        Assert.AreEqual(2, service.Questions.Count);
        Assert.AreEqual("allow-once", (string?)answerA?["outcome"]?["optionId"]);
        Assert.AreEqual("reject-once", (string?)answerB?["outcome"]?["optionId"]);
    }

    [TestMethod]
    public async Task TheGuard_RefusesShellEscalations_InTheFullAccessProcessToo()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        await SetModeAsync(service, id, "full-access");
        JsonNode? answer = null;
        host.FullAccess.OnPrompt = async s =>
        {
            await host.FullAccess.Update(s, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"pwsh","kind":"other","status":"in_progress","rawInput":{"command":"rm -r x","sandbox_permissions":"workspace-write"}}""");
            answer = await (await host.FullAccess.AskAsync(s, "c1")).WaitAsync(Bound);
        };

        await PromptAsync(service, id);

        Assert.AreEqual("reject-once", (string?)answer?["outcome"]?["optionId"]);
        Assert.AreEqual(0, service.Questions.Count);
    }

    [TestMethod]
    public async Task TheFullAccessProcessDying_EndsItsSessions_ButNotTheAdapter()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var a = await NewAsync(service, Work);
        var b = await NewAsync(service, Work);
        await SetModeAsync(service, b, "full-access");
        var dying = host.FullAccess;
        dying.OnPrompt = async s =>
        {
            var dies = Task.Run(async () => await dying.Rpc.DisposeAsync());
            await Task.Delay(Timeout.Infinite);
        };

        await Assert.ThrowsAsync<JsonRpcException>(() => PromptAsync(service, b));

        Assert.IsFalse(service.Instance.Exited.IsCompleted, "the adapter goes on");
        var lost = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => PromptAsync(service, b));
        StringAssert.Contains(lost.Message, "full access DeepSeek Harness process stopped");
        var ran = false;
        host.ReadOnly.OnPrompt = _ => { ran = true; return Task.CompletedTask; };
        await PromptAsync(service, a);
        Assert.IsTrue(ran);
        Assert.AreEqual("full-access", ModeIn(await ResumeAsync(service, b, Work)), "a resume starts the process again");
        Assert.AreEqual(2, host.FullAccessLaunches);
    }

    [TestMethod]
    public async Task ConfigOptionUpdates_KeepTheModeOption()
    {
        var (service, host, _) = await StartModesAsync();
        await using var _ = service;
        var id = await NewAsync(service, Work);
        await SetModeAsync(service, id, "full-access");

        await host.FullAccess.Update(id, $$"""{"sessionUpdate":"config_option_update","configOptions":[{{FakeDsh.ModelOption}}]}""");

        await Until(() => { lock (service.Updates) return service.Updates.Any(u => (string?)u["sessionUpdate"] == "config_option_update"); }, "the update");
        JsonObject update;
        lock (service.Updates) update = service.Updates.Last(u => (string?)u["sessionUpdate"] == "config_option_update");
        Assert.AreEqual("full-access", ModeIn(update));
    }

    [TestMethod]
    public void ThePins_AreReadFromDshsCache_OnlyAsFarAsTheyAreKnown()
    {
        var home = TestDirs.New();
        var cache = Path.Combine(home, "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "a.json"), """{"version":7,"record":{"rows":{"sandboxMode":{"val":"read-only"},"approvalState":{"val":{"policy":"ask"}}}}}""");
        File.WriteAllText(Path.Combine(cache, "unpinned.json"), """{"version":7,"record":{"rows":{"sandboxMode":{"ver":1,"seq":-1,"val":null}}}}""");
        File.WriteAllText(Path.Combine(cache, "newer.json"), """{"version":8,"record":{"rows":{"sandboxMode":{"val":"read-only"}}}}""");
        File.WriteAllText(Path.Combine(cache, "broken.json"), "{");
        var pins = new DshPins(home);

        Assert.AreEqual(new DshPin("read-only", "ask"), pins.Read("a"));
        Assert.IsNull(pins.Read("unpinned"));
        Assert.IsNull(pins.Read("newer"), "another cache version is not guessed at");
        Assert.IsNull(pins.Read("broken"));
        Assert.IsNull(pins.Read("missing"));
        Assert.IsNull(pins.Read("../sessions/a"), "a session ID is never a path");
    }

    [TestMethod]
    public void TheModeStore_KeepsOnlyFullAccess_AndStartsReadOnlyFromABrokenFile()
    {
        var data = TestDirs.New();
        var path = Path.Combine(data, DshAdapter.ModesFile);
        var store = new DshModeStore(path, null);
        store.Set("a", DshModes.FullAccess);
        store.Set("b", DshModes.FullAccess);
        store.Set("b", DshModes.ReadOnly);

        var again = new DshModeStore(path, null);
        Assert.AreEqual(DshModes.FullAccess, again.Get("a"));
        Assert.AreEqual(DshModes.ReadOnly, again.Get("b"));
        Assert.IsFalse(File.ReadAllText(path).Contains("\"b\"", StringComparison.Ordinal));

        File.WriteAllText(path, "not json");
        var logged = new List<string>();
        Assert.AreEqual(DshModes.ReadOnly, new DshModeStore(path, logged.Add).Get("a"));
        Assert.AreEqual(1, logged.Count);
    }
}
