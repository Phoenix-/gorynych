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
    public async Task ASessionsModel_IsInItsChatsDraft_AndOnlyAnotherModelThanTheSessionsIsSet()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        var id = await rig.NewPromptedAsync("kimi", "Review the notes");
        await using var vscode = await rig.ConnectAsync();
        await using var other = await rig.ConnectAsync("vscode-2"); // a second window
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "listed");
        var chatUri = AhpNames.ChatUri(id);
        await vscode.SubscribeAsync(AhpNames.SessionUri(id));
        var chat = (await vscode.SubscribeAsync(chatUri)).State.Chat!;
        await other.SubscribeAsync(chatUri);
        string? Drafted() => vscode.Of<ChatDraftChangedAction>(chatUri).LastOrDefault()?.Draft?.Model?.Id ?? chat.Draft?.Model?.Id;
        string Model() { lock (rig.Kimi.ModelsChosen) return rig.Kimi.ModelsChosen.GetValueOrDefault(id, "small"); }
        int Sets() => rig.Kimi.CallsFor(id).Count(c => c == AcpMethods.Agent.SessionSetConfigOption);
        var turns = 0;
        async Task Say(Peer peer, string model)
        {
            var turn = $"t{++turns}";
            await Send(peer, chatUri, turn, "Go on", new ModelSelection { Id = model });
            await TurnEndedAsync(vscode, chatUri, turn);
        }

        // VS Code's model picker shows a chat's draft's model, and follows it: the session's.
        await LinkHarness.Until(() => Drafted() == "small", "the draft has the session's model");
        // Changed elsewhere (Zed): the draft follows, and the window's picker with it; its model again is no choice.
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        await LinkHarness.Until(() => Drafted() == "large", "the draft follows a change made elsewhere");
        var sets = Sets();
        await Say(vscode, "large");
        Assert.AreEqual("large", Model());
        Assert.AreEqual(sets, Sets(), "nothing set");

        // Another model is the user's choice: set. And picked again after a change elsewhere (a window reloaded), again.
        await Say(vscode, "small");
        Assert.AreEqual("small", Model());
        await LinkHarness.Until(() => Drafted() == "small", "the draft says the choice");
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        await LinkHarness.Until(() => Drafted() == "large", "changed elsewhere again");
        await Say(vscode, "small");
        Assert.AreEqual("small", Model(), "the same pick, made again, is set again");

        // A set that fails is no choice remembered: the same pick again is set.
        var refusals = 1;
        rig.Kimi.OnSetConfig = (key, _) => key == "model" && Interlocked.Decrement(ref refusals) >= 0
            ? throw new Gorynych.Acp.JsonRpc.JsonRpcException(Gorynych.Acp.JsonRpc.JsonRpcException.InternalError, "Not now")
            : Task.FromResult(false);
        await Say(vscode, "large");
        Assert.IsTrue(vscode.Of<ChatErrorAction>(chatUri).Any(), "the failed set ends its turn with the error");
        Assert.AreEqual("small", Model());
        await Say(vscode, "large");
        Assert.AreEqual("large", Model(), "picked again after a failure, it is set");

        // Two windows: one picks; the other, its picker following the draft, sends that model: nothing undone.
        await LinkHarness.Until(() => Drafted() == "large", "the draft says it");
        await Say(other, "small");
        Assert.AreEqual("small", Model());
        await LinkHarness.Until(() => Drafted() == "small", "both windows' draft says it");
        sets = Sets();
        await Say(vscode, "small");
        Assert.AreEqual("small", Model());
        Assert.AreEqual(sets, Sets(), "the other window's model, followed, is no choice");
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

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAskThatCameUnderACancelledTurn_IsAnsweredCancelled_ThoughItsHandlerRunsOnlyOnceTheTurnEnded()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        Task<JsonNode?>? asked = null;
        var turns = 0;
        rig.Claude.DuringPrompt = async (agent, sid) =>
        {
            if (Interlocked.Increment(ref turns) > 1) return;
            await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "Working" }, MessageId = "m1" });
            await LinkHarness.Until(() => { lock (rig.Claude.Adapter.Methods) return rig.Claude.Adapter.Methods.Contains(AcpMethods.Agent.SessionCancel); }, "the cancel reached the agent");
            asked = agent.Rpc.SendRequestAsync(AcpMethods.Client.SessionRequestPermission, AskToWrite(sid, "w1")); // its turn ends meanwhile
        };
        // Its handler runs late, once the turn's end has come: the ask still belongs to the turn it came in.
        rig.Face.AskHandling = () => LinkHarness.Until(() => !rig.Face.Settling(id), "the cancelled turn's end came");

        await Send(vscode, chatUri, "t1", "Write the notes");
        await LinkHarness.Until(() => vscode.Of<ChatResponsePartAction>(chatUri).Length == 1, "the turn runs");
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatTurnCancelledAction { TurnId = "t1", Duration = 1 }), null, CancellationToken.None);
        await LinkHarness.Until(() => asked is not null, "asked");
        Assert.AreEqual("cancelled", (string?)(await asked!.WaitAsync(Wait))?["outcome"]?["outcome"]);
        Assert.AreEqual(0, vscode.Of<SessionInputNeededSetAction>(sessionUri).Length, "nothing waits for an answer");
        Assert.AreEqual(0, vscode.Of<ChatTurnStartedAction>(chatUri).Count(t => t.TurnId != "t1"), "no turn opened for it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ANewTerminal_IsKept_ThoughEveryOlderOneIsShown()
    {
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { TerminalYoung = TimeSpan.Zero });
        await using var vscode = await rig.ConnectAsync();
        using var raw = await RawPeer.ConnectAsync(rig);
        await raw.AnswerAsync(await raw.RequestAsync("initialize", new JsonObject
        {
            ["channel"] = AhpNames.Root, ["protocolVersions"] = new JsonArray("1.0.0"), ["clientId"] = "raw", ["initialSubscriptions"] = new JsonArray(),
        }));
        var claim = new TerminalClaim(new TerminalClientClaim { Kind = TerminalClaimKind.Client, ClientId = "vscode" });
        async Task<JsonObject> Subscribe(string terminal) => (await raw.AnswerAsync(await raw.RequestAsync("subscribe", new JsonObject { ["channel"] = terminal }))).Answer;
        for (var i = 0; i < 32; i++)
        {
            await vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
                new CreateTerminalParams { Channel = $"agenthost-terminal:/s{i}", Claim = claim }, CancellationToken.None).WaitAsync(Wait);
            Assert.IsNotNull((await Subscribe($"agenthost-terminal:/s{i}"))["result"], "shown");
        }

        // Its client subscribes once it is made: it is not the one that goes.
        await vscode.Client.RequestAsync<CreateTerminalParams, ListSessionsResult>("createTerminal",
            new CreateTerminalParams { Channel = "agenthost-terminal:/new", Claim = claim }, CancellationToken.None).WaitAsync(Wait);
        Assert.IsNotNull((await Subscribe("agenthost-terminal:/new"))["result"], "the new one is there to subscribe to");
        await LinkHarness.Until(() => vscode.Of<RootTerminalsChangedAction>(AhpNames.Root).LastOrDefault()?.Terminals.Any(t => t.Resource == "agenthost-terminal:/new") == true, "in the catalog");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WhatAnAgentSaysOfANewSessionBeforeItsAnswer_ReachesIt()
    {
        await using var rig = await Rig.StartAsync();
        // Said right after its answer (the conductor passes on what comes after it): it may reach the face before the
        // face has the session's ID. (A race: a face that dropped such updates fails this only now and then.)
        rig.Claude.BeforeNew = id =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                await rig.Claude.Adapter.Current!.Update(id, new SessionInfoUpdate { Title = "Said early" });
            });
            return Task.CompletedTask;
        };
        rig.Claude.CommandsOnNew = [new AvailableCommand { Name = "compact", Description = "Compact the context" }];
        await using var vscode = await rig.ConnectAsync();
        var (sessionUri, chatUri, _) = await CreateAsync(rig, vscode, "claude");
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Any(i => i.Resource == sessionUri && i.Title == "Said early"), "its title");
        List<CompletionItem> Complete() => vscode.Client.RequestAsync<CompletionsParams, CompletionsResult>("completions",
            new CompletionsParams { Kind = CompletionItemKind.UserMessage, Channel = chatUri, Text = "/co", Offset = 3 }, CancellationToken.None).WaitAsync(Wait).Result!.Items;
        await LinkHarness.Until(() => Complete().Count == 1, "its commands", ms: 5_000);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AResolvedUri_IsTheRealPath_EachLinkOnTheWayResolved()
    {
        await using var rig = await Rig.StartAsync();
        await using var vscode = await rig.ConnectAsync();
        var root = TestDirs.New();
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(real, "inner"));
        File.WriteAllText(Path.Combine(real, "inner", "a.txt"), "hello");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "hop"), real); // a link
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "hop")); // a link to a link
            Directory.CreateSymbolicLink(Path.Combine(real, "up"), ".."); // relative, back to its parent
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert.Inconclusive($"no symbolic links here: {e.Message}"); }
        async Task<string> Resolve(params string[] parts) => (await vscode.Client.ResourceResolveAsync(new ResourceResolveParams
        {
            Channel = AhpNames.Root, Uri = AhpNamesTests.VsCodeUri(Path.Combine([root, .. parts])),
        }, CancellationToken.None).WaitAsync(Wait)).Uri;
        string? Path_(string uri) => AhpNames.FilePath(uri);
        var file = Path_(await Resolve("real", "inner", "a.txt")); // the folder's own real path (macOS's /tmp is /private/tmp)
        Assert.AreEqual(file, Path_(await Resolve("link", "inner", "a.txt")));
        Assert.AreEqual(file, Path_(await Resolve("real", "up", "real", "inner", "a.txt")));
    }
}
