using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>
/// Reads a page of a session's history from its journal for <c>_gorynych/read</c> and
/// <c>read_session</c>: messages with the chunks of each merged into one, tool calls with their
/// last status folded in, thoughts on request, newest page first (docs/service-protocol.md,
/// "Reading a session"). Pure, so it is tested on journals made up in the test.
/// </summary>
internal static class SessionReader
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;

    /// <summary>A page: its updates oldest first, and where it sits among the session's messages.</summary>
    /// <param name="First">The 1-based number of the page's oldest message; 0 when it has none.</param>
    public sealed record Page(List<JsonObject> Updates, string? NextCursor, int Total, int First, int Count);

    /// <summary>A cursor that names no message of the session.</summary>
    public sealed class UnknownCursorException(string cursor) : Exception($"no message \"{cursor}\"")
    {
        public string Cursor { get; } = cursor;
    }

    sealed class Item(string kind, int index)
    {
        public string Kind { get; } = kind;
        public int Index { get; } = index; // the journal index of its first entry
        public string? MessageId { get; init; }
        public string? ToolCallId { get; init; }
        public JsonObject? Meta { get; init; }
        public StringBuilder Text { get; } = new();
        public string? Title { get; set; }
        public string? ToolKind { get; set; }
        public string? Status { get; set; }
        public string Cursor { get; set; } = "";
        public bool IsMessage => Kind is "user_message_chunk" or "agent_message_chunk";
    }

    /// <param name="cursor">Only messages older than the one it names; null for the newest page.</param>
    /// <param name="limit">Messages per page, 1 to <see cref="MaxLimit"/>.</param>
    /// <param name="budget">At most this many characters of text, filled from the newest message
    /// backwards; a message that does not fit is cut in the middle, and the page always has one.</param>
    public static Page Read(IReadOnlyList<JsonNode> journal, string? cursor, int limit, bool thoughts, int? budget = null)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var items = Items(journal);
        var messages = items.Where(i => i.IsMessage).ToList();
        var ids = messages.Where(m => m.MessageId is not null).GroupBy(m => m.MessageId!).ToDictionary(g => g.Key, g => g.Count());
        bool Unique(Item m) => m.MessageId is { } id && ids[id] == 1;
        // A message's cursor is its own ID when no other message has it. Otherwise (an entry from before
        // message IDs, or chunks of one ID split by a tool call) it is the distance to the nearest later
        // message that has one: compaction drops the oldest entries and merges a message's chunks, which
        // moves neither. Only a journal without any later ID falls back to the entry's index.
        var anchor = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (Unique(messages[i])) { messages[i].Cursor = messages[i].MessageId!; anchor = i; continue; }
            messages[i].Cursor = anchor >= 0 ? $"{messages[anchor].MessageId}~{anchor - i}" : $"#{messages[i].Index}";
        }

        var end = messages.Count;
        if (cursor is not null) end = Find(cursor);

        int Find(string c)
        {
            var exact = messages.FindIndex(m => m.Cursor == c);
            if (exact >= 0) return exact;
            var tilde = c.LastIndexOf('~');
            if (tilde > 0 && int.TryParse(c.AsSpan(tilde + 1), out var back) && back > 0
                && messages.FindIndex(m => m.MessageId == c[..tilde] && Unique(m)) is var later and >= 0)
                return Math.Max(later - back, 0); // before the oldest entry left: nothing older remains
            var index = messages.FindIndex(m => $"#{m.Index}" == c);
            return index >= 0 ? index : throw new UnknownCursorException(c);
        }
        // Each message carries the tool calls and thoughts that follow it, up to the next message.
        int Position(int message) => message < messages.Count ? items.IndexOf(messages[message]) : items.Count;
        var cuts = new Dictionary<Item, int>();
        var start = end;
        var left = budget ?? int.MaxValue;
        while (start > 0 && end - start < limit)
        {
            var candidate = start - 1;
            var segment = items.Skip(Position(candidate)).Take(Position(candidate + 1) - Position(candidate))
                .Where(i => thoughts || i.Kind != "agent_thought_chunk").ToList();
            var cost = segment.Sum(Cost);
            if (cost <= left) { left -= cost; start = candidate; continue; }
            var message = messages[candidate];
            var room = left - (cost - message.Text.Length);
            if (start < end && room < 500) break; // too little room to be worth a cut message
            cuts[message] = Math.Max(room, 200);
            start = candidate;
            break;
        }

        var from = start == 0 ? 0 : Position(start);
        var to = cursor is null ? items.Count : Position(end);
        var updates = items.Skip(from).Take(to - from)
            .Where(i => thoughts || i.Kind != "agent_thought_chunk")
            .Select(i => ToUpdate(i, cuts.TryGetValue(i, out var room) ? room : null))
            .ToList();
        return new Page(updates, start > 0 ? messages[start].Cursor : null, messages.Count, end > start ? start + 1 : 0, end - start);
    }

    static int Cost(Item i) => i.Kind == "tool_call" ? (i.Title?.Length ?? 0) + 20 : i.Text.Length;

    /// <summary>Messages, thoughts and tool calls in journal order, the chunks of one message merged.</summary>
    static List<Item> Items(IReadOnlyList<JsonNode> journal)
    {
        var items = new List<Item>();
        var tools = new Dictionary<string, Item>(StringComparer.Ordinal);
        Item? last = null; // the last message or thought, which a chunk of the same message continues
        for (var index = 0; index < journal.Count; index++)
        {
            if (journal[index] is not JsonObject u) continue;
            var kind = (string?)u["sessionUpdate"];
            switch (kind)
            {
                case "user_message_chunk" or "agent_message_chunk" or "agent_thought_chunk":
                {
                    var id = (string?)u["messageId"];
                    // Chunks of one message share its ID; chunks journaled before IDs existed are one
                    // message while they follow each other.
                    if (last is null || last.Kind != kind || last.MessageId != id || !ReferenceEquals(items[^1], last))
                    {
                        last = new Item(kind, index) { MessageId = id, Meta = u["_meta"]?.DeepClone() as JsonObject };
                        items.Add(last);
                    }
                    last.Text.Append(BlockText(u["content"]));
                    break;
                }
                case "tool_call" or "tool_call_update":
                {
                    var id = (string?)u["toolCallId"] ?? "";
                    if (!tools.TryGetValue(id, out var tool))
                    {
                        tool = new Item("tool_call", index) { ToolCallId = id, Meta = u["_meta"]?.DeepClone() as JsonObject };
                        tools[id] = tool;
                        items.Add(tool);
                    }
                    tool.Title = (string?)u["title"] ?? tool.Title;
                    tool.ToolKind = (string?)u["kind"] ?? tool.ToolKind;
                    tool.Status = (string?)u["status"] ?? tool.Status;
                    last = null;
                    break;
                }
            }
        }
        return items;
    }

    static string BlockText(JsonNode? block) => (string?)block?["type"] switch
    {
        "text" => (string?)block!["text"] ?? "",
        "resource_link" => $"@{(string?)block!["uri"]}",
        "resource" => $"@{(string?)block!["resource"]?["uri"]}",
        null => "",
        var type => $"[{type}]",
    };

    static JsonObject ToUpdate(Item i, int? room)
    {
        var update = new JsonObject { ["sessionUpdate"] = i.Kind };
        if (i.Kind == "tool_call")
        {
            update["toolCallId"] = i.ToolCallId;
            update["title"] = i.Title ?? "tool";
            if (i.ToolKind is not null) update["kind"] = i.ToolKind;
            if (i.Status is not null) update["status"] = i.Status;
        }
        else
        {
            var text = i.Text.ToString();
            update["content"] = new JsonObject { ["type"] = "text", ["text"] = room is { } r && text.Length > r ? CutMiddle(text, r) : text };
            if (i.MessageId is not null) update["messageId"] = i.MessageId;
        }
        if (i.Meta is not null) update["_meta"] = i.Meta.DeepClone();
        return update;
    }

    /// <summary>Keeps the start and the end of a text that is too long, with a note of what was cut.</summary>
    public static string CutMiddle(string text, int room)
    {
        if (text.Length <= room) return text;
        var keep = Math.Max(room - 40, 2);
        var head = keep / 2;
        var tail = keep - head;
        return $"{text[..head]}\n[… {text.Length - head - tail} characters cut …]\n{text[^tail..]}";
    }
}
