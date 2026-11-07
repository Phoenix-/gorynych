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

    /// <summary>Claude's folder-trust dialog at the first launch in a folder, as an approval.</summary>
    public static RequestPermissionRequest TrustFolder(string sessionId, string cwd) => new()
    {
        SessionId = sessionId,
        ToolCall = new ToolCallUpdate
        {
            ToolCallId = "trust-folder",
            Title = $"Claude Code asks whether to trust the folder {cwd}",
            Kind = ToolKind.Other,
            Status = ToolCallStatus.Pending,
            Locations = [new ToolCallLocation { Path = cwd }],
        },
        Options =
        [
            new PermissionOption { OptionId = AllowOptionId, Name = "Trust this folder", Kind = PermissionOptionKind.AllowOnce },
            new PermissionOption { OptionId = DenyOptionId, Name = "Cancel", Kind = PermissionOptionKind.RejectOnce },
        ],
    };

    /// <summary>
    /// AskUserQuestion is a question, not an approval: it becomes one ACP form
    /// (<c>elicitation/create</c>, form mode, scoped to the session and the tool call) with a
    /// property per question, <c>q1</c>, <c>q2</c>, …: a single choice is a string with
    /// <c>oneOf</c>, a multi-select an array of titled items.
    /// </summary>
    public static ElicitationFormMode QuestionForm(string sessionId, string toolCallId, JsonObject toolInput)
    {
        var questions = Questions(toolInput);
        var properties = new Dictionary<string, ElicitationPropertySchema?>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            var options = (q["options"] as JsonArray ?? []).OfType<JsonObject>().Select(o => new EnumOption
            {
                Const = (string?)o["label"] ?? "",
                Title = (string?)o["label"] ?? "",
                Description = (string?)o["description"],
            }).ToList();
            var title = (string?)q["header"] is { Length: > 0 } h ? h : (string?)q["question"];
            properties[$"q{i + 1}"] = q["multiSelect"]?.GetValueKind() == System.Text.Json.JsonValueKind.True
                ? new MultiSelectPropertySchema { Title = title, Description = (string?)q["question"], Items = new TitledMultiSelectItems { AnyOf = options }, MinItems = 1 }
                : new StringPropertySchema { Title = title, Description = (string?)q["question"], OneOf = options };
        }
        return new ElicitationFormMode
        {
            Message = questions.Count == 1 ? (string?)questions[0]["question"] ?? "Claude asks" : "Claude asks you some questions",
            SessionId = sessionId,
            ToolCallId = toolCallId,
            RequestedSchema = new ElicitationSchema
            {
                Type = ElicitationSchemaType.Object,
                Properties = properties,
                Required = properties.Keys.ToList(),
            },
        };
    }

    /// <summary>
    /// The AskUserQuestion input with <c>answers</c> filled in from an accepted form
    /// (<c>{question text: label}</c>, several labels joined by ", "), or null when the person
    /// declined, cancelled or left a question unanswered.
    /// </summary>
    public static JsonObject? AnsweredInput(JsonObject toolInput, CreateElicitationResponse response)
    {
        if (response is not ElicitationAcceptAction { Content: { } content }) return null;
        var answers = new JsonObject();
        var questions = Questions(toolInput);
        for (var i = 0; i < questions.Count; i++)
        {
            var answer = content.GetValueOrDefault($"q{i + 1}") switch
            {
                JsonArray many => string.Join(", ", many.Select(v => (string?)v).Where(v => !string.IsNullOrEmpty(v))),
                JsonValue one when one.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (string.IsNullOrEmpty(answer)) return null;
            answers[(string?)questions[i]["question"] ?? ""] = answer;
        }
        var updated = toolInput.DeepClone().AsObject();
        updated["answers"] = answers;
        return updated;
    }

    static List<JsonObject> Questions(JsonObject toolInput) => (toolInput["questions"] as JsonArray ?? []).OfType<JsonObject>().ToList();

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
        outcome is SelectedPermissionOutcome { OptionId: AllowOptionId };

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
