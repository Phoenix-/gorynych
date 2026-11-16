using System.Text.Json.Nodes;

namespace Gorynych.Acp.Hosting;

/// <summary>A validated ACP v2-style background reference carried by the v1 extension.</summary>
public sealed record BackgroundWorkReference(string Type, string? ReferenceId);

/// <summary>A complete live snapshot. A null snapshot is unknown, not an empty list.</summary>
public sealed class BackgroundWorkSnapshot
{
    readonly List<(BackgroundWorkReference Reference, JsonObject Wire)> items;
    BackgroundWorkSnapshot(List<(BackgroundWorkReference Reference, JsonObject Wire)> items) => this.items = items;
    public int Count => items.Count;
    public IReadOnlyList<BackgroundWorkReference> References => items.Select(item => item.Reference).ToArray();
    public JsonArray ToJson() => new(items.Select(item => item.Wire.DeepClone()).ToArray());

    /// <summary>False means malformed; callers must treat it as unknown, never empty.</summary>
    public static bool TryParse(JsonNode? value, out BackgroundWorkSnapshot? snapshot)
    {
        snapshot = null;
        if (value is null) return true;
        if (value is not JsonArray array) return false;
        var items = new List<(BackgroundWorkReference, JsonObject)>();
        var keys = new HashSet<(string, string)>();
        foreach (var node in array)
        {
            if (node is not JsonObject item || !Text(item["type"], out var type)) return false;
            if (item["_meta"] is not null and not JsonObject) return false;
            var field = type switch { "terminal" => "terminalId", "tool_call" => "toolCallId", "subagent" => "sessionId", _ => null };
            string? reference = null;
            if (field is not null && (!Text(item[field], out reference) || !keys.Add((type!, reference!)))) return false;
            items.Add((new BackgroundWorkReference(type!, reference), (JsonObject)item.DeepClone()));
        }
        snapshot = new BackgroundWorkSnapshot(items);
        return true;
    }

    static bool Text(JsonNode? node, out string? text)
    {
        text = null;
        return node is JsonValue value && value.TryGetValue<string>(out text) && !string.IsNullOrWhiteSpace(text);
    }
}
