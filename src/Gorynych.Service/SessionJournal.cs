using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>
/// The bounded history of one session: every <c>session/update</c> payload, one JSON object
/// per line, replayed to a client on <c>session/load</c>. When it grows past the limit,
/// consecutive text chunks of one message are merged and the oldest entries dropped.
/// </summary>
public sealed class SessionJournal
{
    static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly HashSet<string> ChunkKinds = ["agent_message_chunk", "agent_thought_chunk", "user_message_chunk"];

    readonly string path;
    readonly int maxEntries;
    readonly Lock gate = new();
    List<string>? lines;

    public SessionJournal(string path, int maxEntries)
    {
        this.path = path;
        this.maxEntries = Math.Max(16, maxEntries);
    }

    public int Count
    {
        get { lock (gate) return Load().Count; }
    }

    public void Append(JsonNode update)
    {
        var line = update.ToJsonString(LineOptions);
        lock (gate)
        {
            var all = Load();
            all.Add(line);
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
            File.Delete(path);
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
            if (TryParse(l) is null) { dropped = true; continue; } // a line torn by a crash
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
        if (merged.Count > keep) merged.RemoveRange(0, merged.Count - keep);
        all.Clear();
        all.AddRange(merged);
    }

    static bool IsTextChunk(JsonObject o) =>
        ChunkKinds.Contains((string?)o["sessionUpdate"] ?? "") && (string?)o["content"]?["type"] == "text" && o["_meta"] is null;

    static bool TryMerge(JsonObject into, JsonObject next)
    {
        if (!IsTextChunk(next)) return false;
        if ((string?)into["sessionUpdate"] != (string?)next["sessionUpdate"]) return false;
        if ((string?)into["messageId"] != (string?)next["messageId"]) return false;
        into["content"]!["text"] = (string?)into["content"]!["text"] + (string?)next["content"]!["text"];
        return true;
    }

    void Rewrite(List<string> all)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\n", all) + "\n", Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
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
