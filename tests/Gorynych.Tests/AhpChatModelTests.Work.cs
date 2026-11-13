using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Tests;

/// <summary>The mapping of step 2 (docs/ahp-face.md, "Step 2: working in sessions"): a client's turns, approvals, forms, usage and options.</summary>
public sealed partial class AhpChatModelTests
{
    static StateAction ClientTurn(ChatModel model, string turnId, string text)
    {
        var action = new StateAction(new ChatTurnStartedAction
        {
            TurnId = turnId, StartedAt = "2026-11-13T10:00:00.000Z", Message = new Message { Text = text, Origin = new MessageOrigin { Kind = MessageKind.User } },
        });
        Reducers.ApplyToChat(model.State, action); // the face applies and echoes a client's action
        model.ClientTurnStarted(turnId);
        return action;
    }

    [TestMethod]
    public void AClientsTurn_TakesTheAgentsAnswer_AndEndsByTheMarkOfItsPrompt()
    {
        var model = Model();
        ClientTurn(model, "t1", "Port the parser");
        Assert.IsTrue(model.Busy);
        model.Take(Agent("Done", "a1"), live: true);
        model.Take(End("zed-9"), live: true); // another prompt's mark ends nothing here
        Assert.AreEqual("t1", model.State.ActiveTurn?.Id);
        model.Take(End("t1"), live: true);

        var turn = model.State.Turns.Single();
        Assert.AreEqual("t1", turn.Id);
        Assert.AreEqual("Port the parser", turn.Message.Text);
        Assert.AreEqual(TurnState.Complete, turn.State);
        Assert.AreEqual("Done", ((MarkdownResponsePart)turn.ResponseParts.Single().Value!).Content);
        Assert.IsFalse(model.Busy);
    }

    [TestMethod]
    public void ACancelledTurn_LeavesItsTailOut_UntilItsAgentMarksItsEnd()
    {
        var model = Model();
        ClientTurn(model, "t1", "Port the parser");
        model.Take(Agent("Working", "a1"), live: true);
        Reducers.ApplyToChat(model.State, new StateAction(new ChatTurnCancelledAction { TurnId = "t1", Duration = 5 }));
        model.ClientTurnCancelled();
        Assert.IsTrue(model.Settling);
        Assert.IsFalse(model.Busy, "over for its clients: a queued message may start");

        // The next client turn waits in the conductor for the cancelled one: what comes before its end is the tail.
        ClientTurn(model, "t2", "Then the tests");
        model.Take(Agent(" still", "a1"), live: true);
        model.Take(End("t1", stopReason: "cancelled"), live: true);
        Assert.IsFalse(model.Settling);
        model.Take(Agent("Tests pass", "a2"), live: true);
        model.Take(End("t2"), live: true);

        Assert.AreEqual(TurnState.Cancelled, model.State.Turns[0].State);
        Assert.AreEqual("Working", ((MarkdownResponsePart)model.State.Turns[0].ResponseParts.Single().Value!).Content);
        Assert.AreEqual("Tests pass", ((MarkdownResponsePart)model.State.Turns[1].ResponseParts.Single().Value!).Content, "the tail went to neither turn");
        Assert.AreEqual(TurnState.Complete, model.State.Turns[1].State);
    }

    [TestMethod]
    public void ATurnWhosePromptFailed_EndsWithTheError_AndOnlyThatTurn()
    {
        var model = Model();
        ClientTurn(model, "t1", "Port the parser");
        model.FailTurn("t0", -32603, "not this one");
        Assert.AreEqual("t1", model.State.ActiveTurn?.Id);
        model.FailTurn("t1", -32600, "The service restarts");
        var turn = model.State.Turns.Single();
        Assert.AreEqual(TurnState.Error, turn.State);
        StringAssert.Contains(((ErrorResponsePart)turn.ResponseParts.Last().Value!).Error.Message, "restarts");
    }

