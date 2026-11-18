using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Claude;

/// <summary>Correlates Claude's observed Agent calls, task IDs and run IDs. Reader-thread only.</summary>
internal sealed class ClaudeNativeSubagents(string root, bool markLive)
{
    sealed record Call(string Parent, JsonObject Input);
    sealed class Child(string id, string parent)
    {
        public string Id { get; } = id;
        public string Parent { get; } = parent;
        public ClaudeSubagentRun Run { get; } = new();
        public string? CurrentTool { get; set; }
        public string? FallbackAnswer { get; set; }
        public HashSet<string> Messages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Tools { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PendingPermissions { get; } = new(StringComparer.Ordinal);
    }

    readonly Dictionary<string, Call> calls = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Child> children = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> callToTask = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (string Agent, string Child, long RunVersion)> toolOwners = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, (string Parent, JsonObject State)> current = new(StringComparer.Ordinal);

    public async Task RefreshAsync(Func<string, SessionUpdate, Task> send)
    {
        foreach (var (id, value) in current)
        {
            var update = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = id,
                ["state"] = value.State.DeepClone(), ["_meta"] = new JsonObject { [GorynychProtocol.MetaSubagentSnapshot] = true } };
            await send(value.Parent, new SessionUpdate.Unknown("subagent_update", update));
        }
    }

