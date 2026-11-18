using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Adapters.Claude;

namespace Gorynych.Tests;

[TestClass]
public sealed class ClaudeNativeSubagentsTests
{
    [TestMethod]
    public async Task NativeHistoryRequiresMatchingParentToolAndChildTranscript()
    {
        const string root = "975c2b85-df7d-40b7-8974-e8eb57670c4e";
        const string task = "a7d31de8e7eb0ddf5";
        var dir = TestDirs.New();
        var rootPath = Path.Combine(dir, root + ".jsonl");
        var childDir = Path.Combine(dir, root, "subagents");
        Directory.CreateDirectory(childDir);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        File.Copy(Path.Combine(fixture, "claude-native-history-child.jsonl"), Path.Combine(childDir, $"agent-{task}.jsonl"));
        var sent = new List<(string Id, SessionUpdate Update)>();
        var replay = new ClaudeSubagentHistory(root, rootPath, (id, update) => { sent.Add((id, update)); return Task.CompletedTask; });
        var parent = File.ReadAllLines(Path.Combine(fixture, "claude-native-history-parent.jsonl"))
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        parent[0]["parentUuid"] = null;
        parent[1]["parentUuid"] = parent[0]["uuid"]!.DeepClone();
        File.WriteAllLines(rootPath, parent.Select(x => x.ToJsonString()));
        var result = await TranscriptHistory.ReadAsync(rootPath, null,
            update => { sent.Add((root, update)); return Task.CompletedTask; }, observe: replay.ObserveAsync);
        Assert.IsNotNull(result);
        var announcement = sent.FindIndex(x => x.Id == root && x.Update.Tag == "subagent_update");
        Assert.IsTrue(announcement >= 0);
        Assert.IsInstanceOfType<SessionMessage>(sent[announcement + 1].Update);
        Assert.IsTrue(sent.Any(x => x.Id == $"claude-subagent:{root}:{task}" && x.Update is SessionUpdateAgentMessageChunk));
        Assert.IsFalse(sent.Any(x => AcpJson.ToNode<SessionUpdate>(x.Update)?["state"] is not null));

        var childPath = Path.Combine(childDir, $"agent-{task}.jsonl");
        var altered = File.ReadLines(childPath).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        altered[0]["sessionId"] = "different-parent";
        File.WriteAllLines(childPath, altered.Select(row => row.ToJsonString()));
        sent.Clear();
        var wrong = new ClaudeSubagentHistory(root, rootPath, (id, update) => { sent.Add((id, update)); return Task.CompletedTask; });
        foreach (var row in parent) await wrong.ObserveAsync(row);
        Assert.AreEqual(0, sent.Count, "do not announce an unverified child transcript");
    }

    [TestMethod]
    public async Task PermissionRequiresObservedAgentAndToolPair()
    {
        var mapper = new ClaudeNativeSubagents("root", false);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-native-subagent-permission.jsonl");
        string? session = null;
        foreach (var line in File.ReadLines(path))
        {
            var frame = JsonNode.Parse(line)!.AsObject();
            if (StreamJson.Type(frame) == "assistant" && !StreamJson.IsSubagent(frame))
                foreach (var (call, tool, input) in StreamJson.ToolUses(frame)) mapper.ObserveCall("root", call.ToolCallId, tool, input);
            await mapper.ObserveAsync(frame, (_, _) => Task.CompletedTask);
            if (StreamJson.Type(frame) != "control_request") continue;
            var request = frame["request"]!.AsObject();
            session = mapper.PermissionSession(request);
            Assert.IsNotNull(session);
            var altered = (JsonObject)request.DeepClone(); altered["agent_id"] = "another";
            Assert.IsNull(mapper.PermissionSession(altered));
            altered = (JsonObject)request.DeepClone(); altered.Remove("agent_id");
            Assert.IsNull(mapper.PermissionSession(altered));
            altered = (JsonObject)request.DeepClone(); altered.Remove("tool_use_id");
            Assert.IsNull(mapper.PermissionSession(altered));
            altered.Remove("agent_id");
            Assert.IsNull(mapper.PermissionSession(altered));
            altered = (JsonObject)request.DeepClone();
            altered["tool_use_id"] = "unobserved";
            Assert.IsNull(mapper.PermissionSession(altered));
        }
        StringAssert.StartsWith(session!, "claude-subagent:root:");
    }

