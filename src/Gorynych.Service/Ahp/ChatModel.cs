using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Microsoft.AgentHostProtocol;

namespace Gorynych.Service.Ahp;

/// <summary>
/// One chat of the AHP face: its <see cref="ChatState"/>, kept from the ACP updates of the Gorynych session it
/// shows (docs/ahp-face.md, "Chats"). Each update becomes AHP actions, which go to <see cref="Emit"/> (the face
/// numbers and sends them) and then through the package's chat reducer, as every client reduces them. While a
/// load replays, <see cref="Emit"/> is null and the actions only build the state.
/// </summary>
/// <remarks>
/// Turns: a user message with a new <c>messageId</c> begins one (its ID the message's <c>gorynych/promptId</c>,
/// else its <c>messageId</c>); an agent's update with no turn open, or the mark <c>gorynych/agentTurn: "started"</c>,
/// begins one of the agent's own (<c>agent-&lt;position&gt;</c>); the mark <c>"ended"</c> ends it by its stop reason
/// or error. AHP cannot add to a message once its turn started, so the chunks of one user message are held
/// until another update comes, <see cref="Flush"/> is called (the replay ended, or 100 ms passed live).
/// Not thread-safe: the face calls it under its lock.
/// </remarks>
internal sealed class ChatModel(ChatState state)
{
    public ChatState State { get; } = state;

    /// <summary>Called with each action before the reducer takes it; null while a load replays.</summary>
    public Action<StateAction>? Emit { get; set; }

    /// <summary>The time of a replayed turn: the session's <c>updatedAt</c>, as the journal keeps no times.</summary>
    public string ReplayTime { get; set; } = AhpNames.Time(DateTimeOffset.UnixEpoch);

    /// <summary>The clock of live turns.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>When set, every action the model reduces is added to it, in order; for tests.</summary>
    public List<StateAction>? Applied { get; set; }

    /// <summary>A user message is held: the next update, or <see cref="Flush"/>, begins its turn.</summary>
    public bool Holding => held is not null;

    /// <summary>A turn runs, or is about to (a held message): the face does not close the session under it.</summary>
    public bool Busy => turn is not null || held is not null;

    sealed class Held(string turnId, string startedAt, DateTimeOffset start, JsonObject? meta, string messageId)
    {
        public string TurnId { get; } = turnId;
        public string MessageId { get; } = messageId;
        public string StartedAt { get; } = startedAt;
        public DateTimeOffset Start { get; } = start;
        public JsonObject? Meta { get; } = meta;
        public StringBuilder Text { get; } = new();
    }

    sealed class Tool(string title, string kind)
    {
        public string Title { get; set; } = title;
        public string Kind { get; } = kind;
        public List<ToolResultContent> Content { get; set; } = [];
        public bool Done { get; set; }
    }

    Held? held;
    string? turn;
    DateTimeOffset turnStart;
    bool turnLive;
    string? partId;
    string? partKind;
    string? partMessage;
    readonly Dictionary<string, int> partUses = new(StringComparer.Ordinal);
    readonly Dictionary<string, Tool> tools = new(StringComparer.Ordinal);
    int unnamed;

    /// <summary>The ID of the turn open now; null for none.</summary>
    public string? OpenTurn => turn;

    /// <summary>Takes one ACP update of the session. <paramref name="live"/>: it came now, not in a load's replay.</summary>
    public void Take(JsonObject update, bool live)
    {
        var kind = AhpNames.Text(update["sessionUpdate"]);
        var meta = update["_meta"] as JsonObject;
        if (kind == "user_message_chunk")
        {
            var messageId = AhpNames.Text(update["messageId"]) ?? held?.MessageId ?? $"message-{++unnamed}";
            var text = TextOf(update["content"]);
            if (held is not null && held.MessageId == messageId)
            {
                held.Text.Append(text);
                return;
            }
            Flush(live);
            var now = Now();
            held = new Held(AhpNames.Text(meta?[GorynychProtocol.MetaPromptId]) ?? messageId,
                live ? AhpNames.Time(now) : ReplayTime, now, meta, messageId);
            held.Text.Append(text);
            return;
        }
        Flush(live);
        switch (kind)
        {
            case "agent_message_chunk":
                Part(update, "markdown", live);
                break;
            case "agent_thought_chunk":
                Part(update, "reasoning", live);
                break;
            case "tool_call" or "tool_call_update":
                ToolCall(update, live);
                break;
            case "plan":
                Plan(update, live);
                break;
            case "session_info_update":
                Mark(update, meta, live);
                break;
        }
    }

