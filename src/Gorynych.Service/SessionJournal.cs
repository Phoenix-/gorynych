using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>
/// The bounded history of one session: every <c>session/update</c> payload, one JSON object
/// per line, replayed to a client on <c>session/load</c>. When it grows past the limit,
/// consecutive text chunks of one message (one <c>messageId</c>) are merged and the oldest
/// entries dropped.
/// </summary>
public sealed class SessionJournal
{
    static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<string> ChunkKinds = ["agent_message_chunk", "agent_thought_chunk", "user_message_chunk"];

    /// <summary>Updates that only a turn produces; the rest (commands, modes, config) also come with an empty session.</summary>
    static readonly HashSet<string> TurnKinds = ["user_message_chunk", "agent_message_chunk", "agent_thought_chunk", "tool_call", "tool_call_update", "plan"];

    readonly string path;
    readonly int maxEntries;
    readonly Lock gate = new();
    List<string>? lines;
    bool turns;

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
    /// True once compaction has dropped the journal's oldest entries. Kept in a marker file next to
    /// the journal, so it survives a restart without an entry that clients would be replayed.
    /// </summary>
    public bool Trimmed
    {
        get { lock (gate) return File.Exists(TrimmedPath); }
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

    public void Append(JsonNode update)
    {
        var line = update.ToJsonString(LineOptions);
        lock (gate)
        {
            var all = Load();
            all.Add(line);
            turns |= IsTurn(update);
            if (all.Count > maxEntries)
            {
                Compact(all);
                Rewrite(all);
            }
            else
            {
                File.AppendAllText(path, line + "\n", Encoding.UTF8);
            }
        }
    }

    public List<JsonNode> ReadAll()
    {
        lock (gate)
            return Load().Select(TryParse).OfType<JsonNode>().ToList();
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
            File.Delete(path);
            File.Delete(TrimmedPath);
        }
    }

    List<string> Load()
    {
        if (lines is not null) return lines;
        lines = [];
        if (!File.Exists(path)) return lines;
        var text = File.ReadAllText(path, Encoding.UTF8);
        var dropped = false;
        foreach (var l in text.Split('\n'))
        {
            if (l.Length == 0) continue;
            if (TryParse(l) is not { } node) { dropped = true; continue; } // a line torn by a crash
            turns |= IsTurn(node);
            lines.Add(l);
        }
        if (dropped || (text.Length > 0 && text[^1] != '\n')) Rewrite(lines);
        return lines;
    }

    void Compact(List<string> all)
    {
        // First merge streamed text chunks; that alone usually shrinks a journal a lot.
        var merged = new List<string>(all.Count);
        JsonObject? pending = null;
        foreach (var line in all)
        {
            var node = TryParse(line) as JsonObject;
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
        ChunkKinds.Contains((string?)o["sessionUpdate"] ?? "") && (string?)o["content"]?["type"] == "text" && o["_meta"] is null;

    /// <summary>
    /// Merges <paramref name="next"/> into <paramref name="into"/> when both are text chunks of one
    /// message: the same kind and the same <c>messageId</c>. Chunks without one are never merged,
    /// since nothing says that two of them belong to one message (the service gives every chunk it
    /// publishes an ID; only journals written before it did lack them).
    /// </summary>
    static bool TryMerge(JsonObject into, JsonObject next)
    {
        if (!IsTextChunk(next)) return false;
        if ((string?)into["sessionUpdate"] != (string?)next["sessionUpdate"]) return false;
        if ((string?)into["messageId"] is not { } id || (string?)next["messageId"] != id) return false;
        into["content"]!["text"] = (string?)into["content"]!["text"] + (string?)next["content"]!["text"];
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
