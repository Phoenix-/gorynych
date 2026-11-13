using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Microsoft.AgentHostProtocol;
using AcpJsonRpcRequest = Gorynych.Acp.JsonRpc.JsonRpcRequest;
using AcpMethods = Gorynych.Acp.AcpMethods;

namespace Gorynych.Service.Ahp;

/// <summary>Step 2 of the AHP face (docs/ahp-face.md): working in sessions.</summary>
internal sealed partial class AhpFace
{
    static readonly JsonElement Null = AhpNames.Element(null);

    /// <summary>The <c>_meta</c> key of a completion's attachment that names a slash command: its text is in the message already.</summary>
    const string CommandMeta = "gorynych/command";

    // under gate
    readonly Dictionary<string, FaceAsk> requests = new(StringComparer.Ordinal); // what agents ask of the face, by its ID
    readonly Dictionary<string, SessionConfigState> agentConfigs = new(StringComparer.Ordinal); // agent -> the options of its latest session seen
    readonly Dictionary<string, List<AhpConfig.Choice>> agentModels = new(StringComparer.Ordinal); // agent -> its models, as last seen
    long asks;
    int making; // sessions in session/new
    readonly Dictionary<string, List<(string Name, string? Description)>> unclaimedCommands = new(StringComparer.Ordinal); // their commands, said early

    /// <summary>An agent's commands (<c>available_commands_update</c>), for completions.</summary>
    static List<(string Name, string? Description)> Commands(JsonObject update) =>
        [.. (update["availableCommands"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => AhpNames.Text(c["name"]) is { Length: > 0 })
            .Select(c => (AhpNames.Text(c["name"])!, AhpNames.Text(c["description"])))];

    static T Clone<T>(T value) => Json.Deserialize<T>(Json.SerializeToElement(value))!;

    static string? Text(JsonNode? node) => AhpNames.Text(node);

    /// <summary>
    /// Under the gate: runs a step that writes to the conductor for the session after the steps the face decided for
    /// it before, in that order. A prompt's step writes the prompt (after the session exists, and its model is set);
    /// a cancel decided after it then reaches the conductor after it, as it must: a cancel cancels only the prompts
    /// that came before it. Option changes go so too, so that their echoes come in the order the agent took them.
    /// </summary>
    Task<T> Write<T>(FaceSession s, Func<Task<T>> step)
    {
        var task = s.Written.ContinueWith(_ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        s.Written = task.ContinueWith(_ => { }, TaskScheduler.Default);
        return task;
    }

    // ---------------------------------------------------------------- approvals and forms

    sealed class FaceAsk(string id, bool form, string sessionId, JsonObject @params)
    {
        public string Id { get; } = id;
        public bool Form { get; } = form;
        public string SessionId { get; } = sessionId;
        public JsonObject Params { get; } = @params;
        public readonly TaskCompletionSource<JsonNode?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FaceChat? Chat; // where it is shown; null until the face has its session open
        public bool Needed; // in its session's inputNeeded
        public string? TurnId;
        public string? ToolCallId;
        public readonly List<(string Id, string Kind)> Options = []; // an approval's ACP options

        /// <summary>The answer when nobody here will answer: cancelled, as ACP has a client answer when it cancels its turn or goes.</summary>
        public JsonNode Cancelled() => Form
            ? new JsonObject { ["action"] = "cancel" }
            : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
    }

    /// <summary>
    /// What an agent asks of the face, which the conductor routes to it as to any client that takes requests (the
    /// client attached last): an approval (<c>session/request_permission</c>) or a form (<c>elicitation/create</c>).
    /// Shown in the session's chat and its <c>inputNeeded</c>, answered by a client's action; withdrawn here when the
    /// agent withdraws it.
    /// </summary>
    async Task<JsonNode?> OnAcpRequestAsync(AcpJsonRpcRequest request, CancellationToken ct)
    {
        var form = request.Method switch
        {
            AcpMethods.Client.SessionRequestPermission => false,
            AcpMethods.Client.ElicitationCreate => true,
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, $"Method not found: {request.Method}"),
        };
        var @params = request.Params as JsonObject ?? [];
        // A form of no session, or not a form (a URL to open): nobody here could show it.
        if (Text(@params["sessionId"]) is not { Length: > 0 } sessionId || (form && Text(@params["mode"]) is { } mode && mode != "form"))
            return form ? new JsonObject { ["action"] = "decline" } : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };
        FaceAsk ask;
        lock (gate)
        {
            ask = new FaceAsk($"ask-{++asks}", form, sessionId, (JsonObject)@params.DeepClone());
            requests[ask.Id] = ask;
            Show(ask);
        }
        using var withdrawn = ct.Register(() => { lock (gate) Withdraw(ask); });
        return await ask.Answer.Task;
    }