    /// <summary>Begins the turn of a held user message: another update came, the replay ended, or 100 ms passed.</summary>
    public void Flush(bool live)
    {
        if (held is not { } h) return;
        held = null;
        if (turn is not null) EndTurn(null, null, live); // a new message ends the turn before it
        StartTurn(h.TurnId, Message(h.Text.ToString(), h.Meta), h.StartedAt, h.Start, live);
    }

    /// <summary>A load said no turn runs: one the replay left open is over.</summary>
    public void EndOpenTurn(bool live)
    {
        Flush(live);
        if (turn is not null) EndTurn(null, null, live);
    }

    // ---------------------------------------------------------------- turns

    void StartTurn(string id, Message message, string startedAt, DateTimeOffset start, bool live)
    {
        var unique = id;
        for (var n = 2; Used(unique); n++) unique = $"{id}#{n}";
        Apply(new ChatTurnStartedAction { TurnId = unique, StartedAt = startedAt, Message = message });
        turn = unique;
        turnStart = start;
        turnLive = live;
        partId = partKind = partMessage = null;
        partUses.Clear();
        tools.Clear();
    }

    bool Used(string id) => State.ActiveTurn?.Id == id || State.Turns.Any(t => t.Id == id);

    /// <summary>A turn of the agent's own, for an update that came with no turn open.</summary>
    void EnsureTurn(JsonObject update, bool live)
    {
        if (turn is not null) return;
        var position = AhpNames.Text((update["_meta"] as JsonObject)?[GorynychProtocol.MetaPosition]);
        var now = Now();
        StartTurn(position is null ? $"agent-{++unnamed}" : $"agent-{position}",
            new Message { Text = "", Origin = new MessageOrigin { Kind = MessageKind.Agent } },
            live ? AhpNames.Time(now) : ReplayTime, now, live);
    }

    /// <param name="stopReason">ACP's stop reason; null for a turn that simply ended (complete).</param>
    /// <param name="error">The mark's <c>gorynych/error</c>: the turn failed so.</param>
    void EndTurn(string? stopReason, JsonObject? error, bool live)
    {
        var id = turn!;
        var duration = live && turnLive ? Math.Max(0, (long)(Now() - turnStart).TotalMilliseconds) : 0;
        if (error is not null)
            Apply(new ChatErrorAction
            {
                TurnId = id, Duration = duration,
                Part = new ErrorResponsePart
                {
                    Error = new ErrorInfo
                    {
                        ErrorType = error["code"]?.ToJsonString() ?? "error",
                        Message = AhpNames.Text(error["message"]) ?? "The turn failed.",
                    },
                },
            });
        else if (stopReason == "cancelled") Apply(new ChatTurnCancelledAction { TurnId = id, Duration = duration });
        else Apply(new ChatTurnCompleteAction { TurnId = id, Duration = duration });
        if (State.Activity is not null) Apply(new ChatActivityChangedAction { Activity = null });
        turn = null;
        partId = partKind = partMessage = null;
        partUses.Clear();
        tools.Clear();
    }

