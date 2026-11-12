using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Microsoft.AgentHostProtocol;
using AcpJsonRpcRequest = Gorynych.Acp.JsonRpc.JsonRpcRequest;
using AhpJsonRpcRequest = Microsoft.AgentHostProtocol.JsonRpcRequest;

namespace Gorynych.Service.Ahp;

/// <summary>Timings and bounds of the AHP face.</summary>
internal sealed class AhpFaceOptions
{
    /// <summary>How long a session the face loaded stays open with no AHP client on its chat.</summary>
    public TimeSpan CloseAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often the face looks for sessions to close.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often at most the face reads the list again after <c>_gorynych/sessions_changed</c>.</summary>
    public TimeSpan ListEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the chunks of a live user message are held for the rest of the message.</summary>
    public TimeSpan HoldFor { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How many action envelopes the replay log keeps.</summary>
    public int LogSize { get; init; } = 10_000;

    /// <summary>A client that takes nothing for this long is disconnected.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The AHP face (docs/ahp-face.md): toward AHP clients (VS Code's Agents window) a host that keeps AHP's state
/// tree, numbers actions, serves snapshots and replays; toward the conductor one more ACP client, on an in-process
/// connection. Step 1: reading. All state is reduced under one lock, with the package's reducers, and every
/// message a client gets is serialized under it; each client has its own queue to send from.
/// </summary>
internal sealed class AhpFace : IAsyncDisposable
{
    static readonly SystemTextJsonAhpSerializer Json = SystemTextJsonAhpSerializer.Default;
    static readonly JsonElement Empty = AhpNames.Element(new JsonObject());

    readonly Conductor conductor;
    readonly AhpFaceOptions options;
    readonly Lock gate = new();
    readonly CancellationTokenSource stopping = new();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // under gate
    long serverSeq;
    RootState root = new() { Agents = [] };
    readonly Dictionary<string, FaceSession> sessions = new(StringComparer.Ordinal); // ahp-session URI -> session
    readonly Dictionary<string, FaceChat> chats = new(StringComparer.Ordinal); // ahp-chat URI -> chat
    readonly LinkedList<(long Seq, string Channel, JsonElement Envelope)> log = new();
    readonly HashSet<string> knownClients = new(StringComparer.Ordinal);
    readonly HashSet<Connection> connections = [];
    bool listing, listAgain;

    JsonRpcConnection? acp;
    Task? serving;
    string serviceId = "";

    public AhpFace(Conductor conductor, AhpFaceOptions? options = null)
    {
        this.conductor = conductor;
        this.options = options ?? new AhpFaceOptions();
    }

    /// <summary>The port the endpoint listens on, and why it does not when it does not; for <c>status</c>.</summary>
    public int? Port { get; set; }

    public string? Error { get; set; }

    // ---------------------------------------------------------------- the internal client

    /// <summary>Connects to the conductor as an ACP client, reads the agents and the list, and starts serving.</summary>
    public async Task StartAsync()
    {
        try
        {
            var duplex = new DuplexLink();
            var (serviceIn, serviceOut) = duplex.AgentEnd;
            serving = conductor.ServeClientAsync(serviceIn, serviceOut, "ahp");
            var (faceIn, faceOut) = duplex.ClientEnd;
            acp = new JsonRpcConnection(faceIn, faceOut, "ahp face") { Log = m => ServiceLog.Warn(m) };
            acp.NotificationHandler = OnAcpNotificationAsync;
            acp.RequestHandler = (request, _) => throw new JsonRpcException(JsonRpcException.MethodNotFound,
                $"{request.Method}: the AHP face takes no requests in this version");
            acp.Start();
            var init = await acp.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.Initialize, new JsonObject
            {
                ["protocolVersion"] = (int)Gorynych.Acp.AcpMethods.ProtocolVersion,
                ["clientInfo"] = new JsonObject { ["name"] = "gorynych-ahp", ["title"] = "Gorynych's AHP face", ["version"] = AgentHost.Version },
                ["clientCapabilities"] = new JsonObject(),
                // On the link, so its prompts' ends are marked; and no approvals or forms, which it cannot answer yet.
                ["_meta"] = new JsonObject { [GorynychProtocol.CapLink] = GorynychProtocol.LinkVersion, [GorynychProtocol.MetaRequests] = false },
            }, stopping.Token);
            serviceId = AhpNames.Text(init?["_meta"]?[GorynychProtocol.MetaServiceId]) ?? "";
            var agents = (init?["_meta"]?[GorynychProtocol.MetaAgents] as JsonArray ?? []).OfType<JsonObject>()
                .Select(a => (Name: AhpNames.Text(a["name"]) ?? "", Display: AhpNames.Text(a["displayName"]) ?? AhpNames.Text(a["name"]) ?? ""))
                .Where(a => a.Name.Length > 0).ToList();
            var list = await ReadListAsync(stopping.Token);
            lock (gate)
            {
                root = new RootState
                {
                    Agents = [.. agents.Select(a => new AgentInfo
                    {
                        Provider = a.Name, DisplayName = a.Display, Description = $"{a.Display} through Gorynych", Models = [],
                        Capabilities = new AgentCapabilities { MultipleChats = new MultipleChatsCapability() },
                    })],
                    Config = new RootConfigState { Schema = new ConfigSchema { Type = "object", Properties = [] }, Values = [] },
                };
                ApplyList(list, live: false);
            }
            ready.TrySetResult();
            _ = Task.Run(SweepAsync);
            ServiceLog.Info($"ahp: the face serves {sessions.Count} sessions");
        }
        catch (Exception e)
        {
            Error = $"the AHP face did not start: {e.Message}";
            ServiceLog.Warn($"ahp: {Error}");
            ready.TrySetException(e);
        }
    }

    sealed record Listed(string Id, string Agent, string Title, string Cwd, string UpdatedAt, bool Archived, string? Parent);

    /// <summary>Every session of every agent, archived ones too, every page.</summary>
    async Task<List<Listed>> ReadListAsync(CancellationToken ct)
    {
        var listed = new List<Listed>();
        string? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var @params = new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true } };
            if (cursor is not null) @params["cursor"] = cursor;
            var answer = await acp!.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.SessionList, @params, ct);
            foreach (var info in (answer?["sessions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (AhpNames.Text(info["sessionId"]) is not { Length: > 0 } id) continue;
                var meta = info["_meta"] as JsonObject;
                listed.Add(new Listed(id, AhpNames.Text(meta?[GorynychProtocol.MetaAgent]) ?? "", AhpNames.Text(info["title"]) ?? "",
                    AhpNames.Text(info["cwd"]) ?? "", AhpNames.Time(AhpNames.Text(info["updatedAt"])) ?? AhpNames.Time(DateTimeOffset.UtcNow),
                    meta?[GorynychProtocol.MetaArchived] is JsonValue a && a.TryGetValue<bool>(out var archived) && archived,
                    AhpNames.Text(meta?[GorynychProtocol.MetaParentSessionId])));
            }
            cursor = AhpNames.Text(answer?["nextCursor"]);
            if (cursor is null) break;
        }
        return listed;
    }

    /// <summary>The list changed: it is read again, at most every <see cref="AhpFaceOptions.ListEvery"/>.</summary>
    void ListChanged()
    {
        lock (gate)
        {
            if (listing)
            {
                listAgain = true;
                return;
            }
            listing = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(options.ListEvery, stopping.Token);
                    lock (gate) listAgain = false;
                    var list = await ReadListAsync(stopping.Token);
                    lock (gate) ApplyList(list, live: true);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) when (e is IOException or JsonRpcException)
                {
                    ServiceLog.Warn($"ahp: the list could not be read again: {e.Message}");
                }
                lock (gate)
                    if (!listAgain)
                    {
                        listing = false;
                        return;
                    }
            }
        });
    }

    Task OnAcpNotificationAsync(string method, JsonNode? @params)
    {
        if (method == GorynychProtocol.MethodSessionsChanged)
        {
            ListChanged();
            return Task.CompletedTask;
        }
        if (method != Gorynych.Acp.AcpMethods.Client.SessionUpdate || AhpNames.Text(@params?["sessionId"]) is not { } sessionId || @params?["update"] is not JsonObject update)
            return Task.CompletedTask;
        lock (gate)
        {
            if (!chats.TryGetValue(AhpNames.ChatUri(sessionId), out var chat)) return Task.CompletedTask;
            if (AhpNames.Text(update["sessionUpdate"]) == "session_info_update")
            {
                // Title and archive go to the catalog whether or not the chat is open; the list says them too.
                if (AhpNames.Text(update["title"]) is { Length: > 0 } title) SetTitle(chat, title);
                if (update["_meta"]?[GorynychProtocol.MetaArchived] is JsonValue v && v.TryGetValue<bool>(out var archived)) SetArchived(chat, archived);
            }
            if (chat.Buffer is { } buffer)
            {
                buffer.Add((JsonObject)update.DeepClone());
                return Task.CompletedTask;
            }
            if (chat.Model is not { } model) return Task.CompletedTask;
            model.Take(update, live: true);
            if (model.Holding) ScheduleFlush(chat);
            Sync(chat.Session);
        }
        return Task.CompletedTask;
    }

    void ScheduleFlush(FaceChat chat) => _ = Task.Delay(options.HoldFor).ContinueWith(_ =>
    {
        lock (gate)
        {
            if (chat.Model is not { Holding: true } model) return;
            model.Flush(live: true);
            Sync(chat.Session);
        }
    }, TaskScheduler.Default);

    // ---------------------------------------------------------------- sessions and chats

    sealed class FaceSession(string uri, string sessionId)
    {
        public string Uri { get; } = uri;
        public string SessionId { get; } = sessionId;
        public string Agent = "";
        public string Title = "";
        public string Cwd = "";
        public string UpdatedAt = "";
        public string CreatedAt = "";
        public bool Archived;
        public bool IsRead = true;
        public long BaseSeq;
        public SessionState State = null!;
        public SessionSummary? Sent; // what the clients were told last on the root channel
        public readonly List<FaceChat> Chats = []; // its own first, then its descendants'
    }

    sealed class FaceChat(string uri, string sessionId, FaceSession session)
    {
        public string Uri { get; } = uri;
        public string SessionId { get; } = sessionId;
        public FaceSession Session { get; set; } = session;
        public bool IsChild => SessionId != Session.SessionId;
        public string Title = "";
        public string Cwd = "";
        public string UpdatedAt = "";
        public bool Archived;
        public bool IsRead = true;
        public ChatModel? Model;
        public Task? Loading;
        public List<JsonObject>? Buffer;
        public long BaseSeq;
        public DateTimeOffset? Unwatched;
    }

    /// <summary>Takes the conductor's list: sessions and children that came, went or changed, said to the clients when <paramref name="live"/>.</summary>
    void ApplyList(List<Listed> list, bool live)
    {
        var byId = new Dictionary<string, Listed>(StringComparer.Ordinal);
        foreach (var l in list) byId.TryAdd(l.Id, l);
        string RootOf(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var at = byId[id];
            while (at.Parent is { } parent && byId.TryGetValue(parent, out var up) && seen.Add(at.Id)) at = up;
            return at.Id;
        }

        // Sessions that went, or became children.
        foreach (var s in sessions.Values.ToList())
            if (!byId.ContainsKey(s.SessionId) || RootOf(s.SessionId) != s.SessionId)
                RemoveSession(s, live);
        var added = new List<FaceSession>();
        foreach (var l in byId.Values.Where(l => RootOf(l.Id) == l.Id))
        {
            if (sessions.TryGetValue(AhpNames.SessionUri(l.Id), out var s)) UpdateSession(s, l);
            else added.Add(AddSession(l));
        }
        // Children that went, or moved to another root.
        foreach (var chat in chats.Values.Where(c => c.IsChild).ToList())
            if (!byId.ContainsKey(chat.SessionId) || AhpNames.SessionUri(RootOf(chat.SessionId)) != chat.Session.Uri)
                RemoveChild(chat, quiet: added.Contains(chat.Session));
        foreach (var l in byId.Values.Where(l => RootOf(l.Id) != l.Id))
        {
            var s = sessions[AhpNames.SessionUri(RootOf(l.Id))];
            if (chats.TryGetValue(AhpNames.ChatUri(l.Id), out var chat)) UpdateChat(chat, l);
            else AddChild(s, l, quiet: added.Contains(s));
        }
        foreach (var s in added)
        {
            s.Sent = Summary(s);
            if (live) NotifyRoot("root/sessionAdded", Json.SerializeToElement(new SessionAddedParams { Channel = AhpNames.Root, Summary = s.Sent }));
        }
        foreach (var s in sessions.Values) Sync(s, live);
    }

    FaceSession AddSession(Listed l)
    {
        var s = new FaceSession(AhpNames.SessionUri(l.Id), l.Id)
        {
            Agent = l.Agent, Title = l.Title, Cwd = l.Cwd, UpdatedAt = l.UpdatedAt, CreatedAt = l.UpdatedAt, Archived = l.Archived, BaseSeq = serverSeq,
        };
        var own = new FaceChat(AhpNames.ChatUri(l.Id), l.Id, s) { Title = l.Title, Cwd = l.Cwd, UpdatedAt = l.UpdatedAt };
        s.Chats.Add(own);
        chats[own.Uri] = own;
        s.State = new SessionState
        {
            Provider = s.Agent, Title = s.Title, Status = SessionStatus.Idle | SessionStatus.IsRead | (s.Archived ? SessionStatus.IsArchived : 0),
            WorkingDirectories = AhpNames.FileUri(s.Cwd) is { } folder ? [folder] : null,
            Lifecycle = SessionLifecycle.Ready, ActiveClients = [], Chats = [ChatSummaryOf(own)], DefaultChat = own.Uri,
            Customizations = [], Changesets = [], ServerTools = [], InputNeeded = [],
            Meta = MetaOf(s),
        };
        sessions[s.Uri] = s;
        return s;
    }

    void UpdateSession(FaceSession s, Listed l)
    {
        s.UpdatedAt = l.UpdatedAt;
        s.Chats[0].UpdatedAt = l.UpdatedAt;
        s.Cwd = l.Cwd;
        if (l.Title != s.Title) SetTitle(s.Chats[0], l.Title);
        if (l.Archived != s.Archived) SetArchived(s.Chats[0], l.Archived);
    }

    void RemoveSession(FaceSession s, bool live)
    {
        sessions.Remove(s.Uri);
        foreach (var chat in s.Chats) Drop(chat);
        if (live) NotifyRoot("root/sessionRemoved", Json.SerializeToElement(new SessionRemovedParams { Channel = AhpNames.Root, Session = s.Uri }));
    }

    void AddChild(FaceSession s, Listed l, bool quiet)
    {
        var chat = new FaceChat(AhpNames.ChatUri(l.Id), l.Id, s) { Title = l.Title, Cwd = l.Cwd, UpdatedAt = l.UpdatedAt, Archived = l.Archived };
        chats[chat.Uri] = chat;
        s.Chats.Add(chat);
        if (quiet) s.State.Chats.Add(ChatSummaryOf(chat));
        else ApplySession(s, new SessionChatAddedAction { Summary = ChatSummaryOf(chat) });
    }

    void UpdateChat(FaceChat chat, Listed l)
    {
        chat.UpdatedAt = l.UpdatedAt;
        chat.Cwd = l.Cwd;
        if (l.Title != chat.Title) SetTitle(chat, l.Title);
        if (l.Archived != chat.Archived) SetArchived(chat, l.Archived);
    }

    void RemoveChild(FaceChat chat, bool quiet)
    {
        chat.Session.Chats.Remove(chat);
        if (quiet) chat.Session.State.Chats.RemoveAll(c => c.Resource == chat.Uri);
        else ApplySession(chat.Session, new SessionChatRemovedAction { Chat = chat.Uri });
        Drop(chat);
    }

    /// <summary>A chat that went: forgotten, and its session closed in the conductor if the face had it open.</summary>
    void Drop(FaceChat chat)
    {
        chats.Remove(chat.Uri);
        if (chat.Model is null) return;
        chat.Model = null;
        Close(chat.SessionId);
    }

    void SetTitle(FaceChat chat, string title)
    {
        chat.Title = title;
        if (chat.IsChild || chat.Session.Title == title) return;
        chat.Session.Title = title;
        ApplySession(chat.Session, new SessionTitleChangedAction { Title = title });
    }

    void SetArchived(FaceChat chat, bool archived)
    {
        if (!chat.IsChild)
        {
            if (chat.Session.Archived == archived) return;
            chat.Session.Archived = archived;
            ApplySession(chat.Session, new SessionIsArchivedChangedAction { IsArchived = archived });
            return;
        }
        if (chat.Archived == archived) return;
        chat.Archived = archived;
        if (chat.Model is not null) ApplyChat(chat, new ChatIsArchivedChangedAction { IsArchived = archived });
    }

    static Dictionary<string, JsonElement> MetaOf(FaceSession s) => new(StringComparer.Ordinal)
    {
        [GorynychProtocol.MetaSessionId] = AhpNames.Element(JsonValue.Create(s.SessionId)),
        [GorynychProtocol.MetaAgent] = AhpNames.Element(JsonValue.Create(s.Agent)),
    };

    // ---------------------------------------------------------------- the catalog

    const SessionStatus Activity = SessionStatus.Idle | SessionStatus.Error | SessionStatus.InProgress | SessionStatus.InputNeeded;

    SessionStatus StatusOf(FaceChat chat) => chat.Model is { } model
        ? model.State.Status
        : SessionStatus.Idle | (chat.IsRead ? SessionStatus.IsRead : 0) | (chat.IsChild && chat.Archived ? SessionStatus.IsArchived : 0);

    string ModifiedOf(FaceChat chat) => chat.Model?.State.ModifiedAt ?? chat.UpdatedAt;

    ChatSummary ChatSummaryOf(FaceChat chat) => new()
    {
        Resource = chat.Uri, Title = chat.Title, Status = StatusOf(chat), Activity = chat.Model?.State.Activity, ModifiedAt = ModifiedOf(chat),
        Origin = UserOrigin(), Interactivity = ChatInteractivity.ReadOnly,
        WorkingDirectories = AhpNames.FileUri(chat.Cwd) is { } folder ? [folder] : null,
    };

    static ChatOrigin UserOrigin() => new(new ChatOriginUser { Kind = "user" });

    /// <summary>A session's status: its chats' activity (input needed, then running, then an error, else idle), and its own read and archive bits.</summary>
    SessionStatus SessionStatusOf(FaceSession s)
    {
        var statuses = s.Chats.Select(StatusOf).ToList();
        var activity = statuses.Any(st => (st & SessionStatus.InputNeeded) == SessionStatus.InputNeeded) ? SessionStatus.InputNeeded
            : statuses.Any(st => (st & SessionStatus.InProgress) != 0) ? SessionStatus.InProgress
            : statuses.Any(st => (st & SessionStatus.Error) != 0) ? SessionStatus.Error
            : SessionStatus.Idle;
        return activity | (s.IsRead ? SessionStatus.IsRead : 0) | (s.Archived ? SessionStatus.IsArchived : 0);
    }

    static string Later(string a, string b) =>
        DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x)
        && DateTimeOffset.TryParse(b, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var y) && y > x ? b : a;

    SessionSummary Summary(FaceSession s) => new()
    {
        Resource = s.Uri, Provider = s.Agent, Title = s.Title, Status = SessionStatusOf(s),
        WorkingDirectories = AhpNames.FileUri(s.Cwd) is { } folder ? [folder] : null,
        CreatedAt = s.CreatedAt, ModifiedAt = s.Chats.Select(ModifiedOf).Aggregate(s.UpdatedAt, Later),
        Chats = [.. s.Chats.Select(c => new SessionChatSummary
        {
            Resource = c.Uri, Title = c.Title, Origin = UserOrigin(), Interactivity = ChatInteractivity.ReadOnly, Status = StatusOf(c),
        })],
        DefaultChat = s.Chats[0].Uri,
        Meta = MetaOf(s),
    };

    /// <summary>
    /// Keeps the session's chat catalog in step with its chats (<c>session/chatUpdated</c>), and its summary on the
    /// root channel (<c>root/sessionSummaryChanged</c>, the changed fields only), as AHP requires of a host.
    /// </summary>
    void Sync(FaceSession s, bool live = true)
    {
        if (!sessions.ContainsKey(s.Uri)) return;
        foreach (var chat in s.Chats)
        {
            var now = ChatSummaryOf(chat);
            if (s.State.Chats.FirstOrDefault(c => c.Resource == chat.Uri) is not { } had) continue;
            var changes = new PartialChatSummary();
            var changed = false;
            if (had.Title != now.Title) { changes = changes with { Title = now.Title }; changed = true; }
            if (had.Status != now.Status) { changes = changes with { Status = now.Status }; changed = true; }
            // A partial change cannot remove a field: an activity cleared is the empty string.
            if ((had.Activity ?? "") != (now.Activity ?? "")) { changes = changes with { Activity = now.Activity ?? "" }; changed = true; }
            if (had.ModifiedAt != now.ModifiedAt) { changes = changes with { ModifiedAt = now.ModifiedAt }; changed = true; }
            if (changed) ApplySession(s, new SessionChatUpdatedAction { Chat = chat.Uri, Changes = changes });
        }
        var summary = Summary(s);
        if (s.Sent is not { } sent || !live)
        {
            s.Sent = summary;
            return;
        }
        var partial = new PartialSessionSummary();
        var any = false;
        if (sent.Title != summary.Title) { partial = partial with { Title = summary.Title }; any = true; }
        if (sent.Status != summary.Status) { partial = partial with { Status = summary.Status }; any = true; }
        if (sent.ModifiedAt != summary.ModifiedAt) { partial = partial with { ModifiedAt = summary.ModifiedAt }; any = true; }
        if (!(sent.WorkingDirectories ?? []).SequenceEqual(summary.WorkingDirectories ?? [])) { partial = partial with { WorkingDirectories = summary.WorkingDirectories ?? [] }; any = true; }
        if (Catalog(sent) != Catalog(summary)) { partial = partial with { Chats = summary.Chats }; any = true; }
        s.Sent = summary;
        if (any)
            NotifyRoot("root/sessionSummaryChanged", Json.SerializeToElement(new SessionSummaryChangedParams { Channel = AhpNames.Root, Session = s.Uri, Changes = partial }));
    }

    static string Catalog(SessionSummary s) =>
        string.Join("\n", (s.Chats ?? []).Select(c => $"{c.Resource}\t{c.Title}\t{(int?)c.Status}\t{(int?)c.Interactivity}"));

    // ---------------------------------------------------------------- actions

    /// <summary>Numbers an action, keeps it for replays and sends it to the channel's subscribers, before any reducer takes it.</summary>
    ActionEnvelope Broadcast(string channel, StateAction action, ActionOrigin? origin = null, string? rejection = null, Connection? also = null)
    {
        var envelope = new ActionEnvelope { Channel = channel, Action = action, ServerSeq = ++serverSeq, Origin = origin, RejectionReason = rejection };
        var element = Json.SerializeToElement(envelope);
        log.AddLast((envelope.ServerSeq, channel, element));
        while (log.Count > options.LogSize) log.RemoveFirst();
        var message = Notification("action", element);
        foreach (var c in connections)
            if (c.Initialized && (c.Subscriptions.Contains(channel) || c == also)) c.Send(message);
        return envelope;
    }

    void ApplySession(FaceSession s, object action, ActionOrigin? origin = null)
    {
        var wrapped = new StateAction(action);
        Broadcast(s.Uri, wrapped, origin);
        Reducers.ApplyToSession(s.State, wrapped);
    }

    void ApplyChat(FaceChat chat, object action, ActionOrigin? origin = null)
    {
        var wrapped = new StateAction(action);
        Broadcast(chat.Uri, wrapped, origin);
        Reducers.ApplyToChat(chat.Model!.State, wrapped);
    }

    /// <summary>The chat model's live actions: numbered and sent; a turn that starts makes its session unread, as the reducer makes the chat.</summary>
    void EmitChat(FaceChat chat, StateAction action)
    {
        Broadcast(chat.Uri, action);
        if (action.Value is ChatTurnStartedAction && chat.Session.IsRead)
        {
            chat.Session.IsRead = false;
            ApplySession(chat.Session, new SessionIsReadChangedAction { IsRead = false });
        }
    }

    void NotifyRoot(string method, JsonElement @params)
    {
        var message = Notification(method, @params);
        foreach (var c in connections)
            if (c.Initialized && c.Subscriptions.Contains(AhpNames.Root)) c.Send(message);
    }

    // ---------------------------------------------------------------- loading and closing

    /// <summary>Opens the chat's session in the conductor and builds its state from the replay, once.</summary>
    async Task EnsureLoadedAsync(FaceChat chat)
    {
        Task load;
        lock (gate)
        {
            if (chat.Model is not null) return;
            if (chat.Loading is null)
            {
                chat.Buffer = [];
                chat.Loading = LoadAsync(chat);
            }
            load = chat.Loading;
        }
        await load;
    }

    async Task LoadAsync(FaceChat chat)
    {
        await Task.Yield();
        JsonNode? answer;
        try
        {
            answer = await acp!.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.SessionLoad,
                new JsonObject { ["sessionId"] = chat.SessionId, ["cwd"] = chat.Cwd, ["mcpServers"] = new JsonArray() }, stopping.Token);
        }
        catch (Exception e)
        {
            lock (gate)
            {
                chat.Buffer = null;
                chat.Loading = null;
            }
            throw new AhpError(JsonRpcErrorCodes.InternalError, $"Session {chat.SessionId} could not be opened: {e.Message}");
        }
        lock (gate)
        {
            var model = new ChatModel(NewChatState(chat)) { ReplayTime = chat.UpdatedAt };
            foreach (var update in chat.Buffer ?? []) model.Take(update, live: false);
            model.Flush(live: false);
            if (AhpNames.Text(answer?["_meta"]?[GorynychProtocol.MetaTurn]) != "running") model.EndOpenTurn(live: false);
            // A replay clears the read bit for every past turn: it is set back to what it was.
            if (chat.IsRead && (model.State.Status & SessionStatus.IsRead) == 0)
                Reducers.ApplyToChat(model.State, new StateAction(new ChatIsReadChangedAction { IsRead = true }));
            model.Emit = action => EmitChat(chat, action);
            chat.Model = model;
            chat.Buffer = null;
            chat.Loading = null;
            chat.BaseSeq = serverSeq;
            chat.Unwatched = DateTimeOffset.UtcNow;
            if (!chats.ContainsKey(chat.Uri))
            {
                // It went while it loaded.
                chat.Model = null;
                Close(chat.SessionId);
                return;
            }
            Sync(chat.Session);
        }
    }

    static ChatState NewChatState(FaceChat chat) => new()
    {
        Resource = chat.Uri, Title = chat.Title,
        Status = SessionStatus.Idle | (chat.IsRead ? SessionStatus.IsRead : 0) | (chat.IsChild && chat.Archived ? SessionStatus.IsArchived : 0),
        ModifiedAt = chat.UpdatedAt, Origin = UserOrigin(), Interactivity = ChatInteractivity.ReadOnly, Turns = [],
        WorkingDirectories = AhpNames.FileUri(chat.Cwd) is { } folder ? [folder] : null,
    };

    void Close(string sessionId) => _ = Task.Run(async () =>
    {
        try { await acp!.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId }, stopping.Token); }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException)
        {
            ServiceLog.Info($"ahp: closing {sessionId}: {e.Message}");
        }
    });

    /// <summary>
    /// Closes the sessions no AHP client has looked at for <see cref="AhpFaceOptions.CloseAfter"/>: never while a turn
    /// of one runs, since a close from the session's last client cancels its turn.
    /// </summary>
    async Task SweepAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try { await Task.Delay(options.SweepEvery, stopping.Token); }
            catch (OperationCanceledException) { return; }
            Sweep();
        }
    }

    internal void Sweep()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var chat in chats.Values)
            {
                if (chat.Model is not { } model) continue;
                if (connections.Any(c => c.Subscriptions.Contains(chat.Uri)))
                {
                    chat.Unwatched = null;
                    continue;
                }
                chat.Unwatched ??= now;
                if (now - chat.Unwatched < options.CloseAfter || model.Busy) continue;
                chat.IsRead = (model.State.Status & SessionStatus.IsRead) != 0;
                chat.Model = null;
                Close(chat.SessionId);
                ServiceLog.Info($"ahp: closed {chat.SessionId}, which no AHP client had open");
                Sync(chat.Session);
            }
        }
    }

    /// <summary>Whether the face has the session open in the conductor now; for tests.</summary>
    internal bool HasOpen(string sessionId)
    {
        lock (gate) return chats.TryGetValue(AhpNames.ChatUri(sessionId), out var chat) && chat.Model is not null;
    }

    /// <summary>For <c>_gorynych/status</c>.</summary>
    public JsonObject Status()
    {
        lock (gate)
            return new JsonObject
            {
                ["port"] = Port,
                ["error"] = Error,
                ["clients"] = connections.Count,
                ["sessions"] = sessions.Count,
                ["open"] = chats.Values.Count(c => c.Model is not null),
            };
    }

    // ---------------------------------------------------------------- connections

    sealed class Connection(ITransport transport, TimeSpan sendTimeout)
    {
        readonly Channel<TransportMessage> outbox = System.Threading.Channels.Channel.CreateUnbounded<TransportMessage>(new UnboundedChannelOptions { SingleReader = true });

        public ITransport Transport { get; } = transport;
        public string? ClientId;
        public bool Initialized;
        public readonly HashSet<string> Subscriptions = new(StringComparer.Ordinal); // under the face's gate

        public void Send(TransportMessage message) => outbox.Writer.TryWrite(message);

        public void Complete() => outbox.Writer.TryComplete();

        /// <summary>Sends what was queued, in order; a client that takes nothing for the send timeout is let go.</summary>
        public async Task SendLoopAsync()
        {
            await foreach (var message in outbox.Reader.ReadAllAsync())
            {
                try
                {
                    await Transport.SendAsync(message, CancellationToken.None).AsTask().WaitAsync(sendTimeout);
                }
                catch (TimeoutException)
                {
                    ServiceLog.Warn($"ahp: a client ({ClientId}) took nothing for {sendTimeout.TotalSeconds:0} s; it is disconnected");
                    try { await Transport.CloseAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                    return;
                }
                catch (Exception)
                {
                    return; // gone
                }
            }
        }
    }

    /// <summary>Serves one AHP client over <paramref name="transport"/> until it goes.</summary>
    public async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        var c = new Connection(transport, options.SendTimeout);
        var sending = Task.Run(c.SendLoopAsync);
        lock (gate) connections.Add(c);
        try
        {
            while (!ct.IsCancellationRequested && !stopping.IsCancellationRequested)
            {
                TransportMessage frame;
                try { frame = await transport.ReceiveAsync(ct); }
                catch (Exception e) when (e is TransportClosedException or OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException) { break; }
                JsonRpcMessage message;
                try { message = Json.DecodeMessage(frame); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
                {
                    ServiceLog.Warn($"ahp: a message that is not JSON-RPC: {e.Message}");
                    continue;
                }
                if (message.Request is { } request)
                {
                    // A subscription may wait for its session to open: it does not hold up what follows it.
                    if (request.Method == "subscribe") _ = Task.Run(() => AnswerAsync(c, request));
                    else await AnswerAsync(c, request);
                }
                else if (message.Notification is { } notification) Notified(c, notification);
            }
        }
        finally
        {
            lock (gate) connections.Remove(c);
            c.Complete();
            try { await sending.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    sealed class AhpError(int code, string message, JsonElement? data = null) : Exception(message)
    {
        public int Code { get; } = code;
        public JsonElement? ErrorData { get; } = data;
    }

    async Task AnswerAsync(Connection c, AhpJsonRpcRequest request)
    {
        TransportMessage reply;
        try
        {
            var result = await CommandAsync(c, request.Method, request.Params);
            reply = Json.EncodeMessage(new JsonRpcMessage { SuccessResponse = new JsonRpcSuccessResponse { JsonRpcVersion = "2.0", Id = request.Id, Result = result } });
        }
        catch (Exception e)
        {
            var (code, text, data) = e is AhpError a ? (a.Code, a.Message, a.ErrorData) : (JsonRpcErrorCodes.InternalError, e.Message, null);
            if (e is not AhpError) ServiceLog.Warn($"ahp: {request.Method} failed: {e}");
            reply = Json.EncodeMessage(new JsonRpcMessage
            {
                ErrorResponse = new JsonRpcErrorResponse { JsonRpcVersion = "2.0", Id = request.Id, Error = new JsonRpcErrorObject { Code = code, Message = text, Data = data } },
            });
        }
        c.Send(reply);
    }

    static TransportMessage Notification(string method, JsonElement @params) =>
        Json.EncodeMessage(new JsonRpcMessage { Notification = new JsonRpcNotification { JsonRpcVersion = "2.0", Method = method, Params = @params } });

    static T Params<T>(JsonElement? @params) where T : class
    {
        if (@params is not { } p) throw new AhpError(JsonRpcErrorCodes.InvalidParams, "params are required");
        try { return Json.Deserialize<T>(p) ?? throw new AhpError(JsonRpcErrorCodes.InvalidParams, "params are required"); }
        catch (JsonException e) { throw new AhpError(JsonRpcErrorCodes.InvalidParams, $"params: {e.Message}"); }
    }

    async Task<JsonElement> CommandAsync(Connection c, string method, JsonElement? @params)
    {
        switch (method)
        {
            case "ping":
                return Empty;
            case "initialize":
                return await InitializeAsync(c, Params<InitializeParams>(@params));
            case "reconnect":
                return await ReconnectAsync(c, Params<ReconnectParams>(@params));
        }
        if (!c.Initialized) throw new AhpError(JsonRpcErrorCodes.InvalidRequest, "Send initialize or reconnect first");
        switch (method)
        {
            case "listSessions":
                return ListSessions(Params<ListSessionsParams>(@params));
            case "subscribe":
                return await SubscribeAsync(c, Params<SubscribeParams>(@params).Channel);
            case "unsubscribe":
                lock (gate) c.Subscriptions.Remove(Params<UnsubscribeParams>(@params).Channel);
                return Empty;
            case "dispatchAction":
                Dispatch(c, Params<DispatchActionParams>(@params));
                return Empty;
            case "createSession" or "disposeSession":
                throw new AhpError(AhpErrorCodes.PermissionDenied, "Creating and deleting sessions comes in a later version");
            case "createChat" or "disposeChat" or "moveChat":
                throw new AhpError(AhpErrorCodes.PermissionDenied, "This host has one chat per session and its children");
            case "resolveSessionConfig":
                return Json.SerializeToElement(new ResolveSessionConfigResult
                {
                    Schema = new SessionConfigSchema { Type = "object", Properties = [] },
                    Values = Params<ResolveSessionConfigParams>(@params).Config ?? [],
                });
            case "completions":
                return Json.SerializeToElement(new CompletionsResult { Items = [] });
            case "sessionConfigCompletions":
                return Json.SerializeToElement(new SessionConfigCompletionsResult { Items = [] });
            case "fetchTurns":
                return Json.SerializeToElement(new FetchTurnsResult());
            case "authenticate":
                return Empty;
            case "createResourceWatch":
            case var resource when resource.StartsWith("resource", StringComparison.Ordinal):
                throw new AhpError(AhpErrorCodes.PermissionDenied, "Reading and writing files through this host comes in a later version");
            default:
                throw new AhpError(JsonRpcErrorCodes.MethodNotFound, $"Method not found: {method}");
        }
    }

    void Notified(Connection c, JsonRpcNotification notification)
    {
        try
        {
            switch (notification.Method)
            {
                case "dispatchAction" when c.Initialized:
                    Dispatch(c, Params<DispatchActionParams>(notification.Params));
                    break;
                case "unsubscribe" when c.Initialized:
                    lock (gate) c.Subscriptions.Remove(Params<UnsubscribeParams>(notification.Params).Channel);
                    break;
                // setClientManagedSettingsPermissions (VS Code's, not AHP) and anything else: nothing to do.
            }
        }
        catch (AhpError e)
        {
            ServiceLog.Warn($"ahp: {notification.Method}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- commands

    async Task<JsonElement> InitializeAsync(Connection c, InitializeParams p)
    {
        if (!p.ProtocolVersions.Contains(AhpNames.Version))
            throw new AhpError(AhpErrorCodes.UnsupportedProtocolVersion, $"This host speaks AHP {AhpNames.Version} only",
                Json.SerializeToElement(new UnsupportedProtocolVersionErrorData { SupportedVersions = [AhpNames.Version] }));
        await ready.Task;
        var wanted = p.InitialSubscriptions ?? [];
        await LoadAsync(wanted);
        lock (gate)
        {
            c.ClientId = p.ClientId;
            c.Initialized = true;
            knownClients.Add(p.ClientId);
            var snapshots = new List<Snapshot>();
            foreach (var channel in wanted)
                if (SnapshotOf(channel) is { } snapshot)
                {
                    c.Subscriptions.Add(channel);
                    snapshots.Add(snapshot);
                }
            return Json.SerializeToElement(new InitializeResult
            {
                ProtocolVersion = AhpNames.Version, ServerSeq = serverSeq,
                ServerInfo = new Implementation { Name = "gorynych", Title = "Gorynych", Version = AgentHost.Version },
                Meta = new Dictionary<string, JsonElement> { [GorynychProtocol.MetaServiceId] = AhpNames.Element(JsonValue.Create(serviceId)) },
                Snapshots = snapshots,
                DefaultDirectory = AhpNames.FileUri(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            });
        }
    }

    /// <summary>Opens the sessions of the chats among <paramref name="channels"/> that are not open yet.</summary>
    async Task LoadAsync(IEnumerable<string> channels)
    {
        List<FaceChat> load;
        lock (gate) load = [.. channels.Select(ch => chats.GetValueOrDefault(ch)).OfType<FaceChat>().Where(ch => ch.Model is null)];
        foreach (var chat in load)
            try { await EnsureLoadedAsync(chat); }
            catch (AhpError e) { ServiceLog.Warn($"ahp: {e.Message}"); }
    }

    async Task<JsonElement> ReconnectAsync(Connection c, ReconnectParams p)
    {
        await ready.Task;
        lock (gate)
            if (!knownClients.Contains(p.ClientId))
                throw new AhpError(AhpErrorCodes.NotFound, $"Unknown client {p.ClientId}: this host does not know it (it restarted); initialize afresh");
        await LoadAsync(p.Subscriptions);
        lock (gate)
        {
            c.ClientId = p.ClientId;
            c.Initialized = true;
            var present = p.Subscriptions.Where(Exists).ToList();
            var missing = p.Subscriptions.Where(ch => !Exists(ch)).ToList();
            c.Subscriptions.Clear();
            foreach (var channel in present) c.Subscriptions.Add(channel);
            // A replay only when the log has everything after what the client saw, and no channel's state was made afresh since.
            var replay = p.LastSeenServerSeq <= serverSeq
                && (log.First is null || log.First.Value.Seq <= p.LastSeenServerSeq + 1)
                && present.All(ch => BaseSeqOf(ch) is { } since && since <= p.LastSeenServerSeq);
            ReconnectResult result = replay
                ? new ReconnectResult(new ReconnectReplayResult
                {
                    Type = ReconnectResultType.Replay,
                    Actions = [.. log.Where(e => e.Seq > p.LastSeenServerSeq && c.Subscriptions.Contains(e.Channel)).Select(e => Json.Deserialize<ActionEnvelope>(e.Envelope)!)],
                    Missing = missing,
                })
                : new ReconnectResult(new ReconnectSnapshotResult
                {
                    Type = ReconnectResultType.Snapshot,
                    Snapshots = [.. present.Select(SnapshotOf).OfType<Snapshot>()],
                });
            return Json.SerializeToElement(result);
        }
    }

    /// <summary>Under the gate: whether a channel exists for a client; a chat whose session the face does not have open yet does.</summary>
    bool Exists(string channel) => channel == AhpNames.Root || sessions.ContainsKey(channel) || chats.ContainsKey(channel);

    /// <summary>Under the gate: the number since which the channel's state has been as it is (built afresh then); null when it has none now.</summary>
    long? BaseSeqOf(string channel) =>
        channel == AhpNames.Root ? 0
        : sessions.TryGetValue(channel, out var s) ? s.BaseSeq
        : chats.TryGetValue(channel, out var chat) && chat.Model is not null ? chat.BaseSeq
        : null;

    /// <summary>Under the gate: a channel's state now; null for one with none (a chat whose session is not open).</summary>
    Snapshot? SnapshotOf(string channel)
    {
        SnapshotState state;
        if (channel == AhpNames.Root) state = new SnapshotState { Root = root };
        else if (sessions.TryGetValue(channel, out var s)) state = new SnapshotState { Session = s.State };
        else if (chats.TryGetValue(channel, out var chat) && chat.Model is { } model) state = new SnapshotState { Chat = model.State };
        else return null;
        return new Snapshot { Resource = channel, State = state, FromSeq = serverSeq };
    }

    JsonElement ListSessions(ListSessionsParams p)
    {
        lock (gate)
        {
            var all = sessions.Values.Select(s => s.Sent ?? Summary(s))
                .OrderByDescending(s => s.ModifiedAt, StringComparer.Ordinal).ThenBy(s => s.Resource, StringComparer.Ordinal).ToList();
            var start = 0;
            if (p.Cursor is { Length: > 0 } cursor)
            {
                var (modified, resource) = Cursor(cursor);
                start = all.FindIndex(s => string.CompareOrdinal(s.ModifiedAt, modified) < 0
                    || (s.ModifiedAt == modified && string.CompareOrdinal(s.Resource, resource) > 0));
                if (start < 0) start = all.Count;
            }
            var limit = p.Limit is > 0 and var l ? (int)Math.Min(l, 200) : int.MaxValue;
            var page = all.Skip(start).Take(limit).ToList();
            var more = start + page.Count < all.Count;
            return Json.SerializeToElement(new ListSessionsResult
            {
                Items = page,
                NextCursor = more ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{page[^1].ModifiedAt}\n{page[^1].Resource}")) : null,
            });
        }
    }

    static (string Modified, string Resource) Cursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\n', 2);
            if (parts.Length == 2) return (parts[0], parts[1]);
        }
        catch (FormatException) { }
        throw new AhpError(JsonRpcErrorCodes.InvalidParams, "cursor: not one this host gave");
    }

    async Task<JsonElement> SubscribeAsync(Connection c, string channel)
    {
        FaceChat? chat;
        lock (gate)
        {
            if (!chats.TryGetValue(channel, out chat))
            {
                if (SnapshotOf(channel) is not { } snapshot)
                    throw new AhpError(AhpErrorCodes.SessionNotFound, $"No channel {channel} on this host");
                c.Subscriptions.Add(channel);
                return Json.SerializeToElement(new SubscribeResult { Snapshot = snapshot });
            }
        }
        await EnsureLoadedAsync(chat);
        lock (gate)
        {
            if (SnapshotOf(channel) is not { } snapshot) throw new AhpError(AhpErrorCodes.SessionNotFound, $"No channel {channel} on this host");
            c.Subscriptions.Add(channel);
            chat.Unwatched = null;
            return Json.SerializeToElement(new SubscribeResult { Snapshot = snapshot });
        }
    }

    // ---------------------------------------------------------------- client actions

    /// <summary>
    /// A client's action: applied and echoed with its origin when the channel takes it, else echoed with why not. An
    /// action for a channel that does not exist is dropped, as AHP says.
    /// </summary>
    void Dispatch(Connection c, DispatchActionParams p)
    {
        var origin = new ActionOrigin { ClientId = c.ClientId ?? "", ClientSeq = p.ClientSeq };
        var action = p.Action.Value;
        lock (gate)
        {
            if (!Exists(p.Channel)) return;
            if (!Reducers.IsClientDispatchable(p.Action))
            {
                Broadcast(p.Channel, p.Action, origin, "A client may not dispatch this action", c);
                return;
            }
            if (p.Channel == AhpNames.Root)
            {
                if (action is RootConfigChangedAction)
                {
                    Broadcast(p.Channel, p.Action, origin);
                    Reducers.ApplyToRoot(root, p.Action);
                }
                else Broadcast(p.Channel, p.Action, origin, "This host takes no such change", c);
                return;
            }
            if (sessions.TryGetValue(p.Channel, out var s))
            {
                switch (action)
                {
                    case SessionIsArchivedChangedAction archive:
                        ArchiveAsync(c, p.Channel, p.Action, origin, s.SessionId, archive.IsArchived);
                        return;
                    case SessionIsReadChangedAction read:
                        ApplySession(s, read, origin);
                        s.IsRead = read.IsRead;
                        Sync(s);
                        return;
                    case SessionActiveClientSetAction or SessionActiveClientRemovedAction:
                        ApplySession(s, action, origin);
                        return;
                    case SessionTitleChangedAction:
                        Broadcast(p.Channel, p.Action, origin, "Renaming is not supported yet", c);
                        return;
                    default:
                        Broadcast(p.Channel, p.Action, origin, "This session takes no such change in this version", c);
                        return;
                }
            }
            var chat = chats[p.Channel];
            switch (action)
            {
                case ChatIsReadChangedAction read when chat.Model is not null:
                    ApplyChat(chat, read, origin);
                    chat.IsRead = read.IsRead;
                    Sync(chat.Session);
                    return;
                case ChatDraftChangedAction when chat.Model is not null:
                    ApplyChat(chat, action, origin);
                    return;
                case ChatIsArchivedChangedAction archive when chat.IsChild && chat.Model is not null:
                    ArchiveAsync(c, p.Channel, p.Action, origin, chat.SessionId, archive.IsArchived);
                    return;
                default:
                    Broadcast(p.Channel, p.Action, origin, "This chat is read-only in this version", c);
                    return;
            }
        }
    }

    /// <summary>An archive from a client: <c>_gorynych/archive</c>, then echoed when it succeeded, refused with its error when not.</summary>
    void ArchiveAsync(Connection c, string channel, StateAction action, ActionOrigin origin, string sessionId, bool archived) => _ = Task.Run(async () =>
    {
        string? failed = null;
        try
        {
            await acp!.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject { ["sessionId"] = sessionId, ["archived"] = archived }, stopping.Token);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException)
        {
            failed = e.Message;
        }
        lock (gate)
        {
            if (failed is not null)
            {
                if (Exists(channel)) Broadcast(channel, action, origin, failed, c);
                return;
            }
            if (sessions.TryGetValue(channel, out var s))
            {
                s.Archived = archived;
                ApplySession(s, action.Value!, origin);
                Sync(s);
            }
            else if (chats.TryGetValue(channel, out var chat) && chat.Model is not null)
            {
                chat.Archived = archived;
                ApplyChat(chat, action.Value!, origin);
                Sync(chat.Session);
            }
        }
    });

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        List<Connection> all;
        lock (gate) all = [.. connections];
        foreach (var c in all)
        {
            c.Complete();
            try { await c.Transport.CloseAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
        if (acp is not null) await acp.DisposeAsync();
        if (serving is not null) try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}
