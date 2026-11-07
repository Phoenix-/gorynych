using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Adapters.Claude;
using Gorynych.Adapters.Claude.Pty;

namespace Gorynych.Tests;

/// <summary>Hook payloads recorded from Claude Code 2.1.302 on Windows (probe 1 and 3), mapped to ACP.</summary>
[TestClass]
public sealed class ClaudeHooksTests
{
    static List<JsonObject> Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude", name))
            .Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    static JsonObject First(List<JsonObject> events, string name, string? tool = null) =>
        events.First(e => ClaudeHooks.Name(e) == name && (tool is null || (string?)e["tool_name"] == tool));

    [TestMethod]
    public void PreToolUse_BecomesAPendingToolCall()
    {
        var events = Fixture("hooks-write-and-question.jsonl");
        var call = ClaudeHooks.ToolCall(First(events, "PreToolUse", "Write"));
        Assert.AreEqual("Write hello.txt", call.Title);
        Assert.AreEqual(ToolKind.Edit, call.Kind);
        Assert.AreEqual(ToolCallStatus.Pending, call.Status);
        Assert.IsTrue(call.ToolCallId.StartsWith("toolu_", StringComparison.Ordinal));
        StringAssert.EndsWith(call.Locations![0].Path, "hello.txt");
        Assert.AreEqual("tool_call", (string?)AcpJson.ToNode<SessionUpdate>(call)!["sessionUpdate"]);
    }

    [TestMethod]
    public void PostToolUse_CompletesTheSameToolCall()
    {
        var events = Fixture("hooks-write-and-question.jsonl");
        var pre = ClaudeHooks.ToolCall(First(events, "PreToolUse", "Write"));
        var post = ClaudeHooks.ToolCallFinished(First(events, "PostToolUse", "Write"), failed: false);
        Assert.AreEqual(pre.ToolCallId, post.ToolCallId);
        Assert.AreEqual(ToolCallStatus.Completed, post.Status);
        Assert.AreEqual("create", (string?)post.RawOutput!["type"]);
    }

    [TestMethod]
    public void BashOutput_IsShownAsToolCallContent()
    {
        var events = Fixture("hooks-channel.jsonl");
        var pre = ClaudeHooks.ToolCall(First(events, "PreToolUse", "Bash"));
        Assert.AreEqual(ToolKind.Execute, pre.Kind);
        Assert.AreEqual("Sleep 15 seconds then echo slept", pre.Title);
        var post = ClaudeHooks.ToolCallFinished(First(events, "PostToolUse", "Bash"), failed: false);
        Assert.AreEqual("slept", ((TextContent)((Content)post.Content![0]).ContentValue).Text);
    }

