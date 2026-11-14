using System.Text.Json.Nodes;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Acp.Hosting;

/// <summary>
/// The shared ACP v1 steering extension (docs/steering.md): request parsing, capability flags and
/// the three public outcomes. It is not a standard ACP method; every name that is not defined by
/// ACP lives under <c>_meta</c>, and the method keeps its agreed <c>_</c>-prefixed name.
/// </summary>
public static class AcpSteering
{
    /// <summary>Accepted into the running turn. It does not say the model has read it.</summary>
    public const string Injected = "injected";

    /// <summary>No turn ran, and the request opted in with <c>idleBehavior: "promptRequired"</c>.</summary>
    public const string PromptRequired = "promptRequired";

    /// <summary>No turn ran, so the adapter started one (the legacy default without the opt-in).</summary>
    public const string StartedNewTurn = "startedNewTurn";

    /// <summary>The <c>reason</c> of <see cref="PromptRequired"/> when no turn ran.</summary>
    public const string NoRunningTurn = "noRunningTurn";

    /// <summary>The <c>idleBehavior</c> that keeps an adapter from starting a turn of its own.</summary>
    public const string PromptRequiredIdle = "promptRequired";

    /// <summary>
    /// An adapter's <c>initialize</c> result admits the extension at its top-level <c>_meta</c>:
    /// <c>{"steering": {"supported": true}}</c>. Absent or false: it does not steer.
    /// </summary>
    public static bool Supports(JsonNode? initializeResult) =>
        initializeResult?["_meta"]?[GorynychProtocol.MetaSteering]?["supported"] is JsonValue v
        && v.TryGetValue<bool>(out var supported) && supported;

    /// <summary>Whether an adapter's <c>initialize</c> result admits the extension.</summary>
    public static bool Supports(InitializeResponse? initialize) =>
        initialize?.Meta?[GorynychProtocol.MetaSteering]?["supported"] is JsonValue v
        && v.TryGetValue<bool>(out var supported) && supported;

    /// <summary>
    /// A request parsed and validated. <paramref name="PromptRequired"/> says the caller opted into
    /// <c>idleBehavior: "promptRequired"</c>; absent means the shared extension's legacy idle start.
    /// </summary>
    public sealed record Request(string SessionId, List<ContentBlock> Prompt, bool PromptRequired, string? PromptId, JsonObject? Meta);

    /// <summary>
    /// Parses <c>_session/steering</c> params. A malformed request, an empty prompt, or a content
    /// type outside what the peer negotiated is refused with <c>-32602</c>; steering enables no
    /// content type a prompt does not (docs/steering.md).
    /// </summary>
    public static Request Parse(JsonNode? @params, bool image = false, bool audio = false, bool embeddedContext = false)
    {
        if (@params is not JsonObject p) throw Invalid("params must be an object");
        if (Text(p["sessionId"]) is not { Length: > 0 } sessionId) throw Invalid("sessionId is required");
        if (p["prompt"] is not JsonArray blocks || blocks.Count == 0) throw Invalid("prompt must be a non-empty array of content blocks");
        var prompt = new List<ContentBlock>(blocks.Count);
        foreach (var node in blocks)
        {
            if (node is not JsonObject) throw Invalid("every prompt block must be an object");
            ContentBlock? block;
            try { block = AcpJson.FromNode<ContentBlock>(node); }
            catch (System.Text.Json.JsonException e) { throw Invalid($"invalid content block: {e.Message}"); }
            if (block is null or ContentBlock.Unknown) throw Invalid($"unsupported content block {(string?)node["type"] ?? "(no type)"}");
            switch (block)
            {
                case TextContent or ResourceLink:
                    break;
                case ImageContent when image:
                    break;
                case AudioContent when audio:
                    break;
                case EmbeddedResource when embeddedContext:
                    break;
                default:
                    throw Invalid($"content type {(string?)node["type"]} is not enabled for prompts");
            }
            prompt.Add(block);
        }
        if (p["_meta"] is not null && p["_meta"] is not JsonObject) throw Invalid("_meta must be an object");
        var meta = p["_meta"] as JsonObject;
        var promptRequired = false;
        if (meta?[GorynychProtocol.MetaSteering] is JsonNode steering)
        {
            if (steering is not JsonObject behavior) throw Invalid("_meta.steering must be an object");
            if (behavior["idleBehavior"] is JsonNode idle)
            {
                if (Text(idle) != PromptRequiredIdle) throw Invalid("_meta.steering.idleBehavior must be \"promptRequired\"");
                promptRequired = true;
            }
        }
        var promptId = OptionalString(meta, GorynychProtocol.MetaPromptId, "gorynych/promptId");
        OptionalString(meta, GorynychProtocol.MetaMessageId, "gorynych/messageId");
        return new Request(sessionId, prompt, promptRequired, promptId, meta);
    }

    static string? OptionalString(JsonObject? meta, string key, string name)
    {
        if (meta is null || meta[key] is null) return null;
        return Text(meta[key]) is { Length: > 0 } value ? value : throw Invalid($"{name} must be a non-empty string");
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static JsonRpcException Invalid(string message) => new(JsonRpcException.InvalidParams, message);

    /// <summary>A result of one of the three public outcomes.</summary>
    public static JsonObject Result(string outcome, string? reason = null)
    {
        var result = new JsonObject { ["outcome"] = outcome };
        if (reason is not null) result["reason"] = reason;
        return result;
    }

    /// <summary>The result for a turn that no longer ran when the adapter got there.</summary>
    public static JsonObject PromptRequiredResult() => Result(PromptRequired, NoRunningTurn);

    /// <summary>A refusal: the client keeps the message for the turn's end (or the next prompt).</summary>
    public static JsonRpcException Refused(string reason, string message) =>
        new(JsonRpcException.InvalidRequest, message, new JsonObject { [GorynychProtocol.MetaSteerRefused] = reason });
}
