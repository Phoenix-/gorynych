using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

/// <summary>
/// The pure parts of the headless transport: stream-json → ACP, the options, and folder trust.
/// The JSON lines are shaped as Claude Code 2.1.302 wrote them in the probes
/// (research/probes/claude-headless), with paths shortened.
/// </summary>
[TestClass]
public sealed class StreamJsonTests
{
    static JsonObject J(string json) => (JsonObject)JsonNode.Parse(json)!;

    [TestMethod]
    public void Deltas_BecomeChunksOfTheirMessage()
    {
        Assert.AreEqual("msg_01A", StreamJson.MessageStartId(J("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_01A","role":"assistant"}}}""")));
        var text = StreamJson.Delta(J("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"pong"}}}"""), "msg_01A");
        Assert.AreEqual("pong", ((TextContent)((SessionUpdateAgentMessageChunk)text!).Content).Text);
        Assert.AreEqual("msg_01A", ((SessionUpdateAgentMessageChunk)text).MessageId);
        var thought = StreamJson.Delta(J("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hm"}}}"""), "msg_01A");
        Assert.IsInstanceOfType<SessionUpdateAgentThoughtChunk>(thought);
        Assert.IsNull(StreamJson.Delta(J("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"signature_delta","signature":"x"}}}"""), "m"));
        Assert.IsNull(StreamJson.Delta(J("""{"type":"stream_event","event":{"type":"message_stop"}}"""), "m"));
    }

    [TestMethod]
    public void ToolUses_AndTheirResults_MapAsTheHooksDid()
    {
        var assistant = J("""
            {"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"toolu_01","name":"Write",
              "input":{"file_path":"C:\\work\\probe-approve.txt","content":"hello"}}]},"parent_tool_use_id":null}
            """);
        var (call, tool, _) = StreamJson.ToolUses(assistant).Single();
        Assert.AreEqual(("toolu_01", "Write", ToolKind.Edit), (call.ToolCallId, tool, call.Kind));
        Assert.AreEqual("C:\\work\\probe-approve.txt", call.Locations!.Single().Path);

        var bash = J("""
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_02","content":"slept","is_error":false}]},
             "tool_use_result":{"stdout":"slept","stderr":"","interrupted":false},"parent_tool_use_id":null}
            """);
        var done = StreamJson.ToolResults(bash, _ => "Bash").Single();
        Assert.AreEqual((ToolCallStatus.Completed, "slept"), (done.Status, ((TextContent)((Content)done.Content![0]).ContentValue).Text));
        Assert.AreEqual("slept", (string?)done.RawOutput?["stdout"]);

        var failed = StreamJson.ToolResults(J("""
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_03","content":"<tool_use_error>Blocked</tool_use_error>","is_error":true}]}}
            """), _ => "Bash").Single();
        Assert.AreEqual(ToolCallStatus.Failed, failed.Status);
        StringAssert.Contains(((TextContent)((Content)failed.Content![0]).ContentValue).Text, "Blocked");
    }

    [TestMethod]
    public void Results_EndTheTurn_WithTheRightStopReason()
    {
        var success = J("""{"type":"result","subtype":"success","is_error":false,"result":"pong","stop_reason":"end_turn","terminal_reason":"completed"}""");
        Assert.AreEqual(StopReason.EndTurn, StreamJson.StopReasonOf(success, cancelled: false));
        Assert.IsNull(StreamJson.ResultError(success));
        var aborted = J("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":null,"stop_reason":"tool_use","terminal_reason":"aborted_tools","errors":["[ede_diagnostic] result_type=user"]}""");
        Assert.AreEqual(StopReason.Cancelled, StreamJson.StopReasonOf(aborted, cancelled: true));
        StringAssert.Contains(StreamJson.ResultError(aborted), "ede_diagnostic");
        Assert.AreEqual(StopReason.MaxTokens, StreamJson.StopReasonOf(J("""{"type":"result","subtype":"success","stop_reason":"max_tokens"}"""), false));
        Assert.AreEqual(StopReason.MaxTurnRequests, StreamJson.StopReasonOf(J("""{"type":"result","subtype":"error_max_turns","is_error":true}"""), false));
    }

    [TestMethod]
    public void TheLogin_IsCheckedFromInitialize()
    {
        Assert.IsFalse(StreamJson.SignedOut(J("""{"account":{"email":"a@b","subscriptionType":"Claude Max","apiProvider":"firstParty"}}""")));
        Assert.IsTrue(StreamJson.SignedOut(J("""{"account":{"tokenSource":"none","apiProvider":"firstParty"}}""")), "what --bare looks like");
        var apiKey = J("""{"account":{"tokenSource":"claude.ai","apiKeySource":"ANTHROPIC_API_KEY","apiProvider":"firstParty"}}""");
        Assert.IsFalse(StreamJson.SignedOut(apiKey));
        Assert.AreEqual("ANTHROPIC_API_KEY", StreamJson.ApiKeySource(apiKey));
        Assert.IsNull(StreamJson.ApiKeySource(J("""{"account":{"subscriptionType":"Claude Max"}}""")));
        Assert.AreEqual("billing_error", StreamJson.PlanError(J("""{"type":"assistant","error":"billing_error","message":{"content":[]}}""")));
        Assert.IsNull(StreamJson.PlanError(J("""{"type":"assistant","error":"rate_limit","message":{"content":[]}}""")));
    }

    [TestMethod]
    public void Prompts_AreTextOrBlocks_AndUserMessagesCarryTheirUuid()
    {
        Assert.AreEqual("a\n\nb\n\n@file:///x", (string?)StreamJson.Content([new TextContent { Text = "a" }, new TextContent { Text = "b" }, new ResourceLink { Uri = "file:///x", Name = "x" }]));
        var blocks = StreamJson.Content([new TextContent { Text = "look" }, new ImageContent { Data = "AAA=", MimeType = "image/png" }]).AsArray();
        Assert.AreEqual("image/png", (string?)blocks[1]!["source"]!["media_type"]);
        var message = StreamJson.UserMessage("u-1", JsonValue.Create("hi")!);
        Assert.AreEqual(("user", "u-1", "hi"), ((string?)message["type"], (string?)message["uuid"], (string?)message["message"]!["content"]));
        Assert.IsFalse(StreamJson.HasContent([new TextContent { Text = "" }]));
    }

    static readonly List<ClaudeModel> Catalog = ClaudeOptions.Catalog(J("""
        {"models":[{"value":"default","resolvedModel":"claude-opus-5-5","displayName":"Default (recommended)","supportedEffortLevels":["low","medium","high","xhigh","max"],"supportsAutoMode":true},
                   {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5","supportedEffortLevels":["low","high"],"supportsAutoMode":true},
                   {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5"}]}
        """));

    [TestMethod]
    public void Options_ShowTheCatalog_AndOnlyWhatThePersonChangedIsSent()
    {
        var s = new ClaudeSettings { ReportedModel = "claude-opus-5-5", ReportedMode = "default" };
        var options = ClaudeOptions.ConfigOptions(s, Catalog).Select(o => (JsonObject)AcpJson.ToNode(o)!).ToList();
        Assert.AreEqual("default", (string?)options.Single(o => (string?)o["id"] == "model")["currentValue"], "no pick: Claude's default entry");
        Assert.AreEqual(ClaudeOptions.DefaultEffort, (string?)options.Single(o => (string?)o["id"] == "reasoning_effort")["currentValue"]);
        CollectionAssert.Contains(options.Single(o => (string?)o["id"] == "mode")["options"]!.AsArray().Select(v => (string?)v!["value"]).ToList(), "auto");
        CollectionAssert.AreEqual(new[] { "--permission-mode", "default" }, ClaudeOptions.LaunchArgs(s, "default", null).ToArray(), "nothing picked: nothing passed but the mode");

        Assert.AreEqual("set_model", (string?)ClaudeOptions.Change(s, Catalog, "model", "claude-haiku-4-5-20251001")["subtype"]);
        var effortless = ClaudeOptions.ConfigOptions(s, Catalog).Select(o => o.Id).ToList();
        CollectionAssert.DoesNotContain(effortless, "reasoning_effort", "Haiku 4.5 has no effort levels");
        CollectionAssert.DoesNotContain(ClaudeOptions.ConfigOptions(s, Catalog).OfType<SessionConfigSelect>().Single(o => o.Id == "mode").Options.AsArray()
            .Select(v => (string?)v!["value"]).ToList(), "auto", "nor auto mode");
        Assert.ThrowsExactly<JsonRpcException>(() => ClaudeOptions.Change(s, Catalog, "model", "gpt"));

        ClaudeOptions.Change(s, Catalog, "model", "opus");
        var effort = ClaudeOptions.Change(s, Catalog, "reasoning_effort", "high");
        Assert.AreEqual("high", (string?)effort["settings"]!["effortLevel"]);
        Assert.ThrowsExactly<JsonRpcException>(() => ClaudeOptions.Change(s, Catalog, "reasoning_effort", "max"));
        Assert.IsNull(ClaudeOptions.Change(s, Catalog, "reasoning_effort", ClaudeOptions.DefaultEffort)["settings"]!["effortLevel"], "back to Claude's default");
        ClaudeOptions.Change(s, Catalog, "mode", "plan");
        CollectionAssert.AreEqual(new[] { "--permission-mode", "plan", "--model", "opus" }, ClaudeOptions.LaunchArgs(s, "default", "haiku").ToArray());
    }

    [TestMethod]
    public void Trust_ComesFromClaudesOwnFlag_OrThePersonsKeptAllow()
    {
        var dir = TestDirs.New();
        var project = Directory.CreateDirectory(Path.Combine(dir, "repo", "sub")).FullName;
        var claudeState = Path.Combine(dir, "claude.json");
        var trust = new ClaudeTrust(Path.Combine(dir, "trusted.json"), claudeState, null);
        Assert.IsFalse(trust.IsTrusted(project), "no file: not trusted");

        File.WriteAllText(claudeState, new JsonObject
        {
            ["projects"] = new JsonObject
            {
                [Path.Combine(dir, "repo").Replace('\\', '/')] = new JsonObject { ["hasTrustDialogAccepted"] = true },
                [Path.Combine(dir, "other").Replace('\\', '/')] = new JsonObject { ["hasTrustDialogAccepted"] = false },
            },
        }.ToJsonString());
        Assert.IsTrue(trust.IsTrusted(project), "a trusted parent covers its folders");
        Assert.IsFalse(trust.IsTrusted(Path.Combine(dir, "other")));

        trust.Remember(Path.Combine(dir, "other"));
        Assert.IsTrue(new ClaudeTrust(Path.Combine(dir, "trusted.json"), claudeState, null).IsTrusted(Path.Combine(dir, "other")), "kept in Gorynych's own file");
        File.WriteAllText(claudeState, "{ not json");
        Assert.IsFalse(new ClaudeTrust(Path.Combine(dir, "none.json"), claudeState, null).IsTrusted(project), "a file it cannot read counts as not trusted");
    }
}
