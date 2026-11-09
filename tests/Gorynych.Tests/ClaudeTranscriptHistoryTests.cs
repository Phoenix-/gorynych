using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// Claude's own history of a session from its transcript (TranscriptHistory): the conversation
/// after the service's mark, read from the mark on, mapped as the live stream maps it; all of it
/// when a rewind made another branch the conversation. Transcripts in Claude's shape, written here.
/// </summary>
[TestClass]
public sealed class ClaudeTranscriptHistoryTests
{
    static string NewTranscript() => Path.Combine(TestDirs.New(), "session.jsonl");

    static void Add(string path, params JsonObject[] entries) =>
        File.AppendAllText(path, string.Concat(entries.Select(e => e.ToJsonString() + "\n")));

    static JsonObject User(string uuid, string? parent, string text, bool meta = false, bool summary = false)
    {
        var e = new JsonObject
        {
            ["type"] = "user", ["uuid"] = uuid, ["parentUuid"] = parent, ["isSidechain"] = false,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        if (meta) e["isMeta"] = true;
        if (summary) e["isCompactSummary"] = true;
        return e;
    }

    static JsonObject Assistant(string uuid, string? parent, string id, params JsonObject[] blocks) => new()
    {
        ["type"] = "assistant", ["uuid"] = uuid, ["parentUuid"] = parent, ["isSidechain"] = false,
        ["message"] = new JsonObject { ["id"] = id, ["role"] = "assistant", ["content"] = new JsonArray(blocks) },
    };

    static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    static JsonObject ToolResult(string uuid, string? parent, string toolUseId, string output) => new()
    {
        ["type"] = "user", ["uuid"] = uuid, ["parentUuid"] = parent, ["isSidechain"] = false,
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = output }),
        },
    };

    static string[] Seen(IEnumerable<SessionUpdate> updates) => updates.Select(u => u switch
    {
        SessionUpdateUserMessageChunk m => $"user {m.MessageId}: {((TextContent)m.Content).Text}",
        SessionUpdateAgentMessageChunk m => $"agent {m.MessageId}: {((TextContent)m.Content).Text}",
        SessionUpdateAgentThoughtChunk m => $"thought {m.MessageId}: {((TextContent)m.Content).Text}",
        ToolCall c => $"tool {c.ToolCallId}",
        SessionUpdateToolCallUpdate c => $"result {c.ToolCallId} {c.Status?.Value}",
        _ => u.GetType().Name,
    }).ToArray();

    [TestMethod]
    public void AfterTheMark_OnlyWhatFollowsIsRead_AndMappedAsTheLiveStreamMapsIt()
    {
        var path = NewTranscript();
        // A long conversation before the mark, which a catch-up must not read again.
        string? parent = null;
        for (var i = 0; i < 400; i++)
        {
            Add(path, User($"old-u{i}", parent, new string('x', 400)), Assistant($"old-a{i}", $"old-u{i}", $"msg_old{i}", Text("ok")));
            parent = $"old-a{i}";
        }
        Add(path, User("u1", parent, "First"), Assistant("a1", "u1", "msg_1", Text("Done")));
        var mark = TranscriptHistory.Mark(path)!;
        var before = new FileInfo(path).Length;
        StringAssert.StartsWith(mark, "a1@");
        Add(path,
            User("u2", "a1", "Again, in the terminal"),
            Assistant("a2", "u2", "msg_2", new JsonObject { ["type"] = "thinking", ["thinking"] = "Let me look" }),
            Assistant("a3", "a2", "msg_2", new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_1", ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "ls" } }),
            ToolResult("u3", "a3", "toolu_1", "file.txt"),
            Assistant("a4", "u3", "msg_3", Text("There is one file.")),
            new JsonObject { ["type"] = "ai-title", ["aiTitle"] = "Files" }); // not part of the conversation

        var replay = TranscriptHistory.Read(path, mark)!;

        Assert.IsTrue(replay.FromMark);
        CollectionAssert.AreEqual(new[]
        {
            "user u2: Again, in the terminal", "thought msg_2: Let me look", "tool toolu_1", "result toolu_1 completed", "agent msg_3: There is one file.",
        }, Seen(replay.Updates));
        StringAssert.StartsWith(replay.Mark, "a4@");
        Assert.IsTrue(replay.BytesRead < new FileInfo(path).Length - before + 1000, $"read {replay.BytesRead} bytes: the mark's line and what follows");
        Assert.AreEqual(replay.Mark, TranscriptHistory.Mark(path), "the new mark is the transcript's mark");
    }

    [TestMethod]
    public void NothingNew_KeepsTheMark()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")));
        var mark = TranscriptHistory.Mark(path)!;

        var replay = TranscriptHistory.Read(path, mark)!;

        Assert.AreEqual((true, mark, 0), (replay.FromMark, replay.Mark, replay.Updates.Count));
    }

    [TestMethod]
    public void ARewind_MakesAnotherBranchTheConversation_SoAllOfItIsReplayed()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")), User("u2", "a1", "Second"), Assistant("a2", "u2", "msg_2", Text("Also done")));
        var mark = TranscriptHistory.Mark(path)!;
        Add(path, User("u3", "a1", "Instead"), Assistant("a3", "u3", "msg_3", Text("Another answer"))); // rewound to a1 in Claude Code

        var replay = TranscriptHistory.Read(path, mark)!;

        Assert.IsFalse(replay.FromMark);
        CollectionAssert.AreEqual(new[] { "user u1: First", "agent msg_1: Done", "user u3: Instead", "agent msg_3: Another answer" }, Seen(replay.Updates));
        StringAssert.StartsWith(replay.Mark, "a3@");
    }

    [TestMethod]
    public void ACompaction_IsCrossedByItsLogicalParent_AndItsSummaryIsNotShown()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")));
        var mark = TranscriptHistory.Mark(path)!;
        Add(path,
            new JsonObject { ["type"] = "system", ["subtype"] = "compact_boundary", ["uuid"] = "b1", ["parentUuid"] = null, ["logicalParentUuid"] = "a1", ["isSidechain"] = false },
            User("s1", "b1", "This session is being continued from a previous conversation…", summary: true),
            User("u2", "s1", "Go on"),
            Assistant("a2", "u2", "msg_2", Text("Going on")));

        var replay = TranscriptHistory.Read(path, mark)!;

        Assert.IsTrue(replay.FromMark);
        CollectionAssert.AreEqual(new[] { "user u2: Go on", "agent msg_2: Going on" }, Seen(replay.Updates));
    }

    [TestMethod]
    public void SubagentsCommandsAndNotesClaudeWroteItself_AreLeftOut()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")));
        var mark = TranscriptHistory.Mark(path)!;
        var sidechain = Assistant("x1", "a1", "msg_sub", Text("a subagent's work"));
        sidechain["isSidechain"] = true;
        Add(path,
            User("c1", "a1", "<command-name>/model</command-name>"),
            User("c2", "c1", "<local-command-stdout>Set model to opus</local-command-stdout>"),
            User("m1", "c2", "Caveat: the messages below were generated by the user", meta: true),
            User("u2", "m1", "Real question"),
            User("i1", "u2", "[Request interrupted by user]"),
            Assistant("a2", "i1", "msg_2", Text("Answer")),
            sidechain); // after the conversation's last entry: not its end

        var replay = TranscriptHistory.Read(path, mark)!;

        CollectionAssert.AreEqual(new[] { "user u2: Real question", "agent msg_2: Answer" }, Seen(replay.Updates));
        StringAssert.StartsWith(replay.Mark, "a2@");
    }

    [TestMethod]
    public void WithoutAMark_TheWholeConversation_AndATornLastLineIsLeftOut()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")));
        File.AppendAllText(path, "{\"type\":\"user\",\"uuid\":\"u2\",\"parentUuid\":\"a1\",\"mess"); // being written

        var replay = TranscriptHistory.Read(path, null)!;

        Assert.IsFalse(replay.FromMark);
        CollectionAssert.AreEqual(new[] { "user u1: First", "agent msg_1: Done" }, Seen(replay.Updates));
        StringAssert.StartsWith(replay.Mark, "a1@");
        Assert.IsNull(TranscriptHistory.Read(Path.Combine(TestDirs.New(), "missing.jsonl"), null), "no transcript: not known");
        Assert.IsNull(TranscriptHistory.Mark(Path.Combine(TestDirs.New(), "missing.jsonl")));
    }

    [TestMethod]
    public void AMarkWhoseLineMoved_IsFoundInTheFile()
    {
        var path = NewTranscript();
        Add(path, User("u1", null, "First"), Assistant("a1", "u1", "msg_1", Text("Done")), User("u2", "a1", "Next"), Assistant("a2", "u2", "msg_2", Text("Ok")));

        var replay = TranscriptHistory.Read(path, "a1@3")!; // a1, though its line is not at 3

        Assert.IsTrue(replay.FromMark);
        CollectionAssert.AreEqual(new[] { "user u2: Next", "agent msg_2: Ok" }, Seen(replay.Updates));
    }
}
