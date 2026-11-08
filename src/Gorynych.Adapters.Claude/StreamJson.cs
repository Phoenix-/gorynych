using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Pure mapping between Claude Code's stream-json messages (<c>claude -p --input-format stream-json
/// --output-format stream-json</c>, the protocol the Agent SDK speaks) and ACP. Kept free of state
/// and I/O, like <see cref="ClaudeHooks"/>, so tests can run it on recorded lines. The mapping is
/// documented in docs/adapters.md.
/// </summary>
public static class StreamJson
{
    /// <summary>The flags every headless launch starts with.</summary>
    public static readonly string[] BaseArgs =
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        "--include-partial-messages", "--replay-user-messages", "--permission-prompt-tool=stdio",
    ];

    /// <summary>The per-launch settings of a headless session: no hooks, only the rule that hides gorynych-outside.</summary>
    public static JsonObject Settings() => new()
    {
        ["permissions"] = new JsonObject { ["deny"] = new JsonArray(ClaudeHooks.OutsideServerDeny) },
    };

    // ---------------------------------------------------------------- adapter → Claude

    /// <summary>A user message for Claude's stdin, stamped with <paramref name="uuid"/> so its replay can be told apart.</summary>
    public static JsonObject UserMessage(string uuid, JsonNode content) => new()
    {
        ["type"] = "user",
        ["uuid"] = uuid,
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        ["parent_tool_use_id"] = null,
    };

    /// <summary>
    /// ACP prompt content as Anthropic message content: plain text when every block is text (joined
    /// by blank lines, resource links as <c>@uri</c>), otherwise text and image blocks.
    /// </summary>
    public static JsonNode Content(IEnumerable<ContentBlock> prompt)
    {
        var blocks = prompt.ToList();
        if (blocks.All(b => b is not ImageContent))
            return JsonValue.Create(string.Join("\n\n", blocks.Select(BlockText).Where(t => t.Length > 0)))!;
        var content = new JsonArray();
        foreach (var block in blocks)
        {
            if (block is ImageContent image)
                content.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MimeType, ["data"] = image.Data },
                });
            else if (BlockText(block) is { Length: > 0 } text)
                content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        }
        return content;
    }

    /// <summary>The text of an ACP block the adapter passes on as text; empty for others.</summary>
    public static string BlockText(ContentBlock block) => block switch
    {
        TextContent t => t.Text,
        ResourceLink l => $"@{l.Uri}",
        _ => "",
    };

    /// <summary>True when the prompt has some text or an image to send.</summary>
    public static bool HasContent(IEnumerable<ContentBlock> prompt) =>
        prompt.Any(b => b is ImageContent || BlockText(b).Length > 0);

    public static JsonObject Allow(JsonObject input) => new() { ["behavior"] = "allow", ["updatedInput"] = input.DeepClone() };

    public static JsonObject Deny(string message) => new() { ["behavior"] = "deny", ["message"] = message };

    // ---------------------------------------------------------------- Claude → ACP

    /// <summary>
    /// True when the <c>account</c> of an <c>initialize</c> answer shows no login at all: no plan and
    /// <c>tokenSource: "none"</c>. That is what <c>--bare</c> looks like (it never reads the OAuth
    /// login; probed with 2.1.302), and also a person who is signed out.
    /// </summary>
    public static bool SignedOut(JsonObject? initialize) =>
        initialize?["account"] is JsonObject account
        && (string?)account["subscriptionType"] is not { Length: > 0 }
        && (string?)account["tokenSource"] == "none"
        && account["apiKeySource"] is null;

    /// <summary>
    /// The API key Claude would bill instead of the plan (<c>account.apiKeySource</c> of an
    /// <c>initialize</c> answer, such as <c>ANTHROPIC_API_KEY</c>, which wins over the login), or null.
    /// </summary>
    public static string? ApiKeySource(JsonObject? initialize) =>
        (string?)initialize?["account"]?["apiKeySource"] is { Length: > 0 } source && source != "none" ? source : null;

    /// <summary>The API key a <c>system/init</c> message reports (<c>apiKeySource</c> other than <c>none</c>), or null.</summary>
    public static string? ApiKeySourceOfInit(JsonObject init) =>
        (string?)init["apiKeySource"] is { Length: > 0 } source && source != "none" ? source : null;

    public static string ApiKeyProblem(string apiKey) =>
        $"Claude Code would run on {apiKey}, billed outside your plan. Gorynych runs Claude on your own plan login only: " +
        $"remove {apiKey} from the environment the Gorynych service starts with.";

    /// <summary>
    /// Why a launch must not run, from its <c>initialize</c> answer, or null when it runs on the
    /// person's plan login. Fails closed: an answer without an <c>account</c> does not run either.
    /// </summary>
    public static string? PlanProblem(JsonObject initialize)
    {
        if (initialize["account"] is not JsonObject)
            return "Claude Code did not report its account, so Gorynych cannot tell whether it runs on your plan, and does not run it. " + TuiHint;
        if (SignedOut(initialize))
            return "Claude Code started without your login (its account shows no plan and no token). Sign in with `claude` in a " +
                "terminal; if you are signed in, `claude -p` may now run bare by default, which ignores the login, so start the " +
                "Gorynych service with --claude-transport tui.";
        return ApiKeySource(initialize) is { } apiKey ? ApiKeyProblem(apiKey) : null;
    }

    /// <summary>The assistant errors that mean Claude cannot run on the person's plan (<c>SDKAssistantMessageError</c>).</summary>
    public static readonly HashSet<string> PlanErrors =
        ["authentication_failed", "oauth_org_not_allowed", "account_on_hold", "verification_required", "billing_error"];

    /// <summary>The <c>error</c> of an <c>assistant</c> message when it is one of <see cref="PlanErrors"/>.</summary>
    public static string? PlanError(JsonObject assistant) =>
        (string?)assistant["error"] is { } error && PlanErrors.Contains(error) ? error : null;

    /// <summary>What the person reads when Claude cannot run headless on their plan, with the way back.</summary>
    public const string TuiHint =
        "If `claude -p` no longer runs on your plan (Anthropic may change that), start the Gorynych service with " +
        "--claude-transport tui to drive the interactive Claude Code instead.";

    public static string? Type(JsonObject m) => (string?)m["type"];

    public static string? Subtype(JsonObject m) => (string?)m["subtype"];

    /// <summary>True for messages of a subagent (they carry the <c>Task</c> tool call they belong to).</summary>
    public static bool IsSubagent(JsonObject m) => (string?)m["parent_tool_use_id"] is { Length: > 0 };

    /// <summary>The ID of the assistant message a <c>stream_event</c> <c>message_start</c> opens.</summary>
    public static string? MessageStartId(JsonObject streamEvent) =>
        streamEvent["event"] is JsonObject e && (string?)e["type"] == "message_start" ? (string?)e["message"]?["id"] : null;

    /// <summary>A text or thinking delta of a <c>stream_event</c> as an ACP chunk of <paramref name="messageId"/>; null for other events.</summary>
    public static SessionUpdate? Delta(JsonObject streamEvent, string? messageId)
    {
        if (streamEvent["event"] is not JsonObject e || (string?)e["type"] != "content_block_delta" || e["delta"] is not JsonObject d) return null;
        return (string?)d["type"] switch
        {
            "text_delta" when (string?)d["text"] is { Length: > 0 } text =>
                new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = text }, MessageId = messageId },
            "thinking_delta" when (string?)d["thinking"] is { Length: > 0 } thinking =>
                new SessionUpdateAgentThoughtChunk { Content = new TextContent { Text = thinking }, MessageId = messageId },
            _ => null,
        };
    }

    /// <summary>The ID of a complete <c>assistant</c> message.</summary>
    public static string? AssistantId(JsonObject assistant) => (string?)assistant["message"]?["id"];

    /// <summary>The text blocks of a complete <c>assistant</c> message, joined.</summary>
    public static string AssistantText(JsonObject assistant) =>
        string.Concat(Blocks(assistant).Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"] ?? ""));

    /// <summary>The <c>tool_use</c> blocks of an <c>assistant</c> message, as ACP tool calls, with the tool's name and input.</summary>
    public static IEnumerable<(ToolCall Call, string Tool, JsonObject? Input)> ToolUses(JsonObject assistant)
    {
        foreach (var block in Blocks(assistant).Where(b => (string?)b["type"] == "tool_use"))
        {
            var tool = (string?)block["name"] ?? "tool";
            var input = block["input"] as JsonObject;
            var call = ClaudeHooks.ToolCall(new JsonObject
            {
                ["tool_name"] = tool,
                ["tool_input"] = input?.DeepClone(),
                ["tool_use_id"] = (string?)block["id"],
            });
            yield return (call, tool, input);
        }
    }

    /// <summary>
    /// The <c>tool_result</c> blocks of a <c>user</c> message as ACP tool call updates. A message with
    /// one result carries the tool's structured output in <c>tool_use_result</c>, which becomes
    /// <c>rawOutput</c>; <paramref name="toolOf"/> names the tool of a <c>tool_use_id</c>.
    /// </summary>
    public static IEnumerable<SessionUpdateToolCallUpdate> ToolResults(JsonObject user, Func<string, string?> toolOf)
    {
        var results = Blocks(user).Where(b => (string?)b["type"] == "tool_result").ToList();
        foreach (var block in results)
        {
            var id = (string?)block["tool_use_id"] ?? "";
            var failed = (bool?)block["is_error"] == true;
            var structured = results.Count == 1 ? user["tool_use_result"] : null;
            var text = ResultText(block["content"]);
            yield return ClaudeHooks.ToolCallFinished(new JsonObject
            {
                ["tool_name"] = toolOf(id) ?? "tool",
                ["tool_use_id"] = id,
                ["tool_response"] = failed ? null : (structured?.DeepClone() ?? (text is null ? null : JsonValue.Create(text))),
                ["error"] = failed ? text ?? (string?)structured : null,
            }, failed);
        }
    }

    /// <summary>The text of a replayed user message, or null when it carries none (a tool result).</summary>
    public static string? UserText(JsonObject user) => user["message"]?["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray blocks when blocks.OfType<JsonObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"]).ToList() is { Count: > 0 } texts
            => string.Join("\n\n", texts),
        _ => null,
    };

    /// <summary>True for the echo of a user message the adapter wrote (<c>--replay-user-messages</c>).</summary>
    public static bool IsReplay(JsonObject user) => (bool?)user["isReplay"] == true;

    /// <summary>How a <c>result</c> ends the ACP turn.</summary>
    /// <param name="cancelled">The client cancelled this turn.</param>
    public static StopReason StopReasonOf(JsonObject result, bool cancelled)
    {
        if (cancelled) return StopReason.Cancelled;
        return (Subtype(result), (string?)result["stop_reason"]) switch
        {
            ("error_max_turns", _) => StopReason.MaxTurnRequests,
            (_, "max_tokens") => StopReason.MaxTokens,
            (_, "refusal") => StopReason.Refusal,
            _ => StopReason.EndTurn,
        };
    }

    /// <summary>What went wrong in a failed <c>result</c>, for a note in the feed; null for a success.</summary>
    public static string? ResultError(JsonObject result)
    {
        if ((bool?)result["is_error"] != true && Subtype(result) == "success") return null;
        if (Subtype(result) == "error_max_turns") return null; // the stop reason says it
        var errors = (result["errors"] as JsonArray)?.Select(e => (string?)e).Where(e => !string.IsNullOrEmpty(e)).ToList() ?? [];
        var text = errors.Count > 0 ? string.Join("; ", errors) : (string?)result["result"];
        return string.IsNullOrWhiteSpace(text) ? Subtype(result) ?? "error" : text;
    }

    static IEnumerable<JsonObject> Blocks(JsonObject m) => (m["message"]?["content"] as JsonArray)?.OfType<JsonObject>() ?? [];

    static string? ResultText(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Where(p => (string?)p["type"] == "text").Select(p => (string?)p["text"])),
        _ => null,
    };
}
