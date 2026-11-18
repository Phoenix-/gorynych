using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>Best-effort replay from Claude's active parent transcript and verified child files.</summary>
internal sealed class ClaudeSubagentHistory(string root, string parentPath, Func<string, SessionUpdate, Task> send, Action<string>? log = null)
{
    readonly Dictionary<string, JsonObject> calls = new(StringComparer.Ordinal);
    readonly HashSet<string> replayed = new(StringComparer.Ordinal);
    const long MaxChildBytes = 8 * 1024 * 1024;
    static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    static bool Safe(string value) => value.Length <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task ObserveAsync(JsonObject entry)
    {
        if (entry["isSidechain"] is JsonValue side && side.TryGetValue<bool>(out var isSide) && isSide) return;
        if (Text(entry["type"]) == "assistant")
        {
            foreach (var (call, name, callInput) in StreamJson.ToolUses(entry))
                if (name is "Agent" or "Task" && callInput is not null && calls.Count < 512) calls[call.ToolCallId] = (JsonObject)callInput.DeepClone();
            return;
        }
        if (Text(entry["type"]) != "user" || entry["toolUseResult"] is not JsonObject result ||
            Text(result["agentId"]) is not { } agent || !Safe(agent) || replayed.Count >= 128 || replayed.Contains(agent)) return;
        var blocks = (entry["message"]?["content"] as JsonArray)?.OfType<JsonObject>()
            .Where(b => Text(b["type"]) == "tool_result").ToArray() ?? [];
        if (blocks.Length != 1 || Text(blocks[0]["tool_use_id"]) is not { } tool || !calls.TryGetValue(tool, out var input)) return;
        if (Text(input["isolation"]) is { } isolation && isolation != "none") return;
        var path = Path.Combine(Path.GetDirectoryName(parentPath)!, Path.GetFileNameWithoutExtension(parentPath), "subagents", $"agent-{agent}.jsonl");
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxChildBytes) return;
            // Validate the entire bounded file before announcing any association or child content.
            var rows = new List<JsonObject>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length > 1024 * 1024 || JsonNode.Parse(line) is not JsonObject row) return;
                if (Text(row["type"]) is not ("user" or "assistant")) continue;
                if (Text(row["agentId"]) != agent || Text(row["sessionId"]) != root || (bool?)row["isSidechain"] != true) return;
                rows.Add(row);
            }
            var child = $"claude-subagent:{root}:{agent}";
            var association = new JsonObject { ["sessionUpdate"] = "subagent_update", ["sessionId"] = child };
            if (Text(input["description"]) is { } title) association["title"] = title;
            if (Text(input["subagent_type"]) is { } role) association["description"] = role;
            await send(root, new SessionUpdate.Unknown("subagent_update", association));
            if (Text(input["prompt"]) is { } prompt)
                await send(root, new SessionMessage { MessageId = $"{tool}:to:{child}", SenderSessionId = root,
                    RecipientSessionId = child, Content = [new TextContent { Text = prompt }] });
            replayed.Add(agent);
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var type = Text(row["type"]);
                var id = Text(row["uuid"]);
                if (type == "user")
                {
                    if (StreamJson.UserText(row) is { Length: > 0 } text && id is not null)
                        await send(child, new SessionMessage { MessageId = id, RecipientSessionId = child, Content = [new TextContent { Text = text }] });
                    foreach (var detail in StreamJson.ToolResultDetails(row, key => tools.GetValueOrDefault(key)))
                        await send(child, StreamJson.ToolResultUpdate(detail));
                }
                else if (type == "assistant")
                {
                    if (StreamJson.AssistantText(row) is { Length: > 0 } text)
                        await send(child, new SessionUpdateAgentMessageChunk { MessageId = StreamJson.AssistantId(row) ?? id, Content = new TextContent { Text = text } });
                    foreach (var (call, toolName, _) in StreamJson.ToolUses(row))
                    { tools[call.ToolCallId] = toolName; await send(child, call); }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { log?.Invoke($"child transcript {agent} could not be replayed: {e.Message}"); }
    }
}
