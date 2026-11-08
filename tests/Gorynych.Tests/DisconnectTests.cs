using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// A client that goes away without <c>session/close</c> (Zed sends none when it quits) lets go of
/// its sessions all the same: once nothing uses one, it is closed in its agent, and the lists show
/// it saved. A turn, a service turn or an approval waiting for a client keeps it loaded until it
/// ends (docs/service-protocol.md, "Letting go of a session").
/// </summary>
[TestClass]
public sealed class DisconnectTests
{
    static readonly string Cwd = Path.GetTempPath();
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    const string Resume = AcpMethods.Agent.SessionResume;
    const string Prompt = AcpMethods.Agent.SessionPrompt;
    const string Close = AcpMethods.Agent.SessionClose;

    static Conductor Conductor(params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
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

    /// <summary>A session with a turn that its client closed: saved, not loaded in its agent.</summary>
    static async Task<string> SavedSessionAsync(Conductor conductor, string agent, string text)
    {
        await using var owner = await TestClient.ConnectAsync(conductor, "owner");
        var id = await NewPromptedAsync(owner, agent, text);
        await owner.Rpc.SendRequestAsync(Close, new JsonObject { ["sessionId"] = id });
        return id;
    }

    static Task PromptAsync(TestClient client, string sessionId, string text) =>
        client.Rpc.RequestAsync<PromptRequest, PromptResponse>(Prompt,
            new PromptRequest { SessionId = sessionId, Prompt = [new TextContent { Text = text }] });

    static Task LoadAsync(TestClient client, string sessionId) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad,
            new JsonObject { ["sessionId"] = sessionId, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() });

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

    [TestMethod]
    public async Task Load_ThenQuit_ClosesTheSessionsInTheirAgents_AndTheListsShowThemSaved()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { HasChannel = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var kimiSession = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
        var claudeSession = await SavedSessionAsync(conductor, "claude", "Write the notes");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, kimiSession);
        await LoadAsync(zed, claudeSession);
        Assert.AreEqual(("open", "open"), (Status(conductor, kimiSession), Status(conductor, claudeSession)));

        await QuitAsync(conductor, zed);

