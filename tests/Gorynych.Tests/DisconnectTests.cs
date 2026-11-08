using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The idle timer: a session loaded in its agent, idle (no turn running, no operation registered)
/// and with no client attached is closed in its agent after <c>UnloadAfter</c>, counted from the
/// later of its last client leaving and its going idle. A client that goes away without
/// <c>session/close</c> (Zed when it quits) stops nothing; a waking question or report brings a
/// saved session back (docs/service-protocol.md, "Unloading idle sessions"). The clock is moved
/// by hand.
/// </summary>
[TestClass]
public sealed class DisconnectTests
{
    static readonly string Cwd = Path.GetTempPath();
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    const string Resume = AcpMethods.Agent.SessionResume;
    const string Prompt = AcpMethods.Agent.SessionPrompt;
    const string Close = AcpMethods.Agent.SessionClose;
    const string Cancel = AcpMethods.Agent.SessionCancel;

    /// <param name="time">The clock of a <see cref="Idle"/> wait; without one, sessions unload at once (<c>--unload-after 0</c>).</param>
    static Conductor Conductor(ManualTime? time, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        UnloadAfter = time is null ? TimeSpan.Zero : Idle,
        Time = (TimeProvider?)time ?? TimeProvider.System,
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await PromptAsync(client, id, text);
        return id;
    }

