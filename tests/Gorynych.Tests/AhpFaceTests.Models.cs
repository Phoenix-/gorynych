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
        // Changed elsewhere meanwhile; then the queued message is edited, its picker's model as it was.
        await rig.Zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = id, ["configId"] = "model", ["value"] = "large" }).WaitAsync(Wait);
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatPendingMessageSetAction
        {
            Kind = PendingMessageKind.Queued, Id = "q1", Message = UserMessage("Then the tests, all of them", new ModelSelection { Id = "small" }),
        }), null, CancellationToken.None);
        await LinkHarness.Until(() => vscode.Actions(chatUri).Count(a => a.Action.Value is ChatPendingMessageSetAction) == 2, "edited");
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
    public async Task TheModelOfferedBefore_PickedAgainInADraft_IsAPick()
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

        // Its user picks the model of before again: the draft keeps the pick, and the message sets it.
        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatDraftChangedAction
        {
            Draft = UserMessage("half a thought", new ModelSelection { Id = "small" }),
        }), null, CancellationToken.None);
        await Task.Delay(300);
        Assert.AreEqual("small", Draft()?.Model?.Id, "the pick stays");
        await Send(vscode, chatUri, "t1", "Smaller", new ModelSelection { Id = "small" });
        await TurnEndedAsync(vscode, chatUri, "t1");
        lock (rig.Kimi.ModelsChosen) Assert.AreEqual("small", rig.Kimi.ModelsChosen[id]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheModelANewSessionsAnswerSaid_StaysPickableAfterItsAgentReportedAnother()
    {
        await using var rig = await Rig.StartAsync();
        rig.Kimi.ReportsModes = true;
        rig.Kimi.ReportsModels = true;
        // As Claude's "default" before it reports "opus": the answer says "small", the agent "large" right after.
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
        await using var vscode = await rig.ConnectAsync();
        var (_, chatUri, id) = await CreateAsync(rig, vscode, "kimi");
        Message? Draft() => vscode.Of<ChatDraftChangedAction>(chatUri).LastOrDefault()?.Draft;
        await LinkHarness.Until(() => Draft()?.Model?.Id == "large", "the model reported");

        await vscode.Client.DispatchAsync(chatUri, new StateAction(new ChatDraftChangedAction
        {
            Draft = UserMessage("", new ModelSelection { Id = "small" }),
        }), null, CancellationToken.None);
        await Task.Delay(300);
        Assert.AreEqual("small", Draft()?.Model?.Id, "the pick stays");
        await Send(vscode, chatUri, "t1", "Hello", new ModelSelection { Id = "small" });
        await TurnEndedAsync(vscode, chatUri, "t1");
        lock (rig.Kimi.ModelsChosen) Assert.AreEqual("small", rig.Kimi.ModelsChosen[id]);
    }

    /// <summary>A file's text, read as another process may write it meanwhile; null while it cannot be read.</summary>
    static string? ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheStateFile_IsWritten_HoweverSavesFallAcrossItsWritersEnd()
    {
        var state = Path.Combine(TestDirs.New(), "ahp-face.json");
        await using var rig = await Rig.StartAsync(new AhpFaceOptions { StateFile = state, StateWriteDelay = TimeSpan.Zero });
        // A save just as the writer ends, having found nothing more: written all the same.
        rig.Face.StateWriterEnding = () =>
        {
            rig.Face.StateWriterEnding = null;
            rig.Face.ModelsSeenForTests("kimi", "in-the-gap");
            return Task.CompletedTask;
        };
        rig.Face.ModelsSeenForTests("kimi", "first");
        await LinkHarness.Until(() => ReadShared(state)?.Contains("in-the-gap") == true, "the save in the gap written", ms: 5_000);
        // Saves from many threads, many of them just as a writer finds nothing more and ends: the last one is written.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
        {
            for (var i = 0; i < 300; i++)
            {
                rig.Face.ModelsSeenForTests("kimi", $"t{t}-{i}");
                if (i % 3 == 0) await Task.Yield();
            }
        })));
        for (var round = 0; round < 20; round++)
        {
            rig.Face.ModelsSeenForTests("kimi", $"final-{round}");
            await LinkHarness.Until(() => ReadShared(state)?.Contains($"\"final-{round}\"") == true, $"round {round} written", ms: 5_000);
            await Task.Delay(round % 4); // around the writer's end
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AStateFileTheFaceCannotRead_IsNone_AndIsWrittenAnew()
    {
        foreach (var broken in new[]
        {
            "not json",
            """{"version":1,"agents":{"kimi":{"models":[{"id":"small"}]},"kimi":{"models":[]}}}""",
            """{"version":1,"agents":{"kimi":{"models":[{"id":"small","name":"Small"}],"schema":{"type":"object","properties":null}}}}""",
            """{"version":1,"agents":{"kimi":{"models":[{"name":"No ID"}]}}}""",
        })
        {
            var state = Path.Combine(TestDirs.New(), "ahp-face.json");
            File.WriteAllText(state, broken);
            await using var rig = await Rig.StartAsync(new AhpFaceOptions { StateFile = state, StateWriteDelay = TimeSpan.Zero });
            rig.Kimi.ReportsModes = true;
            rig.Kimi.ReportsModels = true;
            await using var vscode = await rig.ConnectAsync();
            var kimi = vscode.Initialized!.Snapshots[0].State.Root!.Agents.Single(a => a.Provider == "kimi");
            Assert.AreEqual(AhpFace.AgentDefault, kimi.Models.Single().Id, broken);
            var config = await vscode.Client.RequestAsync<ResolveSessionConfigParams, ResolveSessionConfigResult>("resolveSessionConfig",
                new ResolveSessionConfigParams { Channel = AhpNames.Root, Provider = "kimi" }, CancellationToken.None).WaitAsync(Wait);
            Assert.AreEqual(0, config!.Schema.Properties.Count, broken);

            await CreateAsync(rig, vscode, "kimi");
            await LinkHarness.Until(() => ReadShared(state)?.Contains("\"large\"") == true, "written anew");
            Assert.IsNotNull(System.Text.Json.Nodes.JsonNode.Parse(ReadShared(state)!)?["agents"]?["kimi"]?["schema"], broken);
        }
    }
}
