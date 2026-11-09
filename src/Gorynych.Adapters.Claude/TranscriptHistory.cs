using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Claude's own history of a session, read from its transcript, for the service's journal to catch
/// up with what was done outside Gorynych (<c>claude --resume</c>, Claude Desktop; docs/adapters.md,
/// "History made outside Gorynych"). The transcript is append-only JSONL: every entry has a
/// <c>uuid</c> and its <c>parentUuid</c> (<c>logicalParentUuid</c> across a compaction), and the
/// conversation as it is now is the chain from the last entry back, not the file's order: a rewind
/// starts a new branch further down the file. A mark, <c>&lt;uuid&gt;@&lt;offset&gt;</c>, names the
/// chain's last entry and where its line starts, so a catch-up reads only what follows it. Entries
/// become ACP updates as the live stream maps them, with the same IDs. Reads only.
/// </summary>
internal static class TranscriptHistory
{
    /// <summary>The updates, whether they follow the mark (else they are the whole conversation), and the new mark.</summary>
    public sealed record Replay(List<SessionUpdate> Updates, bool FromMark, string? Mark, long BytesRead);

    sealed record Entry(long Start, JsonObject Json)
    {
        public string? Uuid => (string?)Json["uuid"];

        public string? Parent => (string?)Json["parentUuid"] ?? (string?)Json["logicalParentUuid"];

        /// <summary>A part of the main conversation: a user, assistant or system entry, not a subagent's.</summary>
        public bool InChain => Uuid is not null && (bool?)Json["isSidechain"] != true && (string?)Json["type"] is "user" or "assistant" or "system";
    }

