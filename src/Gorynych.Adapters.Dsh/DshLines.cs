using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Dsh;

/// <summary>Reading JSON-RPC lines that pass through the adapter: DSH's output is read, never trusted.</summary>
internal static class DshLines
{
    public static readonly JsonSerializerOptions Wire = AcpJson.TextOptions;

    public static JsonNode? Parse(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }

    /// <summary>A string value, or null for anything else (a number, an object, nothing).</summary>
    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string Key(JsonNode id) => id.ToJsonString(Wire);

    public static string Shorten(string line) => line.Length > 300 ? line[..300] + "…" : line;

    public static JsonObject Error(JsonNode id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
