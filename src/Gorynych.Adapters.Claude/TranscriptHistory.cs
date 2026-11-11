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
/// conversation as it is now is a chain back from its last entry, not the file's order: a rewind
/// starts a new branch further down the file, and two processes on one session write two
/// branches. Which branch is the conversation is decided as <c>claude --resume</c> decides it
/// (<see cref="LastEntry"/>). A mark, <c>&lt;uuid&gt;@&lt;offset&gt;</c>, names the chain's last
/// entry and where its line starts, so a catch-up reads only what follows it.
/// <para>
/// Lines are read as a stream and only their top-level keys are taken (<c>uuid</c>, parents,
/// <c>type</c>, the sidechain flag, a <c>last-prompt</c> entry's <c>leafUuid</c> and
/// <c>explicit</c>, a compaction's <c>subtype</c>); an entry's body is parsed only when it is part
/// of the conversation sent, and its updates go out as they are made. Entries become ACP updates as
/// the live stream maps them, with the same IDs. Anything unexpected fails open: no replay, never an
/// exception. Reads only.
/// </para>
/// </summary>
internal static class TranscriptHistory
{
    /// <summary>Whether the updates sent follow the mark (else they are the whole conversation), the new mark, and how much was read.</summary>
    public sealed record Replay(bool FromMark, string? Mark, long BytesRead, int Sent);

    /// <summary>
    /// The most a catch-up reads of a transcript in one pass (from the mark on, or the whole file)
    /// before it gives up (fails open).
    /// </summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>A longer line is left out (its entry is not taken).</summary>
    const int MaxLine = 64 * 1024 * 1024;

    const int Block = 64 * 1024;
    const string Reminder = "<system-reminder>";
    const string ReminderEnd = "</system-reminder>";

    sealed class TooLarge() : Exception("the transcript is larger than a catch-up reads");

    /// <summary>
    /// A line's top-level keys. <paramref name="Leaf"/> is set on a <c>last-prompt</c> entry that
    /// names the conversation's last entry (a non-empty <c>leafUuid</c>), <paramref name="Clears"/>
    /// on one that empties it (<c>leafUuid: null</c>, <c>explicit: true</c>), <paramref name="Explicit"/>
    /// when it says <c>explicit: true</c>; <paramref name="Boundary"/> marks a compaction's boundary.
    /// </summary>
    readonly record struct Entry(long Start, int Length, string? Uuid, string? Parent, string? Type, bool Sidechain,
        string? Leaf = null, bool Clears = false, bool Explicit = false, bool Boundary = false)
    {
        /// <summary>A part of the main conversation: a user, assistant or system entry, not a subagent's.</summary>
        public bool InChain => Uuid is not null && !Sidechain && (Type is "user" or "assistant" or "system");

        /// <summary>One of the session's messages, as Claude loads them (a subagent's too; progress entries are not).</summary>
        public bool IsMessage => Uuid is not null && Type is "user" or "assistant" or "system" or "attachment";

        /// <summary>A message of the main conversation, an attachment too: it ends a checkpoint's explicit hold.</summary>
        public bool IsRow => IsMessage && !Sidechain;

        /// <summary>Whether it sets or clears Claude's checkpoint: a <c>last-prompt</c> that names a leaf or empties it, or a compaction.</summary>
        public bool SetsCheckpoint => Leaf is not null || Clears || Boundary;
    }