        Assert.AreEqual(Close, kimi.CallsFor(kimiSession).Last(), "Kimi no longer keeps it in memory");
        Assert.AreEqual(Close, claude.CallsFor(claudeSession).Last(), "the Claude adapter no longer keeps its process");
        Assert.AreEqual(("saved", "saved"), (Status(conductor, kimiSession), Status(conductor, claudeSession)));
    }

    [TestMethod]
    public async Task TwoClients_OneQuits_TheSessionStaysOpen_UntilTheOtherGoesToo()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(kimi.Adapter);
        var id = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var other = await TestClient.ConnectAsync(conductor, "zed-2");
        await LoadAsync(zed, id);
        await LoadAsync(other, id);

        await QuitAsync(conductor, zed);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "the other client still has it open");
        Assert.AreEqual("open", Status(conductor, id));

        await QuitAsync(conductor, other);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
        Assert.AreEqual("saved", Status(conductor, id));
    }

    [TestMethod]
    public async Task QuitDuringATurn_ClosesTheSessionOnlyAfterTheTurnEnds()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(kimi.Adapter);
        var id = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
        kimi.BeforePrompt = sid => sid == id ? hold.Task : Task.CompletedTask;
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, id);
        var turn = PromptAsync(zed, id, "Run the tests");
        await Until(() => kimi.CallsFor(id).Last() == Prompt, "the turn");

        await QuitAsync(conductor, zed);
        _ = turn.ContinueWith(t => t.Exception, TaskScheduler.Default); // its client is gone before the answer
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "not closed under the turn: closing would stop it");
        Assert.AreEqual("open", Status(conductor, id));

        hold.SetResult();
        await Until(() => kimi.CallsFor(id).Last() == Close, "the close after the turn");
        await Until(() => Status(conductor, id) == "saved", "saved");
    }

    [TestMethod]
    public async Task QuitDuringAnotherSessionsQuestion_ClosesTheAskedSessionAfterItsTurn()
    {
        var claude = new LifecycleAgent("claude", canDelete: false);
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(claude.Adapter, kimi.Adapter);
        var target = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
        await using var peer = await TestClient.ConnectAsync(conductor, "peer");
        var asker = await NewPromptedAsync(peer, "claude", "Coordinate the release");
        kimi.BeforePrompt = sid => sid == target ? hold.Task : Task.CompletedTask;
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        await LoadAsync(zed, target);
        var asking = peer.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target, ["text"] = "Done?", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = claude.Tokens[asker] },
        });
        await Until(() => kimi.CallsFor(target).Last() == Prompt, "the question's turn");

        await QuitAsync(conductor, zed);
        Assert.AreEqual(Prompt, kimi.CallsFor(target).Last(), "not closed under the question's turn");

        hold.SetResult();
        Assert.AreEqual("ok", (string?)(await asking)!["text"]);
        await Until(() => kimi.CallsFor(target).Last() == Close, "the close after the question's turn");
        await Until(() => Status(conductor, target) == "saved", "saved");
    }

    [TestMethod]
    public async Task AnApprovalWaitingForAClient_KeepsTheSessionLoaded_ForAClientThatComesBack()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        var answered = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var conductor = Conductor(kimi.Adapter);
        var id = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
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
        await Task.Delay(200);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "still loaded: a client may come back to answer");
        Assert.AreEqual("open", Status(conductor, id));

        // A client that opens it gets the approval, and keeps the session while it has it open.
        var back = await TestClient.ConnectAsync(conductor, "zed again");
        await LoadAsync(back, id);
        Assert.AreEqual("allow", await answered.Task.WaitAsync(Wait));
        await Task.Delay(200);
        await conductor.CleanupsIdleAsync().WaitAsync(Wait);
        Assert.AreEqual(Prompt, kimi.CallsFor(id).Last(), "the client that answered has it open");

        await QuitAsync(conductor, back);
        Assert.AreEqual(Close, kimi.CallsFor(id).Last());
        Assert.AreEqual("saved", Status(conductor, id));
    }

    [TestMethod]
    public async Task AnAgentsRequestOutsideATurn_KeepsTheSessionLoaded_UntilTheAgentWithdrawsIt()
    {
        // Claude asks for approvals in turns its channel starts, which no session/prompt of the service holds.
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(kimi.Adapter);
        var id = await SavedSessionAsync(conductor, "kimi", "Fix the flaky test");
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        zed.Approve = Unanswered;
        await LoadAsync(zed, id);
        using var withdraw = new CancellationTokenSource();
        var asking = kimi.Adapter.Current!.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AcpJson.ToNode(Approval(id)), withdraw.Token);
        _ = asking.ContinueWith(t => t.Exception, TaskScheduler.Default);
        await Until(() => zed.Approvals.Count == 1, "the approval in Zed");

        await QuitAsync(conductor, zed);
        await Task.Delay(200);
        Assert.AreEqual(Resume, kimi.CallsFor(id).Last(), "still loaded while the approval waits");

        withdraw.Cancel();
        await Until(() => kimi.CallsFor(id).Last() == Close, "the close once the agent withdrew its request");
        await Until(() => Status(conductor, id) == "saved", "saved");
    }

    [TestMethod]
    public async Task ADraft_IsStillDiscarded_AndASessionWithATurn_ClosedButKept()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(kimi.Adapter);
        var zed = await TestClient.ConnectAsync(conductor, "zed");
        var draft = (await zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
            new NewSessionRequest { Cwd = Cwd, McpServers = [] })).SessionId;
        var used = await NewPromptedAsync(zed, "kimi", "Fix the flaky test");

        await QuitAsync(conductor, zed);

        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, Close, AcpMethods.Agent.SessionDelete }, kimi.CallsFor(draft));
        Assert.IsNull(conductor.Store.Get(draft));
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionNew, Prompt, Close }, kimi.CallsFor(used));
        Assert.AreEqual("saved", Status(conductor, used));
    }
}
