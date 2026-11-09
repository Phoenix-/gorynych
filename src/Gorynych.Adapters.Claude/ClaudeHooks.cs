using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// Pure mapping between Claude Code hook payloads and ACP. Kept free of state and I/O so the
/// contract tests can run it on recorded hook events. The mapping is documented in
/// docs/claude-adapter.md.
/// </summary>
public static class ClaudeHooks
{
    /// <summary>
    /// The text of an ACP <c>systemPrompt</c>, for <c>--append-system-prompt</c>. Claude Code can only
    /// add to its own system prompt, so the adapter advertises append mode alone, and as ACP
    /// requires it fails the request, instead of creating or resuming the session, for any other
    /// mode and for content it cannot pass on: an empty list, or a block that is not text.
    /// </summary>
    public static string? SystemPromptText(SystemPrompt? systemPrompt)
    {
        if (systemPrompt is null) return null;
        if (systemPrompt.Mode != SystemPromptMode.Append)
            throw new JsonRpcException(JsonRpcException.InvalidParams,
                $"Claude sessions take systemPrompt only in append mode, not \"{systemPrompt.Mode}\".");
        if (systemPrompt.Content.Count == 0)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "systemPrompt.content is empty.");
        var texts = new List<string>(systemPrompt.Content.Count);
        foreach (var block in systemPrompt.Content)
            texts.Add(block is TextContent text
                ? text.Text
                : throw new JsonRpcException(JsonRpcException.InvalidParams,
                    $"Claude sessions take only text blocks in systemPrompt.content, not \"{(string?)AcpJson.ToNode(block)?["type"]}\"."));
        return string.Join("\n\n", texts);
    }

    /// <summary>The hook events the adapter subscribes to with <c>--settings</c>.</summary>
    public static readonly string[] Events =
    [
        "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
        "Notification", "MessageDisplay", "Stop", "StopFailure", "SessionStart", "SessionEnd",
    ];

    public const string AllowOptionId = "allow";
    public const string DenyOptionId = "deny";

    /// <summary>Builds the per-launch settings JSON that posts every hook event to <paramref name="url"/>.</summary>
    /// <summary>The permission rule that removes the gorynych-outside server's tools from Claude's list.</summary>
    public const string OutsideServerDeny = "mcp__gorynych-outside";

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
        return new JsonObject { ["hooks"] = hooks, ["permissions"] = Permissions() };
    }

    /// <summary>
    /// The permission rules of every launch: the gorynych tools that need no approval
    /// (<see cref="ChannelHost.AllowedTools"/>) are allowed; the gorynych-outside server is denied.
    /// That server is for agents outside Gorynych; a session inside Gorynych that reads it from the
    /// user's configuration must not use it. The server hides itself by its ancestry too; this rule
    /// matches the name the guide registers it under.
    /// </summary>
    public static JsonObject Permissions() => new()
    {
        ["allow"] = new JsonArray(ChannelHost.AllowedTools.Select(t => (JsonNode)t).ToArray()),
        ["deny"] = new JsonArray(OutsideServerDeny),
    };

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

    /// <summary>
    /// A piece of Claude's answer. Its <c>messageId</c> is the hook's <c>message_id</c>, Claude's own
    /// ID for the displayed message, the same for all its pieces. (Claude's transcript does not keep
    /// it, and the hook carries no transcript ID.)
    /// </summary>
    public static SessionUpdateAgentMessageChunk MessageChunk(JsonObject e) => new()
    {
        Content = new TextContent { Text = (string?)e["delta"] ?? "" },
        MessageId = (string?)e["message_id"],
    };

    public static SessionUpdateAgentMessageChunk TextChunk(string text, string messageId) =>
        new() { Content = new TextContent { Text = text }, MessageId = messageId };

    /// <summary>
    /// The ACP <c>messageId</c> of a user message Claude reports itself (typed in the terminal, or
    /// a channel message): the hook's <c>prompt_id</c>, which Claude's transcript keeps as the
    /// <c>promptId</c> of the prompt that started the turn. A channel message that arrives while
    /// Claude works on a turn carries that turn's <c>prompt_id</c> (recorded with Claude Code
    /// 2.1.302), so an ID in <paramref name="taken"/> is not reused: such a message, or one without
    /// a <c>prompt_id</c>, gets a new ID. The ID returned is added to <paramref name="taken"/>.
    /// </summary>
    public static string UserMessageId(ISet<string> taken, string? promptId) =>
        promptId is { Length: > 0 } && taken.Add(promptId) ? promptId : Guid.NewGuid().ToString();

    /// <summary>A user message that did not come from the ACP client, such as a channel message.</summary>
    /// <param name="meta">For a channel message, the <c>_meta</c> its request or note came with (<see cref="ChannelHost.MetaFor"/>).</param>
    public static SessionUpdateUserMessageChunk ForeignPrompt(string prompt, string messageId, JsonObject? meta = null)
    {
        var journaled = meta?.DeepClone().AsObject() ?? [];
        journaled[Acp.Hosting.GorynychProtocol.MetaOrigin] = IsChannelMessage(prompt) ? "channel" : "terminal"; // how it arrived
        return new SessionUpdateUserMessageChunk { Content = new TextContent { Text = prompt }, MessageId = messageId, Meta = journaled };
    }

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

    /// <summary>The tool call ID of the development-channels question (<see cref="ChannelConsentQuestion"/>).</summary>
    public const string ChannelConsentToolCallId = "development-channels";

    /// <summary>What the development-channels question tells the person.</summary>
    public const string ChannelConsentText =
        "Gorynych passes other agents' questions and child sessions' reports to a running Claude Code session through a channel. " +
        "Channels are a Claude Code research preview: Claude loads Gorynych's channel only when started with " +
        "--dangerously-load-development-channels, and then shows a notice at every launch.\n\n" +
        "Allow: Gorynych starts Claude with that flag and confirms the notice for you on each launch.\n" +
        "Don't allow: Claude runs without the channel; those messages reach it as ordinary turns once it is idle.\n\n" +
        "Gorynych asks once and keeps your answer in its own data (claude\\channels.json), never in Claude's configuration. " +
        "To give the answer up front, start the service with --claude-channels accept or --claude-channels decline.";

    /// <summary>
    /// The one question whether Gorynych may start Claude with development channels and confirm
    /// Claude Code's notice for them on each launch. Its answer is kept, so the options are the
    /// <c>always</c> kinds.
    /// </summary>
    public static RequestPermissionRequest ChannelConsentQuestion(string sessionId) => new()
    {
        SessionId = sessionId,
        ToolCall = new ToolCallUpdate
        {
            ToolCallId = ChannelConsentToolCallId,
            Title = "Start Claude Code with development channels and confirm its notice on each launch?",
            Kind = ToolKind.Other,
            Status = ToolCallStatus.Pending,
            Content = [new Content { ContentValue = new TextContent { Text = ChannelConsentText } }],
        },
        Options =
        [
            new PermissionOption { OptionId = AllowOptionId, Name = "Allow", Kind = PermissionOptionKind.AllowAlways },
            new PermissionOption { OptionId = DenyOptionId, Name = "Don't allow", Kind = PermissionOptionKind.RejectAlways },
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
