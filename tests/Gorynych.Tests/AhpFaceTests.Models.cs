using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Tests;

/// <summary>Step 2 of the face: agents' models and a message's model (docs/ahp-face.md, "Root channel" and "Configuration").</summary>
public sealed partial class AhpFaceTests
{
    static SessionConfigOption ModelOption(string current) => new SessionConfigSelect
    {
        Id = "model", Name = "Model", Category = SessionConfigOptionCategory.Model, CurrentValue = current,
        Options = new JsonArray(
            AcpJson.ToNode(new SessionConfigSelectOption { Value = "small", Name = "Small", Description = "Quick" }),
            AcpJson.ToNode(new SessionConfigSelectOption { Value = "large", Name = "Large" })),
    };

    static SessionConfigOption ModeOption(string current) => new SessionConfigSelect
    {
        Id = "mode", Name = "Permissions", Category = SessionConfigOptionCategory.Mode, CurrentValue = current,
        Options = new JsonArray(
            AcpJson.ToNode(new SessionConfigSelectOption { Value = "ask", Name = "Ask" }),
            AcpJson.ToNode(new SessionConfigSelectOption { Value = "full", Name = "Full access" })),
    };

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAgentTheFaceNeverSaw_HasItsDefaultAsItsOneModel_WhichIsNeverSet()
    {
        await using var rig = await Rig.StartAsync();
        rig.Claude.ReportsModes = true;
        rig.Claude.ReportsModels = true;
        await using var vscode = await rig.ConnectAsync();
        var claude = vscode.Initialized!.Snapshots[0].State.Root!.Agents.Single(a => a.Provider == "claude");
        Assert.AreEqual(AhpFace.AgentDefault, claude.Models.Single().Id, "VS Code disables an agent with no models");

        // The window sends it with a new session's first message: no model is set, the agent keeps its own.
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "claude");
        await Send(vscode, chatUri, "t1", "Hello", new ModelSelection { Id = AhpFace.AgentDefault });
        await TurnEndedAsync(vscode, chatUri, "t1");
        Assert.IsFalse(rig.Claude.CallsFor(id).Contains(AcpMethods.Agent.SessionSetConfigOption), "nothing set");
        await LinkHarness.Until(() => vscode.Of<RootAgentsChangedAction>(AhpNames.Root).LastOrDefault()?.Agents.Single(a => a.Provider == "claude").Models
            .Select(m => m.Id).SequenceEqual(["small", "large"]) == true, "its own models once a session of it is seen");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAgentsModelsAndOptions_SeenBeforeARestart_AreItsAfter()
    {
        var state = Path.Combine(TestDirs.New(), "ahp-face.json");
        await using (var first = await Rig.StartAsync(new AhpFaceOptions { StateFile = state }))
        {
            first.Kimi.ReportsModes = true;
            first.Kimi.ReportsModels = true;
            await using var vscode = await first.ConnectAsync();
            await CreateAsync(first, vscode, "kimi");
        }
        Assert.IsTrue(File.Exists(state));

        await using var rig = await Rig.StartAsync(new AhpFaceOptions { StateFile = state });
        await using var again = await rig.ConnectAsync();
        var agents = again.Initialized!.Snapshots[0].State.Root!.Agents;
        CollectionAssert.AreEqual(new[] { "small", "large" }, agents.Single(a => a.Provider == "kimi").Models.Select(m => m.Id).ToArray());
        Assert.AreEqual(AhpFace.AgentDefault, agents.Single(a => a.Provider == "claude").Models.Single().Id, "one never seen");
        var config = await again.Client.RequestAsync<ResolveSessionConfigParams, ResolveSessionConfigResult>("resolveSessionConfig",
            new ResolveSessionConfigParams { Channel = AhpNames.Root, Provider = "kimi" }, CancellationToken.None).WaitAsync(Wait);
        Assert.IsTrue(config!.Schema.Properties.ContainsKey("mode"), "the options of the last session seen, before any this run");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task OptionsAnAgentSaysRightAfterANewSessionsAnswer_WinOverTheAnswers()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        // Right after its answer the agent says its real model (as Claude's adapter does), before the face takes the answer.
        rig.Kimi.BeforeNew = id =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(50);
                lock (rig.Kimi.ModelsChosen) rig.Kimi.ModelsChosen[id] = "large";
                await rig.Kimi.Adapter.Current!.Update(id, new ConfigOptionUpdate { ConfigOptions = [ModelOption("large"), ModeOption("ask")] });
            });
            return Task.CompletedTask;
        };
        rig.Face.NewAnswered = () => Task.Delay(500);
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, _) = await CreateAsync(rig, vscode, "kimi");
        string? Drafted() => vscode.Of<ChatDraftChangedAction>(chatUri).LastOrDefault()?.Draft?.Model?.Id;
        await LinkHarness.Until(() => Drafted() == "large", "the model said after the answer");
        await Task.Delay(300);
        Assert.AreEqual("large", Drafted(), "and not the answer's, older");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQueuedMessagesModel_IsAPickAgainstTheModelWhenItWasQueued_NotWhenItStarts()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        var id = await rig.NewPromptedAsync("kimi", "Review the notes");
        await using var vscode = await rig.ConnectAsync();
        await LinkHarness.Until(() => vscode.ListAsync().Result.Items.Count == 1, "listed");
        var chatUri = AhpNames.ChatUri(id);
        await vscode.SubscribeAsync(AhpNames.SessionUri(id));
        await vscode.SubscribeAsync(chatUri);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = 0;
        rig.Kimi.DuringPrompt = (_, _) => Interlocked.Increment(ref turns) == 1 ? release.Task : Task.CompletedTask;
        string Model() { lock (rig.Kimi.ModelsChosen) return rig.Kimi.ModelsChosen.GetValueOrDefault(id, "small"); }

        await Send(vscode, chatUri, "t1", "Port the parser", new ModelSelection { Id = "small" });
        await LinkHarness.Until(() => turns == 1, "the turn runs");
        // Queued on "small", the session's model then: its picker's model, no pick.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatPendingMessageSetAction
        {
            Kind = PendingMessageKind.Queued, Id = "q1", Message = UserMessage("Then the tests", new ModelSelection { Id = "small" }),
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(chatUri).Any(a => a.Action.Value is ChatPendingMessageSetAction), "queued");
        // Changed elsewhere meanwhile.
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        var sets = rig.Kimi.CallsFor(id).Count(c => c == AcpMethods.Agent.SessionSetConfigOption);

        release.SetResult();
        await LinkHarness.Until(() => vscode.Of<ChatTurnStartedAction>(chatUri).Any(t => t.QueuedMessageId == "q1"), "the queued message starts");
        var started = vscode.Of<ChatTurnStartedAction>(chatUri).Single(t => t.QueuedMessageId == "q1").TurnId;
        await TurnEndedAsync(vscode, chatUri, started);
        Assert.AreEqual("large", Model(), "the change made elsewhere stays");
        Assert.AreEqual(sets, rig.Kimi.CallsFor(id).Count(c => c == AcpMethods.Agent.SessionSetConfigOption), "nothing set");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AClientsDraftWithTheModelOfferedBefore_GetsTheSessionsModelAgain()
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
        Message? Draft() => vscode.Of<ChatDraftChangedAction>(chatUri).Length > 0 ? vscode.Of<ChatDraftChangedAction>(chatUri).Last().Draft : chat.Draft;
        await LinkHarness.Until(() => Draft()?.Model?.Id == "small", "offered");
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        await LinkHarness.Until(() => Draft()?.Model?.Id == "large", "offered again");

        // A draft the window built before it took the new offer: its text kept, the session's model again.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatDraftChangedAction
        {
            Draft = UserMessage("half a thought", new ModelSelection { Id = "small" }),
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => Draft() is { Text: "half a thought", Model.Id: "large" }, "the session's model, the client's text");
    }
}
