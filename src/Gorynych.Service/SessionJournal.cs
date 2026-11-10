using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>
/// The bounded history of one session: every <c>session/update</c> payload but the list of
/// commands (<see cref="IsCommands"/>), one JSON object per line, replayed to a client on
/// <c>session/load</c>. When it grows past the limit,
/// consecutive text chunks of one message (one <c>messageId</c>) are merged and the oldest
/// entries dropped. Every entry has its position, <c>_meta["gorynych/position"]</c>, which grows
/// with each one appended and is never given twice: a client that resumes the session after one
/// gets what follows it (<see cref="ReadAfter"/>).
/// </summary>
public sealed class SessionJournal
{
    static readonly JsonSerializerOptions LineOptions = Gorynych.Acp.AcpJson.TextOptions;
    static readonly HashSet<string> ChunkKinds = ["agent_message_chunk", "agent_thought_chunk", "user_message_chunk"];

    /// <summary>Updates that only a turn produces; the rest (commands, modes, config) also come with an empty session.</summary>
    static readonly HashSet<string> TurnKinds = ["user_message_chunk", "agent_message_chunk", "agent_thought_chunk", "tool_call", "tool_call_update", "plan"];

    const string PositionKey = Gorynych.Acp.Hosting.GorynychProtocol.MetaPosition;

    /// <summary>
    /// How many of the newest entries compaction never merges: a client on the resumable link
    /// resumes after one of them, which must still stand alone under its position.
    /// </summary>
    internal const int TailKept = 256;

    readonly string path;
    readonly int maxEntries;
    readonly Lock gate = new();
    List<string>? lines;
    bool turns;
    long last; // the newest entry's position; valid once loaded

    public SessionJournal(string path, int maxEntries)
    {
        this.path = path;
        this.maxEntries = Math.Max(16, maxEntries);
    }

    public int Count
    {
        get { lock (gate) return Load().Count; }
    }

    /// <summary>
    /// True once compaction has dropped the journal's oldest entries, or a catch-up could not keep
    /// all that the agent's own history gained (<see cref="MarkTrimmed"/>). Kept in a marker file
    /// next to the journal, so it survives a restart without an entry that clients would be replayed.
    /// </summary>
    public bool Trimmed
    {
        get { lock (gate) return File.Exists(TrimmedPath); }
    }

    /// <summary>Marks the journal <see cref="Trimmed"/>: it misses a part of the session's history.</summary>
    public void MarkTrimmed()
    {
        lock (gate)
            try { File.WriteAllText(TrimmedPath, ""); }
            catch (IOException) { }
    }

    string TrimmedPath => path + ".trimmed";

    /// <summary>True when the journal holds any part of a turn: a message, a thought, a tool call or a plan.</summary>
    public bool HasTurns
    {
        get
        {
            lock (gate)
            {
                Load();
                return turns;
            }
        }
    }

    static bool IsTurn(JsonNode? node) => node is JsonObject o && TurnKinds.Contains((string?)o["sessionUpdate"] ?? "");

    /// <summary>A message, thought or user message chunk: the updates that carry a <c>messageId</c>.</summary>
    public static bool IsChunkKind(string? kind) => kind is not null && ChunkKinds.Contains(kind);

    /// <summary>An update that only a turn produces: a message, a thought, a tool call or a plan.</summary>
    public static bool IsTurnKind(string? kind) => kind is not null && TurnKinds.Contains(kind);

    /// <summary>
    /// An <c>available_commands_update</c>: the session's state, which the service keeps apart
    /// (<see cref="SessionFeed.PublishCommandsAsync"/>) and no longer journals.
    /// </summary>
    public static bool IsCommands(JsonNode? update) => update is JsonObject o && (string?)o["sessionUpdate"] == "available_commands_update";

    /// <summary>How many entries the journal keeps at most; past it, it compacts.</summary>
    public int MaxEntries => maxEntries;

    public void Append(JsonNode update) => AppendMany([update]);

