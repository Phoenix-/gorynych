using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Service.Ahp;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Tests;

/// <summary>The AHP face's mapping (docs/ahp-face.md, "Chats"): ACP updates in, AHP actions and the reduced chat state out.</summary>
[TestClass]
public sealed class AhpChatModelTests
{
    static ChatModel Model(Func<DateTimeOffset>? now = null, bool live = true)
    {
        var model = new ChatModel(new ChatState
        {
            Resource = "ahp-chat:/s1", Title = "t", Status = SessionStatus.Idle | SessionStatus.IsRead, ModifiedAt = "2026-11-13T09:00:00.000Z",
            Turns = [], Interactivity = ChatInteractivity.ReadOnly,
        })
        {
            ReplayTime = "2026-11-13T09:00:00.000Z",
            Now = now ?? (() => new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero)),
            Applied = [],
        };
        if (live) model.Emit = _ => { };
        return model;
    }

    static JsonObject User(string text, string messageId, JsonObject? meta = null) => Update("user_message_chunk", text, messageId, meta);

    static JsonObject Agent(string text, string messageId, string? position = null) =>
        Update("agent_message_chunk", text, messageId, position is null ? null : new JsonObject { [GorynychProtocol.MetaPosition] = position });

    static JsonObject Thought(string text, string messageId) => Update("agent_thought_chunk", text, messageId, null);

    static JsonObject Update(string kind, string text, string messageId, JsonObject? meta)
    {
        var u = new JsonObject { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text }, ["messageId"] = messageId };
        if (meta is not null) u["_meta"] = meta;
        return u;
    }

    static JsonObject End(string? promptId = null, string stopReason = "end_turn", JsonObject? error = null)
    {
        var meta = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "ended" };
        if (promptId is not null) meta[GorynychProtocol.MetaPromptId] = promptId;
        if (error is not null) meta[GorynychProtocol.MetaError] = error;
        else meta[GorynychProtocol.MetaStopReason] = stopReason;
        return new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = meta };
    }

    static string[] Types(ChatModel model) => [.. model.Applied!.Select(a => a.Value!.GetType().Name.Replace("Action", ""))];

    [TestMethod]
    public void AClientsPrompt_IsATurn_ByItsPromptId_WithItsMessageText_PartsAndToolCall()
    {
        var model = Model();
        model.Take(User("Port ", "u1", new JsonObject { [GorynychProtocol.MetaPromptId] = "zed-1" }), live: true);
        model.Take(User("the parser", "u1"), live: true);
        Assert.IsTrue(model.Holding, "the message's chunks are held");
        Assert.AreEqual(0, model.Applied!.Count);
        model.Take(Agent("Look", "a1"), live: true);
        model.Take(Agent("ing.", "a1"), live: true);
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "c1", ["title"] = "Read parser.rs", ["kind"] = "read", ["status"] = "in_progress",
            ["rawInput"] = new JsonObject { ["path"] = "parser.rs" },
        }, live: true);
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "c1", ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "fn parse()" } }),
        }, live: true);
        model.Take(Agent("Done.", "a1"), live: true);
        model.Take(End("zed-1"), live: true);

        CollectionAssert.AreEqual(new[]
        {
            "ChatTurnStarted", "ChatResponsePart", "ChatDelta", "ChatToolCallStart", "ChatToolCallReady", "ChatToolCallComplete", "ChatResponsePart", "ChatTurnComplete",
        }, Types(model));
        var turn = model.State.Turns.Single();
        Assert.AreEqual("zed-1", turn.Id);
        Assert.AreEqual("Port the parser", turn.Message.Text);
        Assert.AreEqual(MessageKind.User, turn.Message.Origin.Kind);
        Assert.AreEqual(TurnState.Complete, turn.State);
        var parts = turn.ResponseParts.Select(p => p.Value).ToList();
        Assert.AreEqual("Looking.", ((MarkdownResponsePart)parts[0]!).Content);
        Assert.AreEqual("a1", ((MarkdownResponsePart)parts[0]!).Id);
        var tool = (ToolCallCompletedState)((ToolCallResponsePart)parts[1]!).ToolCall.Value!;
        Assert.IsTrue(tool.Success);
        Assert.AreEqual("read", tool.ToolName);
        Assert.AreEqual("Read parser.rs", tool.DisplayName);
        Assert.AreEqual("fn parse()", ((ToolResultTextContent)tool.Content![0].Value!).Text);
        Assert.AreEqual("a1#2", ((MarkdownResponsePart)parts[2]!).Id, "the same message after another part is a part of its own");
        Assert.AreEqual("Done.", ((MarkdownResponsePart)parts[2]!).Content);
        Assert.IsNull(model.State.ActiveTurn);
        Assert.IsFalse(model.Busy);
    }

    [TestMethod]
    public void AnEndMark_EndsOnlyTheTurnItIsFor()
    {
        var model = Model();
        model.Take(User("One", "u1", new JsonObject { [GorynychProtocol.MetaPromptId] = "p1" }), live: true);
        model.Take(Agent("Working", "a1"), live: true);

        model.Take(End("p0"), live: true);
        Assert.AreEqual("p1", model.OpenTurn, "the late mark of an earlier prompt ends nothing");
        model.Take(End(), live: true);
        Assert.AreEqual("p1", model.OpenTurn, "the end of a turn of the agent's own is not the end of a prompt's");
        model.Take(End("p1"), live: true);
        Assert.IsNull(model.OpenTurn);
        Assert.AreEqual(TurnState.Complete, model.State.Turns.Single().State);

        // The agent's own turn, begun by its mark, is ended by its mark, not by a prompt's.
        model.Take(new JsonObject { ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "started" } }, live: true);
        model.Take(End("p1"), live: true);
        Assert.IsNotNull(model.OpenTurn);
        Assert.IsFalse(model.Unmarked, "a marked turn: its mark ends it");
        model.EndUnmarked(live: true);
        Assert.IsNotNull(model.OpenTurn, "quiet does not end it");
        model.Take(End(), live: true);
        Assert.IsNull(model.OpenTurn);
    }

    [TestMethod]
    public void ALateUpdate_AfterItsTurnEnded_BeginsATurnNoMarkEnds_WhichQuietEnds()
    {
        var model = Model();
        model.Take(User("One", "u1", new JsonObject { [GorynychProtocol.MetaPromptId] = "p1" }), live: true);
        model.Take(Agent("Done", "a1"), live: true);
        model.Take(End("p1"), live: true);

        model.Take(Agent(" and one more thing", "a1"), live: true);
        Assert.IsTrue(model.Unmarked, "an update outside any turn, with no mark of its start");
        Assert.IsTrue(model.Busy);
        model.Take(End("p1"), live: true);
        Assert.IsTrue(model.Unmarked, "the prompt's mark again is not its end");
        model.EndUnmarked(live: true);
        Assert.IsFalse(model.Busy);
        Assert.AreEqual(2, model.State.Turns.Count);
        Assert.IsTrue(model.State.Turns.All(t => t.State == TurnState.Complete));
    }

    [TestMethod]
    public void AnUpdateOfTheAgentWithNoTurnOpen_BeginsATurnOfItsOwn()
    {
        var model = Model();
        model.Take(Agent("I noticed", "a1", position: "41"), live: true);
        model.Take(End(), live: true);

        var turn = model.State.Turns.Single();
        Assert.AreEqual("agent-41", turn.Id);
        Assert.AreEqual(MessageKind.Agent, turn.Message.Origin.Kind);
        Assert.AreEqual("", turn.Message.Text);

        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "session_info_update", ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgentTurn] = "started", [GorynychProtocol.MetaPosition] = "50" },
        }, live: true);
        Assert.AreEqual("agent-50", model.OpenTurn, "a started mark begins one");
    }

    [TestMethod]
    [DataRow("cancelled", null, TurnState.Cancelled)]
    [DataRow(null, -32603, TurnState.Error)]
    [DataRow("end_turn", null, TurnState.Complete)]
    [DataRow("max_tokens", null, TurnState.Complete)]
    public void TheEndMark_EndsTheTurn_ByItsStopReasonOrError(string? stopReason, int? errorCode, TurnState expected)
    {
        var model = Model();
        model.Take(User("Go", "u1"), live: true);
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "c1", ["title"] = "Run tests", ["kind"] = "execute", ["status"] = "in_progress",
        }, live: true);
        model.Take(End(stopReason: stopReason ?? "end_turn", error: errorCode is { } code ? new JsonObject { ["code"] = code, ["message"] = "boom" } : null), live: true);

        var turn = model.State.Turns.Single();
        Assert.AreEqual(expected, turn.State);
        if (errorCode is not null)
        {
            var error = turn.ResponseParts.Select(p => p.Value).OfType<ErrorResponsePart>().Single().Error;
            Assert.AreEqual("-32603", error.ErrorType);
            Assert.AreEqual("boom", error.Message);
        }
        Assert.IsNull(model.State.ActiveTurn, "the turn is over");
    }

    [TestMethod]
    public void ANewUserMessage_EndsTheTurnBeforeIt_AsComplete()
    {
        var model = Model();
        model.Take(User("One", "u1"), live: true);
        model.Take(Agent("1", "a1"), live: true);
        model.Take(User("Two", "u2"), live: true);
        model.Flush(live: true);

        Assert.AreEqual(1, model.State.Turns.Count);
        Assert.AreEqual(TurnState.Complete, model.State.Turns[0].State);
        Assert.AreEqual("u2", model.State.ActiveTurn!.Id);
        Assert.AreEqual("Two", model.State.ActiveTurn.Message.Text);
    }

    [TestMethod]
    public void WhoWroteAMessage_IsItsOrigin_AndTheServicesMarksStayInItsMeta()
    {
        var model = Model();
        model.Take(User("SQLite or Postgres?", "u1", new JsonObject
        {
            [GorynychProtocol.MetaOrigin] = "peer", [GorynychProtocol.MetaSender] = "peer_claude", [GorynychProtocol.MetaFromSessionId] = "s0",
            [GorynychProtocol.MetaOpinion] = "m1", [GorynychProtocol.MetaPromptId] = "service-u1",
        }), live: true);
        model.Take(End("service-u1"), live: true);
        model.Take(User("The service's note", "u2", new JsonObject { [GorynychProtocol.MetaOrigin] = "notice" }), live: true);
        model.Take(User("Mine", "u3", new JsonObject { [GorynychProtocol.MetaOrigin] = "terminal" }), live: true);
        model.Flush(live: true);

        var asked = model.State.Turns[0];
        Assert.AreEqual("service-u1", asked.Id);
        Assert.AreEqual(MessageKind.Agent, asked.Message.Origin.Kind);
        Assert.AreEqual("peer_claude", asked.Message.Meta![GorynychProtocol.MetaSender].GetString());
        Assert.AreEqual("s0", asked.Message.Meta[GorynychProtocol.MetaFromSessionId].GetString());
        Assert.AreEqual("m1", asked.Message.Meta[GorynychProtocol.MetaOpinion].GetString());
        Assert.AreEqual(MessageKind.SystemNotification, model.State.Turns[1].Message.Origin.Kind);
        Assert.AreEqual(MessageKind.User, model.State.ActiveTurn!.Message.Origin.Kind);
    }

    [TestMethod]
    public void AThought_IsAReasoningPart_ThenItsMoreReasoning()
    {
        var model = Model();
        model.Take(User("Think", "u1"), live: true);
        model.Take(Thought("Hm", "t1"), live: true);
        model.Take(Thought("m.", "t1"), live: true);
        model.Take(End(), live: true);

        CollectionAssert.AreEqual(new[] { "ChatTurnStarted", "ChatResponsePart", "ChatReasoning", "ChatTurnComplete" }, Types(model));
        Assert.AreEqual("Hmm.", ((ReasoningResponsePart)model.State.Turns[0].ResponseParts[0].Value!).Content);
    }

    [TestMethod]
    public void APlan_IsTheChatsActivity_ClearedWhenTheTurnEnds()
    {
        var model = Model();
        model.Take(User("Plan", "u1"), live: true);
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "plan",
            ["entries"] = new JsonArray(
                new JsonObject { ["content"] = "Read the code", ["status"] = "completed", ["priority"] = "high" },
                new JsonObject { ["content"] = "Write the tests", ["status"] = "in_progress", ["priority"] = "high" }),
        }, live: true);
        Assert.AreEqual("Write the tests", model.State.Activity);
        model.Take(End(), live: true);
        Assert.IsNull(model.State.Activity);
    }

    [TestMethod]
    public void ADiffAndATerminal_AreSaidInText_ForNow()
    {
        var model = Model();
        model.Take(User("Edit", "u1"), live: true);
        model.Take(new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "c1", ["title"] = "Edit a.txt", ["kind"] = "edit", ["status"] = "completed",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "diff", ["path"] = @"C:\w\a.txt", ["oldText"] = "one", ["newText"] = "two" },
                new JsonObject { ["type"] = "terminal", ["terminalId"] = "t7" }),
        }, live: true);

        var tool = (ToolCallCompletedState)((ToolCallResponsePart)model.State.ActiveTurn!.ResponseParts[0].Value!).ToolCall.Value!;
        var texts = tool.Content!.Select(c => ((ToolResultTextContent)c.Value!).Text).ToList();
        Assert.AreEqual("Edited C:\\w\\a.txt\n```diff\n-one\n+two\n```", texts[0]);
        Assert.AreEqual("Terminal t7", texts[1]);
    }

    [TestMethod]
    public void AReplay_BuildsTheTurns_UnnumberedAtTheSessionsTime_AndALoadThatSaysIdleEndsTheLastOne()
    {
        var model = Model(live: false);
        model.Take(User("One", "u1"), live: false);
        model.Take(Agent("1", "a1"), live: false);
        model.Take(End(), live: false);
        model.Take(User("Two", "u2"), live: false);
        model.Take(Agent("2", "a2"), live: false);
        model.Flush(live: false);
        Assert.IsTrue(model.Busy, "the replay ended in a turn");
        model.EndOpenTurn(live: false);

        Assert.AreEqual(2, model.State.Turns.Count);
        Assert.IsTrue(model.State.Turns.All(t => t.StartedAt == "2026-11-13T09:00:00.000Z" && t.Duration == 0), "the journal keeps no times");
        Assert.AreEqual("2026-11-13T09:00:00.000Z", model.State.ModifiedAt);
        Assert.IsFalse(model.Busy);
        Assert.AreEqual(SessionStatus.Idle, model.State.Status & ~SessionStatus.IsRead, "idle");
        Assert.IsFalse(model.State.Status.HasFlag(SessionStatus.IsRead), "a replayed turn clears the read bit: the face sets it back");
    }

    [TestMethod]
    public void ALiveTurn_HasItsTimeAndDuration()
    {
        var now = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        var model = Model(() => now);
        model.Take(User("Go", "u1"), live: true);
        model.Flush(live: true);
        now = now.AddSeconds(3);
        model.Take(End(), live: true);

        Assert.AreEqual("2026-11-13T10:00:00.000Z", model.State.Turns[0].StartedAt);
        Assert.AreEqual(3000, model.State.Turns[0].Duration);
    }
}