    [TestMethod]
    public void AnApproval_PutsItsToolCallBackToConfirmation_WithTheAgentsOptions_AndAWithdrawalLetsItRun()
    {
        var model = Model();
        ClientTurn(model, "t1", "Write the notes");
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "w1", ["title"] = "Write notes.md", ["kind"] = "edit", ["status"] = "pending",
            ["rawInput"] = new JsonObject { ["path"] = "notes.md" },
        }, live: true);
        Assert.IsInstanceOfType<ToolCallRunningState>(ToolCallOf(model, "w1"), "a tool call runs until its agent asks");

        List<ConfirmationOption> options =
        [
            new() { Id = "yes", Label = "Allow", Kind = ConfirmationOptionKind.Approve },
            new() { Id = "no", Label = "Reject", Kind = ConfirmationOptionKind.Deny },
        ];
        Assert.AreEqual("t1", model.Approval("w1", "Write notes.md", "edit", null, options));
        var pending = model.Pending("t1", "w1");
        Assert.IsNotNull(pending);
        CollectionAssert.AreEqual(new[] { "yes", "no" }, pending.Options!.Select(o => o.Id).ToArray());
        Assert.IsTrue(model.State.Status.HasFlag(SessionStatus.InputNeeded), "the chat waits for its client");

        model.Withdraw("t1", "w1");
        Assert.IsNull(model.Pending("t1", "w1"));
        Assert.IsInstanceOfType<ToolCallRunningState>(ToolCallOf(model, "w1"));
        Assert.IsFalse(model.State.Status.HasFlag(SessionStatus.InputNeeded));
    }

    [TestMethod]
    public void AnApproval_ForAToolCallTheAgentHadNotSaid_StartsIt()
    {
        var model = Model();
        ClientTurn(model, "t1", "Run it");
        var turn = model.Approval("x1", "Run tests", "execute", JsonNode.Parse("{\"command\":\"dotnet test\"}"),
            [new() { Id = "ok", Label = "Allow", Kind = ConfirmationOptionKind.Approve }]);
        Assert.AreEqual("t1", turn);
        var pending = model.Pending("t1", "x1")!;
        Assert.AreEqual("Run tests", pending.DisplayName);
        Assert.AreEqual("{\"command\":\"dotnet test\"}", pending.ToolInput!.Inline);
    }

    [TestMethod]
    public void AForm_IsAnInputRequest_AndAWithdrawnOneIsCancelled()
    {
        var model = Model();
        ClientTurn(model, "t1", "Plan it");
        var request = new ChatInputRequest
        {
            Id = "ask-1", Message = "Which way?",
            Questions = [new ChatInputQuestion(new ChatInputSingleSelectQuestion
            {
                Id = "way", Message = "Way", Kind = ChatInputQuestionKind.SingleSelect, Options = [new ChatInputOption { Id = "a", Label = "A" }],
            })],
        };
        Assert.AreEqual("t1", model.InputRequest(request));
        Assert.IsNotNull(model.OpenInput("ask-1"));
        Assert.IsTrue(model.State.Status.HasFlag(SessionStatus.InputNeeded));
        model.CancelInput("ask-1");
        Assert.IsNull(model.OpenInput("ask-1"));
        var part = model.State.ActiveTurn!.ResponseParts.Select(p => p.Value).OfType<InputRequestResponsePart>().Single();
        Assert.AreEqual(ChatInputResponseKind.Cancel, part.Response);
    }

    [TestMethod]
    public void TheAgentsContextUsage_IsTheTurnsUsage()
    {
        var model = Model();
        model.Take(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 1200, ["size"] = 200000 }, live: true);
        Assert.AreEqual(0, model.Applied!.Count, "usage outside a turn opens none");
        ClientTurn(model, "t1", "Go");
        model.Take(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 5300, ["size"] = 200000, ["cost"] = new JsonObject { ["amount"] = 0.02, ["currency"] = "USD" } }, live: true);
        var usage = model.State.ActiveTurn!.Usage!;
        Assert.AreEqual(5300L, usage.InputTokens);
        Assert.AreEqual(200000, usage.Meta!["gorynych/contextSize"].GetInt32());
        Assert.AreEqual("USD", usage.Meta["gorynych/cost"].GetProperty("currency").GetString());
    }

    static object? ToolCallOf(ChatModel model, string id) =>
        model.State.ActiveTurn!.ResponseParts.Select(p => p.Value).OfType<ToolCallResponsePart>().Select(p => p.ToolCall.Value)
            .Single(t => t switch { ToolCallRunningState r => r.ToolCallId == id, ToolCallPendingConfirmationState c => c.ToolCallId == id, _ => false });

    // ---------------------------------------------------------------- options

    static JsonElement Str(string text) => JsonDocument.Parse($"\"{text}\"").RootElement.Clone();

    static JsonArray Options() => JsonNode.Parse("""
        [
          {"type":"select","id":"model","name":"Model","category":"model","currentValue":"small",
           "options":[{"value":"small","name":"Small","description":"Quick"},{"value":"large","name":"Large"}]},
          {"type":"select","id":"mode","name":"Permissions","category":"mode","currentValue":"ask",
           "options":[{"group":"g","name":"Usual","options":[{"value":"ask","name":"Ask"}]},{"value":"full","name":"Full access"}]},
          {"type":"boolean","id":"thinking","name":"Thinking","currentValue":true}
        ]
        """)!.AsArray();

    [TestMethod]
    public void AnAgentsOptions_AreTheSessionsConfig_ButTheModel_WhichIsAhpsOwn()
    {
        var config = AhpConfig.Of(Options(), modes: null, out var modesAsMode)!;
        Assert.IsFalse(modesAsMode);
        CollectionAssert.AreEquivalent(new[] { "mode", "thinking" }, config.Schema.Properties.Keys.ToArray());
        var mode = config.Schema.Properties["mode"];
        CollectionAssert.AreEqual(new[] { "ask", "full" }, mode.Enum!.Select(e => e.GetString()).ToArray(), "groups flattened");
        CollectionAssert.AreEqual(new[] { "Ask", "Full access" }, mode.EnumLabels!.ToArray());
        Assert.IsTrue(mode.SessionMutable);
        Assert.AreEqual("ask", config.Values["mode"].GetString());
        Assert.IsTrue(config.Values["thinking"].GetBoolean());
        Assert.AreEqual("boolean", config.Schema.Properties["thinking"].Type);

        Assert.IsTrue(AhpConfig.Takes(config, "mode", Str("full")));
        Assert.IsFalse(AhpConfig.Takes(config, "mode", Str("root")));
        Assert.IsFalse(AhpConfig.Takes(config, "model", Str("large")), "the model is not a config property");

        var model = AhpConfig.Model(Options())!;
        Assert.AreEqual("model", model.ConfigId);
        Assert.AreEqual("small", model.Current);
        CollectionAssert.AreEqual(new[] { "small", "large" }, model.Models.Select(m => m.Value).ToArray());
        Assert.AreEqual("Quick", model.Models[0].Description);
    }

    [TestMethod]
    public void AnAgentWithModesOnly_HasThemAsTheModeProperty()
    {
        var modes = JsonNode.Parse("""{"currentModeId":"default","availableModes":[{"id":"default","name":"Default"},{"id":"plan","name":"Plan","description":"Read only"}]}""")!.AsObject();
        var config = AhpConfig.Of(options: null, modes, out var modesAsMode)!;
        Assert.IsTrue(modesAsMode);
        Assert.AreEqual("default", config.Values[AhpConfig.ModeKey].GetString());
        CollectionAssert.AreEqual(new[] { "", "Read only" }, config.Schema.Properties[AhpConfig.ModeKey].EnumDescriptions!.ToArray());
        Assert.IsNull(AhpConfig.Of(null, null, out _), "nothing to choose: no config");
        Assert.IsNull(AhpConfig.Of(Options(), modes, out var both)!.Schema.Properties[AhpConfig.ModeKey].EnumDescriptions, "a config option of category mode wins");
        Assert.IsFalse(both);
    }
}