    /// <summary>A session with a turn that its client closed and that was unloaded since: saved.</summary>
    static async Task<string> SavedSessionAsync(Conductor conductor, ManualTime time, string agent, string text)
    {
        string id;
        await using (var owner = await TestClient.ConnectAsync(conductor, "owner"))
        {
            id = await NewPromptedAsync(owner, agent, text);
            await owner.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = id });
        }
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        time.Advance(Idle);
        await Until(() => Status(conductor, id) == "saved", "saved");
        return id;
    }

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(Prompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task LoadAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            new JsonObject { ["sessionId"] = sessionId, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

    static Task<JsonNode?> AskAsync(TestClient client, string token, string target) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion,
            new JsonObject { ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token } });

    /// <summary>Disconnects a client without closing anything, as Zed quits, and waits for what that starts.</summary>
    static async Task QuitAsync(Conductor conductor, TestClient client)
    {
        await client.DisposeAsync();
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
    }

    /// <summary>The session's status in the list agents see, inside Gorynych and outside.</summary>
    static string Status(Conductor conductor, string sessionId) =>
        (string)conductor.PeerList(null, includeArchived: true)["sessions"]!.AsArray()
            .Single(s => (string?)s!["session_id"] == sessionId)!["status"]!;

    /// <summary>Moves the clock and lets what it fires run; for a check that nothing happened.</summary>
    static async Task AdvanceAsync(Conductor conductor, ManualTime time, TimeSpan by)
    {
        time.Advance(by);
        await Task.Delay(100);
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
    }

    static string[] Methods(LifecycleAgent agent)
    {
        lock (agent.Adapter.Methods) return [.. agent.Adapter.Methods];
    }

    static RequestPermissionRequest Approval(string sessionId) => new()
    {
        SessionId = sessionId,
        ToolCall = new ToolCallUpdate { ToolCallId = "t1", Title = "Remove the build folder", Kind = ToolKind.Delete },
        Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
    };

    /// <summary>An approval a client never answers, as one the person has not answered yet when the client quits.</summary>
    static Task<RequestPermissionOutcome> Unanswered(RequestPermissionRequest _) => new TaskCompletionSource<RequestPermissionOutcome>().Task;

    static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    // ---------------------------------------------------------------- a client goes away

    [TestMethod]
    public async Task Quit_UnloadsTheSessionsAfterTheIdleTime_AndTheListsShowThemSaved()
    {
        var time = new ManualTime();
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, claude.Adapter, kimi.Adapter);
        var kimiSession = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var claudeSession = await SavedSessionAsync(conductor, time, "claude", "Write the notes");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, kimiSession);
        await LoadAsync(zed, claudeSession);

        await QuitAsync(conductor, zed);
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual((Resume, Resume), (kimi.CallsFor(kimiSession).Last(), claude.CallsFor(claudeSession).Last()), "still loaded");
        Assert.AreEqual(("open", "open"), (Status(conductor, kimiSession), Status(conductor, claudeSession)));

        time.Advance(Second);
        await Until(() => kimi.CallsFor(kimiSession).Last() == Close && claude.CallsFor(claudeSession).Last() == Close, "both unloaded");
        await Until(() => (Status(conductor, kimiSession), Status(conductor, claudeSession)) == ("saved", "saved"), "both saved");
    }

    [TestMethod]
    public async Task QuitDuringATurn_DoesNotStopIt_AndTheIdleTimeCountsFromItsEnd()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        kimi.BeforePrompt = sid => sid == id ? hold.Task : Task.CompletedTask;
        kimi.Answer = "All green.";
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        var turn = PromptAsync(zed, id, "Run the tests");
        await Until(() => kimi.CallsFor(id).Last() == Prompt, "the turn");

        await QuitAsync(conductor, zed);
        _ = turn.ContinueWith(t => t.Exception, TaskScheduler.Default); // its client is gone before the answer
        await AdvanceAsync(conductor, time, Idle * 3);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "not unloaded under the turn");
        CollectionAssert.DoesNotContain(Methods(kimi), Cancel, "nothing is cancelled");

        var timers = time.Waiting;
        hold.SetResult();
        await Until(() => time.Waiting > timers, "the idle timer, once the turn ended");
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "the idle time counts from the turn's end");

        time.Advance(Second);
        await Until(() => kimi.CallsFor(id).Last() == Close, "the unload");
        await using var observer = await TestClient.ConnectAsync(conductor, "observer");
        var updates = (await observer.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = id }))!["updates"]!.AsArray();
        Assert.AreEqual("All green.", (string?)updates.Last()!["content"]!["text"], "the turn ran to its end");
    }

    [TestMethod]
    public async Task QuitWithAQuestionQueuedBehindTheTurn_RunsBothFirst()
    {
        var time = new ManualTime();
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, claude.Adapter, kimi.Adapter);
        var target = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        await using var peer = await TestClient.ConnectAsync(conductor, "peer");
        var asker = await NewPromptedAsync(peer, "claude", "Coordinate the release");
        var held = 0;
        kimi.BeforePrompt = sid => sid == target && Interlocked.Increment(ref held) == 1 ? hold.Task : Task.CompletedTask;
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, target);
        var turn = PromptAsync(zed, target, "Run the tests");
        await Until(() => kimi.CallsFor(target).Last() == Prompt, "the turn");
        var asking = AskAsync(peer, claude.Tokens[asker], target);
        await Until(() => kimi.CallsFor(target).Last() == GorynychProtocol.MethodOpinion, "the question, waiting for the turn");

        await QuitAsync(conductor, zed);
        _ = turn.ContinueWith(t => t.Exception, TaskScheduler.Default);
        await AdvanceAsync(conductor, time, Idle * 2);
        hold.SetResult();

        Assert.AreEqual("ok", (string?)(await asking.WaitAsync(Wait))!["text"], "the queued question was asked and answered");
        CollectionAssert.AreEqual(new[] { Resume, Prompt, GorynychProtocol.MethodOpinion, Prompt }, kimi.CallsFor(target)[^4..]);
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(target).Last() == Close, "the unload after both");
    }

    [TestMethod]
    public async Task AClientThatComesBackWithinTheIdleTime_KeepsTheSessionLoaded()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        await QuitAsync(conductor, zed);
        await AdvanceAsync(conductor, time, Idle / 2);

        var back = await TestClient.ConnectAsync(conductor, "zed again");
        await LoadAsync(back, id);
        await AdvanceAsync(conductor, time, Idle * 2);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "loaded once, and kept");

        await QuitAsync(conductor, back);
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(id).Last() == Close, "the unload once that client went too");
    }

    [TestMethod]
    public async Task TwoClients_OneQuits_TheSessionStaysLoaded_UntilTheOtherGoesToo()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var other = await TestClient.ConnectAsync(conductor, "zed-2");
        await LoadAsync(zed, id);
        await LoadAsync(other, id);

        await QuitAsync(conductor, zed);
        await AdvanceAsync(conductor, time, Idle * 2);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "the other client still has it open");
        Assert.AreEqual("open", Status(conductor, id));

        await QuitAsync(conductor, other);
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(id).Last() == Close, "the unload");
    }

    [TestMethod]
    public async Task UnloadAfterZero_UnloadsAtOnce()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var quit = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var closed = await NewPromptedAsync(zed, "kimi", "Write the notes");

        await zed.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = closed });
        Assert.AreEqual(Close, kimi.CallsFor(closed).Last(), "unloaded before the close returns");
        await QuitAsync(conductor, zed);
        Assert.AreEqual(Close, kimi.CallsFor(quit).Last());
        Assert.AreEqual("saved", Status(conductor, quit));
    }

    // ---------------------------------------------------------------- approvals keep a session

    [TestMethod]
    public async Task AnApprovalWaitingForAClient_IsNeverUnloaded_AndAClientThatComesBackAnswersIt()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var answered = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        kimi.BeforePrompt = async sid =>
        {
            if (sid != id) return;
            var answer = await kimi.Adapter.Current!.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(Approval(id)));
            answered.TrySetResult((string?)answer?["outcome"]?["optionId"]);
        };
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Unanswered;
        await LoadAsync(zed, id);
        var turn = PromptAsync(zed, id, "Clean the build");
        await Until(() => zed.Approvals.Count == 1, "the approval in Zed");

        await QuitAsync(conductor, zed);
        _ = turn.ContinueWith(t => t.Exception, TaskScheduler.Default);
        await AdvanceAsync(conductor, time, Idle * 6);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "still loaded: a client may come back to answer");
        Assert.AreEqual("open", Status(conductor, id));

        var back = await TestClient.ConnectAsync(conductor, "zed again");
        await LoadAsync(back, id);
        Assert.AreEqual("allow", await answered.Task.WaitAsync(Wait));
        await AdvanceAsync(conductor, time, Idle * 2);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "the client that answered has it open");

        await QuitAsync(conductor, back);
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(id).Last() == Close, "the unload");
    }

    [TestMethod]
    public async Task AnAgentsRequestOutsideATurn_KeepsTheSessionLoaded_UntilTheAgentWithdrawsIt()
    {
        // Claude asks for approvals in turns its channel starts, which no session/prompt of the service holds.
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Unanswered;
        await LoadAsync(zed, id);
        using var withdraw = new CancellationTokenSource();
        var asking = kimi.Adapter.Current!.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(Approval(id)), withdraw.Token);
        _ = asking.ContinueWith(t => t.Exception, TaskScheduler.Default);
        await Until(() => zed.Approvals.Count == 1, "the approval in Zed");

        await QuitAsync(conductor, zed);
        await AdvanceAsync(conductor, time, Idle * 6);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "still loaded while the approval waits");

        var timers = time.Waiting;
        withdraw.Cancel();
        await Until(() => time.Waiting > timers, "the idle timer, once the agent withdrew its request");
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(id).Last() == Close, "the unload");
    }

    // ---------------------------------------------------------------- sessions the service wakes

    [TestMethod]
    public async Task AnAsk_KeepsAWokenSessionLoadedForTheIdleTime_SoASecondAskNeedsNoStart()
    {
        var time = new ManualTime();
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, claude.Adapter, kimi.Adapter);
        var target = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        await using var peer = await TestClient.ConnectAsync(conductor, "peer");
        var asker = await NewPromptedAsync(peer, "claude", "Coordinate the release");
        var resumes = kimi.CallsFor(target).Count(c => c == Resume);

        Assert.AreEqual("ok", (string?)(await AskAsync(peer, claude.Tokens[asker], target))!["text"]);
        Assert.AreEqual(resumes + 1, kimi.CallsFor(target).Count(c => c == Resume), "woken");
        await AdvanceAsync(conductor, time, Idle / 2);
        Assert.AreEqual("open", Status(conductor, target), "loaded after its turn");

        Assert.AreEqual("ok", (string?)(await AskAsync(peer, claude.Tokens[asker], target))!["text"]);
        Assert.AreEqual(resumes + 1, kimi.CallsFor(target).Count(c => c == Resume), "the second ask needs no start");
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Prompt, kimi.CallsFor(target).Last(), "the idle time counts from the second turn's end");

        time.Advance(Second);
        await Until(() => kimi.CallsFor(target).Last() == Close, "the unload");
        await Until(() => Status(conductor, target) == "saved", "saved");
    }

    // ---------------------------------------------------------------- close, delete, drafts

    [TestMethod]
    public async Task CloseByTheLastClient_CancelsARunningTurn_AndUnloadsAfterTheIdleTime()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, kimi.Adapter);
        var idle = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var busy = await SavedSessionAsync(conductor, time, "kimi", "Write the notes");
        kimi.BeforePrompt = sid => sid == busy ? hold.Task : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, idle);
        await LoadAsync(zed, busy);
        var turn = PromptAsync(zed, busy, "Run the tests");
        await Until(() => kimi.CallsFor(busy).Last() == Prompt, "the turn");

        await zed.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = idle });
        await zed.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = busy });
        await Until(() => Methods(kimi).Count(m => m == Cancel) == 1, "the running turn cancelled, as ACP asks of a close");
        Assert.AreEqual((Resume, Prompt), (kimi.CallsFor(idle).Last(), kimi.CallsFor(busy).Last()), "neither unloaded yet");
        hold.SetResult(); // the fake ends its turn whatever the cancel says
        await turn.WaitAsync(Wait);
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual((Resume, Prompt), (kimi.CallsFor(idle).Last(), kimi.CallsFor(busy).Last()), "not unloaded within the idle time");

        time.Advance(Second);
        await Until(() => kimi.CallsFor(idle).Last() == Close && kimi.CallsFor(busy).Last() == Close, "both unloaded");
    }

    [TestMethod]
    public async Task Delete_ClosesAndDeletesAtOnce()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var id = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");

        await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id });

        CollectionAssert.AreEqual(new[] { Close, AcpMethods.Agent.SessionDelete }, kimi.CallsFor(id)[^2..]);
    }

    [TestMethod]
    public async Task ADraft_IsStillDiscardedAtOnce_WhenItsClientQuits()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;
        var used = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");

        await QuitAsync(conductor, zed);

        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, Close, AcpMethods.Agent.SessionDelete }, kimi.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft));
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, Prompt }, kimi.CallsFor(used), "a session with a turn waits for its idle time");
        time.Advance(Idle);
        await Until(() => kimi.CallsFor(used).Last() == Close, "its unload");
    }
}
