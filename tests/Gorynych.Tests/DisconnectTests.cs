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
/// <c>session/close</c> (Zed when it quits) stops nothing, and a client's close stops only a
/// client's turn; a waking question or report brings a saved session back
/// (docs/service-protocol.md, "Unloading idle sessions"). The clock is moved by hand, and the
/// timer runs what it fires before the move returns.
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
        // Activity is announced at once, so the clock's only timers are the idle ones these tests count.
        ActivityNoticeInterval = TimeSpan.Zero,
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
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual("saved", Status(conductor, id));
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

    static Task CloseSessionAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = sessionId });

    /// <summary>Disconnects a client without closing anything, as Zed quits, and waits for what that starts.</summary>
    static async Task QuitAsync(Conductor conductor, TestClient client)
    {
        await client.DisposeAsync();
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
    }

    /// <summary>The session's status in the list agents see, inside Gorynych and outside.</summary>
    static string Status(Conductor conductor, string sessionId) =>
        (string)conductor.PeerListAsync(null, includeArchived: true, CancellationToken.None).GetAwaiter().GetResult()["sessions"]!.AsArray()
            .Single(s => (string?)s!["session_id"] == sessionId)!["status"]!;

    /// <summary>Moves the clock and waits for the unloads it fired, which the timer starts before the move returns.</summary>
    static async Task AdvanceAsync(Conductor conductor, ManualTime time, TimeSpan by)
    {
        time.Advance(by);
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
    }

    static string[] Methods(LifecycleAgent agent)
    {
        lock (agent.Adapter.Methods) return [.. agent.Adapter.Methods];
    }

    static int PromptsOf(LifecycleAgent agent, string sessionId)
    {
        lock (agent.Prompts) return agent.Prompts.Count(p => p.SessionId == sessionId);
    }

    static RequestPermissionRequest Approval(string sessionId) => new()
    {
        SessionId = sessionId,
        ToolCall = new ToolCallUpdate { ToolCallId = "t1", Title = "Remove the build folder", Kind = ToolKind.Delete },
        Options = [new PermissionOption { OptionId = "allow", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
    };

    /// <summary>An approval a client never answers, as one the person has not answered yet when the client quits.</summary>
    static Task<RequestPermissionOutcome> Unanswered(RequestPermissionRequest _) => new TaskCompletionSource<RequestPermissionOutcome>().Task;

    /// <summary>An update the agent sends on its own, with the agent-turn mark when one is given.</summary>
    static Task AgentSaysAsync(LifecycleAgent agent, string sessionId, string text, string? mark = null) =>
        agent.Adapter.Current!.Update(sessionId, new SessionUpdateAgentMessageChunk
        {
            Content = new TextContent { Text = text },
            Meta = mark is null ? null : new JsonObject { [GorynychProtocol.MetaAgentTurn] = mark },
        });

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

        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual((Close, Close), (kimi.CallsFor(kimiSession).Last(), claude.CallsFor(claudeSession).Last()), "both unloaded");
        Assert.AreEqual(("saved", "saved"), (Status(conductor, kimiSession), Status(conductor, claudeSession)));
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

        hold.SetResult();
        await Until(() => time.Waiting == 1, "the idle timer, once the turn ended");
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "the idle time counts from the turn's end");

        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
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
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(target).Last(), "the unload after both");
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
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last(), "unloaded once that client went too");
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
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    [TestMethod]
    public async Task ADetachThatLeavesNoClient_StartsTheIdleTimer()
    {
        // As when a send to a client fails while its connection stays up: no close, no disconnect.
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        await QuitAsync(conductor, zed);
        var feed = conductor.Feed(id);
        var stuck = new ClientConnection(conductor, new JsonRpcConnection(Stream.Null, Stream.Null, "stuck"));
        feed.Attach(stuck);
        Assert.AreEqual(0, time.Waiting, "a client attached: no timer");

        feed.Detach(stuck);
        Assert.AreEqual(1, time.Waiting, "the detach that left no client started it");
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    [TestMethod]
    public async Task ReadingAClientlessSession_KeepsOneTimer()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        await QuitAsync(conductor, zed);
        await using var observer = await TestClient.ConnectAsync(conductor, "observer");

        for (var i = 0; i < 5; i++)
        {
            await AdvanceAsync(conductor, time, Idle / 2);
            await observer.Rpc.SendRequestAsync(GorynychProtocol.MethodRead, new JsonObject { ["sessionId"] = id });
            Assert.AreEqual(1, time.Waiting, "each read stops the timer and starts it again: one wait, not one per read");
        }
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "a read is an operation: the idle time counts from the last one");
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    [TestMethod]
    public async Task UnloadAfterZero_UnloadsAtOnce()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(null, kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var quit = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");
        var closed = await NewPromptedAsync(zed, "kimi", "Write the notes");

        await CloseSessionAsync(zed, closed);
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
        await Until(() => conductor.Feed(id).Users == 0, "the end of the turn the approval let go on");
        await AdvanceAsync(conductor, time, Idle * 2);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "the client that answered has it open");

        await QuitAsync(conductor, back);
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    [TestMethod]
    public async Task AnAgentsRequestOutsideATurn_KeepsTheSessionLoaded_UntilTheAgentWithdrawsIt()
    {
        // As Claude asks in a turn of its own, which no session/prompt of the service holds.
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

        withdraw.Cancel();
        await Until(() => time.Waiting == 1, "the idle timer, once the agent withdrew its request");
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    // ---------------------------------------------------------------- the agent's own turns

    [TestMethod]
    public async Task ATurnTheAgentRunsOnItsOwn_KeepsTheSessionLoaded_AndItsEndStartsTheTimer()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        await QuitAsync(conductor, zed);
        Assert.AreEqual(1, time.Waiting);

        await AgentSaysAsync(kimi, id, "A message reached me through my own channel.", mark: "started");
        await Until(() => time.Waiting == 0, "the turn stopping the timer");
        await AdvanceAsync(conductor, time, Idle * 3);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "not unloaded under the agent's own turn");

        await AgentSaysAsync(kimi, id, "Done.", mark: "ended");
        await Until(() => time.Waiting == 1, "the idle timer, once the turn ended");
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last());
        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
    }

    [TestMethod]
    public async Task AnUpdateOnAClientlessSession_PushesItsUnloadBack()
    {
        // An agent that marks no turn of its own is not idle while it sends something.
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        await QuitAsync(conductor, zed);
        await AdvanceAsync(conductor, time, Idle - TimeSpan.FromMinutes(1));

        await AgentSaysAsync(kimi, id, "Still working on it.");
        await Until(() => conductor.Feed(id).LastActivity == time.GetUtcNow(), "the update");
        await AdvanceAsync(conductor, time, TimeSpan.FromMinutes(1));
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "the agent spoke a minute ago");
        await AdvanceAsync(conductor, time, Idle - TimeSpan.FromMinutes(1) - Second);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last());
        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last(), "unloaded the idle time after the agent's last update");
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

        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(target).Last());
        Assert.AreEqual("saved", Status(conductor, target));
    }

    // ---------------------------------------------------------------- close, delete, drafts

    [TestMethod]
    public async Task CloseByTheLastClient_CancelsAClientsTurn_AndUnloadsAfterTheIdleTime()
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

        await CloseSessionAsync(zed, idle);
        await CloseSessionAsync(zed, busy);
        await Until(() => Methods(kimi).Count(m => m == Cancel) == 1, "the client's turn cancelled, as ACP asks of a close");
        Assert.AreEqual((Resume, Prompt), (kimi.CallsFor(idle).Last(), kimi.CallsFor(busy).Last()), "neither unloaded yet");
        hold.SetResult(); // the fake ends its turn whatever the cancel says
        await turn.WaitAsync(Wait);
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual((Resume, Prompt), (kimi.CallsFor(idle).Last(), kimi.CallsFor(busy).Last()), "not unloaded within the idle time");

        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual((Close, Close), (kimi.CallsFor(idle).Last(), kimi.CallsFor(busy).Last()), "both unloaded");
    }

    [TestMethod]
    public async Task CloseDuringAnotherSessionsQuestion_CancelsNothing()
    {
        var time = new ManualTime();
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, claude.Adapter, kimi.Adapter);
        var target = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        await using var peer = await TestClient.ConnectAsync(conductor, "peer");
        var asker = await NewPromptedAsync(peer, "claude", "Coordinate the release");
        kimi.BeforePrompt = sid => sid == target ? hold.Task : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, target);
        var asking = AskAsync(peer, claude.Tokens[asker], target);
        await Until(() => kimi.CallsFor(target).Last() == Prompt, "the question's turn");

        await CloseSessionAsync(zed, target); // housekeeping in Zed: the question is not its work
        hold.SetResult();

        Assert.AreEqual("ok", (string?)(await asking.WaitAsync(Wait))!["text"], "the question was answered");
        CollectionAssert.DoesNotContain(Methods(kimi), Cancel);
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Prompt, kimi.CallsFor(target).Last(), "the idle time counts from the question's turn");
        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(target).Last());
    }

    [TestMethod]
    public async Task CloseDuringAChildsReport_CancelsNothing()
    {
        var time = new ManualTime();
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, claude.Adapter, kimi.Adapter);
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        var parent = await NewPromptedAsync(zed, "kimi", "Coordinate the release");
        kimi.BeforePrompt = sid => sid == parent ? hold.Task : Task.CompletedTask;
        await zed.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, new JsonObject
        {
            ["agent"] = "claude", ["prompt"] = "Draft the notes", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = kimi.Tokens[parent] },
        });
        await Until(() => PromptsOf(kimi, parent) == 2, "the report's turn in the parent");
        lock (kimi.Prompts) StringAssert.StartsWith(kimi.Prompts.Last(p => p.SessionId == parent).Text, "[Report · from session_id");

        await CloseSessionAsync(zed, parent); // housekeeping in Zed: the report is not its work
        hold.SetResult();
        await conductor.BackgroundTurnsIdleAsync().WaitAsync(Wait);

        CollectionAssert.DoesNotContain(Methods(kimi), Cancel);
        await AdvanceAsync(conductor, time, Idle - Second);
        Assert.AreEqual(Prompt, kimi.CallsFor(parent).Last(), "the idle time counts from the report's turn");
        await AdvanceAsync(conductor, time, Second);
        Assert.AreEqual(Close, kimi.CallsFor(parent).Last());
    }

    [TestMethod]
    public async Task CloseWhoseCancelFails_StillLeavesTheSessionToTheIdleTimer()
    {
        var time = new ManualTime();
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(time, kimi.Adapter);
        var id = await SavedSessionAsync(conductor, time, "kimi", "Fix the flaky test");
        kimi.BeforePrompt = sid => sid == id ? hold.Task : Task.CompletedTask;
        await using var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        var turn = PromptAsync(zed, id, "Run the tests");
        await Until(() => kimi.CallsFor(id).Last() == Prompt, "the turn");
        kimi.Adapter.BreakWritesToAgent();

        await CloseSessionAsync(zed, id); // the cancel cannot be written: logged, and the close still succeeds
        hold.SetResult();
        await turn.WaitAsync(Wait);

        await Until(() => time.Waiting == 1, "the idle timer, once the turn ended");
        CollectionAssert.DoesNotContain(Methods(kimi), Cancel, "the cancel never reached the agent");
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
        await AdvanceAsync(conductor, time, Idle);
        Assert.AreEqual(Close, kimi.CallsFor(used).Last());
    }
}
