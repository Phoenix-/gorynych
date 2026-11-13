using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;
using AcpToolCallStatus = Gorynych.Acp.ToolCallStatus;

namespace Gorynych.Tests;

/// <summary>Step 2 of the face end to end (docs/ahp-face.md, "Step 2: working in sessions").</summary>
public sealed partial class AhpFaceTests
{
    static Message UserMessage(string text, ModelSelection? model = null, List<MessageAttachment>? attachments = null) =>
        new() { Text = text, Origin = new MessageOrigin { Kind = MessageKind.User }, Model = model, Attachments = attachments };

    static string Now() => AhpNames.Time(DateTimeOffset.UtcNow);

    /// <summary>
    /// Creates a session through the face, as VS Code does (its folder as VS Code writes a <c>file:</c> URI), subscribes to
    /// it and its chat, and waits until it is ready.
    /// </summary>
    static async Task<(string Session, string Chat, string Id)> CreateAsync(Rig rig, Peer vscode, string agent, string? folder = null,
        Dictionary<string, JsonElement>? config = null)
    {
        var sessionUri = $"ahp-session:/{Guid.NewGuid()}";
        await vscode.Client.RequestAsync<CreateSessionParams, ListSessionsResult>("createSession", new CreateSessionParams
        {
            Channel = sessionUri, Provider = agent, WorkingDirectories = folder is null ? null : [AhpNamesTests.VsCodeUri(folder)], Config = config,
        }, CancellationToken.None).WaitAsync(Wait);
        var state = (await vscode.SubscribeAsync(sessionUri)).State.Session!;
        var chatUri = AhpNames.ChatOf(sessionUri);
        await vscode.SubscribeAsync(chatUri);
        await LinkHarness.Until(() => state.Lifecycle == SessionLifecycle.Ready || vscode.Of<SessionReadyAction>(sessionUri).Length > 0, "the session is ready");
        var id = rig.Service.Store.All().Single(r => r.AhpUri == sessionUri).SessionId;
        return (sessionUri, chatUri, id);
    }

