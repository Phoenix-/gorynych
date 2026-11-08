using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gorynych.Service;

/// <summary>
/// Reads a page of a session's history from its journal for <c>_gorynych/read</c> and
/// <c>read_session</c>: messages with the chunks of each merged into one, tool calls with their
/// last status folded in, thoughts on request, newest page first (docs/service-protocol.md,
/// "Reading a session"). Pure, so it is tested on journals made up in the test.
/// </summary>
internal static partial class SessionReader
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
        public JsonObject? RawInput { get; set; }
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
        // Each message carries the tool calls and thoughts that follow it, up to the next message, except
        // that an agent message takes those between it and the user message before it: they are its
        // turn's (Claude's hooks report a tool call before the text that announces it). The first
        // message also takes anything before it.
        int Position(int message) => message < messages.Count ? items.IndexOf(messages[message]) : items.Count;
        var begin = new int[messages.Count + 1];
        for (var i = 0; i < messages.Count; i++)
            begin[i] = i == 0 ? 0
                : messages[i].Kind == "agent_message_chunk" && messages[i - 1].Kind == "user_message_chunk" ? Position(i - 1) + 1
                : Position(i);
        begin[messages.Count] = items.Count;
        var cuts = new Dictionary<Item, int>();
        var start = end;
        var left = budget ?? int.MaxValue;
        while (start > 0 && end - start < limit)
        {
            var candidate = start - 1;
            var segment = items.Skip(begin[candidate]).Take(begin[candidate + 1] - begin[candidate])
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

        var from = begin[start];
        var to = cursor is null ? items.Count : begin[end];
        var updates = items.Skip(from).Take(to - from)
            .Where(i => thoughts || i.Kind != "agent_thought_chunk")
            .Select(i => ToUpdate(i, cuts.TryGetValue(i, out var room) ? room : null))
            .ToList();
        return new Page(updates, start > 0 ? messages[start].Cursor : null, messages.Count, end > start ? start + 1 : 0, end - start);
    }

    static int Cost(Item i) => i.Kind == "tool_call"
        ? (i.Title?.Length ?? 0) + 20 + (PeerInput(i)?.Sum(p => p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length : 0) ?? 0)
        : i.Text.Length;

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
                    if (u["rawInput"] is JsonObject raw) tool.RawInput = raw.DeepClone().AsObject();
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
            // What a session asked, answered, started or archived through the gorynych server: its
            // input (PeerInput). Other tools' input stays out.
            if (PeerInput(i) is { } kept) update["rawInput"] = kept;
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

    /// <summary>About how much of a text in a gorynych tool's input a page keeps, except a question or an answer.</summary>
    public const int PeerInputLimit = 300;

    /// <summary>
    /// The input of a gorynych tool's call that says what a session decided: <c>ask_agent</c>'s
    /// question and <c>reply</c>'s answer whole, since they are messages (for Kimi, whose turn may
    /// end with a word, the reply holds the only copy of its answer), and any other text cut at a
    /// word near <see cref="PeerInputLimit"/>, saying how much is left out. Null for other tools.
    /// </summary>
    static JsonObject? PeerInput(Item i)
    {
        if (i.RawInput is not { } raw || PeerTool(i.Title) is not { } tool) return null;
        var kept = new JsonObject();
        foreach (var (key, value) in raw)
            kept[key] = value is JsonValue v && v.TryGetValue<string>(out var text) && (tool, key) is not (("reply", "text") or ("ask_agent", "question"))
                ? CutAtWord(text, PeerInputLimit)
                : value?.DeepClone();
        return kept;
    }

    /// <summary>
    /// A text longer than <paramref name="limit"/> cut at the last space before it (inside a word
    /// only when there is none in its second half), with <c>… (+N characters)</c> for the rest.
    /// </summary>
    public static string CutAtWord(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var cut = limit;
        while (cut > limit / 2 && !char.IsWhiteSpace(text[cut])) cut--;
        if (cut == limit / 2) cut = char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
        var kept = text[..cut].TrimEnd();
        return $"{kept}… (+{text.Length - kept.Length} characters)";
    }

    /// <summary>
    /// A tool's name and its MCP server, from a tool call's title in either form the agents give it:
    /// <c>mcp__gorynych__reply</c> (Kimi) or <c>reply (gorynych)</c> (Claude); for any other title, the
    /// title and no server.
    /// </summary>
    public static (string Tool, string? Server) ToolName(string? title)
    {
        title ??= "tool";
        if (McpName().Match(title) is { Success: true } m) return (m.Groups[2].Value, m.Groups[1].Value);
        if (ShownName().Match(title) is { Success: true } s) return (s.Groups[1].Value, s.Groups[2].Value);
        return (title, null);
    }

    /// <summary>The gorynych server's tool a title names whose input says what a session decided; null for any other.</summary>
    public static string? PeerTool(string? title) =>
        ToolName(title) is ("ask_agent" or "reply" or "spawn_session" or "archive_session" or "restore_session", "gorynych") and var (tool, _) ? tool : null;

    [GeneratedRegex("^mcp__(.+?)__(.+)$")]
    private static partial Regex McpName();

    [GeneratedRegex(@"^([^\s()]+) \(([^()]+)\)$")]
    private static partial Regex ShownName();

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