    /// <summary>Under the gate: shows what the agent asks in its session's chat, once the face has that open.</summary>
    void Show(FaceAsk ask)
    {
        if (!chatsById.TryGetValue(ask.SessionId, out var chat) || chat.Model is not { } model)
        {
            // Shown once the face has the session open, if it is opening it (an approval waiting for a client comes right
            // after the replay); otherwise nobody here will see it (the face let the session go): answered as cancelled.
            if (chat?.Loading is null) Resolve(ask, ask.Cancelled());
            return;
        }
        if (model.Settling)
        {
            // Asked under a turn a client cancelled: ACP has a cancelling client answer it so, and no turn here would show it.
            Resolve(ask, ask.Cancelled());
            return;
        }
        ask.Chat = chat;
        SessionInputRequest needed;
        if (ask.Form)
        {
            var request = FormOf(ask);
            ask.TurnId = model.InputRequest(request);
            needed = new SessionInputRequest(new SessionChatInputRequest
            {
                Id = ask.Id, Chat = chat.Uri, Kind = SessionInputRequestKind.ChatInput, Request = Clone(request),
            });
        }
        else
        {
            var call = ask.Params["toolCall"] as JsonObject ?? [];
            var toolCallId = Text(call["toolCallId"]) ?? ask.Id;
            var options = new List<ConfirmationOption>();
            foreach (var option in (ask.Params["options"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Text(option["optionId"]) is not { } id) continue;
                var kind = Text(option["kind"]) ?? "allow_once";
                ask.Options.Add((id, kind));
                options.Add(new ConfirmationOption
                {
                    Id = id, Label = Text(option["name"]) ?? id,
                    Kind = kind.StartsWith("allow", StringComparison.Ordinal) ? ConfirmationOptionKind.Approve : ConfirmationOptionKind.Deny,
                });
            }
            if (model.Approval(toolCallId, Text(call["title"]), Text(call["kind"]), call["rawInput"], options) is not { } turn
                || model.Pending(turn, toolCallId) is not { } pending)
            {
                Resolve(ask, ask.Cancelled()); // its tool call finished already: nothing to approve
                return;
            }
            ask.TurnId = turn;
            ask.ToolCallId = toolCallId;
            needed = new SessionInputRequest(new SessionToolConfirmationRequest
            {
                Id = ask.Id, Chat = chat.Uri, Kind = SessionInputRequestKind.ToolConfirmation, TurnId = turn,
                ToolCall = new ToolCallConfirmationState(Clone(pending)),
            });
        }
        ask.Needed = true;
        ApplySession(chat.Session, new SessionInputNeededSetAction { Request = needed });
        Unread(chat.Session);
        if (model.Unmarked) ScheduleQuiet(chat); // a turn it opened ends once the session goes quiet after the answer
        Sync(chat.Session);
    }

    /// <summary>A form (<c>elicitation/create</c>) as AHP's input request: a question for each field of its schema.</summary>
    static ChatInputRequest FormOf(FaceAsk ask)
    {
        var schema = ask.Params["requestedSchema"] as JsonObject ?? [];
        var required = (schema["required"] as JsonArray ?? []).Select(Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var questions = new List<ChatInputQuestion>();
        foreach (var (key, node) in schema["properties"] as JsonObject ?? [])
            if (node is JsonObject field) questions.Add(Question(key, field, required.Contains(key)));
        return new ChatInputRequest { Id = ask.Id, Message = Text(ask.Params["message"]), Questions = questions };
    }

    static ChatInputQuestion Question(string key, JsonObject field, bool required)
    {
        var title = Text(field["title"]);
        var message = Text(field["description"]) ?? title ?? key;
        static long? Whole(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : null;
        static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;
        List<ChatInputOption>? Choices(JsonNode? list, bool titled) => list is JsonArray items
            ? [.. items.Select(i => titled
                ? (Text(i?["const"]), Text(i?["title"]))
                : (Text(i), Text(i))).Where(c => c.Item1 is not null).Select(c => new ChatInputOption { Id = c.Item1!, Label = c.Item2 ?? c.Item1! })]
            : null;
        switch (Text(field["type"]))
        {
            case "string" when (Choices(field["oneOf"], titled: true) ?? Choices(field["enum"], titled: false)) is { Count: > 0 } options:
                return new ChatInputQuestion(new ChatInputSingleSelectQuestion
                {
                    Id = key, Title = title, Message = message, Required = required, Kind = ChatInputQuestionKind.SingleSelect, Options = options,
                });
            case "number" or "integer":
                return new ChatInputQuestion(new ChatInputNumberQuestion
                {
                    Id = key, Title = title, Message = message, Required = required,
                    Kind = Text(field["type"]) == "integer" ? ChatInputQuestionKind.Integer : ChatInputQuestionKind.Number,
                    Min = Number(field["minimum"]), Max = Number(field["maximum"]), DefaultValue = Number(field["default"]),
                });
            case "boolean":
                return new ChatInputQuestion(new ChatInputBooleanQuestion
                {
                    Id = key, Title = title, Message = message, Required = required, Kind = ChatInputQuestionKind.Boolean,
                    DefaultValue = field["default"] is JsonValue d && d.TryGetValue<bool>(out var b) ? b : null,
                });
            case "array" when (Choices(field["items"]?["anyOf"], titled: true) ?? Choices(field["items"]?["enum"], titled: false)) is { Count: > 0 } many:
                return new ChatInputQuestion(new ChatInputMultiSelectQuestion
                {
                    Id = key, Title = title, Message = message, Required = required, Kind = ChatInputQuestionKind.MultiSelect, Options = many,
                    Min = Whole(field["minItems"]), Max = Whole(field["maxItems"]),
                });
            default:
                return new ChatInputQuestion(new ChatInputTextQuestion
                {
                    Id = key, Title = title, Message = message, Required = required, Kind = ChatInputQuestionKind.Text,
                    Format = Text(field["format"]), Min = Whole(field["minLength"]), Max = Whole(field["maxLength"]), DefaultValue = Text(field["default"]),
                });
        }
    }

    /// <summary>Under the gate: answers what the agent asked, and takes it out of its session's <c>inputNeeded</c>.</summary>
    void Resolve(FaceAsk ask, JsonNode answer)
    {
        if (!requests.Remove(ask.Id)) return;
        NoLongerNeeded(ask);
        ask.Answer.TrySetResult(answer);
    }

    /// <summary>Under the gate: the agent withdrew what it asked; withdrawn here too.</summary>
    void Withdraw(FaceAsk ask)
    {
        if (!requests.Remove(ask.Id)) return;
        if (ask.Chat?.Model is { } model)
        {
            if (ask.Form) model.CancelInput(ask.Id);
            else if (ask.TurnId is { } turn && ask.ToolCallId is { } toolCallId) model.Withdraw(turn, toolCallId);
        }
        NoLongerNeeded(ask);
        ask.Answer.TrySetCanceled();
    }

    void NoLongerNeeded(FaceAsk ask)
    {
        if (!ask.Needed || ask.Chat is not { } chat) return;
        ask.Needed = false;
        ApplySession(chat.Session, new SessionInputNeededRemovedAction { Id = ask.Id });
        Sync(chat.Session);
    }

    /// <summary>A client's answer to an approval: the option it chose, else the agent's first of the kind it chose.</summary>
    void Confirmed(Connection c, FaceChat chat, StateAction raw, ChatToolCallConfirmedAction x, ActionOrigin origin)
    {
        var ask = requests.Values.FirstOrDefault(r => !r.Form && r.Chat == chat && r.TurnId == x.TurnId && r.ToolCallId == x.ToolCallId);
        if (chat.Model is not { } model || ask is null || model.Pending(x.TurnId, x.ToolCallId) is null)
        {
            Broadcast(chat.Uri, raw, origin, "No tool call waits for confirmation there", c);
            return;
        }
        ApplyChat(chat, x, origin);
        string? First(params string[] kinds) => kinds.Select(k => ask.Options.FirstOrDefault(o => o.Kind == k).Id).FirstOrDefault(id => id is not null);
        var chosen = x.SelectedOptionId is { } id && ask.Options.Any(o => o.Id == id) ? id
            : x.Approved ? First("allow_once", "allow_always") : First("reject_once", "reject_always");
        Resolve(ask, chosen is null ? ask.Cancelled() : new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen } });
    }

    /// <summary>A client's draft of an answer to a form: kept in the chat.</summary>
    void InputAnswered(Connection c, FaceChat chat, StateAction raw, ChatInputAnswerChangedAction x, ActionOrigin origin)
    {
        if (chat.Model?.OpenInput(x.RequestId) is null)
        {
            Broadcast(chat.Uri, raw, origin, "No such input request waits for an answer", c);
            return;
        }
        ApplyChat(chat, x, origin);
    }

    /// <summary>A client's answer to a form: its answers as the form's content, or the form declined or cancelled.</summary>
    void InputCompleted(Connection c, FaceChat chat, StateAction raw, ChatInputCompletedAction x, ActionOrigin origin)
    {
        var ask = requests.GetValueOrDefault(x.RequestId);
        if (chat.Model is not { } model || ask is not { Form: true } || ask.Chat != chat || model.OpenInput(x.RequestId) is not { } part)
        {
            Broadcast(chat.Uri, raw, origin, "No such input request waits for an answer", c);
            return;
        }
        var answers = new Dictionary<string, ChatInputAnswer>(part.Request.Answers ?? [], StringComparer.Ordinal);
        foreach (var (key, answer) in x.Answers ?? []) answers[key] = answer;
        var questions = (part.Request.Questions ?? []).Select(q => q.Value).ToList();
        if (x.Response == ChatInputResponseKind.Accept)
            foreach (var (id, required) in questions.Select(QuestionId))
                if (required && !(answers.TryGetValue(id, out var given) && given.Value is ChatInputAnswered { State: ChatInputAnswerState.Submitted }))
                {
                    Broadcast(chat.Uri, raw, origin, $"Answer the required question {id} first", c);
                    return;
                }
        ApplyChat(chat, x, origin);
        Resolve(ask, x.Response switch
        {
            ChatInputResponseKind.Accept => new JsonObject { ["action"] = "accept", ["content"] = Content(questions, answers) },
            ChatInputResponseKind.Decline => new JsonObject { ["action"] = "decline" },
            _ => new JsonObject { ["action"] = "cancel" },
        });
    }

    static (string Id, bool Required) QuestionId(object? question) => question switch
    {
        ChatInputTextQuestion q => (q.Id, q.Required == true),
        ChatInputNumberQuestion q => (q.Id, q.Required == true),
        ChatInputBooleanQuestion q => (q.Id, q.Required == true),
        ChatInputSingleSelectQuestion q => (q.Id, q.Required == true),
        ChatInputMultiSelectQuestion q => (q.Id, q.Required == true),
        _ => ("", false),
    };

    /// <summary>A form's content from AHP's answers: each answered field's value; a skipped one left out.</summary>
    static JsonObject Content(List<object?> questions, Dictionary<string, ChatInputAnswer> answers)
    {
        var content = new JsonObject();
        foreach (var question in questions)
        {
            var (id, _) = QuestionId(question);
            if (!answers.TryGetValue(id, out var answer) || answer.Value is not ChatInputAnswered answered) continue;
            content[id] = answered.Value.Value switch
            {
                ChatInputTextAnswerValue t => (JsonNode?)JsonValue.Create(t.Value),
                ChatInputNumberAnswerValue n when question is ChatInputNumberQuestion { Kind.Value: "integer" } => JsonValue.Create((long)Math.Round(n.Value)),
                ChatInputNumberAnswerValue n => JsonValue.Create(n.Value),
                ChatInputBooleanAnswerValue b => JsonValue.Create(b.Value),
                ChatInputSelectedAnswerValue s => JsonValue.Create(s.Value.Length > 0 ? s.Value : s.FreeformValues?.FirstOrDefault() ?? ""),
                ChatInputSelectedManyAnswerValue m => new JsonArray([.. m.Value.Concat(m.FreeformValues ?? []).Select(v => (JsonNode?)JsonValue.Create(v))]),
                _ => null,
            };
        }
        return content;
    }

    // ---------------------------------------------------------------- prompts

    /// <summary>Why a client may not prompt the chat now; null when it may.</summary>
    static string? Unwritable(FaceChat chat) =>
        chat.Model is null ? "This chat is not open"
        : chat.IsChild ? "A child's chat is read-only in this version"
        : chat.ReadOnly ? chat.ReadOnlyReason ?? "This session is read-only for now"
        : chat.Session.State.Lifecycle == SessionLifecycle.Failed ? "This session could not be created"
        : null;

    /// <summary>A turn that starts, or a question it asks, makes its session unread, as the chat reducer makes the chat.</summary>
    void Unread(FaceSession s)
    {
        if (!s.IsRead) return;
        s.IsRead = false;
        ApplySession(s, new SessionIsReadChangedAction { IsRead = false });
    }

    /// <summary>A client's message: its turn, and <c>session/prompt</c> with the turn's ID as the prompt's.</summary>
    void TurnStarted(Connection c, FaceChat chat, StateAction raw, ChatTurnStartedAction x, ActionOrigin origin)
    {
        string? why = Unwritable(chat);
        if (why is null && (chat.Model!.Busy || chat.Model.State.ActiveTurn is not null)) why = "A turn is running: send the message as a queued one";
        if (why is null && x.Message.Origin.Kind != MessageKind.User) why = "A client sends user messages only";
        if (why is null && chat.Model!.State.Turns.Any(t => t.Id == x.TurnId)) why = "A turn has this ID already";
        if (why is not null)
        {
            Broadcast(chat.Uri, raw, origin, why, c);
            return;
        }
        ApplyChat(chat, x, origin);
        chat.Model!.ClientTurnStarted(x.TurnId);
        Unread(chat.Session);
        Prompt(chat, x.TurnId, x.Message, Chosen(chat, x.Message));
        Sync(chat.Session);
    }

    /// <summary>
    /// Under the gate: sends a turn's message to the conductor, in the session's order (<see cref="Write{T}"/>), decided
    /// now: once the session exists, the model the message names first, when it is another than the session's; then the
    /// prompt, with the turn's ID as its <c>gorynych/promptId</c>, whose end mark ends the turn. A prompt that fails
    /// before its agent could mark its end (a restart refused it) ends its turn with the error.
    /// </summary>
    void Prompt(FaceChat chat, string turnId, Message message, string? chosen)
    {
        var s = chat.Session;
        var started = Write(s, async () =>
        {
            await s.Created; // a session still being made: its first prompt waits for it
            if (s.SessionId.Length == 0) throw new JsonRpcException(JsonRpcException.InternalError, "The session could not be created");
            if (chosen is not null) await UseModelAsync(s, chosen);
            var @params = new JsonObject
            {
                ["sessionId"] = s.SessionId, ["prompt"] = Blocks(message),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = turnId },
            };
            var response = await acp!.StartRequestAsync(AcpMethods.Agent.SessionPrompt, @params, stopping.Token);
            // Prompted: the conductor keeps it now, and lists it once it has made it a session; a draft until then, which
            // a list read before that does not take away.
            lock (gate) s.Prompted = true;
            return response;
        });
        _ = Task.Run(async () =>
        {
            try { await await started; }
            catch (Exception) when (stopping.IsCancellationRequested) { }
            catch (Exception e)
            {
                lock (gate)
                {
                    if (chat.Model is not { } model) return;
                    model.FailTurn(turnId, e is JsonRpcException j ? j.Code : JsonRpcErrorCodes.InternalError, e.Message);
                    ConsumeQueued(chat);
                    Sync(s);
                }
            }
        });
    }