    [TestMethod]
    public void PermissionRequest_BecomesAnAcpApproval_AndTheAnswerAHookDecision()
    {
        var events = Fixture("hooks-write-and-question.jsonl");
        var request = ClaudeHooks.Permission("s1", "toolu_x", First(events, "PermissionRequest", "Write"));
        Assert.AreEqual("s1", request.SessionId);
        Assert.AreEqual("Write hello.txt", request.ToolCall.Title);
        CollectionAssert.AreEqual(new[] { "allow_once", "reject_once" }, request.Options.Select(o => o.Kind.Value).ToArray());

        var allow = new SelectedPermissionOutcome { OptionId = ClaudeHooks.AllowOptionId };
        Assert.IsTrue(ClaudeHooks.IsAllowed(allow));
        Assert.AreEqual("""{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}""",
            ClaudeHooks.AllowReply().ToJsonString());
        Assert.IsFalse(ClaudeHooks.IsAllowed(new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId }));
        Assert.IsFalse(ClaudeHooks.IsAllowed(new RequestPermissionOutcomeCancelled()));
        Assert.AreEqual("deny", (string?)ClaudeHooks.DenyReply("no")["hookSpecificOutput"]!["decision"]!["behavior"]);
    }

    [TestMethod]
    public void AskUserQuestion_OffersTheAnswers_AndRepliesWithUpdatedInput()
    {
        var events = Fixture("hooks-write-and-question.jsonl");
        var permission = First(events, "PermissionRequest", "AskUserQuestion");
        var question = permission["tool_input"]!["questions"]![0]!.AsObject();
        var request = ClaudeHooks.Question("s1", "toolu_q", question);
        Assert.AreEqual("Which color do you prefer?", request.ToolCall.Title);
        CollectionAssert.AreEqual(new[] { "Red", "Blue", "Do not answer" }, request.Options.Select(o => o.Name).ToArray());

        var label = ClaudeHooks.ChosenLabel(question, new SelectedPermissionOutcome { OptionId = request.Options[1].OptionId });
        Assert.AreEqual("Blue", label);
        Assert.IsNull(ClaudeHooks.ChosenLabel(question, new SelectedPermissionOutcome { OptionId = ClaudeHooks.DenyOptionId }));

        // The probe's listener answered with the same shape, and Claude took it (PostToolUse carries the answer).
        var post = First(events, "PostToolUse", "AskUserQuestion");
        Assert.AreEqual("Blue", (string?)post["tool_response"]!["answers"]!["Which color do you prefer?"]);
    }

    [TestMethod]
    public void MessageDisplay_IsAnAgentMessageChunk()
    {
        var events = Fixture("hooks-plain-turn.jsonl");
        var chunk = ClaudeHooks.MessageChunk(First(events, "MessageDisplay"));
        StringAssert.StartsWith(((TextContent)chunk.Content).Text, "Sailboats");
        Assert.IsNotNull(chunk.MessageId);
        StringAssert.StartsWith((string?)First(events, "Stop")["last_assistant_message"], "Sailboats");
    }

    [TestMethod]
    public void ChannelMessages_AreRecognised_AndTheReplyToolHasAReadableTitle()
    {
        var events = Fixture("hooks-channel.jsonl");
        var prompts = events.Where(e => ClaudeHooks.Name(e) == "UserPromptSubmit").Select(e => (string)e["prompt"]!).ToList();
        CollectionAssert.AreEqual(new[] { false, true, true, false }, prompts.Select(ClaudeHooks.IsChannelMessage).ToArray());
        var foreign = ClaudeHooks.ForeignPrompt(prompts[1]);
        Assert.AreEqual("channel", (string?)foreign.Meta!["gorynych/origin"]);
        var reply = ClaudeHooks.ToolCall(First(events, "PreToolUse", "mcp__probechan__reply"));
        Assert.AreEqual("reply (probechan)", reply.Title);
    }

    [TestMethod]
    public void Settings_SubscribeEveryEventOverHttp()
    {
        var settings = ClaudeHooks.Settings("http://127.0.0.1:1/hook/s/t");
        foreach (var name in ClaudeHooks.Events)
        {
            var hook = settings["hooks"]![name]![0]!["hooks"]![0]!;
            Assert.AreEqual("http", (string?)hook["type"]);
            Assert.AreEqual("http://127.0.0.1:1/hook/s/t", (string?)hook["url"]);
        }
        Assert.AreEqual(86400, (int)settings["hooks"]!["PermissionRequest"]![0]!["hooks"]![0]!["timeout"]!);
    }

    [TestMethod]
    public void TranscriptTitles_PreferTheChosenName()
    {
        var t = new TranscriptTitles();
        Assert.IsFalse(t.Feed("""{"type":"user","message":"x"}"""));
        Assert.IsTrue(t.Feed("""{"type":"ai-title","aiTitle":"Hookprobe file write and color question","sessionId":"s"}"""));
        Assert.AreEqual("Hookprobe file write and color question", t.Title);
        Assert.IsTrue(t.Feed("""{"type":"custom-title","customTitle":"Меню Фара","sessionId":"s"}"""));
        Assert.IsFalse(t.Feed("""{"type":"ai-title","aiTitle":"Something newer","sessionId":"s"}"""));
        Assert.AreEqual("Меню Фара", t.Title);
    }

    [TestMethod]
    public void TranscriptPath_FollowsClaudesFolderNaming()
    {
        var path = TranscriptTitles.ExpectedPath(@"D:\gorynych\.claude\worktrees\mvp-slice-42ca4b", "abc");
        StringAssert.EndsWith(path, @"\.claude\projects\D--gorynych--claude-worktrees-mvp-slice-42ca4b\abc.jsonl");
    }

    [TestMethod]
    [DataRow("plain")]
    [DataRow("two words")]
    [DataRow("say \"hi\"")]
    [DataRow(@"C:\dir with space\")]
    [DataRow("line one\nline two")]
    [DataRow("")]
    [DataRow(@"back\\slash""quote")]
    public void CommandLineQuoting_RoundTrips(string arg)
    {
        var line = "app.exe " + TerminalText.Quote(arg);
        CollectionAssert.AreEqual(new[] { "app.exe", arg }, CommandLineToArgv(line));
    }

    [TestMethod]
    public void ScreenTail_TracksBracketedPaste_AndStripsEscapes()
    {
        var screen = new ScreenTail();
        screen.Append("\x1b]0;title\x07\x1b[1mWARNING\x1b[0m: development channels\x1b[?20"u8);
        Assert.IsFalse(screen.BracketedPaste);
        screen.Append("04h ready"u8);
        Assert.IsTrue(screen.BracketedPaste);
        StringAssert.Contains(screen.Text, "WARNING : development channels");
        Assert.IsFalse(screen.Text.Contains('\x1b'));
    }

    static string[] CommandLineToArgv(string line)
    {
        var argv = Native.CommandLineToArgvW(line, out var count);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally { Native.LocalFree(argv); }
    }
}

static partial class Native
{
    [System.Runtime.InteropServices.LibraryImport("shell32.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    internal static partial nint CommandLineToArgvW(string line, out int count);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);
}