    /// <summary>
    /// The transcript's mark now: the last entry of its conversation; null when it has none, is not
    /// there or cannot be read. A last line without its newline is still being written and is left
    /// out, as a replay leaves it out. Read backwards, as far as the last <c>last-prompt</c> entry
    /// and the entry it names (all of the file when it has none).
    /// </summary>
    public static string? Mark(string path)
    {
        try
        {
            using var file = Open(path);
            using var lines = Backwards(file).GetEnumerator();
            var seen = new List<Entry>(); // last first
            var enough = new Enough();
            var whole = true;
            while (lines.MoveNext())
            {
                seen.Add(lines.Current);
                if (enough.After(lines.Current)) { whole = false; break; }
            }
            var (known, last) = LastEntry(InFileOrder(seen), whole);
            if (!known)
            {
                while (lines.MoveNext()) seen.Add(lines.Current);
                (_, last) = LastEntry(InFileOrder(seen), whole: true);
            }
            return last is { } entry ? MarkOf(entry) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the conversation after <paramref name="mark"/>: the chain from its last entry back to the
    /// mark. When the chain does not reach the mark (a rewind, or another process's branch, made
    /// another branch the conversation), or no mark is given, the whole chain from its root. Null
    /// when the transcript cannot be read, or is larger than <paramref name="maxBytes"/>: the service
    /// then keeps its journal as it is.
    /// </summary>
    public static async Task<Replay?> ReadAsync(string path, string? mark, Func<SessionUpdate, Task> send, Action<string>? log = null, long maxBytes = MaxBytes)
    {
        try
        {
            using var file = Open(path);
            long read = 0;
            var parsed = Parse(mark);
            var markUuid = parsed?.Uuid;
            if (parsed is { } at)
            {
                var (uuid, offset) = at;
                // Read once from the mark's line: it must be the first entry there, where the mark says.
                var fromMark = offset < file.Length ? Entries(file, offset, ref read, maxBytes) : [];
                if (fromMark.Count > 0 && fromMark[0].Start == offset && fromMark[0].Uuid == uuid)
                {
                    if (AfterMark(fromMark) is { } leaf)
                    {
                        if (leaf.Uuid == uuid) return new Replay(true, mark, read, 0);
                        if (ChainBack(leaf, fromMark, until: uuid) is { } chain)
                            return new Replay(true, MarkOf(leaf), read, await SendAsync(file, chain, send, log));
                    }
                    log?.Invoke("what follows the mark does not continue from it (a rewind, or another process's branch): reading the whole transcript");
                }
                else log?.Invoke($"the mark's line is not at {offset}: reading the whole transcript");
            }
            var all = Entries(file, 0, ref read, maxBytes);
            if (LastEntry(all, whole: true).Last is not { } last) return new Replay(false, null, read, 0);
            if (markUuid is not null)
            {
                if (last.Uuid == markUuid) return new Replay(true, MarkOf(last), read, 0);
                if (ChainBack(last, all, until: markUuid) is { } chain)
                    return new Replay(true, MarkOf(last), read, await SendAsync(file, chain, send, log));
                log?.Invoke("the conversation does not reach the mark: replaying all of it");
            }
            return new Replay(false, MarkOf(last), read, await SendAsync(file, ChainBack(last, all, until: null)!, send, log));
        }
        catch (TooLarge e)
        {
            log?.Invoke(e.Message);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"the transcript could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The transcript's mark at a turn's end. Claude may still be writing the turn's last entries
    /// when its result comes, so it is taken once two reads, <paramref name="wait"/> apart (50 ms),
    /// agree; at most ten reads after the first.
    /// </summary>
    public static async Task<string?> SettledMarkAsync(string path, Func<Task>? wait = null)
    {
        wait ??= () => Task.Delay(50);
        var mark = Mark(path);
        for (var i = 0; i < 10 && mark is not null; i++)
        {
            await wait();
            var again = Mark(path);
            if (again == mark) break;
            mark = again;
        }
        return mark;
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
        for (Entry? at = leaf; at is { } entry;)
        {
            if (!seen.Add(entry.Uuid!)) return null; // a loop: not a chain
            chain.Add(entry);
            var parent = entry.Parent;
            if (parent is null) return until is null ? Reversed(chain) : null;
            if (parent == until) return Reversed(chain);
            at = byUuid.TryGetValue(parent, out var p) ? p : null;
            if (at is null) return until is null ? Reversed(chain) : null; // a parent the file lacks: its root, as far as it goes
        }
        return null;
    }

    static List<Entry> Reversed(List<Entry> chain)
    {
        chain.Reverse();
        return chain;
    }

    static List<Entry> InFileOrder(List<Entry> lastFirst)
    {
        var entries = new List<Entry>(lastFirst.Count);
        for (var i = lastFirst.Count - 1; i >= 0; i--) entries.Add(lastFirst[i]);
        return entries;
    }

    /// <summary>
    /// The conversation's last entry, as <c>claude --resume</c> picks it (Claude Code 2.1.302; the
    /// Agent SDK's <c>getSessionMessages</c> does not read <c>last-prompt</c> and takes the file's
    /// last branch). Claude keeps a checkpoint: the leaf the last <c>last-prompt</c> entry names,
    /// which every process writes for its own branch when it closes, and a rewind writes as
    /// <c>explicit</c>. The conversation ends at the checkpoint, unless it is not explicit and the
    /// file's last main entry continues from it; then at that entry, as it does with no checkpoint.
    /// <list type="bullet">
    /// <item>A main entry after the checkpoint's <c>last-prompt</c> ends its explicit hold; a
    /// <c>last-prompt</c> for the same leaf right after an explicit one keeps it (a rewind, then the
    /// session closed).</item>
    /// <item><c>leafUuid: null</c> with <c>explicit: true</c> empties the conversation until the
    /// next main entry; a compaction drops the checkpoint; one that names no entry of the file (a
    /// rewound session's next turn names the zero UUID) is no checkpoint.</item>
    /// </list>
    /// Not mirrored, as the probe never met them: Claude's newest-timestamp choice when the file has
    /// no <c>last-prompt</c> at all and an entry was written before its parent, its leaf-set choice
    /// after a compaction that keeps a segment, and its parallel-tool-call batch check.
    /// <para>
    /// <paramref name="entries"/> are in the file's order and end where it ends. When they are not all
    /// of it (<paramref name="whole"/> false) and do not settle the answer, <c>Known</c> is false.
    /// An entry is taken to come after its parent in the file. <c>Last</c> is null for an empty
    /// conversation.
    /// </para>
    /// </summary>
    static (bool Known, Entry? Last) LastEntry(List<Entry> entries, bool whole)
    {
        string? checkpoint = null;
        var cleared = false;
        var settled = whole;                // whether the checkpoint is known
        bool? isExplicit = whole ? false : null;
        Entry? last = null;
        foreach (var e in entries)
        {
            if (e.IsRow)
            {
                isExplicit = false;
                cleared = false;
                if (e.Boundary) { checkpoint = null; settled = true; }
                if (e.InChain) last = e;
            }
            else if (e.Leaf is { } leaf)
            {
                isExplicit = e.Explicit ? true : isExplicit is { } was ? was && leaf == checkpoint : null;
                checkpoint = leaf;
                cleared = false;
                settled = true;
            }
            else if (e.Clears)
            {
                checkpoint = null;
                cleared = true;
                isExplicit = false;
                settled = true;
            }
        }
        if (!settled) return (false, null);
        if (cleared) return (true, null);
        if (last is not { } end) return (whole, null);
        if (checkpoint is null || checkpoint == end.Uuid) return (true, end);
        var byUuid = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Uuid is { } id) byUuid[id] = e;
        if (!byUuid.TryGetValue(checkpoint, out var named) || !named.IsMessage) return whole ? (true, end) : (false, null);
        if (isExplicit == true || !Descends(end, checkpoint, byUuid)) return (true, named);
        return isExplicit is null ? (false, null) : (true, end);
    }

    /// <summary>Whether <paramref name="ancestor"/> is on the chain back from <paramref name="entry"/>.</summary>
    static bool Descends(Entry entry, string ancestor, Dictionary<string, Entry> byUuid)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var at = entry.Parent; at is not null && seen.Add(at); at = byUuid.TryGetValue(at, out var e) ? e.Parent : null)
            if (at == ancestor) return true;
        return false;
    }

    /// <summary>
    /// The conversation's last entry from the mark's line on (<paramref name="fromMark"/>, the mark's
    /// entry first), or null when what follows the mark does not settle it. With no
    /// <c>last-prompt</c> after the mark Claude's checkpoint is the one the mark was taken under, so
    /// the conversation ends at the last main entry if that continues from the mark (the caller
    /// checks), or at the mark when nothing was added.
    /// </summary>
    static Entry? AfterMark(List<Entry> fromMark)
    {
        var tail = fromMark.Skip(1);
        if (!tail.Any(e => e.SetsCheckpoint))
            return tail.LastOrDefault(e => e.InChain) is { Uuid: not null } last ? last : fromMark[0];
        return LastEntry(fromMark, whole: false) is (true, { } leaf) ? leaf : null;
    }

    /// <summary>
    /// Read backwards, whether the entries met so far settle the conversation's last entry: the last
    /// main one, the last entry that sets the checkpoint, a main entry before it (so its explicit hold
    /// is known), and the entry the checkpoint names.
    /// </summary>
    sealed class Enough
    {
        bool last, found, anchored, named;
        string? leaf;

        public bool After(Entry e)
        {
            if (e.InChain) last = true;
            if (!found)
            {
                if (!e.SetsCheckpoint) return false;
                found = true;
                leaf = e.Leaf;
            }
            else
            {
                if (e.IsRow) anchored = true;
                if (e.Uuid is not null && e.Uuid == leaf) named = true;
            }
            return last && (leaf is null || anchored && named); // a compaction or an emptied conversation needs nothing before it
        }
    }

    /// <summary>
    /// The entries of the file's whole lines, last first, each projected; a last line without its
    /// newline is still being written and is left out. Gives up past <see cref="MaxBytes"/>.
    /// </summary>
    static IEnumerable<Entry> Backwards(FileStream file)
    {
        // Read backwards a block at a time; a line that spans blocks is kept as its pieces until its start is found.
        var pieces = new List<byte[]>();
        var piecesLength = 0L;
        var pos = file.Length;
        long read = 0;
        var ended = false; // past the file's last newline: the lines from here on are whole
        while (pos > 0)
        {
            var size = (int)Math.Min(Block, pos);
            pos -= size;
            var block = new byte[size];
            file.Position = pos;
            file.ReadExactly(block);
            if ((read += size) > MaxBytes) throw new TooLarge();
            var lineEnd = size;
            for (var i = size - 1; i >= 0; i--)
            {
                if (block[i] != '\n') continue;
                if (ended && Project(Joined(block, i + 1, lineEnd, pieces, piecesLength), pos + i + 1) is { } entry) yield return entry;
                ended = true;
                pieces.Clear();
                piecesLength = 0;
                lineEnd = i;
            }
            if (ended && lineEnd > 0)
            {
                pieces.Insert(0, block[..lineEnd]);
                piecesLength += lineEnd;
            }
            if (piecesLength > MaxLine) { pieces.Clear(); piecesLength = 0; } // a line too long to take
        }
        if (ended && Project(Joined([], 0, 0, pieces, piecesLength), 0) is { } first) yield return first;
    }

    /// <summary>
    /// The entries of the lines from <paramref name="offset"/> on (at most <paramref name="limit"/>),
    /// each projected, never kept whole. Adds what it reads to <paramref name="read"/>; gives up past
    /// <paramref name="maxBytes"/> read in this pass.
    /// </summary>
    static List<Entry> Entries(FileStream file, long offset, ref long read, long maxBytes, int limit = int.MaxValue)
    {
        var entries = new List<Entry>();
        var most = read + maxBytes;
        file.Position = offset;
        var buffer = new byte[Block];
        var line = new MemoryStream();
        var lineStart = offset;
        var bufferStart = offset;
        var tooLong = false;
        int got;
        while ((got = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            if ((read += got) > most) throw new TooLarge();
            var from = 0;
            for (var i = 0; i < got; i++)
            {
                if (buffer[i] != '\n') continue;
                if (!tooLong)
                {
                    line.Write(buffer, from, i - from);
                    if (Project(line.ToArray(), lineStart) is { } entry) entries.Add(entry);
                    if (entries.Count >= limit) return entries;
                }
                line.SetLength(0);
                tooLong = false;
                lineStart = bufferStart + i + 1;
                from = i + 1;
            }
            bufferStart += got;
            if (!tooLong)
            {
                line.Write(buffer, from, got - from);
                if (line.Length > MaxLine) { tooLong = true; line.SetLength(0); }
            }
        }
        // A last line without its newline is still being written: left out.
        return entries;
    }

    static byte[] Joined(byte[] block, int from, int to, List<byte[]> pieces, long piecesLength)
    {
        var all = new byte[to - from + piecesLength];
        block.AsSpan(from, to - from).CopyTo(all);
        var at = to - from;
        foreach (var piece in pieces)
        {
            piece.CopyTo(all, at);
            at += piece.Length;
        }
        return all;
    }

    /// <summary>The entry a line holds, from its top-level keys only; null when it is not a JSON object.</summary>
    static Entry? Project(byte[] line, long start)
    {
        if (line.Length == 0) return null;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? uuid = null, parent = null, logical = null, type = null, subtype = null, leaf = null;
            bool sidechain = false, leafNull = false, isExplicit = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "uuid": uuid = Text(ref reader); break;
                    case "parentUuid": parent = Text(ref reader); break;
                    case "logicalParentUuid": logical = Text(ref reader); break;
                    case "type": type = Text(ref reader); break;
                    case "subtype": subtype = Text(ref reader); break;
                    case "isSidechain": sidechain = reader.TokenType == JsonTokenType.True; break;
                    case "leafUuid": leafNull = reader.TokenType == JsonTokenType.Null; leaf = Text(ref reader); break;
                    case "explicit": isExplicit = reader.TokenType == JsonTokenType.True; break;
                    default: reader.Skip(); break;
                }
            }
            var checkpoint = type == "last-prompt";
            return new Entry(start, line.Length, uuid, parent ?? logical, type, sidechain,
                Leaf: checkpoint && leaf is { Length: > 0 } ? leaf : null,
                Clears: checkpoint && leafNull && isExplicit,
                Explicit: checkpoint && isExplicit,
                Boundary: type == "system" && subtype == "compact_boundary");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Text(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        reader.Skip();
        return null;
    }

    /// <summary>Parses each entry of the chain in turn and sends its updates; an entry of an unexpected shape is left out.</summary>
    static async Task<int> SendAsync(FileStream file, List<Entry> chain, Func<SessionUpdate, Task> send, Action<string>? log)
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        var sent = 0;
        var odd = 0;
        foreach (var entry in chain)
        {
            List<SessionUpdate> updates;
            try
            {
                var bytes = new byte[entry.Length];
                file.Position = entry.Start;
                file.ReadExactly(bytes);
                updates = JsonNode.Parse(bytes) is JsonObject json ? UpdatesOf(json, entry.Uuid!, tools) : [];
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or IOException)
            {
                odd++;
                continue;
            }
            foreach (var update in updates)
            {
                await send(update);
                sent++;
            }
        }
        if (odd > 0) log?.Invoke($"{odd} transcript entries of an unexpected shape were left out");
        return sent;
    }