    /// <summary>The transcript's mark now: the last entry of its conversation; null when it has none or is not there.</summary>
    public static string? Mark(string path)
    {
        try
        {
            using var file = Open(path);
            const int Block = 64 * 1024;
            var end = file.Length;
            var bytes = Array.Empty<byte>();
            while (end > 0)
            {
                var from = Math.Max(0, end - Block);
                var block = new byte[end - from];
                file.Position = from;
                file.ReadExactly(block);
                bytes = [.. block, .. bytes];
                end = from;
                // The first line of what is read may be cut, unless the file starts there.
                var lines = Lines(bytes, end);
                for (var i = lines.Count - 1; i >= (end == 0 ? 0 : 1); i--)
                    if (lines[i].InChain) return MarkOf(lines[i]);
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The conversation after <paramref name="mark"/>: the chain from its last entry back to the mark.
    /// When the chain does not reach the mark (a rewind made another branch the conversation), or no
    /// mark is given, the whole chain from its root. Null when the transcript cannot be read.
    /// </summary>
    public static Replay? Read(string path, string? mark)
    {
        try
        {
            using var file = Open(path);
            long read = 0;
            List<Entry>? all = null;
            if (Parse(mark) is { } parsed)
            {
                var (uuid, offset) = parsed;
                List<Entry>? tail = null;
                if (offset < file.Length && LineAt(file, offset, ref read) is { } line && line.Uuid == uuid)
                    tail = Entries(file, offset, ref read).Skip(1).ToList();
                else
                {
                    // Not where it was: the whole file, once.
                    all = Entries(file, 0, ref read);
                    var at = all.FindIndex(e => e.Uuid == uuid);
                    if (at >= 0) tail = all.Skip(at + 1).ToList();
                }
                if (tail is not null)
                {
                    if (tail.LastOrDefault(e => e.InChain) is not { } leaf) return new Replay([], true, mark, read);
                    if (ChainBack(leaf, tail, until: uuid) is { } chain) return new Replay(Updates(chain), true, MarkOf(leaf), read);
                }
            }
            all ??= Entries(file, 0, ref read);
            if (all.LastOrDefault(e => e.InChain) is not { } last) return new Replay([], false, null, read);
            return new Replay(Updates(ChainBack(last, all, until: null)!), false, MarkOf(last), read);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static string MarkOf(Entry entry) => $"{entry.Uuid}@{entry.Start}";

    static (string Uuid, long Offset)? Parse(string? mark)
    {
        if (mark is null || mark.LastIndexOf('@') is var at && at <= 0) return null;
        return long.TryParse(mark.AsSpan(at + 1), out var offset) && offset >= 0 ? (mark[..at], offset) : null;
    }

    /// <summary>
    /// The chain from <paramref name="leaf"/> back, oldest first: to the entry after <paramref name="until"/>,
    /// or to its root when <paramref name="until"/> is null. Null when it leaves <paramref name="entries"/> first.
    /// </summary>
    static List<Entry>? ChainBack(Entry leaf, List<Entry> entries, string? until)
    {
        var byUuid = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Uuid is { } id) byUuid[id] = e;
        var chain = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Entry? at = leaf; at is not null; )
        {
            if (!seen.Add(at.Uuid!)) return null; // a loop: not a chain
            chain.Add(at);
            var parent = at.Parent;
            if (parent is null) return until is null ? Reversed(chain) : null;
            if (parent == until) return Reversed(chain);
            at = byUuid.GetValueOrDefault(parent);
            if (at is null) return until is null ? Reversed(chain) : null; // a parent the file lacks: its root, as far as it goes
        }
        return null;
    }

    static List<Entry> Reversed(List<Entry> chain)
    {
        chain.Reverse();
        return chain;
    }

    static Entry? LineAt(FileStream file, long offset, ref long read)
    {
        file.Position = offset;
        var bytes = new List<byte>();
        int b;
        while ((b = file.ReadByte()) >= 0 && b != '\n') bytes.Add((byte)b);
        read += bytes.Count + 1;
        return Lines([.. bytes], offset).FirstOrDefault();
    }

    static List<Entry> Entries(FileStream file, long offset, ref long read)
    {
        file.Position = offset;
        var bytes = new byte[file.Length - offset];
        file.ReadExactly(bytes);
        read += bytes.Length;
        return Lines(bytes, offset);
    }

    /// <summary>The JSON lines of <paramref name="bytes"/>, which start at <paramref name="offset"/> of the file; a line that does not parse (being written) is left out.</summary>
    static List<Entry> Lines(byte[] bytes, long offset)
    {
        var entries = new List<Entry>();
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != '\n') continue;
            if (i > start)
            {
                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(bytes, start, i - start)) is JsonObject json)
                        entries.Add(new Entry(offset + start, json));
                }
                catch (JsonException) { }
            }
            start = i + 1;
        }
        return entries;
    }

    /// <summary>The conversation's entries as ACP updates, as the live stream maps Claude's messages (HeadlessSession).</summary>
    static List<SessionUpdate> Updates(List<Entry> chain)
    {
        var updates = new List<SessionUpdate>();
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in chain)
        {
            var json = entry.Json;
            var message = json["message"] as JsonObject;
            switch ((string?)json["type"])
            {
                case "user" when (bool?)json["isMeta"] != true && (bool?)json["isCompactSummary"] != true && message is not null:
                {
                    var asStream = new JsonObject { ["message"] = message.DeepClone(), ["tool_use_result"] = json["toolUseResult"]?.DeepClone() };
                    if (StreamJson.UserText(asStream) is { } text && !IsNotTyped(text))
                        updates.Add(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = text }, MessageId = entry.Uuid });
                    updates.AddRange(StreamJson.ToolResults(asStream, id => tools.GetValueOrDefault(id)));
                    break;
                }
                case "assistant" when message is not null && message["content"] is JsonArray blocks:
                {
                    var id = (string?)message["id"] ?? entry.Uuid;
                    foreach (var block in blocks.OfType<JsonObject>())
                    {
                        switch ((string?)block["type"])
                        {
                            case "thinking" when (string?)block["thinking"] is { Length: > 0 } thinking:
                                updates.Add(new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = thinking }, MessageId = id });
                                break;
                            case "text" when (string?)block["text"] is { Length: > 0 } text:
                                updates.Add(ClaudeHooks.TextChunk(text, id!));
                                break;
                            case "tool_use":
                                foreach (var (call, tool, _) in StreamJson.ToolUses(new JsonObject { ["message"] = new JsonObject { ["content"] = new JsonArray(block.DeepClone()) } }))
                                {
                                    tools[call.ToolCallId] = tool;
                                    updates.Add(call);
                                }
                                break;
                        }
                    }
                    break;
                }
            }
        }
        return updates;
    }

    /// <summary>Text in a user entry that the person did not type: a slash command's echo or output, an interruption.</summary>
    static bool IsNotTyped(string text) =>
        text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command-", StringComparison.Ordinal)
        || text.StartsWith("[Request interrupted by user", StringComparison.Ordinal);
}