    public async Task LostAsync(Func<string, SessionUpdate, Task> send)
    {
        foreach (var child in children.Values) { child.Run.Lost(); child.PendingPermissions.Clear(); }
        foreach (var (id, value) in current)
        {
            var state = new JsonObject { ["state"] = "unknown" };
            current[id] = (value.Parent, (JsonObject)state.DeepClone());
            var update = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = id, ["state"] = state };
            if (markLive) update["_meta"] = new JsonObject { [GorynychProtocol.MetaSubagentLiveState] = true };
            await send(value.Parent, new SessionUpdate.Unknown("subagent_update", update));
        }
    }

    public string? PermissionSession(JsonObject request)
    {
        var tool = Text(request["tool_use_id"]); var agent = Text(request["agent_id"]);
        return tool is not null && agent is not null && toolOwners.TryGetValue(tool, out var owner) && owner.Agent == agent
            ? owner.Child : null;
    }

    public async Task<(string Session, long RunVersion, string RequestId)?> BeginPermissionAsync(string requestId, JsonObject request, Func<string, SessionUpdate, Task> send)
    {
        var session = PermissionSession(request);
        if (session is null) return null;
        var child = children.Values.FirstOrDefault(c => c.Id == session);
        if (child is null) return null;
        var tool = Text(request["tool_use_id"])!;
        if (!child.Run.IsActive || !toolOwners.TryGetValue(tool, out var owner) || owner.RunVersion != child.Run.Version)
            return (session, 0, requestId);
        var version = child.Run.Version;
        if (child.PendingPermissions.Add(requestId) && child.PendingPermissions.Count == 1)
        {
            try { await StateAsync(child, "requires_action", null, send); }
            catch { child.PendingPermissions.Remove(requestId); throw; }
        }
        return (session, version, requestId);
    }

    public async Task EndPermissionAsync((string Session, long RunVersion, string RequestId) permission, Func<string, SessionUpdate, Task> send)
    {
        var child = children.Values.FirstOrDefault(c => c.Id == permission.Session);
        if (child is not null && child.Run.IsActive && child.Run.Version == permission.RunVersion
            && child.PendingPermissions.Remove(permission.RequestId) && child.PendingPermissions.Count == 0)
            await StateAsync(child, "running", null, send);
    }

    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static bool SafeId(string value) => value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    string ChildId(string id) => $"claude-subagent:{root}:{id}";

    public void ObserveCall(string parent, string id, string tool, JsonObject? input)
    {
        if (tool is not ("Agent" or "Task") || input is null || calls.Count >= 512) return;
        calls[id] = new Call(parent, (JsonObject)input.DeepClone());
    }

    public async Task ObserveAsync(JsonObject frame, Func<string, SessionUpdate, Task> send)
    {
        var type = Text(frame["type"]);
        if (type == "system" && Text(frame["subtype"]) == "task_started")
        {
            var task = Text(frame["task_id"]); var tool = Text(frame["tool_use_id"]);
            if (Text(frame["task_type"]) != "local_agent" || task is null || !SafeId(task) || tool is null || !calls.TryGetValue(tool, out var call)) return;
            if (Text(call.Input["isolation"]) is { } isolation && isolation != "none") return;
            if (Text(frame["parent_task_id"]) is { } parentTask && (!children.TryGetValue(parentTask, out var parent) || parent.Id != call.Parent)) return;
            if (call.Parent != root && !children.Values.Any(c => c.Id == call.Parent)) return;
            if (!children.TryGetValue(task, out var child))
            {
                if (children.Count >= 256) return;
                children[task] = child = new Child(ChildId(task), call.Parent);
            }
            if (child.Parent != call.Parent || !child.Run.Start(Text(frame["run_id"]), tool)) return;
            child.PendingPermissions.Clear();
            child.CurrentTool = tool;
            child.FallbackAnswer = null;
            callToTask[tool] = task;
            var association = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = child.Id };
            if (Text(frame["description"]) is { } title) association["title"] = title;
            if (Text(frame["subagent_type"]) is { } role) association["description"] = role;
            await send(call.Parent, new SessionUpdate.Unknown("subagent_update", association));
            if (Text(call.Input["prompt"]) is { } prompt)
                await send(call.Parent, new SessionMessage { MessageId = $"{tool}:to:{child.Id}", SenderSessionId = call.Parent,
                    RecipientSessionId = child.Id, Content = [new TextContent { Text = prompt }] });
            await StateAsync(child, "running", null, send);
            return;
        }
        if (type == "system" && Text(frame["subtype"]) == "task_notification")
        {
            var task = Text(frame["task_id"]);
            if (task is null || !children.TryGetValue(task, out var child)) return;
            var status = Text(frame["status"]);
            var result = child.Run.Finish(Text(frame["run_id"]), status, Text(frame["summary"]), Text(frame["tool_use_id"]));
            if (!result.Accepted) return;
            child.PendingPermissions.Clear();
            if (result.Fallback is { } answer)
            {
                child.FallbackAnswer = answer;
                await send(child.Id, new SessionUpdateAgentMessageChunk { MessageId = Text(frame["uuid"]), Content = new TextContent { Text = answer } });
            }
            if (status == "stopped" && Text(frame["reason"]) == "worker_restart") await StateAsync(child, "unknown", null, send);
            else await StateAsync(child, "idle", status == "completed" ? "end_turn" : status == "failed" ? "error" : null, send);
            return;
        }
        if (!StreamJson.IsSubagent(frame)) return;
        var parentTool = Text(frame["parent_tool_use_id"]); var agent = Text(frame["agent_id"]);
        if (parentTool is null || agent is null || !callToTask.TryGetValue(parentTool, out var linked) || linked != agent ||
            !children.TryGetValue(agent, out var owner)) return;
        if (type == "assistant")
        {
            var messageId = StreamJson.AssistantId(frame) ?? Text(frame["uuid"]);
            if (StreamJson.AssistantText(frame) is { Length: > 0 } answer && answer != owner.FallbackAnswer && (messageId is null || owner.Messages.Add(messageId)))
            {
                owner.Run.Answered();
                await send(owner.Id, new SessionUpdateAgentMessageChunk { MessageId = messageId, Content = new TextContent { Text = answer } });
            }
            foreach (var (call, tool, input) in StreamJson.ToolUses(frame))
            {
                owner.Tools[call.ToolCallId] = tool;
                toolOwners.TryAdd(call.ToolCallId, (agent, owner.Id, owner.Run.Version));
                ObserveCall(owner.Id, call.ToolCallId, tool, input);
                await send(owner.Id, call);
            }
        }
        else if (type == "user")
        {
            if (StreamJson.UserText(frame) is { Length: > 0 } incoming && Text(frame["uuid"]) is { } id)
                await send(owner.Id, new SessionMessage { MessageId = id, RecipientSessionId = owner.Id, Content = [new TextContent { Text = incoming }] });
            foreach (var detail in StreamJson.ToolResultDetails(frame, id => owner.Tools.GetValueOrDefault(id)))
                await send(owner.Id, StreamJson.ToolResultUpdate(detail));
        }
    }

    Task StateAsync(Child child, string state, string? reason, Func<string, SessionUpdate, Task> send)
    {
        var snapshot = new JsonObject { ["state"] = state };
        if (reason is not null) snapshot["stopReason"] = reason;
        current[child.Id] = (child.Parent, (JsonObject)snapshot.DeepClone());
        var update = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = child.Id, ["state"] = snapshot };
        if (markLive) update["_meta"] = new JsonObject { [GorynychProtocol.MetaSubagentLiveState] = true };
        return send(child.Parent, new SessionUpdate.Unknown("subagent_update", update));
    }
}
