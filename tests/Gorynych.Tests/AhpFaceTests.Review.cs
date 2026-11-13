using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Tests;

/// <summary>Step 2 of the face: what its live check and its review found (docs/ahp-face.md, "Step 2: working in sessions").</summary>
public sealed partial class AhpFaceTests
{
    static JsonNode AskToWrite(string sessionId, string toolCallId) => AcpJson.ToNode(new RequestPermissionRequest
    {
        SessionId = sessionId, ToolCall = new ToolCallUpdate { ToolCallId = toolCallId, Title = "Write notes.md" },
        Options = [new PermissionOption { OptionId = "yes", Name = "Allow", Kind = PermissionOptionKind.AllowOnce }],
    })!;

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionsModel_IsInItsChatsDraft_AndAModelVsCodeSendsAgainUnchanged_IsNoChoice()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        var id = await rig.NewPromptedAsync("kimi", "Review the notes");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "listed");
        var chatUri = AhpNames.ChatUri(id);
        await vscode.SubscribeAsync(AhpNames.SessionUri(id));
        var chat = (await vscode.SubscribeAsync(chatUri)).State.Chat!;
        string? Drafted() => vscode.Of<ChatDraftChangedAction>(chatUri).LastOrDefault()?.Draft?.Model?.Id ?? chat.Draft?.Model?.Id;

        // VS Code's model picker takes a chat's model from its draft: the session's.
        await LinkHarness.Until(() => Drafted() == "small", "the draft has the session's model");
        // Changed elsewhere (Zed), the draft follows.
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        await LinkHarness.Until(() => Drafted() == "large", "the draft follows a change made elsewhere");

        // VS Code's picker took "small" once and sends it with every message: no choice, so the session keeps "large".
        int Sets() => rig.Kimi.CallsFor(id).Count(c => c == AcpMethods.Agent.SessionSetConfigOption);
        var sets = Sets();
        await Send(vscode, chatUri, "t1", "Go on", new ModelSelection { Id = "small" });
        await TurnEndedAsync(vscode, chatUri, "t1");
        lock (rig.Kimi.ModelsChosen) Assert.AreEqual("large", rig.Kimi.ModelsChosen[id]);
        Assert.AreEqual(sets, Sets(), "nothing set");
        // A model picked in VS Code (another than it sent last) is set.
        await Send(vscode, chatUri, "t2", "Bigger", new ModelSelection { Id = "large" });
        await TurnEndedAsync(vscode, chatUri, "t2");
        await Send(vscode, chatUri, "t3", "Smaller", new ModelSelection { Id = "small" });
        await TurnEndedAsync(vscode, chatUri, "t3");
        lock (rig.Kimi.ModelsChosen) Assert.AreEqual("small", rig.Kimi.ModelsChosen[id]);
        await LinkHarness.Until(() => Drafted() == "small", "and the draft says it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionInItsFirstTurn_StaysThough_AListReadBeforeTheConductorPromotedItLacksIt()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Face.ListRead = async () =>
        {
            read.TrySetResult();
            await release.Task;
        };
        await rig.NewPromptedAsync("kimi", "Review the notes"); // the list changes; read without the draft, then held
        await read.Task.WaitAsync(Wait);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.DuringPrompt = (_, _) => answer.Task;

        await Send(vscode, chatUri, "t1", "Port the parser");
        await LinkHarness.Until(() => rig.Claude.Prompts.Any(p => p.SessionId == id), "the prompt reached its agent");
        await Task.Delay(200); // the face has marked it prompted
        rig.Face.ListRead = null;
        release.TrySetResult(); // the list read before the conductor promoted the draft is taken now
        await rig.NewPromptedAsync("kimi", "Again");
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 3, "a later list taken");
        Assert.IsFalse(vscode.Events().Any(e => e.Event is SubscriptionEventSessionRemoved r && r.Params.Session == sessionUri), "the session stays");
        Assert.IsTrue(rig.Face.HasOpen(id), "open, its chat with it");

        answer.TrySetResult();
        await TurnEndedAsync(vscode, chatUri, "t1");
        Assert.IsTrue(vscode.Of<ChatTurnCompleteAction>(chatUri).Any(a => a.TurnId == "t1"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnApprovalAskedWhileACancelledTurnSettles_IsAnsweredCancelled_AndOpensNoTurn()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        var outcome = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = 0;
        rig.Claude.DuringPrompt = async (agent, sid) =>
        {
            if (Interlocked.Increment(ref turns) > 1) return;
            await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Working" }, MessageId = "m1" });
            await LinkHarness.Until(() => { lock (rig.Claude.Adapter.Methods) return rig.Claude.Adapter.Methods.Contains(AcpMethods.Agent.SessionCancel); }, "the cancel reached the agent");
            outcome.TrySetResult(await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AskToWrite(sid, "w1")));
        };

        await Send(vscode, chatUri, "t1", "Write the notes");
        await LinkHarness.Until(() => vscode.Of<ChatResponsePartAction>(chatUri).Length == 1, "the turn runs");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnCancelledAction { TurnId = "t1", Duration = 1 }), null, CancellationToken.None);
        var answer = await outcome.Task.WaitAsync(Wait);
        Assert.AreEqual("cancelled", (string?)answer?["outcome"]?["outcome"]);
        await LinkHarness.Until(() => !rig.Face.Settling(id), "the cancelled turn's end mark came");
        Assert.AreEqual(0, vscode.Of<SessionInputNeededSetAction>(sessionUri).Length, "nothing waits for an answer");
        Assert.AreEqual(0, vscode.Of<ChatTurnStartedAction>(chatUri).Count(t => t.TurnId != "t1"), "no turn opened for it");

        await Send(vscode, chatUri, "t2", "Again");
        await TurnEndedAsync(vscode, chatUri, "t2");
        Assert.IsNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatTurnStartedAction { TurnId: "t2" }).RejectionReason, "the next message runs");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnApproval_MakesAReadSessionUnread()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, _) = await CreateAsync(rig, vscode, "claude");
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.DuringPrompt = async (agent, sid) =>
        {
            await go.Task;
            outcome.TrySetResult(await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AskToWrite(sid, "w1")));
        };
        await Send(vscode, chatUri, "t1", "Write the notes");
        await LinkHarness.Until(() => vscode.Of<SessionIsReadChangedAction>(sessionUri).Any(r => !r.IsRead), "a turn makes it unread");
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionIsReadChangedAction { IsRead = true }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Of<SessionIsReadChangedAction>(sessionUri).LastOrDefault()?.IsRead == true, "read");

        go.TrySetResult();
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededSetAction>(sessionUri).Length == 1, "the approval is shown");
        await LinkHarness.Until(() => vscode.Of<SessionIsReadChangedAction>(sessionUri).LastOrDefault()?.IsRead == false, "and makes the session unread");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatToolCallConfirmedAction
        {
            TurnId = "t1", ToolCallId = "w1", Approved = true, Confirmed = ToolCallConfirmationReason.UserAction,
        }), null, CancellationToken.None);
        Assert.AreEqual("yes", (string?)(await outcome.Task.WaitAsync(Wait))?["outcome"]?["optionId"]);
        await TurnEndedAsync(vscode, chatUri, "t1");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADisposedRoot_LetsGoOfTheChildrenTheFaceHadOpen()
    {
        await using var rig = await Rig.StartAsync(before: async r => await r.NewPromptedAsync("kimi", "Coordinate"));
        var parent = rig.Service.Store.All().Single().SessionId;
        var child = await rig.NewPromptedAsync("claude", "Do the part");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 2, "both listed");
        var sessionUri = AhpNames.SessionUri(parent);
        await vscode.SubscribeAsync(sessionUri);
        rig.Service.Store.Update(child, "claude", r => r.ParentSessionId = parent); // as spawn_session records it
        await LinkHarness.Until(() => vscode.Of<SessionChatAddedAction>(sessionUri).Length == 1, "the child is a chat of its root's session");
        await vscode.SubscribeAsync(AhpNames.ChatUri(child));
        Assert.IsTrue(rig.Face.HasOpen(child), "the face has the child open");
        var attached = rig.Service.Feed(child).Clients().Length;

        await vscode.Client.RequestAsync<DisposeSessionParams, ListSessionsResult>("disposeSession", new DisposeSessionParams { Channel = sessionUri }, CancellationToken.None).WaitAsync(Wait);
        await LinkHarness.Until(() => !rig.Face.HasOpen(child), "the child, which a delete leaves, is let go of");
        await LinkHarness.Until(() => rig.Service.Feed(child).Clients().Length == attached - 1, "closed in the conductor too");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheCommands_ANewSessionsAgentSaysRightAway_AreCompleted()
    {
        await using var rig = await Rig.StartAsync();
        rig.Claude.CommandsOnNew = [new AvailableCommand { Name = "compact", Description = "Compact the context" }];
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, _) = await CreateAsync(rig, vscode, "claude");
        // Said before the face has the session's ID, or after: either way it has them. (Which comes first is a race,
        // so a face that dropped the early ones fails this only now and then.)
        List<CompletionItem> Complete() => vscode.Client.RequestAsync<CompletionsParams, CompletionsResult>("completions",
            new CompletionsParams { Kind = CompletionItemKind.UserMessage, Channel = chatUri, Text = "/co", Offset = 3 }, CancellationToken.None).WaitAsync(Wait).Result!.Items;
        await LinkHarness.Until(() => Complete().Count == 1, "the agent's commands", ms: 5_000);
        Assert.AreEqual("/compact ", Complete().Single().InsertText);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionBeingMade_IsOnNoListNorTheRoot_TillItIsReady()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        await using var vscode = await rig.ConnectAsync();
        await CreateAsync(rig, vscode, "kimi"); // the face has seen the agent's options
        var setting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Kimi.OnSetConfig = async (_, _) =>
        {
            setting.TrySetResult();
            await release.Task;
            return false;
        };
        var sessionUri = $"ahp-session:/{Guid.NewGuid()}";
        await vscode.Client.RequestAsync<CreateSessionParams, ListSessionsResult>("createSession", new CreateSessionParams
        {
            Channel = sessionUri, Provider = "kimi", Config = new() { ["mode"] = AhpNames.Element(JsonValue.Create("full")) },
        }, CancellationToken.None).WaitAsync(Wait);
        await setting.Task.WaitAsync(Wait); // made in its agent, its options being set: not ready yet
        Assert.IsFalse((await vscode.ListAsync()).Items.Any(i => i.Resource == sessionUri), "not listed while made");

        release.TrySetResult();
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionAdded a && a.Params.Summary.Resource == sessionUri), "added once ready");
        var events = vscode.Events().ToList();
        var added = events.FindIndex(e => e.Event is SubscriptionEventSessionAdded a && a.Params.Summary.Resource == sessionUri);
        Assert.IsFalse(events.Take(added).Any(e => e.Event is SubscriptionEventSessionSummaryChanged c && c.Params.Session == sessionUri), "no change said before it is added");
        Assert.IsTrue((await vscode.ListAsync()).Items.Any(i => i.Resource == sessionUri));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientsChanges_AreEchoedInTheOrderItSentThem_AndOneTheAgentRefusesGoesBack()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        var id = await rig.NewPromptedAsync("kimi", "Review the notes");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "listed");
        var sessionUri = AhpNames.SessionUri(id);
        await vscode.SubscribeAsync(sessionUri);
        var refuse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Kimi.OnSetConfig = async (_, _) =>
        {
            await refuse.Task;
            throw new Gorynych.Acp.JsonRpc.JsonRpcException(Gorynych.Acp.JsonRpc.JsonRpcException.InternalError, "Not now");
        };

        // An option change, which waits for the agent, then another change: echoed in that order.
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionConfigChangedAction
        {
            Config = new Dictionary<string, JsonElement> { ["mode"] = AhpNames.Element(JsonValue.Create("full")) },
        }), null, CancellationToken.None);
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionIsReadChangedAction { IsRead = false }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(sessionUri).Count(a => a.Origin?.ClientId == "vscode") == 2, "both echoed");
        var echoes = vscode.Actions(sessionUri).Where(a => a.Origin?.ClientId == "vscode").ToList();
        Assert.IsInstanceOfType<SessionConfigChangedAction>(echoes[0].Action.Value);
        Assert.IsNull(echoes[0].RejectionReason);
        Assert.IsInstanceOfType<SessionIsReadChangedAction>(echoes[1].Action.Value);

        // The agent refuses it: the host puts the value back.
        refuse.TrySetResult();
        await LinkHarness.Until(() => vscode.Actions(sessionUri).LastOrDefault(a => a.Action.Value is SessionConfigChangedAction)?.Action.Value is SessionConfigChangedAction back
            && back.Config["mode"].GetString() == "ask", "the refused value goes back");
        Assert.IsNull(vscode.Actions(sessionUri).Last(a => a.Action.Value is SessionConfigChangedAction).Origin, "by the host");
    }
}