    [TestMethod]
    public async Task ResumedChildCanKeepOriginalParentToolIdentity()
    {
        var mapper = new ClaudeNativeSubagents("root", true);
        var sent = new List<SessionUpdate>();
        Task Send(string _, SessionUpdate update) { sent.Add(update); return Task.CompletedTask; }
        foreach (var (tool, run) in new[] { ("first", "001"), ("second", "002") })
        {
            mapper.ObserveCall("root", tool, "Agent", new JsonObject { ["prompt"] = "go" });
            await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
                ["task_id"] = "task1", ["tool_use_id"] = tool, ["run_id"] = run }, Send);
        }
        await mapper.ObserveAsync(new JsonObject { ["type"] = "assistant", ["agent_id"] = "task1", ["parent_tool_use_id"] = "first",
            ["message"] = new JsonObject { ["id"] = "resumed", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "resumed answer" }) } }, Send);
        Assert.AreEqual(1, sent.Count(x => x is SessionUpdateAgentMessageChunk));
    }

    [TestMethod]
    public async Task PermissionResponseCannotResetNewRunState()
    {
        var mapper = new ClaudeNativeSubagents("root", true);
        var sent = new List<SessionUpdate>();
        Task Send(string _, SessionUpdate update) { sent.Add(update); return Task.CompletedTask; }
        mapper.ObserveCall("root", "first", "Agent", new JsonObject { ["prompt"] = "one" });
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
            ["task_id"] = "task1", ["tool_use_id"] = "first", ["run_id"] = "001" }, Send);
        await ObserveChildTool(mapper, "first", "permission", Send);
        var permission = await mapper.BeginPermissionAsync("request-1", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "permission" }, Send);
        Assert.IsNotNull(permission);
        Assert.AreEqual("requires_action", (string?)AcpJson.ToNode<SessionUpdate>(sent[^1])?["state"]?["state"]);
        mapper.ObserveCall("root", "second", "Agent", new JsonObject { ["prompt"] = "two" });
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
            ["task_id"] = "task1", ["tool_use_id"] = "second", ["run_id"] = "002" }, Send);
        var before = sent.Count;
        await mapper.EndPermissionAsync(permission.Value, Send);
        Assert.AreEqual(before, sent.Count, "an old approval cannot alter the new run");
        await ObserveChildTool(mapper, "second", "new-permission", Send);
        var oldRequest = await mapper.BeginPermissionAsync("late-old", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "permission" }, Send);
        Assert.IsNotNull(oldRequest);
        Assert.AreEqual(before + 1, sent.Count, "an old tool remains routed but cannot change the new run state");
        var current = await mapper.BeginPermissionAsync("request-2", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "new-permission" }, Send);
        Assert.IsNotNull(current);
        var also = await mapper.BeginPermissionAsync("request-3", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "new-permission" }, Send);
        Assert.IsNotNull(also);
        before = sent.Count;
        await mapper.EndPermissionAsync(current.Value, Send);
        Assert.AreEqual(before, sent.Count, "another approval is still pending");
        await mapper.EndPermissionAsync(also.Value, Send);
        Assert.AreEqual("running", (string?)AcpJson.ToNode<SessionUpdate>(sent[^1])?["state"]?["state"]);
        var pending = await mapper.BeginPermissionAsync("request-4", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "new-permission" }, Send);
        Assert.IsNotNull(pending);
        await mapper.LostAsync(Send);
        before = sent.Count;
        await mapper.EndPermissionAsync(pending.Value, Send);
        Assert.AreEqual(before, sent.Count, "a lost process cannot become running after approval");
        Assert.AreEqual("unknown", (string?)AcpJson.ToNode<SessionUpdate>(sent[^1])?["state"]?["state"]);
    }

    static Task ObserveChildTool(ClaudeNativeSubagents mapper, string parent, string tool, Func<string, SessionUpdate, Task> send) =>
        mapper.ObserveAsync(new JsonObject { ["type"] = "assistant", ["agent_id"] = "task1", ["parent_tool_use_id"] = parent,
            ["message"] = new JsonObject { ["id"] = "msg-" + tool, ["content"] = new JsonArray(new JsonObject {
                ["type"] = "tool_use", ["id"] = tool, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = "true" } }) } }, send);

    [TestMethod]
    public async Task StoppedTaskIsIdleButWorkerRestartIsUnknown()
    {
        foreach (var (reason, expected) in new[] { ((string?)null, "idle"), ("worker_restart", "unknown") })
        {
            var mapper = new ClaudeNativeSubagents("root", true);
            var sent = new List<SessionUpdate>();
            Task Send(string _, SessionUpdate update) { sent.Add(update); return Task.CompletedTask; }
            mapper.ObserveCall("root", "first", "Agent", new JsonObject { ["prompt"] = "one" });
            await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
                ["task_id"] = "task1", ["tool_use_id"] = "first", ["run_id"] = "001" }, Send);
            await ObserveChildTool(mapper, "first", "pending-tool", Send);
            var pending = await mapper.BeginPermissionAsync("pending", new JsonObject { ["agent_id"] = "task1", ["tool_use_id"] = "pending-tool" }, Send);
            Assert.IsNotNull(pending);
            await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = "task1",
                ["run_id"] = "001", ["status"] = "stopped", ["reason"] = reason }, Send);
            Assert.AreEqual(expected, (string?)AcpJson.ToNode<SessionUpdate>(sent[^1])?["state"]?["state"]);
            var count = sent.Count;
            await mapper.EndPermissionAsync(pending.Value, Send);
            Assert.AreEqual(count, sent.Count, "a terminal run cannot be revived by a late approval");
        }
    }

    [TestMethod]
    public async Task LateCompletionCannotStopResumedRunOrRepeatAnswer()
    {
        var mapper = new ClaudeNativeSubagents("root", true);
        var sent = new List<SessionUpdate>();
        Task Send(string _, SessionUpdate update) { sent.Add(update); return Task.CompletedTask; }
        mapper.ObserveCall("root", "first", "Agent", new JsonObject { ["prompt"] = "one" });
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
            ["task_id"] = "task1", ["tool_use_id"] = "first", ["run_id"] = "001" }, Send);
        mapper.ObserveCall("root", "second", "Agent", new JsonObject { ["prompt"] = "two" });
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
            ["task_id"] = "task1", ["tool_use_id"] = "second", ["run_id"] = "002" }, Send);
        var before = sent.Count;
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification",
            ["task_id"] = "task1", ["run_id"] = "001", ["status"] = "completed", ["summary"] = "old" }, Send);
        Assert.AreEqual(before, sent.Count);
        await mapper.ObserveAsync(new JsonObject { ["type"] = "assistant", ["agent_id"] = "task1", ["parent_tool_use_id"] = "second",
            ["message"] = new JsonObject { ["id"] = "answer", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "answer" }) } }, Send);
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification",
            ["task_id"] = "task1", ["run_id"] = "002", ["status"] = "completed", ["summary"] = "answer" }, Send);
        Assert.AreEqual(1, sent.Count(x => x is SessionUpdateAgentMessageChunk));
        Assert.AreEqual(1, sent.Count(x => x.Tag == "subagent_update" && (string?)AcpJson.ToNode<SessionUpdate>(x)?["state"]?["state"] == "idle"));
        mapper.ObserveCall("root", "third", "Agent", new JsonObject { ["prompt"] = "three" });
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = "local_agent",
            ["task_id"] = "task1", ["tool_use_id"] = "third", ["run_id"] = "003" }, Send);
        await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_notification", ["uuid"] = "end-3",
            ["task_id"] = "task1", ["run_id"] = "003", ["status"] = "completed", ["summary"] = "late answer" }, Send);
        await mapper.ObserveAsync(new JsonObject { ["type"] = "assistant", ["agent_id"] = "task1", ["parent_tool_use_id"] = "third",
            ["message"] = new JsonObject { ["id"] = "late", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "late answer" }) } }, Send);
        Assert.AreEqual(2, sent.Count(x => x is SessionUpdateAgentMessageChunk));
    }

    [TestMethod]
    public async Task UnknownOrNonAgentTasksNeverCreateAChild()
    {
        var mapper = new ClaudeNativeSubagents("root", false);
        var sent = new List<SessionUpdate>();
        Task Send(string _, SessionUpdate update) { sent.Add(update); return Task.CompletedTask; }
        mapper.ObserveCall("root", "agent", "Agent", new JsonObject { ["prompt"] = "test" });
        foreach (var (type, id, parent) in new[] { ("local_bash", "task1", (string?)null), ("mcp_task", "task2", (string?)null),
                     ("local_agent", "task3", "unobserved"), ("local_agent", "../unsafe", (string?)null) })
            await mapper.ObserveAsync(new JsonObject { ["type"] = "system", ["subtype"] = "task_started", ["task_type"] = type,
                ["task_id"] = id, ["tool_use_id"] = "agent", ["parent_task_id"] = parent, ["run_id"] = "001" }, Send);
        Assert.AreEqual(0, sent.Count);
    }

    [TestMethod]
    [DataRow("fg", 1)]
    [DataRow("bg", 1)]
    [DataRow("pair", 2)]
    public async Task NativeFramesAssociateOnlyObservedChildren(string fixture, int count)
    {
        var mapper = new ClaudeNativeSubagents("root", true);
        var sent = new List<(string Id, SessionUpdate Update)>();
        Task Send(string id, SessionUpdate update) { sent.Add((id, update)); return Task.CompletedTask; }
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"claude-native-subagent-{fixture}.jsonl");
        foreach (var line in File.ReadLines(path))
        {
            var frame = JsonNode.Parse(line)!.AsObject();
            if (StreamJson.Type(frame) == "assistant" && !StreamJson.IsSubagent(frame))
                foreach (var (call, tool, input) in StreamJson.ToolUses(frame)) mapper.ObserveCall("root", call.ToolCallId, tool, input);
            await mapper.ObserveAsync(frame, Send);
        }
        var associations = sent.Where(x => x.Id == "root" && x.Update.Tag == "subagent_update")
            .Where(x => AcpJson.ToNode<SessionUpdate>(x.Update)?["title"] is not null).ToArray();
        Assert.AreEqual(count, associations.Length);
        Assert.AreEqual(count, associations.Select(x => (string?)AcpJson.ToNode<SessionUpdate>(x.Update)?["sessionId"]).Distinct().Count());
        Assert.AreEqual(count, sent.Count(x => x.Update is SessionMessage && x.Id == "root"));
        Assert.AreEqual(count, sent.Count(x => x.Update is SessionUpdateAgentMessageChunk));
        Assert.AreEqual(count, sent.Count(x => x.Update.Tag == "subagent_update" && (string?)AcpJson.ToNode<SessionUpdate>(x.Update)?["state"]?["state"] == "idle"));
    }
}