    /// <summary>One entry's updates, as the live stream maps Claude's messages (HeadlessSession).</summary>
    static List<SessionUpdate> UpdatesOf(JsonObject json, string uuid, Dictionary<string, string> tools)
    {
        var updates = new List<SessionUpdate>();
        var message = json["message"] as JsonObject;
        switch (Str(json["type"]))
        {
            case "user" when !Flag(json["isMeta"]) && !Flag(json["isCompactSummary"]) && message is not null:
            {
                var asStream = new JsonObject { ["message"] = WithoutReminders(message), ["tool_use_result"] = json["toolUseResult"]?.DeepClone() };
                if (StreamJson.UserText(asStream) is { Length: > 0 } text && !IsNotTyped(text))
                    updates.Add(new SessionUpdateUserMessageChunk { Content = new TextContent { Text = text }, MessageId = uuid });
                updates.AddRange(StreamJson.ToolResults(asStream, id => tools.GetValueOrDefault(id)));
                break;
            }
            case "assistant" when message is not null && message["content"] is JsonArray blocks:
            {
                var id = Str(message["id"]) ?? uuid;
                foreach (var block in blocks.OfType<JsonObject>())
                {
                    switch (Str(block["type"]))
                    {
                        case "thinking" when Str(block["thinking"]) is { Length: > 0 } thinking:
                            updates.Add(new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = thinking }, MessageId = id });
                            break;
                        case "text" when Str(block["text"]) is { Length: > 0 } text:
                            updates.Add(ClaudeHooks.TextChunk(text, id));
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
        return updates;
    }

    static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// The message without the system reminders Claude Code wrote into it: a text block that is only
    /// a reminder is left out, and reminders a string starts with are cut off. A reminder inside text
    /// the person wrote stays.
    /// </summary>
    static JsonObject WithoutReminders(JsonObject message)
    {
        var copy = (JsonObject)message.DeepClone();
        switch (copy["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                copy["content"] = LeadingRemindersCut(text);
                break;
            case JsonArray blocks:
                foreach (var block in blocks.OfType<JsonObject>().ToList())
                    if (Str(block["type"]) == "text" && Str(block["text"]) is { } t && LeadingRemindersCut(t).Trim().Length == 0 && t.Contains(Reminder, StringComparison.Ordinal))
                        blocks.Remove(block);
                break;
        }
        return copy;
    }

    static string LeadingRemindersCut(string text)
    {
        var rest = text;
        while (rest.TrimStart().StartsWith(Reminder, StringComparison.Ordinal) && rest.IndexOf(ReminderEnd, StringComparison.Ordinal) is var end && end >= 0)
            rest = rest[(end + ReminderEnd.Length)..];
        return ReferenceEquals(rest, text) ? text : rest.TrimStart();
    }

    /// <summary>Text in a user entry that the person did not type: a slash command's echo or output, an interruption.</summary>
    static bool IsNotTyped(string text) =>
        text.StartsWith("<command-", StringComparison.Ordinal) || text.StartsWith("<local-command-", StringComparison.Ordinal)
        || text.StartsWith("[Request interrupted by user", StringComparison.Ordinal);
}