    /// <summary>A message's ACP prompt: its text, and its attachments a prompt can carry (a file as a resource link, a client's own text).</summary>
    static JsonArray Blocks(Message message)
    {
        var blocks = new List<JsonNode?>();
        static JsonNode Text(string text) => new JsonObject { ["type"] = "text", ["text"] = text };
        if (message.Text.Length > 0 || message.Attachments is not { Count: > 0 }) blocks.Add(Text(message.Text));
        foreach (var attachment in message.Attachments ?? [])
            switch (attachment.Value)
            {
                case MessageResourceAttachment r:
                    // A file of this host as the agents see one (file:///C:/x, not VS Code's file:///c%3A/x); any other as it came.
                    blocks.Add(new JsonObject { ["type"] = "resource_link", ["uri"] = AhpNames.FileUri(AhpNames.FilePath(r.Uri)) ?? r.Uri, ["name"] = r.Label });
                    break;
                case SimpleMessageAttachment simple when simple.Meta?.ContainsKey(CommandMeta) != true && simple.ModelRepresentation is { Length: > 0 } text:
                    blocks.Add(Text(text));
                    break;
            }
        if (blocks.Count == 0) blocks.Add(Text(message.Text));
        return new JsonArray([.. blocks]);
    }

    /// <summary>The model a message names, set on its session before its prompt when it is another of the agent's models.</summary>
    async Task UseModelAsync(FaceSession s, string model)
    {
        string configId;
        lock (gate)
        {
            if (AhpConfig.Model(s.AcpOptions) is not { } option || option.Current == model || option.Models.All(m => m.Value != model)) return;
            configId = option.ConfigId;
        }
        var answer = await acp!.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = s.SessionId, ["configId"] = configId, ["value"] = model }, stopping.Token);
        lock (gate)
        {
            var options = answer?["configOptions"]?.DeepClone() as JsonArray;
            if (options is null && s.AcpOptions?.DeepClone() is JsonArray kept)
            {
                // An answer that says no options: the model is the one set all the same.
                foreach (var option in kept.OfType<JsonObject>()) if (Text(option["id"]) == configId) option["currentValue"] = model;
                options = kept;
            }
            OptionsChanged(s, options, s.AcpModes);
        }
    }

    /// <summary>
    /// A client cancels the running turn: over here at once, <c>session/cancel</c> to the conductor, and what the
    /// agent still asks of the face answered as cancelled, as ACP has a client do when it cancels. What the agent
    /// still sends of the turn is left out until its end mark comes (<see cref="ChatModel.Settling"/>).
    /// </summary>
    void TurnCancelled(Connection c, FaceChat chat, StateAction raw, ChatTurnCancelledAction x, ActionOrigin origin)
    {
        var why = Unwritable(chat);
        if (why is null && chat.Model!.State.ActiveTurn?.Id != x.TurnId) why = "No such turn runs";
        if (why is not null)
        {
            Broadcast(chat.Uri, raw, origin, why, c);
            return;
        }
        var model = chat.Model!;
        ApplyChat(chat, x, origin);
        model.ClientTurnCancelled();
        foreach (var ask in requests.Values.Where(r => r.Chat == chat).ToList()) Resolve(ask, ask.Cancelled());
        var s = chat.Session;
        _ = Write(s, async () =>
        {
            await s.Created;
            if (s.SessionId.Length == 0) return false;
            try { await acp!.SendNotificationAsync(AcpMethods.Agent.SessionCancel, new JsonObject { ["sessionId"] = s.SessionId }); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { ServiceLog.Info($"ahp: cancelling {s.SessionId}: {e.Message}"); }
            return true;
        });
        ScheduleQuiet(chat);
        ConsumeQueued(chat);
        Sync(chat.Session);
    }

    /// <summary>A client queues a message, or steers: kept in the chat, and sent at once if no turn runs.</summary>
    void PendingSet(Connection c, FaceChat chat, StateAction raw, ChatPendingMessageSetAction x, ActionOrigin origin)
    {
        var why = Unwritable(chat);
        if (why is null && x.Message.Origin.Kind != MessageKind.User) why = "A client sends user messages only";
        if (why is not null)
        {
            Broadcast(chat.Uri, raw, origin, why, c);
            return;
        }
        ApplyChat(chat, x, origin);
        ConsumeQueued(chat);
        Sync(chat.Session);
    }

    void PendingRemoved(Connection c, FaceChat chat, StateAction raw, ChatPendingMessageRemovedAction x, ActionOrigin origin)
    {
        var state = chat.Model?.State;
        var there = x.Kind == PendingMessageKind.Steering ? state?.SteeringMessage?.Id == x.Id : state?.QueuedMessages?.Any(m => m.Id == x.Id) == true;
        if (!there)
        {
            Broadcast(chat.Uri, raw, origin, "No such message waits", c);
            return;
        }
        ApplyChat(chat, x, origin);
    }

    /// <summary>
    /// Under the gate: when no turn runs, the next waiting message starts one, as AHP's pending messages are consumed: a
    /// steering message first (the face cannot put one into a running turn, so it goes next), then the queue in order.
    /// </summary>
    void ConsumeQueued(FaceChat chat)
    {
        if (chat.Model is not { } model || model.Busy || model.State.ActiveTurn is not null || Unwritable(chat) is not null) return;
        var (next, kind) = model.State.SteeringMessage is { } steering ? (steering, PendingMessageKind.Steering)
            : model.State.QueuedMessages is [var first, ..] ? (first, PendingMessageKind.Queued)
            : ((PendingMessage?)null, PendingMessageKind.Queued);
        if (next is null) return;
        var turnId = next.Id;
        for (var n = 2; model.State.Turns.Any(t => t.Id == turnId); n++) turnId = $"{next.Id}#{n}";
        ApplyChat(chat, new ChatPendingMessageRemovedAction { Kind = kind, Id = next.Id });
        ApplyChat(chat, new ChatTurnStartedAction { TurnId = turnId, StartedAt = AhpNames.Time(DateTimeOffset.UtcNow), Message = next.Message, QueuedMessageId = next.Id });
        model.ClientTurnStarted(turnId);
        Unread(chat.Session);
        Prompt(chat, turnId, next.Message, Chosen(chat, next.Message));
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>
    /// Under the gate: the agent's options for the session, as it said them (a load or a new session's answer,
    /// <c>config_option_update</c>, an answer to <c>session/set_config_option</c>). The session's config follows: its
    /// values by <c>session/configChanged</c>; a schema that changed reaches a client when it subscribes again, as AHP
    /// 1.0.0 has no action for it. The agent's models follow in the root's <c>AgentInfo.models</c>.
    /// </summary>
    void OptionsChanged(FaceSession s, JsonArray? options, JsonObject? modes)
    {
        s.AcpOptions = options;
        s.AcpModes = modes;
        var config = AhpConfig.Of(options, modes, out var modesAsMode);
        s.ModesAsMode = modesAsMode;
        if (AhpConfig.Model(options) is { } model)
        {
            ModelsSeen(s.Agent, model.Models);
            OfferModel(s.Chats[0]);
        }
        if (config is null) return;
        agentConfigs[s.Agent] = Clone(config);
        if (s.State.Config is not { } had)
        {
            s.State.Config = config;
            return;
        }
        had.Schema = config.Schema;
        // An option that went takes its value with it (all values replaced); otherwise the changed ones.
        if (had.Values.Keys.Any(k => !config.Values.ContainsKey(k)))
        {
            ApplySession(s, new SessionConfigChangedAction { Config = new(config.Values, StringComparer.Ordinal), Replace = true });
            return;
        }
        var changed = config.Values.Where(v => !had.Values.TryGetValue(v.Key, out var was) || !AhpConfig.Same(was, v.Value))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        if (changed.Count > 0) ApplySession(s, new SessionConfigChangedAction { Config = changed });
    }

    /// <summary>
    /// Under the gate: the session's model, in its chat's draft (<c>chat/draftChanged</c>), where a client's model picker
    /// takes it from (VS Code's reads the draft's model, else its last turn's, else its own default). The face puts it
    /// there when the chat has no draft and when the model changed; the draft's text, and a model a client chose in it,
    /// are left alone otherwise.
    /// </summary>
    void OfferModel(FaceChat chat)
    {
        if (chat.IsChild || chat.Model is not { } model || AhpConfig.Model(chat.Session.AcpOptions)?.Current is not { Length: > 0 } current) return;
        var draft = model.State.Draft;
        if (draft is not null && (chat.Offered == current || draft.Model?.Id == current))
        {
            chat.Offered = current;
            chat.ClientModel ??= current;
            return;
        }
        var offered = draft is null ? new Message { Text = "", Origin = new MessageOrigin { Kind = MessageKind.User } } : Clone(draft);
        offered.Model = new ModelSelection { Id = current };
        ApplyChat(chat, new ChatDraftChangedAction { Draft = offered });
        chat.Offered = current;
        chat.ClientModel ??= current;
    }

    /// <summary>
    /// The model a message asks for when its client chose it: another than its picker held before (the model it sent
    /// last, or the one the face offered). VS Code sends its picker's model with every message, and its picker does not
    /// follow a change made elsewhere: the same model again is no choice, and does not undo that change.
    /// </summary>
    static string? Chosen(FaceChat chat, Message message)
    {
        if (message.Model?.Id is not { Length: > 0 } model) return null;
        var chosen = model != chat.ClientModel ? model : null;
        chat.ClientModel = model;
        return chosen;
    }

    /// <summary>Under the gate: the agent's mode changed (<c>current_mode_update</c>).</summary>
    void ModeChanged(FaceSession s, string? modeId)
    {
        if (modeId is null) return;
        if (s.AcpModes is { } modes) modes["currentModeId"] = modeId;
        if (!s.ModesAsMode || s.State.Config is not { } config) return;
        var value = AhpNames.Element(JsonValue.Create(modeId));
        if (!AhpConfig.Same(config.Values.GetValueOrDefault(AhpConfig.ModeKey), value))
            ApplySession(s, new SessionConfigChangedAction { Config = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [AhpConfig.ModeKey] = value } });
    }

    /// <summary>Under the gate: the models the face has seen for an agent, in the root's agents when they changed.</summary>
    void ModelsSeen(string agent, List<AhpConfig.Choice> models)
    {
        if (agentModels.TryGetValue(agent, out var had) && had.SequenceEqual(models)) return;
        agentModels[agent] = models;
        var agents = root.Agents.Select(a => a.Provider != agent ? a : new AgentInfo
        {
            Provider = a.Provider, DisplayName = a.DisplayName, Description = a.Description, Capabilities = a.Capabilities,
            Models = [.. models.Select(m => new SessionModelInfo
            {
                Id = m.Value, Provider = agent, Name = m.Name,
                Meta = m.Description is { } d ? new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["description"] = AhpNames.Element(JsonValue.Create(d)) } : null,
            })],
        }).ToList();
        var action = new StateAction(new RootAgentsChangedAction { Agents = agents });
        Broadcast(AhpNames.Root, action);
        Reducers.ApplyToRoot(root, action);
    }

    /// <summary>
    /// A client changes the session's options (<c>session/configChanged</c>): refused at once when the session has no
    /// such option or value; else echoed at once, in the order the client sent its actions (an AHP client matches its
    /// pending actions to the echoes in that order), then each value set in its agent with
    /// <c>session/set_config_option</c> (or <c>session/set_mode</c>). A value its agent refuses goes back, by the host.
    /// </summary>
    void ConfigChanged(Connection c, FaceSession s, StateAction raw, SessionConfigChangedAction x, ActionOrigin origin)
    {
        string? why = s.State.Config is not { } config || s.SessionId.Length == 0 ? "This session has no options to change"
            : x.Replace == true ? "Change the options one by one"
            : x.Config.Where(v => !AhpConfig.Takes(config, v.Key, v.Value)).Select(v => $"{v.Key}: not an option of this session, or not one of its values").FirstOrDefault();
        if (why is not null)
        {
            Broadcast(s.Uri, raw, origin, why, c);
            return;
        }
        var changes = x.Config.ToList();
        var before = changes.Where(v => s.State.Config!.Values.ContainsKey(v.Key)).ToDictionary(v => v.Key, v => s.State.Config!.Values[v.Key], StringComparer.Ordinal);
        ApplySession(s, x, origin);
        _ = Write(s, async () =>
        {
            string? failed = null;
            JsonArray? options = null;
            var set = new List<string>();
            try { options = await ConfigureAsync(s, changes, set); }
            catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException) { failed = e.Message; }
            lock (gate)
            {
                if (!sessions.TryGetValue(s.Uri, out var now) || now != s) return false;
                if (options is not null) OptionsChanged(s, options, s.AcpModes);
                if (failed is null) return true;
                // What the agent did not take goes back to what it was, unless it changed since.
                ServiceLog.Warn($"ahp: {s.SessionId}: an option change was refused: {failed}");
                var back = before.Where(b => !set.Contains(b.Key) && s.State.Config?.Values.TryGetValue(b.Key, out var value) == true && AhpConfig.Same(value, x.Config[b.Key]))
                    .ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);
                if (back.Count > 0) ApplySession(s, new SessionConfigChangedAction { Config = back });
                return false;
            }
        });
    }

    /// <summary>
    /// Sets options of the session in its agent; returns its options after the last change, when it said them. The keys
    /// it set go in <paramref name="set"/> as it goes, so that a failure part way says which.
    /// </summary>
    async Task<JsonArray?> ConfigureAsync(FaceSession s, List<KeyValuePair<string, JsonElement>> changes, List<string>? set = null)
    {
        JsonArray? options = null;
        foreach (var (key, value) in changes)
        {
            bool asMode;
            lock (gate) asMode = s.ModesAsMode && key == AhpConfig.ModeKey;
            if (asMode)
            {
                await acp!.SendRequestAsync(AcpMethods.Agent.SessionSetMode, new JsonObject { ["sessionId"] = s.SessionId, ["modeId"] = value.GetString() }, stopping.Token);
                lock (gate) ModeChanged(s, value.GetString());
                set?.Add(key);
                continue;
            }
            var @params = new JsonObject { ["sessionId"] = s.SessionId, ["configId"] = key };
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                @params["type"] = "boolean";
                @params["value"] = value.GetBoolean();
            }
            else @params["value"] = value.GetString();
            var answer = await acp!.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, @params, stopping.Token);
            options = answer?["configOptions"]?.DeepClone() as JsonArray ?? options;
            set?.Add(key);
        }
        return options;
    }

    /// <summary>
    /// The options a new session of an agent will have, before it exists: those of the latest session of that agent
    /// the face has seen (none before it has seen one), with the values the client chose. A new session takes its
    /// agent's own defaults; the values chosen here are set on it once it exists, before it is ready.
    /// </summary>
    JsonElement? ResolveSessionConfig(ResolveSessionConfigParams p)
    {
        lock (gate)
        {
            var agent = p.Provider is { Length: > 0 } named ? named : root.Agents.FirstOrDefault()?.Provider;
            var known = agent is null ? null : agentConfigs.GetValueOrDefault(agent);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in p.Config ?? [])
                if (known is not null && AhpConfig.Takes(known, key, value)) values[key] = value;
            return Json.SerializeToElement(new ResolveSessionConfigResult
            {
                Schema = known is null ? new SessionConfigSchema { Type = "object", Properties = [] } : Clone(known.Schema),
                Values = values,
            });
        }
    }

    // ---------------------------------------------------------------- creating and deleting sessions

    /// <summary>
    /// <c>createSession</c>: the session at the URI its client chose, <c>creating</c> at once; <c>session/new</c> in its
    /// agent with that URI as <c>gorynych/ahpUri</c>, then the options the client chose, then <c>session/ready</c> and
    /// <c>root/sessionAdded</c>; or <c>session/creationFailed</c>.
    /// </summary>
    async Task<JsonElement?> CreateSessionAsync(CreateSessionParams p)
    {
        await AgentsReadyAsync();
        if (!AhpNames.IsSessionUri(p.Channel)) throw new AhpError(JsonRpcErrorCodes.InvalidParams, "channel: an ahp-session:/<id> URI");
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FaceSession s;
        FaceChat own;
        lock (gate)
        {
            var provider = p.Provider is { Length: > 0 } named ? named : root.Agents.FirstOrDefault()?.Provider ?? "";
            if (!agentNames.TryGetValue(provider, out var display)) throw new AhpError(AhpErrorCodes.ProviderNotFound, $"No agent {provider} on this host");
            if (sessions.ContainsKey(p.Channel) || chats.ContainsKey(AhpNames.ChatOf(p.Channel)))
                throw new AhpError(AhpErrorCodes.SessionAlreadyExists, $"Session {p.Channel} exists already");
            // The first folder the host serves; none named, the user's home. Named but none served: refused, not put elsewhere.
            var folder = p.WorkingDirectories is { Count: > 0 } folders
                ? folders.Select(AhpNames.FilePath).FirstOrDefault(f => f is not null)
                    ?? throw new AhpError(JsonRpcErrorCodes.InvalidParams, $"workingDirectories: {folders[0]} is no folder of this host (a file:///<path> URI)")
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var now = AhpNames.Time(DateTimeOffset.UtcNow);
            var title = $"New {display} session";
            s = new FaceSession(p.Channel, "")
            {
                Agent = provider, Title = title, Cwd = folder, UpdatedAt = now, CreatedAt = now, Draft = true, BaseSeq = serverSeq, Created = created.Task,
            };
            own = new FaceChat(AhpNames.ChatOf(p.Channel), "", s) { Agent = provider, Title = title, Cwd = folder, UpdatedAt = now };
            s.Chats.Add(own);
            // Until its agent has it: the options of its agent's latest session seen, with the values the client chose.
            SessionConfigState? config = null;
            if (agentConfigs.GetValueOrDefault(provider) is { } known)
            {
                config = Clone(known);
                config.Values.Clear();
                foreach (var (key, value) in p.Config ?? []) if (AhpConfig.Takes(known, key, value)) config.Values[key] = value;
            }
            s.State = new SessionState
            {
                Provider = provider, Title = title, Status = SessionStatus.Idle | SessionStatus.IsRead,
                WorkingDirectories = AhpNames.FileUri(folder) is { } uri ? [uri] : null,
                Lifecycle = SessionLifecycle.Creating, ActiveClients = [], Chats = [ChatSummaryOf(own)], DefaultChat = own.Uri,
                Customizations = [], Changesets = [], ServerTools = [], InputNeeded = [], Config = config, Meta = MetaOf(s),
            };
            if (p.ActiveClient is { } active)
                Reducers.ApplyToSession(s.State, new StateAction(new SessionActiveClientSetAction { ActiveClient = active }));
            var model = new ChatModel(NewChatState(own));
            model.Emit = action => EmitChat(own, action);
            own.Model = model;
            own.BaseSeq = serverSeq;
            own.Unwatched = DateTimeOffset.UtcNow;
            sessions[s.Uri] = s;
            chats[own.Uri] = own;
        }
        _ = Task.Run(() => CreateInAgentAsync(s, own, [.. p.Config ?? []], created));
        return Null;
    }

    async Task CreateInAgentAsync(FaceSession s, FaceChat own, List<KeyValuePair<string, JsonElement>> wanted, TaskCompletionSource created)
    {
        lock (gate) making++;
        try
        {
            JsonNode? answer;
            try
            {
                answer = await acp!.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
                {
                    ["cwd"] = s.Cwd, ["mcpServers"] = new JsonArray(),
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaAgent] = s.Agent, [GorynychProtocol.MetaAhpUri] = s.Uri },
                }, stopping.Token);
            }
            catch (Exception e) when (!stopping.IsCancellationRequested)
            {
                ServiceLog.Warn($"ahp: {s.Uri} could not be created: {e.Message}");
                lock (gate)
                    if (sessions.TryGetValue(s.Uri, out var now) && now == s)
                        ApplySession(s, new SessionCreationFailedAction
                        {
                            Error = new ErrorInfo { ErrorType = e is JsonRpcException j ? j.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "error", Message = e.Message },
                        });
                return;
            }
            if (Text(answer?["sessionId"]) is not { Length: > 0 } id)
            {
                lock (gate)
                    if (sessions.TryGetValue(s.Uri, out var now) && now == s)
                        ApplySession(s, new SessionCreationFailedAction { Error = new ErrorInfo { ErrorType = "error", Message = "The agent made no session" } });
                return;
            }
            List<KeyValuePair<string, JsonElement>> changes;
            lock (gate)
            {
                if (!sessions.TryGetValue(s.Uri, out var now) || now != s)
                {
                    // Disposed while it was being made: it goes in its agent too.
                    _ = Task.Run(async () =>
                    {
                        try { await acp!.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = id }, stopping.Token); }
                        catch (Exception e) { ServiceLog.Info($"ahp: deleting {id}, disposed while it was made: {e.Message}"); }
                    });
                    return;
                }
                s.SessionId = own.SessionId = id;
                sessionsById[id] = s;
                chatsById[id] = own;
                if (unclaimedCommands.Remove(id, out var commands)) own.Commands = commands;
                ApplySession(s, new SessionMetaChangedAction { Meta = MetaOf(s) });
                OptionsChanged(s, answer?["configOptions"]?.DeepClone() as JsonArray, answer?["modes"]?.DeepClone() as JsonObject);
                var config = s.State.Config;
                changes = config is null ? [] : [.. wanted.Where(w => AhpConfig.Takes(config, w.Key, w.Value) && !AhpConfig.Same(config.Values.GetValueOrDefault(w.Key), w.Value))];
                foreach (var ask in requests.Values.Where(r => r.Chat is null && r.SessionId == id).ToList()) Show(ask);
            }
            // The options its client chose before it existed, before it is ready (and before its first prompt).
            if (changes.Count > 0)
                try
                {
                    var options = await ConfigureAsync(s, changes);
                    lock (gate) if (options is not null) OptionsChanged(s, options, s.AcpModes);
                }
                catch (Exception e) when (!stopping.IsCancellationRequested)
                {
                    ServiceLog.Warn($"ahp: {id}: the options chosen for it could not be set: {e.Message}");
                }
            lock (gate)
            {
                if (!sessions.TryGetValue(s.Uri, out var now) || now != s) return;
                ApplySession(s, new SessionReadyAction());
                s.Sent = Summary(s);
                NotifyRoot("root/sessionAdded", Json.SerializeToElement(new SessionAddedParams { Channel = AhpNames.Root, Summary = s.Sent }));
            }
            ServiceLog.Info($"ahp: created {id} ({s.Agent}) as {s.Uri}");
        }
        catch (Exception) when (stopping.IsCancellationRequested) { }
        finally
        {
            lock (gate)
                if (--making == 0) unclaimedCommands.Clear();
            created.TrySetResult();
        }
    }

    /// <summary><c>disposeSession</c>: <c>session/delete</c>, which deletes the session in its agent where the agent can, as a delete from Zed does.</summary>
    async Task<JsonElement?> DisposeSessionAsync(DisposeSessionParams p)
    {
        FaceSession? s;
        lock (gate) s = sessions.GetValueOrDefault(p.Channel);
        if (s is null) throw new AhpError(AhpErrorCodes.SessionNotFound, $"No session {p.Channel} on this host");
        await s.Created;
        if (s.SessionId.Length > 0)
            try
            {
                await acp!.SendRequestAsync(AcpMethods.Agent.SessionDelete, new JsonObject { ["sessionId"] = s.SessionId }, stopping.Token);
            }
            catch (JsonRpcException e)
            {
                throw new AhpError(JsonRpcErrorCodes.InternalError, $"Session {s.SessionId} could not be deleted: {e.Message}");
            }
        lock (gate)
            if (sessions.TryGetValue(p.Channel, out var now) && now == s) RemoveSession(s, deleted: true);
        return Null;
    }

    // ---------------------------------------------------------------- completions

    /// <summary>Slash commands: the agent's commands whose names begin with what the user typed after a leading <c>/</c>.</summary>
    JsonElement? Completions(CompletionsParams p)
    {
        var items = new List<CompletionItem>();
        lock (gate)
        {
            if (chats.TryGetValue(p.Channel, out var chat) && p.Offset >= 0 && p.Offset <= p.Text.Length)
            {
                var typed = p.Text[..(int)p.Offset];
                if (typed.StartsWith('/') && !typed.Any(char.IsWhiteSpace))
                    foreach (var (name, description) in chat.Commands.Where(cmd => cmd.Name.StartsWith(typed[1..], StringComparison.OrdinalIgnoreCase))
                        .OrderBy(cmd => cmd.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        var meta = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [CommandMeta] = AhpNames.Element(JsonValue.Create(name)) };
                        if (description is not null) meta["gorynych/description"] = AhpNames.Element(JsonValue.Create(description));
                        items.Add(new CompletionItem
                        {
                            InsertText = $"/{name} ", RangeStart = 0, RangeEnd = p.Offset,
                            Attachment = new MessageAttachment(new SimpleMessageAttachment { Type = MessageAttachmentKind.Simple, Label = $"/{name}", Meta = meta }),
                        });
                    }
            }
        }
        return Json.SerializeToElement(new CompletionsResult { Items = items });
    }
}