    void Mark(JsonObject update, JsonObject? meta, bool live)
    {
        switch (AhpNames.Text(meta?[GorynychProtocol.MetaAgentTurn]))
        {
            case "started":
                if (turn is not null) EndTurn(null, null, live);
                EnsureTurn(update, live);
                break;
            case "ended" when turn is not null:
                EndTurn(AhpNames.Text(meta![GorynychProtocol.MetaStopReason]), meta[GorynychProtocol.MetaError] as JsonObject, live);
                break;
        }
    }

    // ---------------------------------------------------------------- within a turn

    void Part(JsonObject update, string kind, bool live)
    {
        var text = TextOf(update["content"]);
        if (text.Length == 0) return;
        EnsureTurn(update, live);
        var messageId = AhpNames.Text(update["messageId"]);
        if (partId is not null && partKind == kind && partMessage == messageId)
        {
            Apply(kind == "markdown"
                ? new ChatDeltaAction { TurnId = turn!, PartId = partId, Content = text }
                : new ChatReasoningAction { TurnId = turn!, PartId = partId, Content = text });
            return;
        }
        var baseId = messageId ?? $"part-{++unnamed}";
        var uses = partUses.GetValueOrDefault(baseId) + 1;
        partUses[baseId] = uses;
        var id = uses == 1 ? baseId : $"{baseId}#{uses}";
        Apply(new ChatResponsePartAction
        {
            TurnId = turn!,
            Part = kind == "markdown"
                ? new ResponsePart(new MarkdownResponsePart { Id = id, Content = text })
                : new ResponsePart(new ReasoningResponsePart { Id = id, Content = text }),
        });
        partId = id;
        partKind = kind;
        partMessage = messageId;
    }

    void ToolCall(JsonObject update, bool live)
    {
        if (AhpNames.Text(update["toolCallId"]) is not { } id) return;
        EnsureTurn(update, live);
        partId = partKind = partMessage = null; // a tool call breaks the message before it
        var title = AhpNames.Text(update["title"]);
        var status = AhpNames.Text(update["status"]);
        if (!tools.TryGetValue(id, out var tool))
        {
            tool = new Tool(title ?? AhpNames.Text(update["kind"]) ?? "Tool", AhpNames.Text(update["kind"]) ?? "other");
            tools[id] = tool;
            Apply(new ChatToolCallStartAction { TurnId = turn!, ToolCallId = id, ToolName = tool.Kind, DisplayName = tool.Title });
            Apply(new ChatToolCallReadyAction
            {
                TurnId = turn!, ToolCallId = id,
                InvocationMessage = StringOrMarkdown.FromPlain(tool.Title),
                ToolInput = update["rawInput"] is { } input ? new ToolInput { Inline = input.ToJsonString() } : null,
                Confirmed = ToolCallConfirmationReason.NotNeeded,
            });
        }
        else if (title is not null) tool.Title = title;
        if (tool.Done) return;
        var finished = status is "completed" or "failed";
        if (update["content"] is JsonArray content)
        {
            tool.Content = Content(content);
            if (!finished) Apply(new ChatToolCallContentChangedAction { TurnId = turn!, ToolCallId = id, Content = tool.Content });
        }
        if (!finished) return;
        tool.Done = true;
        Apply(new ChatToolCallCompleteAction
        {
            TurnId = turn!, ToolCallId = id,
            Result = new ToolCallResult { Success = status == "completed", PastTenseMessage = StringOrMarkdown.FromPlain(tool.Title), Content = tool.Content },
        });
    }