    /// <summary>
    /// Appends several updates with one write (and at most one compaction). Each gets its position
    /// in its <c>_meta</c> here, on the object passed, so what is sent after it carries it too.
    /// </summary>
    public void AppendMany(IReadOnlyList<JsonNode> updates)
    {
        if (updates.Count == 0) return;
        lock (gate)
        {
            var all = Load();
            foreach (var update in updates)
                if (update is JsonObject o) SetPosition(o, ++last);
            var added = updates.Select(u => u.ToJsonString(LineOptions)).ToList();
            all.AddRange(added);
            turns |= updates.Any(IsTurn);
            if (all.Count > maxEntries)
            {
                Compact(all);
                Rewrite(all);
            }
            else
            {
                File.AppendAllText(path, string.Concat(added.Select(l => l + "\n")), Encoding.UTF8);
            }
        }
    }

    public List<JsonNode> ReadAll()
    {
        lock (gate)
            return Load().Select(TryParse).OfType<JsonNode>().ToList();
    }

    /// <summary>The newest entry's position, 0 for an empty journal.</summary>
    public long LastPosition
    {
        get
        {
            lock (gate)
            {
                Load();
                return last;
            }
        }
    }

    /// <summary>
    /// The entries after <paramref name="position"/>, oldest first, but the list of commands (the
    /// session's state, which a client gets apart). After a position compaction has dropped, what
    /// is left after it; after one past the end, nothing.
    /// </summary>
    public List<JsonNode> ReadAfter(long position)
    {
        lock (gate)
            return Load().Select(TryParse).OfType<JsonNode>().Where(e => PositionOf(e) > position && !IsCommands(e)).ToList();
    }

    /// <summary>An entry's position (<c>_meta["gorynych/position"]</c>), 0 when it has none.</summary>
    public static long PositionOf(JsonNode? entry) =>
        entry?["_meta"]?[PositionKey] is JsonValue v && v.TryGetValue<string>(out var text) && long.TryParse(text, out var p) && p > 0 ? p : 0;

