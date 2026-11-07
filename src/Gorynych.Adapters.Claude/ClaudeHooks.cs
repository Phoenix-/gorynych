using System.Text.Json.Nodes;
using Gorynych.Acp;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Pure mapping between Claude Code hook payloads and ACP. Kept free of state and I/O so the
/// contract tests can run it on recorded hook events. The mapping is documented in
/// docs/claude-adapter.md.
/// </summary>
public static class ClaudeHooks
{
    /// <summary>The hook events the adapter subscribes to with <c>--settings</c>.</summary>
    public static readonly string[] Events =
    [
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
        "Notification", "MessageDisplay", "Stop", "StopFailure", "SessionStart", "SessionEnd",
    ];

    public const string AllowOptionId = "allow";
    public const string DenyOptionId = "deny";

    /// <summary>Builds the per-launch settings JSON that posts every hook event to <paramref name="url"/>.</summary>
    public static JsonObject Settings(string url)
    {
        var hooks = new JsonObject();
        foreach (var name in Events)
        {
            // An approval waits for a person, so give it a day; the rest should be quick.
            var timeout = name == "PermissionRequest" ? 86400 : 30;
            hooks[name] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = url, ["timeout"] = timeout }),
            });
        }
        return new JsonObject { ["hooks"] = hooks };
    }

    public static string? Name(JsonObject e) => (string?)e["hook_event_name"];

    public static ToolCall ToolCall(JsonObject e)
    {
        var tool = (string?)e["tool_name"] ?? "tool";
        var input = e["tool_input"] as JsonObject;
        return new ToolCall
        {
            ToolCallId = (string?)e["tool_use_id"] ?? Guid.NewGuid().ToString("N"),
            Title = ToolTitle(tool, input),
            Name = tool,
            Kind = KindOf(tool),
            Status = ToolCallStatus.Pending,
            RawInput = input?.DeepClone(),
            Locations = Locations(input),
        };
    }

    public static SessionUpdateToolCallUpdate ToolCallFinished(JsonObject e, bool failed)
    {
        var tool = (string?)e["tool_name"] ?? "tool";
        var update = new SessionUpdateToolCallUpdate
        {
            ToolCallId = (string?)e["tool_use_id"] ?? "",
            Status = failed ? ToolCallStatus.Failed : ToolCallStatus.Completed,
            RawOutput = (e["tool_response"] ?? e["error"])?.DeepClone(),
        };
        var text = failed ? (string?)e["error"] : OutputText(tool, e["tool_response"]);
        if (!string.IsNullOrEmpty(text))
            update.Content = [new Content { ContentValue = new TextContent { Text = Truncate(text, 4000) } }];
        return update;
    }

    public static SessionUpdateToolCallUpdate ToolCallStatusUpdate(string toolCallId, ToolCallStatus status, string? text = null)
    {
        var update = new SessionUpdateToolCallUpdate { ToolCallId = toolCallId, Status = status };
        if (text is not null) update.Content = [new Content { ContentValue = new TextContent { Text = text } }];
        return update;
    }

    public static SessionUpdateAgentMessageChunk MessageChunk(JsonObject e) => new()
    {
        Content = new TextContent { Text = (string?)e["delta"] ?? "" },
        MessageId = (string?)e["message_id"],
    };

    public static SessionUpdateAgentMessageChunk TextChunk(string text) => new() { Content = new TextContent { Text = text } };

    /// <summary>A user message that did not come from the ACP client, such as a channel message.</summary>
    public static SessionUpdateUserMessageChunk ForeignPrompt(string prompt) => new()
    {
        Content = new TextContent { Text = prompt },
        Meta = new JsonObject { [Acp.Hosting.GorynychProtocol.MetaOrigin] = IsChannelMessage(prompt) ? "channel" : "terminal" },
    };

    public static bool IsChannelMessage(string prompt) => prompt.TrimStart().StartsWith("<channel", StringComparison.Ordinal);

    /// <summary>The approval request for an ordinary tool.</summary>
    public static RequestPermissionRequest Permission(string sessionId, string toolCallId, JsonObject e)
    {
        var tool = (string?)e["tool_name"] ?? "tool";
        var input = e["tool_input"] as JsonObject;
        return new RequestPermissionRequest
        {
            SessionId = sessionId,
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = toolCallId,
                Title = ToolTitle(tool, input),
                Kind = KindOf(tool),
                Status = ToolCallStatus.Pending,
                RawInput = input?.DeepClone(),
                Locations = Locations(input),
            },
            Options =
            [
                new PermissionOption { OptionId = AllowOptionId, Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                new PermissionOption { OptionId = DenyOptionId, Name = "Deny", Kind = PermissionOptionKind.RejectOnce },
            ],
        };
    }

    /// <summary>
    /// One approval-shaped request per AskUserQuestion question: the options are the answers.
    /// (ACP's elicitation would fit better; see docs/claude-adapter.md.)
    /// </summary>
    public static RequestPermissionRequest Question(string sessionId, string toolCallId, JsonObject question)
    {
        var options = new List<PermissionOption>();
        var i = 0;
        foreach (var o in question["options"] as JsonArray ?? [])
            options.Add(new PermissionOption { OptionId = $"answer:{i++}", Name = (string?)o?["label"] ?? $"Option {i}", Kind = PermissionOptionKind.AllowOnce });
        options.Add(new PermissionOption { OptionId = DenyOptionId, Name = "Do not answer", Kind = PermissionOptionKind.RejectOnce });
        return new RequestPermissionRequest
        {
            SessionId = sessionId,
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = toolCallId,
                Title = (string?)question["question"] ?? "Question",
                Kind = ToolKind.Other,
                Status = ToolCallStatus.Pending,
                RawInput = question.DeepClone(),
            },
            Options = options,
        };
    }

    /// <summary>The label chosen for <paramref name="question"/>, or null when the person declined.</summary>
    public static string? ChosenLabel(JsonObject question, RequestPermissionOutcome outcome)
    {
        if (outcome is not SelectedPermissionOutcome { OptionId: var id } || !id.StartsWith("answer:", StringComparison.Ordinal)) return null;
        var index = int.Parse(id.AsSpan(7));
        return (string?)(question["options"] as JsonArray)?[index]?["label"];
    }

    public static JsonObject AllowReply(JsonObject? updatedInput = null)
    {
        var decision = new JsonObject { ["behavior"] = "allow" };
        if (updatedInput is not null) decision["updatedInput"] = updatedInput;
        return Reply(decision);
    }

    public static JsonObject DenyReply(string message) =>
        Reply(new JsonObject { ["behavior"] = "deny", ["message"] = message });

    static JsonObject Reply(JsonObject decision) => new()
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
    };

    public static bool IsAllowed(RequestPermissionOutcome outcome) =>
        outcome is SelectedPermissionOutcome s && (s.OptionId == AllowOptionId || s.OptionId.StartsWith("answer:", StringComparison.Ordinal));

    public static string ToolTitle(string tool, JsonObject? input)
    {
        string? S(string key) => (string?)input?[key] is { Length: > 0 } v ? v : null;
        string? FileName() => (S("file_path") ?? S("notebook_path")) is { } p ? Path.GetFileName(p) : null;
        var title = tool switch
        {
            "Bash" or "PowerShell" => S("description") ?? S("command"),
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => FileName() is { } f ? $"{tool} {f}" : null,
            "Grep" or "Glob" => S("pattern") is { } p ? $"{tool} {p}" : null,
            "WebFetch" => S("url") is { } u ? $"Fetch {u}" : null,
            "WebSearch" => S("query") is { } q ? $"Search {q}" : null,
            "Task" or "Agent" => S("description"),
            "AskUserQuestion" => "Ask a question",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => McpTitle(tool),
            _ => null,
        };
        return Truncate((title ?? tool).ReplaceLineEndings(" "), 120);
    }

    static string McpTitle(string tool)
    {
        var parts = tool.Split("__");
        return parts.Length >= 3 ? $"{parts[2]} ({parts[1]})" : tool;
    }

    public static ToolKind KindOf(string tool) => tool switch
    {
        "Read" => ToolKind.Read,
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => ToolKind.Edit,
        "Bash" or "PowerShell" => ToolKind.Execute,
        "Grep" or "Glob" or "ToolSearch" => ToolKind.Search,
        "WebFetch" or "WebSearch" => ToolKind.Fetch,
        "Task" or "Agent" or "TodoWrite" => ToolKind.Think,
        "ExitPlanMode" or "EnterPlanMode" => ToolKind.SwitchMode,
        _ => ToolKind.Other,
    };

    static List<ToolCallLocation>? Locations(JsonObject? input) =>
        ((string?)input?["file_path"] ?? (string?)input?["notebook_path"]) is { Length: > 0 } path ? [new ToolCallLocation { Path = path }] : null;

    static string? OutputText(string tool, JsonNode? response)
    {
        if (tool is not ("Bash" or "PowerShell") || response is not JsonObject r) return null;
        var stdout = (string?)r["stdout"];
        var stderr = (string?)r["stderr"];
        return string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}".Trim();
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
