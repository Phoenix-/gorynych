using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>Routes restricted ACP sessions through the ordinary session which owns their tree.</summary>
internal sealed class SubagentRouting
{
    // The journal stores update bodies. This key preserves the notification's outer session ID.
    public const string MetaSession = "gorynych/subagentSessionId";

    readonly Lock gate = new();
    readonly Dictionary<string, (string Parent, string Root)> children = new(StringComparer.Ordinal);

    public string? RootOf(string sessionId)
    {
        lock (gate) return children.TryGetValue(sessionId, out var route) ? route.Root : null;
    }

    public bool Announce(string parent, string child, string root)
    {
        if (child == parent || child == root) return false;
        lock (gate)
        {
            if (parent != root && (!children.TryGetValue(parent, out var route) || route.Root != root)) return false;
            if (children.TryGetValue(child, out var known)) return known == (parent, root);
            children.Add(child, (parent, root));
            return true;
        }
    }

    public void Forget(string root)
    {
        lock (gate)
            foreach (var child in children.Where(pair => pair.Value.Root == root).Select(pair => pair.Key).ToArray())
                children.Remove(child);
    }

    public static string? Announcement(JsonNode? update) =>
        ReadString(update?["sessionUpdate"]) == "subagent_update" ? ReadString(update?["sessionId"]) : null;

    public static bool IsPreview(JsonNode? update) =>
        ReadString(update?["sessionUpdate"]) is "subagent_update" or "session_message" or "session_message_chunk"
        || SessionOf(update) is not null;

    public static bool IsCurrentSnapshot(JsonNode? update) => Announcement(update) is not null
        && update?["state"] is JsonObject
        && update["_meta"]?[Acp.Hosting.GorynychProtocol.MetaSubagentSnapshot] is JsonValue value
        && value.TryGetValue<bool>(out var current) && current;

    public static bool IsLiveState(JsonNode? update) => Announcement(update) is not null
        && update?["state"] is JsonObject
        && update["_meta"]?[Acp.Hosting.GorynychProtocol.MetaSubagentLiveState] is JsonValue value
        && value.TryGetValue<bool>(out var live) && live;

    public static string? SessionOf(JsonNode? update) => ReadString(update?["_meta"]?[MetaSession]);

    public static void SetSession(JsonObject update, string sessionId)
    {
        if (update["_meta"] is not JsonObject meta) update["_meta"] = meta = new();
        meta[MetaSession] = sessionId;
    }

    /// <summary>Keep replayed child updates separate from the ordinary session's history matching.</summary>
    public static List<JsonObject> NewHistory(string root, IReadOnlyList<JsonNode> journal, IReadOnlyList<JsonObject> replay)
    {
        var existing = journal.OfType<JsonObject>().Where(IsPreview).ToList();
        var known = existing.Select(update => Key(root, update)).ToHashSet();
        var streamed = existing.Where(update => MessageKey(update).Id is not null && TextOf(update) is not null)
            .GroupBy(MessageKey).ToDictionary(group => group.Key, group => string.Concat(group.Select(TextOf)));
        var result = new List<JsonObject>();
        foreach (var update in replay.Where(IsPreview))
        {
            if (TextOf(update) is { } text && streamed.TryGetValue(MessageKey(update), out var live))
            {
                if (text.Length <= live.Length || !text.StartsWith(live, StringComparison.Ordinal)) continue;
                var tail = (JsonObject)update.DeepClone();
                tail["content"]!["text"] = text[live.Length..];
                if (known.Add(Key(root, tail))) result.Add(tail);
            }
            else if (known.Add(Key(root, update))) result.Add(update);
        }
        return result;
    }

    static (string? Session, string? Kind, string? Id) MessageKey(JsonObject update) =>
        (SessionOf(update), ReadString(update["sessionUpdate"]), ReadString(update["messageId"]));

    static string? TextOf(JsonObject update) => SessionJournal.IsChunkKind(ReadString(update["sessionUpdate"]))
        && update["content"] is JsonObject content && ReadString(content["type"]) == "text" ? ReadString(content["text"]) : null;

    static (string Session, string Update) Key(string root, JsonObject update)
    {
        var copy = (JsonObject)update.DeepClone();
        if (copy["_meta"] is JsonObject meta)
        {
            meta.Remove(MetaSession);
            meta.Remove(Acp.Hosting.GorynychProtocol.MetaHostSeq);
            meta.Remove(Acp.Hosting.GorynychProtocol.MetaPosition);
            if (meta.Count == 0) copy.Remove("_meta");
        }
        return (SessionOf(update) ?? root, copy.ToJsonString());
    }

    static string? ReadString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