    static Task Send(Peer vscode, string chatUri, string turnId, string text, ModelSelection? model = null, List<MessageAttachment>? attachments = null) =>
        vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnStartedAction
        {
            TurnId = turnId, StartedAt = Now(), Message = UserMessage(text, model, attachments),
        }), null, CancellationToken.None);

    static Task TurnEndedAsync(Peer vscode, string chatUri, string turnId) => LinkHarness.Until(() =>
        vscode.Of<ChatTurnCompleteAction>(chatUri).Any(a => a.TurnId == turnId)
        || vscode.Of<ChatTurnCancelledAction>(chatUri).Any(a => a.TurnId == turnId)
        || vscode.Of<ChatErrorAction>(chatUri).Any(a => a.TurnId == turnId), $"turn {turnId} ended");

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASessionCreatedInAFolder_IsPrompted_ItsAnswerStreams_AndZedListsItUnderItsUri()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        // A folder VS Code names with its drive as c%3A, its space, '#' and the rest percent-encoded.
        var folder = Path.Combine(TestDirs.New(), "My Project #2 Дима");
        Directory.CreateDirectory(folder);
        var notes = Path.Combine(folder, "notes 1.md");
        var (sessionUri, chatUri, id) = await CreateAsync(rig, vscode, "claude", folder);

        var record = rig.Service.Store.Get(id)!;
        Assert.AreEqual(Path.GetFullPath(folder), record.Cwd, "the folder picked through the host");
        Assert.IsTrue(record.Draft, "not prompted yet");
        Assert.IsTrue(vscode.Events().Any(e => e.Event is SubscriptionEventSessionAdded added && added.Params.Summary.Resource == sessionUri),
            "the root says it once it is ready");
        // A draft no list has: the face keeps it when the list changes meanwhile.
        var other = await rig.NewPromptedAsync("kimi", "Review the notes");
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Any(i => i.Resource == AhpNames.SessionUri(other)), "the list read again");
        Assert.IsFalse(vscode.Events().Any(e => e.Event is SubscriptionEventSessionRemoved removed && removed.Params.Session == sessionUri), "the draft stays");

        await Send(vscode, chatUri, "t1", "Port the parser", attachments:
            [new MessageAttachment(new MessageResourceAttachment { Uri = AhpNamesTests.VsCodeUri(notes), Label = "notes 1.md" })]);
        await TurnEndedAsync(vscode, chatUri, "t1");
        Assert.AreEqual($"Port the parser\n\n@{AhpNames.FileUri(notes)}", rig.Claude.Prompts.Single(p => p.SessionId == id).Text,
            "a file attached as the agents see one");
        Assert.IsTrue(vscode.Of<ChatTurnCompleteAction>(chatUri).Any(a => a.TurnId == "t1"));
        Assert.AreEqual("ok", string.Concat(vscode.Of<ChatResponsePartAction>(chatUri).Select(p => p.Part.Value).OfType<MarkdownResponsePart>().Select(p => p.Content))
            + string.Concat(vscode.Of<ChatDeltaAction>(chatUri).Select(d => d.Content)));
        Assert.IsNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatTurnStartedAction).RejectionReason);

        // Prompted, it is a session like any: Zed lists it, under the URI VS Code chose, and the face keeps that URI.
        var listed = await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait);
        var info = (listed!["sessions"] as JsonArray)!.OfType<JsonObject>().Single(s => (string?)s["sessionId"] == id);
        Assert.AreEqual(sessionUri, (string?)info["_meta"]?[GorynychProtocol.MetaAhpUri]);
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Any(i => i.Resource == sessionUri && i.Title == "Port the parser"), "the face lists it by its URI");
        Assert.AreEqual(1, vscode.ListAsync().Result.Items.Count(i => i.Resource == sessionUri));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnApproval_IsAnsweredFromAhp_AndOneTheAgentWithdraws_IsWithdrawnThereToo()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, _) = await CreateAsync(rig, vscode, "claude");
        var outcome = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var withdraw = new CancellationTokenSource();
        var withdrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonNode Ask(string sid, string toolCallId) => AcpJson.ToNode(new RequestPermissionRequest
        {
            SessionId = sid, ToolCall = new ToolCallUpdate { ToolCallId = toolCallId, Title = "Write notes.md" },
            Options =
            [
                new PermissionOption { OptionId = "yes", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                new PermissionOption { OptionId = "always", Name = "Always", Kind = PermissionOptionKind.AllowAlways },
                new PermissionOption { OptionId = "no", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
            ],
        })!;
        rig.Claude.DuringPrompt = async (agent, sid) =>
        {
            await agent.Update(sid, new ToolCall { ToolCallId = "w1", Title = "Write notes.md", Kind = ToolKind.Edit, Status = AcpToolCallStatus.Pending });
            outcome.TrySetResult(await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, Ask(sid, "w1")));
            await agent.Update(sid, new SessionUpdateToolCallUpdate { ToolCallId = "w1", Status = AcpToolCallStatus.Completed });
            // A second one the agent takes back before anyone answers.
            try { await agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, Ask(sid, "w2"), withdraw.Token); }
            catch (OperationCanceledException) { withdrawn.TrySetResult(); }
        };

        await Send(vscode, chatUri, "t1", "Write the notes");
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededSetAction>(sessionUri).Length == 1, "the approval is in the session's inputNeeded");
        var needed = (SessionToolConfirmationRequest)vscode.Of<SessionInputNeededSetAction>(sessionUri)[0].Request.Value!;
        Assert.AreEqual(chatUri, needed.Chat);
        Assert.AreEqual("t1", needed.TurnId);
        var pending = (ToolCallPendingConfirmationState)needed.ToolCall.Value!;
        Assert.AreEqual("w1", pending.ToolCallId);
        CollectionAssert.AreEqual(new[] { "yes", "always", "no" }, pending.Options!.Select(o => o.Id).ToArray());
        CollectionAssert.AreEqual(new[] { ConfirmationOptionKind.Approve, ConfirmationOptionKind.Approve, ConfirmationOptionKind.Deny },
            pending.Options!.Select(o => o.Kind).ToArray());
        var ready = vscode.Of<ChatToolCallReadyAction>(chatUri).Last(r => r.ToolCallId == "w1");
        Assert.IsNull(ready.Confirmed, "back to waiting for confirmation");

        // Answered from VS Code: the agent gets the option chosen.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatToolCallConfirmedAction
        {
            TurnId = "t1", ToolCallId = "w1", Approved = true, Confirmed = ToolCallConfirmationReason.UserAction,
        }), null, CancellationToken.None);
        var answer = await outcome.Task.WaitAsync(Wait);
        Assert.AreEqual("selected", (string?)answer?["outcome"]?["outcome"]);
        Assert.AreEqual("yes", (string?)answer?["outcome"]?["optionId"]);
        Assert.IsNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatToolCallConfirmedAction).RejectionReason);
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededRemovedAction>(sessionUri).Length == 1, "the answered approval left inputNeeded");

        // The second: shown, then withdrawn by the agent, and so here.
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededSetAction>(sessionUri).Length == 2, "the second approval is shown");
        withdraw.Cancel();
        await withdrawn.Task.WaitAsync(Wait);
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededRemovedAction>(sessionUri).Length == 2, "the withdrawn approval left inputNeeded");
        await LinkHarness.Until(() => vscode.Of<ChatToolCallReadyAction>(chatUri).Any(r => r.ToolCallId == "w2" && r.Confirmed == ToolCallConfirmationReason.NotNeeded),
            "its tool call no longer waits");
        await TurnEndedAsync(vscode, chatUri, "t1");
        // A confirmation of nothing that waits is refused.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatToolCallConfirmedAction { TurnId = "t1", ToolCallId = "w2", Approved = true }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(chatUri).Any(a => a.Action.Value is ChatToolCallConfirmedAction { ToolCallId: "w2" } && a.RejectionReason is not null),
            "a confirmation of nothing waiting is refused");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AForm_IsAnsweredFromAhp_WithItsFieldsAsQuestions()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, _) = await CreateAsync(rig, vscode, "claude");
        var content = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.DuringPrompt = async (agent, sid) => content.TrySetResult(await agent.Rpc.SendRequestAsync(AcpMethods.Client.ElicitationCreate, JsonNode.Parse("""
            {"sessionId":"SID","mode":"form","message":"How shall I go on?",
             "requestedSchema":{"type":"object","required":["way"],"properties":{
               "way":{"type":"string","title":"Way","oneOf":[{"const":"fast","title":"Fast"},{"const":"careful","title":"Careful"}]},
               "count":{"type":"integer","title":"How many","minimum":1},
               "note":{"type":"string","description":"Anything else"}}}}
            """.Replace("SID", sid))));

        await Send(vscode, chatUri, "t1", "Plan it");
        await LinkHarness.Until(() => vscode.Of<ChatInputRequestedAction>(chatUri).Length == 1, "the form is an input request");
        var request = vscode.Of<ChatInputRequestedAction>(chatUri)[0].Request;
        Assert.AreEqual("How shall I go on?", request.Message);
        var questions = request.Questions!.Select(q => q.Value).ToList();
        var way = (ChatInputSingleSelectQuestion)questions[0]!;
        Assert.IsTrue(way.Required);
        CollectionAssert.AreEqual(new[] { "fast", "careful" }, way.Options.Select(o => o.Id).ToArray());
        Assert.AreEqual(ChatInputQuestionKind.Integer, ((ChatInputNumberQuestion)questions[1]!).Kind);
        Assert.AreEqual("Anything else", ((ChatInputTextQuestion)questions[2]!).Message);
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededSetAction>(sessionUri).Length == 1, "and in the session's inputNeeded");

        // Accepting without the required answer is refused; with it, the agent gets the content.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatInputCompletedAction { RequestId = request.Id, Response = ChatInputResponseKind.Accept }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(chatUri).Any(a => a.Action.Value is ChatInputCompletedAction && a.RejectionReason is not null), "an answer missing a required one is refused");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatInputCompletedAction
        {
            RequestId = request.Id, Response = ChatInputResponseKind.Accept,
            Answers = new Dictionary<string, ChatInputAnswer>
            {
                ["way"] = new(new ChatInputAnswered { State = ChatInputAnswerState.Submitted, Value = new ChatInputAnswerValue(new ChatInputSelectedAnswerValue { Kind = ChatInputAnswerValueKind.Selected, Value = "careful" }) }),
                ["count"] = new(new ChatInputAnswered { State = ChatInputAnswerState.Submitted, Value = new ChatInputAnswerValue(new ChatInputNumberAnswerValue { Kind = ChatInputAnswerValueKind.Number, Value = 3 }) }),
                ["note"] = new(new ChatInputSkipped { State = ChatInputAnswerState.Skipped }),
            },
        }), null, CancellationToken.None);
        var answer = await content.Task.WaitAsync(Wait);
        Assert.AreEqual("accept", (string?)answer?["action"]);
        Assert.AreEqual("careful", (string?)answer?["content"]?["way"]);
        Assert.AreEqual(3, (long?)answer?["content"]?["count"]);
        Assert.IsNull(answer?["content"]?["note"], "a skipped field is left out");
        await TurnEndedAsync(vscode, chatUri, "t1");
        await LinkHarness.Until(() => vscode.Of<SessionInputNeededRemovedAction>(sessionUri).Length == 1, "the answered form left inputNeeded");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACancel_EndsTheTurn_ReachesTheAgent_AndLeavesTheTurnsTailOut()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        rig.Claude.DuringPrompt = async (agent, sid) =>
        {
            await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Working" }, MessageId = "m1" });
            await LinkHarness.Until(() => { lock (rig.Claude.Adapter.Methods) return rig.Claude.Adapter.Methods.Contains(AcpMethods.Agent.SessionCancel); }, "the cancel reached the agent");
        };

        await Send(vscode, chatUri, "t1", "Port the parser");
        await LinkHarness.Until(() => vscode.Of<ChatResponsePartAction>(chatUri).Length == 1, "the turn runs");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnCancelledAction { TurnId = "t1", Duration = 1 }), null, CancellationToken.None);
        await TurnEndedAsync(vscode, chatUri, "t1");
        Assert.IsNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatTurnCancelledAction).RejectionReason);
        await LinkHarness.Until(() => { lock (rig.Claude.Adapter.Methods) return rig.Claude.Adapter.Methods.Contains(AcpMethods.Agent.SessionCancel); }, "session/cancel reached the agent");
        // The agent's answer after the cancel (the test agent's "ok") is the cancelled turn's tail.
        await LinkHarness.Until(() => !rig.Face.Settling(id), "the turn's end mark came");
        Assert.IsFalse(vscode.Of<ChatDeltaAction>(chatUri).Any(), "nothing of the tail reached the chat");
        Assert.AreEqual(1, vscode.Of<ChatResponsePartAction>(chatUri).Length);
        Assert.AreEqual(0, vscode.Of<ChatTurnStartedAction>(chatUri).Count(t => t.TurnId != "t1"), "nor began a turn");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ACancelRightAfterAMessageWithAModel_ReachesTheConductorAfterItsPrompt()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "kimi");
        bool Cancelled() { lock (rig.Kimi.Adapter.Methods) return rig.Kimi.Adapter.Methods.Contains(AcpMethods.Agent.SessionCancel); }
        rig.Kimi.DuringPrompt = (_, _) => LinkHarness.Until(Cancelled, "the cancel reached the agent");

        // The model is set first, a round trip: a cancel sent meanwhile must not overtake the prompt, or the
        // conductor, which cancels only the prompts that came before a cancel, would run the prompt after it.
        await Send(vscode, chatUri, "t1", "Go big", new ModelSelection { Id = "large" });
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnCancelledAction { TurnId = "t1", Duration = 0 }), null, CancellationToken.None);
        await TurnEndedAsync(vscode, chatUri, "t1");
        await LinkHarness.Until(() => !rig.Face.Settling(id), "the cancelled turn ended in its agent too");
        string[] methods;
        lock (rig.Kimi.Adapter.Methods) methods = [.. rig.Kimi.Adapter.Methods];
        var prompt = Array.LastIndexOf(methods, AcpMethods.Agent.SessionPrompt);
        var cancel = Array.LastIndexOf(methods, AcpMethods.Agent.SessionCancel);
        Assert.IsTrue(cancel >= 0, string.Join(", ", methods));
        Assert.IsTrue(prompt < cancel, $"a prompt the agent got came before the cancel: {string.Join(", ", methods)}");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQueuedMessage_StartsOnceTheTurnBeforeItEnds()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Claude.DuringPrompt = async (_, _) =>
        {
            int count;
            lock (rig.Claude.Prompts) count = rig.Claude.Prompts.Count;
            if (count == 1) await release.Task;
        };

        await Send(vscode, chatUri, "t1", "Port the parser");
        await LinkHarness.Until(() => { lock (rig.Claude.Prompts) return rig.Claude.Prompts.Count == 1; }, "the first prompt runs");
        // A message while a turn runs: refused as a turn, taken as a queued message.
        await Send(vscode, chatUri, "t2", "Not now");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatPendingMessageSetAction
        {
            Kind = PendingMessageKind.Queued, Id = "q1", Message = UserMessage("Then the tests"),
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(chatUri).Any(a => a.Action.Value is ChatPendingMessageSetAction), "the queued message is echoed");
        Assert.IsNotNull(vscode.Actions(chatUri).Single(a => a.Action.Value is ChatTurnStartedAction { TurnId: "t2" }).RejectionReason);
        Assert.AreEqual(1, rig.Claude.Prompts.Count, "it waits");

        release.SetResult();
        await TurnEndedAsync(vscode, chatUri, "q1");
        CollectionAssert.AreEqual(new[] { "Port the parser", "Then the tests" }, rig.Claude.Prompts.Where(p => p.SessionId == id).Select(p => p.Text).ToArray());
        var removed = vscode.Of<ChatPendingMessageRemovedAction>(chatUri).Single();
        Assert.AreEqual("q1", removed.Id);
        Assert.AreEqual("q1", vscode.Of<ChatTurnStartedAction>(chatUri).Single(t => t.TurnId == "q1").QueuedMessageId);
        Assert.IsTrue(vscode.Of<ChatTurnCompleteAction>(chatUri).Any(a => a.TurnId == "t1"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OptionsAndModels_AreTheAgents_AChangeReachesItAndZed_AndAMessagesModelIsSetBeforeItsPrompt()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        var id = await rig.NewPromptedAsync("kimi", "Review the notes");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "listed");
        var sessionUri = AhpNames.SessionUri(id);
        var chatUri = AhpNames.ChatUri(id);

        // The session's snapshot has its options (the face opens it for that), the model as AHP's own.
        var session = (await vscode.SubscribeAsync(sessionUri)).State.Session!;
        Assert.AreEqual("ask", session.Config!.Values["mode"].GetString());
        Assert.IsFalse(session.Config.Schema.Properties.ContainsKey("model"));
        await LinkHarness.Until(() => vscode.Of<RootAgentsChangedAction>(AhpNames.Root).Any(), "the models seen reach the root");
        var kimi = vscode.Of<RootAgentsChangedAction>(AhpNames.Root).Last().Agents.Single(a => a.Provider == "kimi");
        CollectionAssert.AreEqual(new[] { "small", "large" }, kimi.Models.Select(m => m.Id).ToArray());
        await vscode.SubscribeAsync(chatUri);

        // A change from VS Code: the agent's, echoed, and Zed (which has the session open) sees it.
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionConfigChangedAction
        {
            Config = new Dictionary<string, JsonElement> { ["mode"] = AhpNames.Element(JsonValue.Create("full")) },
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(sessionUri).Any(a => a.Action.Value is SessionConfigChangedAction && a.Origin?.ClientId == "vscode"), "echoed");
        Assert.IsNull(vscode.Actions(sessionUri).First(a => a.Action.Value is SessionConfigChangedAction && a.Origin?.ClientId == "vscode").RejectionReason);
        lock (rig.Kimi.Modes) Assert.AreEqual("full", rig.Kimi.Modes[id]);
        await LinkHarness.Until(() => { lock (rig.Zed.SessionUpdates) return rig.Zed.SessionUpdates.Any(u => u.SessionId == id && (string?)u.Update["sessionUpdate"] == "config_option_update"
            && (u.Update["configOptions"] as JsonArray)!.Any(o => (string?)o?["id"] == "mode" && (string?)o?["currentValue"] == "full")); }, "Zed sees the change");
        // A value the session does not have is refused.
        await vscode.Client.DispatchAsync(sessionUri, new StateAction(new SessionConfigChangedAction
        {
            Config = new Dictionary<string, JsonElement> { ["mode"] = AhpNames.Element(JsonValue.Create("root")) },
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(sessionUri).Any(a => a.Action.Value is SessionConfigChangedAction && a.RejectionReason is not null), "refused");

        // A message with a model: set first, then the prompt.
        await Send(vscode, chatUri, "t1", "Again, bigger", new ModelSelection { Id = "large" });
        await TurnEndedAsync(vscode, chatUri, "t1");
        lock (rig.Kimi.ModelsChosen) Assert.AreEqual("large", rig.Kimi.ModelsChosen[id]);
        var calls = rig.Kimi.CallsFor(id);
        Assert.IsTrue(Array.LastIndexOf(calls, AcpMethods.Agent.SessionSetConfigOption) < Array.LastIndexOf(calls, AcpMethods.Agent.SessionPrompt),
            string.Join(", ", calls));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheOptionsChosenBeforeASessionExists_AreSetOnIt_BeforeItIsReady()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        await using var vscode = await rig.ConnectAsync();
        // Before any session of the agent: nothing to describe.
        var none = await vscode.Client.RequestAsync<ResolveSessionConfigParams, ResolveSessionConfigResult>("resolveSessionConfig",
            new ResolveSessionConfigParams { Channel = AhpNames.Root, Provider = "kimi" }, CancellationToken.None);
        Assert.AreEqual(0, none!.Schema.Properties.Count);
        await CreateAsync(rig, vscode, "kimi");
        // Then those of the latest session seen, with only the values the client chose.
        var config = await vscode.Client.RequestAsync<ResolveSessionConfigParams, ResolveSessionConfigResult>("resolveSessionConfig",
            new ResolveSessionConfigParams { Channel = AhpNames.Root, Provider = "kimi", Config = new() { ["mode"] = AhpNames.Element(JsonValue.Create("full")), ["x"] = AhpNames.Element(JsonValue.Create(1)) } },
            CancellationToken.None);
        Assert.IsTrue(config!.Schema.Properties.ContainsKey("mode"));
        CollectionAssert.AreEqual(new[] { "mode" }, config.Values.Keys.ToArray());

        var (sessionUri, _, id) = await CreateAsync(rig, vscode, "kimi", config: new() { ["mode"] = AhpNames.Element(JsonValue.Create("full")) });
        lock (rig.Kimi.Modes) Assert.AreEqual("full", rig.Kimi.Modes[id], "set before ready");
        await LinkHarness.Until(() => vscode.Of<SessionConfigChangedAction>(sessionUri).LastOrDefault(c => c.Config.ContainsKey("mode")) is { } last
            && last.Config["mode"].GetString() == "full", "the session's config says it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ADisposedSession_IsDeletedInItsAgent_AndLeavesTheList()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, id) = await CreateAsync(rig, vscode, "kimi");
        await Send(vscode, chatUri, "t1", "Review the notes");
        await TurnEndedAsync(vscode, chatUri, "t1");
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Any(i => i.Resource == sessionUri), "listed once prompted");

        await vscode.Client.RequestAsync<DisposeSessionParams, ListSessionsResult>("disposeSession", new DisposeSessionParams { Channel = sessionUri }, CancellationToken.None).WaitAsync(Wait);
        Assert.IsTrue(rig.Kimi.CallsFor(id).Contains(AcpMethods.Agent.SessionDelete), "deleted in its agent");
        await LinkHarness.Until(() => vscode.Events().Any(e => e.Event is SubscriptionEventSessionRemoved removed && removed.Params.Session == sessionUri), "removed on the root");
        Assert.IsFalse(vscode.ListAsync().Result.Items.Any(i => i.Resource == sessionUri));
        var gone = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => vscode.Client.RequestAsync<DisposeSessionParams, ListSessionsResult>("disposeSession",
            new DisposeSessionParams { Channel = sessionUri }, CancellationToken.None));
        Assert.AreEqual(AhpErrorCodes.SessionNotFound, gone.Code);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SlashCommands_AreCompleted_AndACompletedOneGoesAsItsText()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        rig.Claude.DuringPrompt = (agent, sid) => agent.Update(sid, new AvailableCommandsUpdate
        {
            AvailableCommands = [new AvailableCommand { Name = "compact", Description = "Compact the context" }, new AvailableCommand { Name = "context", Description = "Show it" }, new AvailableCommand { Name = "review", Description = "Review" }],
        });
        await Send(vscode, chatUri, "t1", "Hello");
        await TurnEndedAsync(vscode, chatUri, "t1");

        async Task<List<CompletionItem>> Complete(string text) => (await vscode.Client.RequestAsync<CompletionsParams, CompletionsResult>("completions",
            new CompletionsParams { Kind = CompletionItemKind.UserMessage, Channel = chatUri, Text = text, Offset = text.Length }, CancellationToken.None).WaitAsync(Wait))!.Items;
        await LinkHarness.Until(() => Complete("/co").Result.Count == 2, "the agent's commands");
        var item = (await Complete("/con")).Single();
        Assert.AreEqual("/context ", item.InsertText);
        Assert.AreEqual(0L, item.RangeStart);
        Assert.AreEqual(4L, item.RangeEnd);
        Assert.AreEqual(0, (await Complete("look /co")).Count, "only at the start");

        await Send(vscode, chatUri, "t2", "/context please", attachments: [item.Attachment]);
        await TurnEndedAsync(vscode, chatUri, "t2");
        Assert.AreEqual("/context please", rig.Claude.Prompts.Last(p => p.SessionId == id).Text, "the command's attachment adds nothing to the prompt");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheTerminal_SaysThisHostHasNoShell_AndGoesWhenDisposed()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        const string terminal = "agenthost-terminal:/t1"; // VS Code's own name for the terminals it makes
        var claim = new TerminalClaim(new TerminalClientClaim { Kind = TerminalClaimKind.Client, ClientId = "vscode" });
        await vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
            new CreateTerminalParams { Channel = terminal, Claim = claim, Name = "Gorynych", Cwd = AhpNames.FileUri(Path.GetTempPath()), Cols = 80, Rows = 24 }, CancellationToken.None).WaitAsync(Wait);
        await LinkHarness.Until(() => vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Any(t => t.Terminals.Any(i => i.Resource == terminal)), "in the root's catalog");
        var info = vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Last().Terminals.Single();
        Assert.IsInstanceOfType<TerminalExitedLifecycleState>(info.Lifecycle.Value);

        // The package's .NET client takes any snapshot with a lifecycle for a session's (1.0.0): the terminal is read as JSON, as VS Code reads it.
        using var raw = await RawPeer.ConnectAsync(rig);
        var (_, initialized) = await raw.AnswerAsync(await raw.RequestAsync("initialize", new JsonObject
        {
            ["channel"] = AhpNames.Root, ["protocolVersions"] = new JsonArray("1.0.0"), ["clientId"] = "raw", ["initialSubscriptions"] = new JsonArray(),
        }));
        Assert.IsNotNull(initialized["result"]);
        var (_, subscribed) = await raw.AnswerAsync(await raw.RequestAsync("subscribe", new JsonObject { ["channel"] = terminal }));
        var state = subscribed["result"]!["snapshot"]!["state"]!;
        Assert.AreEqual(AhpFace.NoShell, (string?)state["content"]![0]!["value"]);
        Assert.AreEqual("unclassified", (string?)state["content"]![0]!["type"]);
        Assert.AreEqual("exited", (string?)state["lifecycle"]!["status"], "no process ever ran");
        Assert.IsNull(state["lifecycle"]!["exitCode"], "so it says no exit code, which VS Code shows no error for");
        Assert.AreEqual(false, (bool?)state["isPty"]);
        Assert.AreEqual("Gorynych", (string?)state["title"]);
        Assert.AreEqual("vscode", (string?)state["claim"]!["clientId"]);

        // What a client does in it is taken, and goes nowhere: no process, no output.
        var input = await raw.RequestAsync("dispatchAction", new JsonObject
        {
            ["channel"] = terminal, ["clientSeq"] = 1, ["action"] = new JsonObject { ["type"] = "terminal/input", ["data"] = "dir\r" },
        });
        var (beforeInput, _) = await raw.AnswerAsync(input);
        var echo = beforeInput.Single(m => IsAction(m, terminal));
        Assert.IsNull(echo["params"]!["rejectionReason"]);
        Assert.AreEqual("terminal/input", (string?)echo["params"]!["action"]!["type"]);
        var again = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
            new CreateTerminalParams { Channel = terminal, Claim = claim }, CancellationToken.None));
        Assert.AreEqual(AhpErrorCodes.AlreadyExists, again.Code);
        var nameless = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
            new CreateTerminalParams { Channel = "", Claim = claim }, CancellationToken.None));
        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, nameless.Code);

        // Its title is the catalog's too.
        await raw.AnswerAsync(await raw.RequestAsync("dispatchAction", new JsonObject
        {
            ["channel"] = terminal, ["clientSeq"] = 2, ["action"] = new JsonObject { ["type"] = "terminal/titleChanged", ["title"] = "Renamed" },
        }));
        await LinkHarness.Until(() => vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Last().Terminals.Single(t => t.Resource == terminal).Title == "Renamed", "the catalog has its title");
        // Past 32 terminals the oldest no client shows goes, not one shown.
        for (var i = 0; i < 32; i++)
            await vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
                new CreateTerminalParams { Channel = $"agenthost-terminal:/n{i}", Claim = claim }, CancellationToken.None).WaitAsync(Wait);
        await LinkHarness.Until(() => vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Last().Terminals.Any(t => t.Resource == "agenthost-terminal:/n31"), "all made");
        var kept = vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Last().Terminals.Select(t => t.Resource).ToList();
        Assert.AreEqual(32, kept.Count);
        Assert.IsTrue(kept.Contains(terminal), "the one shown stays");
        Assert.IsFalse(kept.Contains("agenthost-terminal:/n0"), "the oldest not shown went");

        await vscode.Client.RequestAsync<DisposeTerminalParams, ListSessionsResult>("disposeTerminal", new DisposeTerminalParams { Channel = terminal }, CancellationToken.None).WaitAsync(Wait);
        await LinkHarness.Until(() => !vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).Last().Terminals.Any(t => t.Resource == terminal), "out of the catalog");
        var (_, gone) = await raw.AnswerAsync(await raw.RequestAsync("subscribe", new JsonObject { ["channel"] = terminal }));
        Assert.AreEqual(AhpErrorCodes.SessionNotFound, (int?)gone["error"]?["code"]);
        var disposedTwice = await Assert.ThrowsExactlyAsync<AhpRpcException>(() => vscode.Client.RequestAsync<DisposeTerminalParams, ListSessionsResult>("disposeTerminal",
            new DisposeTerminalParams { Channel = terminal }, CancellationToken.None));
        Assert.AreEqual(AhpErrorCodes.NotFound, disposedTwice.Code);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Folders_AreListedResolvedReadAndMade_AndNothingElseIsDone()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var folder = Path.Combine(TestDirs.New(), "a b #1 Дима");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), [0xff, 0xfe, 0x00, 0x01]);
        // As VS Code names them: file:///c%3A/.../a%20b%20%231%20%D0%94...
        string Uri(params string[] parts) => AhpNamesTests.VsCodeUri(Path.Combine([folder, .. parts]));
        var ct = CancellationToken.None;

        var list = await vscode.Client.ResourceListAsync(new ResourceListParams { Channel = AhpNames.Root, Uri = Uri() }, ct).WaitAsync(Wait);
        CollectionAssert.AreEqual(new[] { "src/directory", "a.txt/file", "b.bin/file" }, list.Entries.Select(e => $"{e.Name}/{e.Type}").ToArray());
        var file = await vscode.Client.ResourceResolveAsync(new ResourceResolveParams { Channel = AhpNames.Root, Uri = Uri("a.txt") }, ct).WaitAsync(Wait);
        Assert.AreEqual(ResourceType.File, file.Type);
        Assert.AreEqual(5L, file.Size);
        Assert.AreEqual(ResourceType.Directory, (await vscode.Client.ResourceResolveAsync(new ResourceResolveParams { Channel = AhpNames.Root, Uri = Uri("src") }, ct)).Type);
        var text = await vscode.Client.ResourceReadAsync(new ResourceReadParams { Channel = AhpNames.Root, Uri = Uri("a.txt") }, ct).WaitAsync(Wait);
        Assert.AreEqual(ContentEncoding.Utf8, text.Encoding);
        Assert.AreEqual("hello", text.Data);
        var binary = await vscode.Client.ResourceReadAsync(new ResourceReadParams { Channel = AhpNames.Root, Uri = Uri("b.bin") }, ct).WaitAsync(Wait);
        Assert.AreEqual(ContentEncoding.Base64, binary.Encoding);
        CollectionAssert.AreEqual(new byte[] { 0xff, 0xfe, 0x00, 0x01 }, Convert.FromBase64String(binary.Data));
        await vscode.Client.ResourceMkdirAsync(new ResourceMkdirParams { Channel = AhpNames.Root, Uri = Uri("new", "inner") }, ct).WaitAsync(Wait);
        Assert.IsTrue(Directory.Exists(Path.Combine(folder, "new", "inner")));

        async Task<int> Code(Func<Task> call) => (await Assert.ThrowsExactlyAsync<AhpRpcException>(call)).Code;
        Assert.AreEqual(AhpErrorCodes.NotFound, await Code(() => vscode.Client.ResourceReadAsync(new ResourceReadParams { Channel = AhpNames.Root, Uri = Uri("missing.txt") }, ct)));
        Assert.AreEqual(AhpErrorCodes.NotFound, await Code(() => vscode.Client.ResourceListAsync(new ResourceListParams { Channel = AhpNames.Root, Uri = "ahp-session:/x" }, ct)));
        // Another machine's files are not this host's to serve, nor a folder for a session.
        const string share = "file://server/share/x";
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceListAsync(new ResourceListParams { Channel = AhpNames.Root, Uri = share }, ct)));
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceResolveAsync(new ResourceResolveParams { Channel = AhpNames.Root, Uri = share }, ct)));
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceReadAsync(new ResourceReadParams { Channel = AhpNames.Root, Uri = share }, ct)));
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceMkdirAsync(new ResourceMkdirParams { Channel = AhpNames.Root, Uri = share }, ct)));
        var elsewhere = $"ahp-session:/{Guid.NewGuid()}";
        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, await Code(() => vscode.Client.RequestAsync<CreateSessionParams, ListSessionsResult>("createSession",
            new CreateSessionParams { Channel = elsewhere, Provider = "claude", WorkingDirectories = [share] }, ct)));
        Assert.IsFalse(rig.Service.Store.All().Any(r => r.AhpUri == elsewhere), "no session made elsewhere");
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceWriteAsync(new ResourceWriteParams
        {
            Channel = AhpNames.Root, Uri = Uri("a.txt"), Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("bye")), Encoding = ContentEncoding.Base64,
        }, ct)));
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceDeleteAsync(new ResourceDeleteParams { Channel = AhpNames.Root, Uri = Uri("a.txt") }, ct)));
        Assert.AreEqual(AhpErrorCodes.PermissionDenied, await Code(() => vscode.Client.ResourceMoveAsync(new ResourceMoveParams { Channel = AhpNames.Root, Source = Uri("a.txt"), Destination = Uri("c.txt") }, ct)));
        Assert.AreEqual("hello", File.ReadAllText(Path.Combine(folder, "a.txt")), "nothing changed");
    }
}
