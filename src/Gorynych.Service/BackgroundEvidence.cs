using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

// Host-generated, bounded evidence; this records observation, not completion or success.
internal sealed record BackgroundEvidence(string HostId, string Stream, long Revision, string[] Items)
{
    internal const int MaxItems = 256;
    internal static BackgroundEvidence? FromSnapshot(string hostId, long revision, string? stream, JsonNode? value)
    {
        if (hostId.Length is < 1 or > 256 || revision < 1 || !Guid.TryParseExact(stream, "N", out _) ||
            value is not JsonArray source || source.Count > MaxItems ||
            !BackgroundWorkSnapshot.TryParse(source, out var parsed) || parsed is null) return null;
        var ids = new List<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in parsed.ToJson().OfType<JsonObject>())
        {
            if ((string?)item["type"] != "_claude_task" || item["_meta"]?["gorynych/nativeTaskId"] is not JsonValue v ||
                !v.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || id.Length > 256 || !unique.Add(id)) return null;
            ids.Add(id);
        }
        return new BackgroundEvidence(hostId, stream!, revision, ids.ToArray());
    }
    internal static JsonObject Unknown(string hostId, long revision) => new() { ["hostId"] = hostId, ["revision"] = revision };
    internal static bool TryStamp(JsonNode? node, out string? host, out long revision)
    {
        host = null; revision = 0;
        return node is JsonObject obj && obj["hostId"] is JsonValue h && h.TryGetValue<string>(out host) &&
            !string.IsNullOrWhiteSpace(host) && host.Length <= 256 && obj["revision"] is JsonValue r &&
            r.TryGetValue<long>(out revision) && revision > 0;
    }
    internal JsonObject ToJson() => new() { ["hostId"] = HostId, ["stream"] = Stream, ["revision"] = Revision,
        ["items"] = new JsonArray(Items.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
    internal static BackgroundEvidence? Parse(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["hostId"] is not JsonValue h || !h.TryGetValue<string>(out var host) ||
            string.IsNullOrWhiteSpace(host) || host.Length > 256 || obj["stream"] is not JsonValue s || !s.TryGetValue<string>(out var stream) ||
            !Guid.TryParseExact(stream, "N", out _) || obj["revision"] is not JsonValue rev || !rev.TryGetValue<long>(out var revision) || revision < 1 ||
            obj["items"] is not JsonArray items || items.Count > MaxItems) return null;
        var ids = new List<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonValue v || !v.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) ||
                id.Length > 256 || !unique.Add(id)) return null;
            ids.Add(id);
        }
        return new BackgroundEvidence(host, stream, revision, ids.ToArray());
    }
}