    /// <summary>Reads a position a client sends back; false for anything the journal never gave.</summary>
    public static bool TryParsePosition(string? text, out long position) =>
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out position);

    /// <summary>A position as clients get it: an opaque string.</summary>
    public static string FormatPosition(long position) => position.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static void SetPosition(JsonObject entry, long position)
    {
        if (entry["_meta"] is not JsonObject meta) entry["_meta"] = meta = [];
        meta[PositionKey] = FormatPosition(position);
    }

    /// <summary>
    /// A <c>_meta</c> without the position and the place in a host's stream, for comparing two
    /// entries' own <c>_meta</c>; null when nothing else is in it.
    /// </summary>
    static JsonObject? OwnMeta(JsonNode? meta)
    {
        if (meta is not JsonObject o) return null;
        if (!o.ContainsKey(PositionKey) && !o.ContainsKey(HostSeqKey)) return o.Count == 0 ? null : o;
        var copy = o.DeepClone().AsObject();
        copy.Remove(PositionKey);
        copy.Remove(HostSeqKey);
        return copy.Count == 0 ? null : copy;
    }

    const string HostSeqKey = Gorynych.Acp.Hosting.GorynychProtocol.MetaHostSeq;

    /// <summary>Reads a host's <c>"&lt;hostId&gt;/&lt;seq&gt;"</c>.</summary>
    public static bool TryParseHostSeq(string? text, out string hostId, out long number)
    {
        hostId = "";
        number = 0;
        var slash = text?.LastIndexOf('/') ?? -1;
        if (slash <= 0 || !long.TryParse(text!.AsSpan(slash + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
            return false;
        hostId = text![..slash];
        return true;
    }

    /// <summary>The place in a host's stream of the newest entry from that host, 0 when it has none.</summary>
    public long LastHostSeq(string hostId)
    {
        lock (gate)
        {
            var lines = Load();
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (!lines[i].Contains(hostId, StringComparison.Ordinal)) continue;
                if (TryParse(lines[i]) is { } entry && entry["_meta"]?[HostSeqKey] is JsonValue v && v.TryGetValue<string>(out var mark)
                    && TryParseHostSeq(mark, out var id, out var number) && id == hostId)
                    return number;
            }
            return 0;
        }
    }

    static JsonNode? TryParse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }

    public void Delete()
    {
        lock (gate)
        {
            lines = [];
            turns = false;
            last = 0;
            mark = null;
            markRead = true;
            File.Delete(path);
            File.Delete(TrimmedPath);
            File.Delete(MarkPath);
            foreach (var kept in KeptHistories()) File.Delete(kept);
        }
    }

    string MarkPath => path + ".native";
    string? mark;
    bool markRead;

    /// <summary>
    /// How far into the agent's own history the journal goes: the adapter's opaque mark
    /// (<c>gorynych/historyMark</c>), or null when none is known. Kept in a file next to the journal.
    /// </summary>
    public string? NativeMark
    {
        get
        {
            lock (gate)
            {
                if (!markRead)
                {
                    try { mark = File.Exists(MarkPath) ? File.ReadAllText(MarkPath, Encoding.UTF8) : null; }
                    catch (IOException) { mark = null; }
                    markRead = true;
                }
                return mark is { Length: > 0 } ? mark : null;
            }
        }
        set
        {
            lock (gate)
            {
                if (markRead && mark == value) return;
                if (value is null) File.Delete(MarkPath);
                else Gorynych.Acp.Hosting.AtomicFile.WriteAllText(MarkPath, value, Encoding.UTF8);
                mark = value;
                markRead = true;
            }
        }
    }

    /// <summary>How many earlier journals <see cref="Replace"/> keeps.</summary>
    public const int KeptHistoryCount = 3;

    /// <summary>The earlier journals <see cref="Replace"/> kept, oldest first.</summary>
    public IReadOnlyList<string> KeptHistories()
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, Path.GetFileName(path) + ".*.before").Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Replaces the journal with <paramref name="entries"/> (the agent's history imported again),
    /// keeping the one it replaces next to it as <c>&lt;journal&gt;.&lt;UTC time&gt;-&lt;n&gt;.before</c>, the
    /// last <see cref="KeptHistoryCount"/> of them. The new <paramref name="mark"/> is written first:
    /// a crash before the journal is written then leaves the earlier journal and a mark past it,
    /// which a later open finds nothing after, rather than a second import. <paramref name="trimmed"/>
    /// says that the entries are only the end of the history. Every entry gets a new position past
    /// the old ones, on the object passed: the clients get them all again as new.
    /// </summary>
    public void Replace(IEnumerable<JsonNode> entries, DateTimeOffset now, string? mark, bool trimmed)
    {
        lock (gate)
        {
            Load(); // for the last position
            var list = entries.ToList();
            foreach (var entry in list)
                if (entry is JsonObject o) SetPosition(o, ++last);
            if (File.Exists(path))
            {
                // Numbered after any kept at the same instant, so they sort as they were made.
                var stamp = $"{path}.{now.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}";
                var n = KeptHistories().Where(k => k.StartsWith(stamp + "-", StringComparison.Ordinal))
                    .Select(k => int.TryParse(k.AsSpan(stamp.Length + 1, k.Length - stamp.Length - 1 - ".before".Length), out var i) ? i + 1 : 0)
                    .DefaultIfEmpty(0).Max();
                File.Copy(path, $"{stamp}-{n:D3}.before");
                var kept = KeptHistories();
                for (var i = 0; i < kept.Count - KeptHistoryCount; i++) File.Delete(kept[i]);
            }
            if (mark is not null)
            {
                Gorynych.Acp.Hosting.AtomicFile.WriteAllText(MarkPath, mark, Encoding.UTF8);
                this.mark = mark;
                markRead = true;
            }
            var all = list.Select(e => e.ToJsonString(LineOptions)).ToList();
            turns = list.Any(IsTurn);
            File.Delete(TrimmedPath); // the whole history again, unless it is trimmed once more
            if (trimmed)
                try { File.WriteAllText(TrimmedPath, ""); }
                catch (IOException) { }
            if (all.Count > maxEntries) Compact(all);
            Rewrite(all);
            lines = all;
        }
    }

    List<string> Load()
    {
        if (lines is not null) return lines;
        lines = [];
        last = 0;
        if (!File.Exists(path)) return lines;
        var text = File.ReadAllText(path, Encoding.UTF8);
        var dropped = false;
        var lists = new List<int>();
        var nodes = new List<JsonNode>();
        foreach (var l in text.Split('\n'))
        {
            if (l.Length == 0) continue;
            if (TryParse(l) is not { } node) { dropped = true; continue; } // a line torn by a crash
            turns |= IsTurn(node);
            if (IsCommands(node)) lists.Add(lines.Count);
            lines.Add(l);
            nodes.Add(node);
        }
        // A journal written before the commands were kept as state has a list for every time the
        // session was opened; each replaced the one before, so only the last one is kept.
        if (lists.Count > 1)
        {
            var stale = lists.Take(lists.Count - 1).ToHashSet();
            lines = lines.Where((_, i) => !stale.Contains(i)).ToList();
            nodes = nodes.Where((_, i) => !stale.Contains(i)).ToList();
            dropped = true;
        }
        // Every entry has a position, growing in file order. A journal written before there were
        // positions, or by an older build after them, gets the missing ones here, once.
        var numbered = false;
        for (var i = 0; i < nodes.Count; i++)
        {
            var position = PositionOf(nodes[i]);
            if (position > last)
            {
                last = position;
                continue;
            }
            if (nodes[i] is not JsonObject entry) continue;
            SetPosition(entry, ++last);
            lines[i] = entry.ToJsonString(LineOptions);
            numbered = true;
        }
        if (dropped || numbered || (text.Length > 0 && text[^1] != '\n')) Rewrite(lines);
        return lines;
    }

    void Compact(List<string> all)
    {
        // First merge streamed text chunks; that alone usually shrinks a journal a lot. The newest
        // entries stay as they are, so a client resuming after one finds it under its position.
        var mergeable = all.Count - Math.Min(TailKept, maxEntries / 4);
        var merged = new List<string>(all.Count);
        JsonObject? pending = null;
        for (var i = 0; i < all.Count; i++)
        {
            var line = all[i];
            var node = i < mergeable ? TryParse(line) as JsonObject : null;
            if (pending is not null && node is not null && TryMerge(pending, node)) continue;
            if (pending is not null) merged.Add(pending.ToJsonString(LineOptions));
            pending = node is not null && IsTextChunk(node) ? node : null;
            if (pending is null) merged.Add(line);
        }
        if (pending is not null) merged.Add(pending.ToJsonString(LineOptions));
        // Then drop the oldest entries down to three quarters of the limit.
        var keep = maxEntries * 3 / 4;
        if (merged.Count > keep)
        {
            merged.RemoveRange(0, merged.Count - keep);
            try { File.WriteAllText(TrimmedPath, ""); }
            catch (IOException) { }
        }
        all.Clear();
        all.AddRange(merged);
    }

    static bool IsTextChunk(JsonObject o) =>
        ChunkKinds.Contains((string?)o["sessionUpdate"] ?? "") && (string?)o["content"]?["type"] == "text";

    /// <summary>
    /// Merges <paramref name="next"/> into <paramref name="into"/> when both are text chunks of one
    /// message: the same kind, the same <c>messageId</c> and the same <c>_meta</c> but the position (a
    /// message from the agent's own history has <c>gorynych/origin: native</c> on every chunk). Chunks
    /// without an ID are never merged, since nothing says that two of them belong to one message (the
    /// service gives every chunk it publishes an ID; only journals written before it did lack them).
    /// The merged entry stands at the position of its last part.
    /// </summary>
    static bool TryMerge(JsonObject into, JsonObject next)
    {
        if (!IsTextChunk(next)) return false;
        if ((string?)into["sessionUpdate"] != (string?)next["sessionUpdate"]) return false;
        if ((string?)into["messageId"] is not { } id || (string?)next["messageId"] != id) return false;
        if (!JsonNode.DeepEquals(OwnMeta(into["_meta"]), OwnMeta(next["_meta"]))) return false;
        into["content"]!["text"] = (string?)into["content"]!["text"] + (string?)next["content"]!["text"];
        if (PositionOf(next) is > 0 and var position) SetPosition(into, position);
        // And at its last part's place in a host's stream, which a host that comes back sends what follows.
        if (next["_meta"]?[HostSeqKey]?.DeepClone() is { } hostSeq && into["_meta"] is JsonObject meta) meta[HostSeqKey] = hostSeq;
        return true;
    }

    void Rewrite(List<string> all)
    {
        Gorynych.Acp.Hosting.AtomicFile.WriteAllText(path, string.Join("\n", all) + "\n", Encoding.UTF8);
    }

    /// <summary>A file name for a session ID that may contain characters a file system rejects.</summary>
    public static string FileNameFor(string sessionId)
    {
        var safe = new string(sessionId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length > 80) safe = safe[..80];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..8].ToLowerInvariant();
        return $"{safe}.{hash}.jsonl";
    }
}