    /// <summary>AHP has no plan: the entry in progress is the chat's activity.</summary>
    void Plan(JsonObject update, bool live)
    {
        EnsureTurn(update, live);
        var doing = (update["entries"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(e => AhpNames.Text(e["status"]) == "in_progress") is { } entry ? AhpNames.Text(entry["content"]) : null;
        if (doing != State.Activity) Apply(new ChatActivityChangedAction { Activity = doing });
    }

    /// <summary>ACP tool content as AHP's: text as text, a resource link as a resource, a diff and a terminal described in text (step 1).</summary>
    static List<ToolResultContent> Content(JsonArray content)
    {
        var mapped = new List<ToolResultContent>();
        foreach (var item in content.OfType<JsonObject>())
            switch (AhpNames.Text(item["type"]))
            {
                case "content" when item["content"] is JsonObject block:
                    switch (AhpNames.Text(block["type"]))
                    {
                        case "text" when AhpNames.Text(block["text"]) is { } text:
                            mapped.Add(new ToolResultContent(new ToolResultTextContent { Text = text }));
                            break;
                        case "resource_link" when AhpNames.Text(block["uri"]) is { } uri:
                            mapped.Add(new ToolResultContent(new ToolResultResourceContent { Uri = uri }));
                            break;
                        case "resource" when block["resource"] is JsonObject resource:
                            if (AhpNames.Text(resource["text"]) is { } inline) mapped.Add(new ToolResultContent(new ToolResultTextContent { Text = inline }));
                            else if (AhpNames.Text(resource["uri"]) is { } link) mapped.Add(new ToolResultContent(new ToolResultResourceContent { Uri = link }));
                            break;
                    }
                    break;
                case "diff":
                    mapped.Add(new ToolResultContent(new ToolResultTextContent { Text = DiffText(item) }));
                    break;
                case "terminal":
                    mapped.Add(new ToolResultContent(new ToolResultTextContent { Text = $"Terminal {AhpNames.Text(item["terminalId"])}" }));
                    break;
            }
        return mapped;
    }

    /// <summary>
    /// A diff in text. AHP's <c>fileEdit</c> refers to its two sides by URIs the client reads through
    /// <c>resourceRead</c>, which the face serves only from step 2; until then a diff is said in text.
    /// </summary>
    internal static string DiffText(JsonObject diff)
    {
        var path = AhpNames.Text(diff["path"]) ?? "a file";
        var before = AhpNames.Text(diff["oldText"]);
        var after = AhpNames.Text(diff["newText"]) ?? "";
        var removed = before is null ? [] : before.Split('\n');
        var added = after.Split('\n');
        var text = new StringBuilder(before is null ? $"Created {path}" : $"Edited {path}");
        const int Shown = 200;
        if (removed.Length + added.Length > Shown)
            return text.Append($" ({removed.Length} lines before, {added.Length} after)").ToString();
        text.Append("\n```diff");
        foreach (var line in removed) text.Append("\n-").Append(line.TrimEnd('\r'));
        foreach (var line in added) text.Append("\n+").Append(line.TrimEnd('\r'));
        return text.Append("\n```").ToString();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The user message of a turn: who wrote it, from <c>gorynych/origin</c>, and the service's marks of it in <c>_meta</c>.</summary>
    static Message Message(string text, JsonObject? meta)
    {
        var kind = AhpNames.Text(meta?[GorynychProtocol.MetaOrigin]) switch
        {
            "peer" or "channel" or "live" or "report" or "spawn" => MessageKind.Agent,
            "notice" => MessageKind.SystemNotification,
            _ => MessageKind.User,
        };
        var message = new Message { Text = text, Origin = new MessageOrigin { Kind = kind } };
        var kept = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in new[] { GorynychProtocol.MetaOrigin, GorynychProtocol.MetaSender, GorynychProtocol.MetaFromSessionId, GorynychProtocol.MetaOpinion })
            if (meta?[key] is { } value) kept[key] = AhpNames.Element(value);
        if (kept.Count > 0) message.Meta = kept;
        return message;
    }

    /// <summary>The text of a content block; other blocks are left out in step 1.</summary>
    static string TextOf(JsonNode? content) =>
        content is JsonObject block && AhpNames.Text(block["type"]) == "text" ? AhpNames.Text(block["text"]) ?? "" : "";

    void Apply(object action)
    {
        var wrapped = new StateAction(action);
        Emit?.Invoke(wrapped);
        Reducers.ApplyToChat(State, wrapped);
        Applied?.Add(wrapped);
    }
}
