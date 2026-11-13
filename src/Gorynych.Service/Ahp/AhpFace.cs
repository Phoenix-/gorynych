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

    /// <summary>How long a <c>listSessions</c> or a subscription to a channel not known yet waits for the first list.</summary>
    public TimeSpan FirstListWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the chunks of a live user message are held for the rest of the message.</summary>
    public TimeSpan HoldFor { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a turn no mark will end (<see cref="ChatModel.Unmarked"/>) stays open after the session's last update.</summary>
    public TimeSpan QuietFor { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many action envelopes the replay log keeps, and as many root notifications.</summary>
    public int LogSize { get; init; } = 10_000;

    /// <summary>A client that takes nothing for this long is disconnected.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// The AHP face (docs/ahp-face.md): toward AHP clients (VS Code's Agents window) a host that keeps AHP's state
/// tree, numbers actions, serves snapshots and replays; toward the conductor one more ACP client, on an in-process
/// connection. Steps 1 and 2: reading, and working in sessions. All state is reduced under one lock, with the
/// package's reducers, and every message a client gets is queued under it, a reply that carries state too, so that
/// nothing a client gets is older than what it got before; each client has its own queue to send from.
/// </summary>
internal sealed partial class AhpFace : IAsyncDisposable
{
    static readonly SystemTextJsonAhpSerializer Json = SystemTextJsonAhpSerializer.Default;
    static readonly JsonElement Empty = AhpNames.Element(new JsonObject());

    readonly Conductor conductor;
    readonly AhpFaceOptions options;
    readonly Lock gate = new();
    readonly CancellationTokenSource stopping = new();
    readonly TaskCompletionSource agentsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource listed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // under gate
    long serverSeq;
    RootState root = new() { Agents = [] };
    readonly Dictionary<string, string> agentNames = new(StringComparer.Ordinal); // agent -> its display name
    readonly Dictionary<string, FaceSession> sessions = new(StringComparer.Ordinal); // ahp-session URI -> session
    readonly Dictionary<string, FaceChat> chats = new(StringComparer.Ordinal); // ahp-chat URI -> chat
    readonly Dictionary<string, FaceSession> sessionsById = new(StringComparer.Ordinal); // Gorynych session ID -> root session
    readonly Dictionary<string, FaceChat> chatsById = new(StringComparer.Ordinal); // Gorynych session ID -> the chat that shows it
    readonly LinkedList<(long Seq, string Channel, JsonElement Envelope)> log = new();
    readonly LinkedList<(long Seq, TransportMessage Message)> rootLog = new(); // root notifications, stamped with the serverSeq they came after
    long rootTrimmed = -1; // the newest stamp among the root notifications no longer kept
    readonly Dictionary<string, ChatModel> closing = new(StringComparer.Ordinal); // sessions let go of under a turn: closed when it ends
    readonly Dictionary<string, Task> sessionWork = new(StringComparer.Ordinal); // the last load or close of each session in the conductor
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

    /// <summary>
    /// Connects to the conductor as an ACP client and reads the agents: from then on it answers <c>initialize</c>.
    /// The list follows, as slow as the slowest agent's: a session it brings is said to the clients as it comes.
    /// </summary>
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
            acp.RequestHandler = OnAcpRequestAsync;
            acp.Start();
            var init = await acp.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.Initialize, new JsonObject
            {
                ["protocolVersion"] = (int)Gorynych.Acp.AcpMethods.ProtocolVersion,
                ["clientInfo"] = new JsonObject { ["name"] = "gorynych-ahp", ["title"] = "Gorynych's AHP face", ["version"] = AgentHost.Version },
                // Approvals and forms, shown as AHP's tool confirmations and input requests.
                ["clientCapabilities"] = new JsonObject { ["elicitation"] = new JsonObject { ["form"] = new JsonObject() } },
                // On the link, so its prompts' ends are marked.
                ["_meta"] = new JsonObject { [GorynychProtocol.CapLink] = GorynychProtocol.LinkVersion },
            }, stopping.Token);
            serviceId = AhpNames.Text(init?["_meta"]?[GorynychProtocol.MetaServiceId]) ?? "";
            var agents = (init?["_meta"]?[GorynychProtocol.MetaAgents] as JsonArray ?? []).OfType<JsonObject>()
                .Select(a => (Name: AhpNames.Text(a["name"]) ?? "", Display: AhpNames.Text(a["displayName"]) ?? AhpNames.Text(a["name"]) ?? ""))
                .Where(a => a.Name.Length > 0).ToList();
            lock (gate)
            {
                foreach (var (name, display) in agents) agentNames[name] = display;
                root = new RootState
                {
                    Agents = [.. agents.Select(a => new AgentInfo
                    {
                        Provider = a.Name, DisplayName = a.Display, Description = $"{a.Display} through Gorynych", Models = [],
                        // Without it VS Code shows a session's chats that came from a tool or a side chat only, so not its children (docs/ahp-face.md).
                        Capabilities = new AgentCapabilities { MultipleChats = new MultipleChatsCapability() },
                    })],
                    Config = new RootConfigState { Schema = new ConfigSchema { Type = "object", Properties = [] }, Values = [] },
                    Terminals = [],
                };
            }
            agentsReady.TrySetResult();
            _ = Task.Run(SweepAsync);
            ListChanged(now: true);
            ServiceLog.Info($"ahp: the face serves {agents.Count} agents and reads their sessions");
        }
        catch (Exception e)
        {
            Error = $"the AHP face did not start: {e.Message}";
            ServiceLog.Warn($"ahp: {Error}");
            agentsReady.TrySetException(e);
            listed.TrySetResult();
        }
    }

    sealed record Listed(string Id, string Agent, string Title, string Cwd, string UpdatedAt, bool Archived, string? Parent,
        string? AhpUri = null, bool ReadOnly = false, string? Reason = null);

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
                    AhpNames.Text(info["cwd"]) ?? "",
                    // No time said is the oldest, never now: an old session must not look new.
                    AhpNames.Time(AhpNames.Text(info["updatedAt"])) ?? AhpNames.Time(DateTimeOffset.UnixEpoch),
                    meta?[GorynychProtocol.MetaArchived] is JsonValue a && a.TryGetValue<bool>(out var archived) && archived,
                    AhpNames.Text(meta?[GorynychProtocol.MetaParentSessionId]),
                    AhpNames.Text(meta?[GorynychProtocol.MetaAhpUri]) is { } uri && AhpNames.IsSessionUri(uri) ? uri : null,
                    AhpNames.Text(meta?[GorynychProtocol.MetaInteractivity]) == GorynychProtocol.InteractivityReadOnly,
                    AhpNames.Text(meta?[GorynychProtocol.MetaInteractivityReason])));
            }
            cursor = AhpNames.Text(answer?["nextCursor"]);
            if (cursor is null) break;
        }
        return listed;
    }

    /// <summary>The list changed: it is read again, at most every <see cref="AhpFaceOptions.ListEvery"/>; <paramref name="now"/> for the first.</summary>
    void ListChanged(bool now = false)
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
        _ = Task.Run(() => ListLoopAsync(now));
    }

    async Task ListLoopAsync(bool now)
    {
        var done = false;
        try
        {
            while (true)
            {
                if (!now) await Task.Delay(options.ListEvery, stopping.Token);
                now = false;
                lock (gate) listAgain = false;
                try
                {
                    var list = await ReadListAsync(stopping.Token);
                    lock (gate) ApplyList(list);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    ServiceLog.Warn($"ahp: the list could not be read: {e.Message}");
                }
                finally
                {
                    listed.TrySetResult(); // the first list is read, or will not be: what waits for it goes on
                }
                lock (gate)
                    if (!listAgain)
                    {
                        listing = false;
                        done = true;
                        return;
                    }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            // However it ended, a later change reads the list again.
            if (!done) lock (gate) listing = false;
        }
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
            if (closing.TryGetValue(sessionId, out var leaving))
            {
                // A session the face let go of under its turn: closed once that turn is over.
                leaving.Take(update, live: true);
                leaving.Flush(live: true);
                if (!Running(leaving))
                {
                    closing.Remove(sessionId);
                    Close(sessionId);
                }
            }
            if (!chatsById.TryGetValue(sessionId, out var chat)) return Task.CompletedTask;
            switch (AhpNames.Text(update["sessionUpdate"]))
            {
                case "session_info_update":
                    // Title, archive and interactivity go to the catalog whether or not the chat is open; the list says them too.
                    if (AhpNames.Text(update["title"]) is { Length: > 0 } title) SetTitle(chat, title);
                    if (update["_meta"]?[GorynychProtocol.MetaArchived] is JsonValue v && v.TryGetValue<bool>(out var archived)) SetArchived(chat, archived);
                    if (AhpNames.Text(update["_meta"]?[GorynychProtocol.MetaInteractivity]) is { } interactivity)
                        SetReadOnly(chat, interactivity == GorynychProtocol.InteractivityReadOnly, AhpNames.Text(update["_meta"]?[GorynychProtocol.MetaInteractivityReason]));
                    break;
                // The session's state, not its history: kept by the face, for its config and its commands.
                case "config_option_update" when !chat.IsChild:
                    OptionsChanged(chat.Session, update["configOptions"] as JsonArray, chat.Session.AcpModes);
                    return Task.CompletedTask;
                case "current_mode_update" when !chat.IsChild:
                    ModeChanged(chat.Session, AhpNames.Text(update["currentModeId"]));
                    return Task.CompletedTask;
                case "available_commands_update":
                    chat.Commands = [.. (update["availableCommands"] as JsonArray ?? []).OfType<JsonObject>()
                        .Where(c => AhpNames.Text(c["name"]) is { Length: > 0 })
                        .Select(c => (AhpNames.Text(c["name"])!, AhpNames.Text(c["description"])))];
                    return Task.CompletedTask;
            }
            if (chat.Buffer is { } buffer)
            {
                buffer.Add((JsonObject)update.DeepClone());
                return Task.CompletedTask;
            }
            if (chat.Model is not { } model) return Task.CompletedTask;
            chat.Updates++;
            model.Take(update, live: true);
            if (model.Holding) ScheduleFlush(chat);
            if (model.Unmarked || model.Settling) ScheduleQuiet(chat);
            ConsumeQueued(chat);
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
            if (model.Unmarked) ScheduleQuiet(chat);
            ConsumeQueued(chat);
            Sync(chat.Session);
        }
    }, TaskScheduler.Default);

    /// <summary>
    /// A turn no mark will end, or a cancelled turn whose end mark has not come: over once
    /// <see cref="AhpFaceOptions.QuietFor"/> passes with no update of its session, unless it waits for an answer here.
    /// </summary>
    void ScheduleQuiet(FaceChat chat)
    {
        var seen = chat.Updates;
        _ = Task.Delay(options.QuietFor, stopping.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (gate)
            {
                if (chat.Updates != seen || chat.Model is not { } model || !(model.Unmarked || model.Settling)) return;
                if (requests.Values.Any(r => r.Chat == chat))
                {
                    ScheduleQuiet(chat); // the agent waits for an answer here: its turn is not over
                    return;
                }
                model.Settled(live: true);
                ConsumeQueued(chat);
                Sync(chat.Session);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>A turn runs in the session, one a close would cancel: not one only quiet will end.</summary>
    static bool Running(ChatModel model) => model.Busy && !model.Unmarked;

    // ---------------------------------------------------------------- sessions and chats

    sealed class FaceSession(string uri, string sessionId)
    {
        public string Uri { get; } = uri;
        public string SessionId { get; set; } = sessionId; // "" while a session an AHP client created is being made
        public bool Draft; // created through the face and not prompted yet: no list has it
        public Task Created = Task.CompletedTask; // its session/new, for one created through the face
        public JsonArray? AcpOptions; // its agent's config options, as it last said them
        public JsonObject? AcpModes; // its agent's modes, as it last said them
        public bool ModesAsMode; // its modes are the config property "mode"
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
        public string SessionId { get; set; } = sessionId;
        public FaceSession Session { get; set; } = session;
        public bool IsChild => SessionId != Session.SessionId;
        public string Agent = "";
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
        public long Updates; // live updates taken, for the quiet check
        public bool ReadOnly; // gorynych/interactivity says so
        public string? ReadOnlyReason;
        public List<(string Name, string? Description)> Commands = []; // its agent's commands, for completions
    }

    /// <summary>Takes the conductor's list: sessions and children that came, went or changed, said to the clients.</summary>
    void ApplyList(List<Listed> list)
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

        // Sessions that went, or became children; not one created here that no list has yet (a draft until its first prompt).
        foreach (var s in sessions.Values.ToList())
            if (s.SessionId.Length > 0 && !s.Draft && (!byId.ContainsKey(s.SessionId) || RootOf(s.SessionId) != s.SessionId))
                RemoveSession(s);
        var added = new List<FaceSession>();
        foreach (var l in byId.Values.Where(l => RootOf(l.Id) == l.Id))
        {
            if (sessionsById.TryGetValue(l.Id, out var s))
            {
                s.Draft = false;
                UpdateSession(s, l);
            }
            else if (!sessions.ContainsKey(l.AhpUri ?? AhpNames.SessionUri(l.Id))) added.Add(AddSession(l));
        }
        // Children that went, or moved to another root.
        foreach (var chat in chats.Values.Where(c => c.IsChild).ToList())
            if (!byId.ContainsKey(chat.SessionId) || !sessionsById.TryGetValue(RootOf(chat.SessionId), out var root) || root != chat.Session)
                RemoveChild(chat, quiet: added.Contains(chat.Session));
        foreach (var l in byId.Values.Where(l => RootOf(l.Id) != l.Id))
        {
            if (!sessionsById.TryGetValue(RootOf(l.Id), out var s)) continue;
            if (chatsById.TryGetValue(l.Id, out var chat)) UpdateChat(chat, l);
            else AddChild(s, l, quiet: added.Contains(s));
        }
        foreach (var s in added)
        {
            s.Sent = Summary(s);
            NotifyRoot("root/sessionAdded", Json.SerializeToElement(new SessionAddedParams { Channel = AhpNames.Root, Summary = s.Sent }));
        }
        foreach (var s in sessions.Values) Sync(s);
    }

    /// <summary>
    /// A session's title, never empty: an untitled one (DSH lists none, a Kimi session before its first prompt)
    /// is named for its agent and ID, the same each time, since VS Code shows an empty title as a blank row.
    /// </summary>
    string TitleOf(Listed l) => l.Title.Length > 0 ? l.Title : Untitled(l.Agent, l.Id);

    string Untitled(string agent, string id)
    {
        var name = agentNames.GetValueOrDefault(agent) is { Length: > 0 } display ? display : agent;
        return name.Length > 0 ? $"{name} session {ShortId(id)}" : $"Session {ShortId(id)}";
    }

    /// <summary>The first eight characters of a session ID, after a word that only names its kind (Kimi's <c>session_</c>).</summary>
    internal static string ShortId(string id)
    {
        var cut = id.IndexOf('_');
        var rest = cut > 0 && cut + 1 < id.Length && id[..cut].All(char.IsAsciiLetter) ? id[(cut + 1)..] : id;
        return rest.Length > 8 ? rest[..8] : rest;
    }

    FaceSession AddSession(Listed l)
    {
        var title = TitleOf(l);
        // A session an AHP client created goes by the URI it chose.
        var s = new FaceSession(l.AhpUri ?? AhpNames.SessionUri(l.Id), l.Id)
        {
            Agent = l.Agent, Title = title, Cwd = l.Cwd, UpdatedAt = l.UpdatedAt, CreatedAt = l.UpdatedAt, Archived = l.Archived, BaseSeq = serverSeq,
        };
        var own = new FaceChat(AhpNames.ChatOf(s.Uri), l.Id, s)
        {
            Agent = l.Agent, Title = title, Cwd = l.Cwd, UpdatedAt = l.UpdatedAt, ReadOnly = l.ReadOnly, ReadOnlyReason = l.Reason,
        };
        s.Chats.Add(own);
        chats[own.Uri] = own;
        chatsById[l.Id] = own;
        sessionsById[l.Id] = s;
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
        if (TitleOf(l) is var title && title != s.Title) SetTitle(s.Chats[0], title);
        if (l.Archived != s.Archived) SetArchived(s.Chats[0], l.Archived);
        // The list says a read-only session so, and nothing of one that is not: an open one learns a change from its updates.
        if (l.ReadOnly) SetReadOnly(s.Chats[0], true, l.Reason);
        else if (s.Chats[0].Model is null) SetReadOnly(s.Chats[0], false, null);
    }

    /// <param name="deleted">Deleted in the conductor already: nothing to close there.</param>
    void RemoveSession(FaceSession s, bool deleted = false)
    {
        sessions.Remove(s.Uri);
        if (sessionsById.TryGetValue(s.SessionId, out var known) && known == s) sessionsById.Remove(s.SessionId);
        foreach (var chat in s.Chats) Drop(chat, close: !deleted);
        NotifyRoot("root/sessionRemoved", Json.SerializeToElement(new SessionRemovedParams { Channel = AhpNames.Root, Session = s.Uri }));
    }

    void AddChild(FaceSession s, Listed l, bool quiet)
    {
        var chat = new FaceChat(AhpNames.ChatUri(l.Id), l.Id, s) { Agent = l.Agent, Title = TitleOf(l), Cwd = l.Cwd, UpdatedAt = l.UpdatedAt, Archived = l.Archived };
        chats[chat.Uri] = chat;
        chatsById[l.Id] = chat;
        s.Chats.Add(chat);
        if (quiet) s.State.Chats.Add(ChatSummaryOf(chat));
        else ApplySession(s, new SessionChatAddedAction { Summary = ChatSummaryOf(chat) });
    }

    void UpdateChat(FaceChat chat, Listed l)
    {
        chat.UpdatedAt = l.UpdatedAt;
        chat.Cwd = l.Cwd;
        if (TitleOf(l) is var title && title != chat.Title) SetTitle(chat, title);
        if (l.Archived != chat.Archived) SetArchived(chat, l.Archived);
    }

    void RemoveChild(FaceChat chat, bool quiet)
    {
        chat.Session.Chats.Remove(chat);
        if (quiet) chat.Session.State.Chats.RemoveAll(c => c.Resource == chat.Uri);
        else ApplySession(chat.Session, new SessionChatRemovedAction { Chat = chat.Uri });
        Drop(chat);
    }

    /// <summary>A chat that went: forgotten, and its session let go of in the conductor if the face had it open.</summary>
    void Drop(FaceChat chat, bool close = true)
    {
        if (chats.TryGetValue(chat.Uri, out var now) && ReferenceEquals(now, chat)) chats.Remove(chat.Uri);
        if (chatsById.TryGetValue(chat.SessionId, out var byId) && ReferenceEquals(byId, chat)) chatsById.Remove(chat.SessionId);
        // What the agent asked of it here can no longer be shown: it is answered as cancelled, as by a client that goes.
        foreach (var ask in requests.Values.Where(r => r.Chat == chat).ToList()) Resolve(ask, ask.Cancelled());
        if (chat.Model is not { } model) return;
        chat.Model = null;
        if (close && chat.SessionId.Length > 0) LetGo(chat.SessionId, model);
    }

    /// <summary>
    /// The face is done with a session it had open: closed in the conductor, but never under a turn that runs,
    /// since a close from the session's last client cancels its turn. Such a session is closed when the turn ends.
    /// </summary>
    void LetGo(string sessionId, ChatModel model)
    {
        if (Running(model))
        {
            model.Emit = null; // its chat is gone: nobody sees it
            closing[sessionId] = model;
            return;
        }
        Close(sessionId);
    }

    void SetTitle(FaceChat chat, string title)
    {
        chat.Title = title;
        if (chat.IsChild || chat.Session.Title == title) return;
        chat.Session.Title = title;
        ApplySession(chat.Session, new SessionTitleChangedAction { Title = title });
    }

    /// <summary>
    /// Whether a client may prompt the chat (<c>gorynych/interactivity</c>). AHP 1.0.0 has no chat action for it: the
    /// catalog says it (<c>session/chatUpdated</c>), and the chat's state for later snapshots, with the reason in its
    /// <c>_meta</c>, since a catalog entry has none.
    /// </summary>
    void SetReadOnly(FaceChat chat, bool readOnly, string? reason)
    {
        if (chat.ReadOnly == readOnly && chat.ReadOnlyReason == reason) return;
        chat.ReadOnly = readOnly;
        chat.ReadOnlyReason = readOnly ? reason : null;
        if (chat.Model is { } model) Interactive(chat, model.State);
        Sync(chat.Session);
    }

    static void Interactive(FaceChat chat, ChatState state)
    {
        state.Interactivity = InteractivityOf(chat);
        state.Meta?.Remove(GorynychProtocol.MetaInteractivityReason);
        if (chat.ReadOnlyReason is { } why)
        {
            state.Meta ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            state.Meta[GorynychProtocol.MetaInteractivityReason] = AhpNames.Element(JsonValue.Create(why));
        }
    }

    /// <summary>A chat a client may prompt (<c>full</c>), or watch only: a child until step 4, and a session its agent opened for reading only.</summary>
    static ChatInteractivity InteractivityOf(FaceChat chat) => chat.IsChild || chat.ReadOnly ? ChatInteractivity.ReadOnly : ChatInteractivity.Full;

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

    SessionStatus StatusOf(FaceChat chat) => chat.Model is { } model
        ? model.State.Status
        : SessionStatus.Idle | (chat.IsRead ? SessionStatus.IsRead : 0) | (chat.IsChild && chat.Archived ? SessionStatus.IsArchived : 0);

    string ModifiedOf(FaceChat chat) => chat.Model?.State.ModifiedAt ?? chat.UpdatedAt;

    ChatSummary ChatSummaryOf(FaceChat chat) => new()
    {
        Resource = chat.Uri, Title = chat.Title, Status = StatusOf(chat), Activity = chat.Model?.State.Activity, ModifiedAt = ModifiedOf(chat),
        Origin = UserOrigin(), Interactivity = InteractivityOf(chat),
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
            Resource = c.Uri, Title = c.Title, Origin = UserOrigin(), Interactivity = InteractivityOf(c), Status = StatusOf(c),
        })],
        DefaultChat = s.Chats[0].Uri,
        Meta = MetaOf(s),
    };

    /// <summary>
    /// Keeps the session's chat catalog in step with its chats (<c>session/chatUpdated</c>), and its summary on the
    /// root channel (<c>root/sessionSummaryChanged</c>, the changed fields only), as AHP requires of a host.
    /// </summary>
    void Sync(FaceSession s)
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
            if (had.Interactivity != now.Interactivity) { changes = changes with { Interactivity = now.Interactivity }; changed = true; }
            if (changed) ApplySession(s, new SessionChatUpdatedAction { Chat = chat.Uri, Changes = changes });
        }
        var summary = Summary(s);
        if (s.Sent is not { } sent)
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
        if (action.Value is ChatTurnStartedAction) TurnBegan(chat);
    }

    /// <summary>
    /// A root notification: sent to the root's subscribers, and kept, stamped with the number of the last action
    /// before it, so that a reconnect's replay sends again those a client may have missed (they carry no number).
    /// </summary>
    void NotifyRoot(string method, JsonElement @params)
    {
        var message = Notification(method, @params);
        rootLog.AddLast((serverSeq, message));
        while (rootLog.Count > options.LogSize)
        {
            rootTrimmed = Math.Max(rootTrimmed, rootLog.First!.Value.Seq);
            rootLog.RemoveFirst();
        }
        foreach (var c in connections)
            if (c.Initialized && c.Subscriptions.Contains(AhpNames.Root)) c.Send(message);
    }

    // ---------------------------------------------------------------- loading and closing

    /// <summary>
    /// Runs a load or close of the session in the conductor after the face's earlier ones: they reach it in the
    /// order the face decided them, so a quick reload never comes before the close it follows.
    /// </summary>
    Task InTurn(string sessionId, Func<Task> work)
    {
        lock (gate)
        {
            var before = sessionWork.GetValueOrDefault(sessionId) ?? Task.CompletedTask;
            var task = before.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            sessionWork[sessionId] = task;
            _ = task.ContinueWith(_ =>
            {
                lock (gate)
                    if (sessionWork.TryGetValue(sessionId, out var last) && last == task) sessionWork.Remove(sessionId);
            }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Opens the chat's session in the conductor and builds its state from the replay, once.</summary>
    async Task EnsureLoadedAsync(FaceChat chat)
    {
        Task load;
        lock (gate)
        {
            if (chat.Model is not null) return;
            if (chat.Loading is null)
            {
                // A session the face let go of under a turn is its own again: no close for it.
                closing.Remove(chat.SessionId);
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
        JsonNode? answer = null;
        try
        {
            await InTurn(chat.SessionId, async () => answer = await acp!.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.SessionLoad,
                new JsonObject { ["sessionId"] = chat.SessionId, ["cwd"] = chat.Cwd, ["mcpServers"] = new JsonArray() }, stopping.Token));
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
            chat.Buffer = null;
            chat.Loading = null;
            if (!chats.TryGetValue(chat.Uri, out var now) || !ReferenceEquals(now, chat))
            {
                // It went while it loaded (and may have come back as another chat of the same session).
                LetGo(chat.SessionId, model);
                return;
            }
            model.Emit = action => EmitChat(chat, action);
            chat.Model = model;
            chat.BaseSeq = serverSeq;
            chat.Unwatched = DateTimeOffset.UtcNow;
            // The session's state: its agent's options, and whether a client may prompt it.
            if (!chat.IsChild) OptionsChanged(chat.Session, answer?["configOptions"]?.DeepClone() as JsonArray, answer?["modes"]?.DeepClone() as JsonObject);
            if (AhpNames.Text(answer?["_meta"]?[GorynychProtocol.MetaInteractivity]) is { } interactivity)
                SetReadOnly(chat, interactivity == GorynychProtocol.InteractivityReadOnly, AhpNames.Text(answer?["_meta"]?[GorynychProtocol.MetaInteractivityReason]));
            // What the agent asked while the session opened (an approval waiting for a client comes right after the replay).
            foreach (var ask in requests.Values.Where(r => r.Chat is null && r.SessionId == chat.SessionId).ToList()) Show(ask);
            Sync(chat.Session);
        }
    }

    static ChatState NewChatState(FaceChat chat)
    {
        var state = new ChatState
        {
            Resource = chat.Uri, Title = chat.Title,
            Status = SessionStatus.Idle | (chat.IsRead ? SessionStatus.IsRead : 0) | (chat.IsChild && chat.Archived ? SessionStatus.IsArchived : 0),
            ModifiedAt = chat.UpdatedAt, Origin = UserOrigin(), Turns = [],
            WorkingDirectories = AhpNames.FileUri(chat.Cwd) is { } folder ? [folder] : null,
        };
        Interactive(chat, state);
        return state;
    }

    void Close(string sessionId) => _ = InTurn(sessionId, async () =>
    {
        try { await acp!.SendRequestAsync(Gorynych.Acp.AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = sessionId }, stopping.Token); }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException)
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
            foreach (var chat in chats.Values.ToList())
            {
                if (chat.Model is not { } model) continue;
                if (connections.Any(c => c.Subscriptions.Contains(chat.Uri) || c.Pending.ContainsKey(chat.Uri)))
                {
                    chat.Unwatched = null;
                    continue;
                }
                chat.Unwatched ??= now;
                if (now - chat.Unwatched < options.CloseAfter || Running(model)) continue;
                chat.IsRead = (model.State.Status & SessionStatus.IsRead) != 0;
                chat.Model = null;
                Close(chat.SessionId);
                ServiceLog.Info($"ahp: closed {chat.SessionId}, which no AHP client had open");
                // A session created here and never prompted is a draft: the conductor discards it on its close.
                if (chat.Session.Draft && !chat.IsChild) RemoveSession(chat.Session);
                else Sync(chat.Session);
            }
        }
    }

    /// <summary>Whether the face has the session open in the conductor now, its own chat's or one it let go of under a turn; for tests.</summary>
    internal bool HasOpen(string sessionId)
    {
        lock (gate) return (chatsById.TryGetValue(sessionId, out var chat) && chat.Model is not null) || closing.ContainsKey(sessionId);
    }

    /// <summary>Whether a turn a client cancelled in the session has not ended in its agent yet; for tests.</summary>
    internal bool Settling(string sessionId)
    {
        lock (gate) return chatsById.TryGetValue(sessionId, out var chat) && chat.Model is { Settling: true };
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
        public Task Sending = Task.CompletedTask;
        public readonly CancellationTokenSource Ended = new(); // its reading stops
        public readonly HashSet<string> Subscriptions = new(StringComparer.Ordinal); // under the face's gate
        public readonly Dictionary<string, object> Pending = new(StringComparer.Ordinal); // subscriptions asked for, waiting for their session to open; under the gate

        public void Send(TransportMessage message) => outbox.Writer.TryWrite(message);

        public void Complete() => outbox.Writer.TryComplete();

        /// <summary>Ends the connection at once: its queue, its reading, and its socket, so what waits on it fails.</summary>
        public void Abort()
        {
            Complete();
            try { Ended.Cancel(); } catch (ObjectDisposedException) { }
            if (Transport is WebSocketServerTransport socket) socket.Abort();
        }

        /// <summary>Sends what was queued, in order, until the queue is completed; false when the client stopped taking it.</summary>
        public async Task<bool> SendLoopAsync()
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
                    return false;
                }
                catch (Exception)
                {
                    return false; // gone
                }
            }
            return true;
        }
    }

    /// <summary>Serves one AHP client over <paramref name="transport"/> until it goes, or the face stops.</summary>
    public async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        var c = new Connection(transport, options.SendTimeout);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token, c.Ended.Token);
        lock (gate)
        {
            if (stopping.IsCancellationRequested) return;
            connections.Add(c);
        }
        c.Sending = Task.Run(async () =>
        {
            // A client that takes nothing more is let go of at once: forgotten, so nothing more is queued for
            // it and it watches no session, and its socket ended, so its reading here ends too.
            if (!await c.SendLoopAsync()) Drop(c);
        });
        try
        {
            while (!ended.IsCancellationRequested)
            {
                TransportMessage frame;
                try { frame = await transport.ReceiveAsync(ended.Token); }
                catch (Exception e) when (e is TransportClosedException or OperationCanceledException or IOException or ObjectDisposedException
                    or System.Net.WebSockets.WebSocketException) { break; }
                JsonRpcMessage message;
                try { message = Json.DecodeMessage(frame); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
                {
                    ServiceLog.Warn($"ahp: a message that is not JSON-RPC: {e.Message}");
                    continue;
                }
                if (message.Request is { } request)
                {
                    if (request.Method == "subscribe" && c.Initialized) Subscribe(c, request);
                    // The first list may take a while: it does not hold up what follows it.
                    else if (request.Method == "listSessions" && c.Initialized && !listed.Task.IsCompleted)
                        _ = Task.Run(() => AnswerAsync(c, request, () => ListSessionsAsync(c, request)));
                    // A delete waits for its agent: it does not hold up what follows it.
                    else if (request.Method == "disposeSession" && c.Initialized)
                        _ = Task.Run(() => AnswerAsync(c, request, () => CommandAsync(c, request)));
                    else await AnswerAsync(c, request, () => CommandAsync(c, request));
                }
                else if (message.Notification is { } notification) Notified(c, notification);
            }
        }
        finally
        {
            lock (gate) connections.Remove(c);
            c.Complete();
            try { await c.Sending.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    void Drop(Connection c)
    {
        lock (gate) connections.Remove(c);
        c.Abort();
    }

    sealed class AhpError(int code, string message, JsonElement? data = null) : Exception(message)
    {
        public int Code { get; } = code;
        public JsonElement? ErrorData { get; } = data;
    }

    /// <summary>
    /// Runs a request and answers it. A command whose answer carries state (a snapshot, the list) queues the answer
    /// itself, under the gate with that state, and gives null; the others' answers are queued here.
    /// </summary>
    async Task AnswerAsync(Connection c, AhpJsonRpcRequest request, Func<Task<JsonElement?>> run)
    {
        try
        {
            if (await run() is { } result) c.Send(Success(request, result));
        }
        catch (Exception e)
        {
            var (code, text, data) = e is AhpError a ? (a.Code, a.Message, a.ErrorData) : (JsonRpcErrorCodes.InternalError, e.Message, null);
            if (e is not AhpError) ServiceLog.Warn($"ahp: {request.Method} failed: {e}");
            c.Send(Json.EncodeMessage(new JsonRpcMessage
            {
                ErrorResponse = new JsonRpcErrorResponse { JsonRpcVersion = "2.0", Id = request.Id, Error = new JsonRpcErrorObject { Code = code, Message = text, Data = data } },
            }));
        }
    }

    static TransportMessage Success(AhpJsonRpcRequest request, JsonElement result) =>
        Json.EncodeMessage(new JsonRpcMessage { SuccessResponse = new JsonRpcSuccessResponse { JsonRpcVersion = "2.0", Id = request.Id, Result = result } });

    static TransportMessage Notification(string method, JsonElement @params) =>
        Json.EncodeMessage(new JsonRpcMessage { Notification = new JsonRpcNotification { JsonRpcVersion = "2.0", Method = method, Params = @params } });

    static T Params<T>(JsonElement? @params) where T : class
    {
        if (@params is not { } p) throw new AhpError(JsonRpcErrorCodes.InvalidParams, "params are required");
        try { return Json.Deserialize<T>(p) ?? throw new AhpError(JsonRpcErrorCodes.InvalidParams, "params are required"); }
        catch (JsonException e) { throw new AhpError(JsonRpcErrorCodes.InvalidParams, $"params: {e.Message}"); }
    }

    async Task<JsonElement?> CommandAsync(Connection c, AhpJsonRpcRequest request)
    {
        var method = request.Method;
        var @params = request.Params;
        switch (method)
        {
            case "ping":
                return Empty;
            case "initialize":
                return await InitializeAsync(c, request, Params<InitializeParams>(@params));
            case "reconnect":
                return await ReconnectAsync(c, request, Params<ReconnectParams>(@params));
        }
        if (!c.Initialized) throw new AhpError(JsonRpcErrorCodes.InvalidRequest, "Send initialize or reconnect first");
        switch (method)
        {
            case "listSessions":
                return await ListSessionsAsync(c, request);
            case "unsubscribe":
                Unsubscribe(c, Params<UnsubscribeParams>(@params).Channel);
                return Empty;
            case "dispatchAction":
                Dispatch(c, Params<DispatchActionParams>(@params));
                return Empty;
            case "createSession":
                return await CreateSessionAsync(Params<CreateSessionParams>(@params));
            case "disposeSession":
                return await DisposeSessionAsync(Params<DisposeSessionParams>(@params));
            case "createChat" or "disposeChat" or "moveChat":
                throw new AhpError(AhpErrorCodes.PermissionDenied, "This host has one chat per session and its children");
            case "resolveSessionConfig":
                return ResolveSessionConfig(Params<ResolveSessionConfigParams>(@params));
            case "completions":
                return Completions(Params<CompletionsParams>(@params));
            case "sessionConfigCompletions":
                return Json.SerializeToElement(new SessionConfigCompletionsResult { Items = [] });
            case "fetchTurns":
                return Json.SerializeToElement(new FetchTurnsResult());
            case "authenticate":
                return Empty;
            case "createTerminal":
                return CreateTerminal(Params<CreateTerminalParams>(@params));
            case "disposeTerminal":
                return DisposeTerminal(Params<DisposeTerminalParams>(@params));
            case "resourceList":
                return ResourceList(Params<ResourceListParams>(@params));
            case "resourceResolve":
                return ResourceResolve(Params<ResourceResolveParams>(@params));
            case "resourceRead":
                return ResourceRead(Params<ResourceReadParams>(@params));
            case "resourceMkdir":
                return ResourceMkdir(Params<ResourceMkdirParams>(@params));
            case "createResourceWatch":
            case var resource when resource.StartsWith("resource", StringComparison.Ordinal):
                throw new AhpError(AhpErrorCodes.PermissionDenied, "This host lists and reads files and makes folders; it writes, copies, moves, deletes and watches none");
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
                    Unsubscribe(c, Params<UnsubscribeParams>(notification.Params).Channel);
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

    /// <summary>The agents are known; or why the face cannot serve.</summary>
    async Task AgentsReadyAsync()
    {
        try { await agentsReady.Task; }
        catch (Exception e) { throw new AhpError(JsonRpcErrorCodes.InternalError, $"This host did not start: {e.Message}"); }
    }

    /// <summary>Waits for the first list, as long as <see cref="AhpFaceOptions.FirstListWait"/> at most.</summary>
    async Task FirstListAsync()
    {
        try { await listed.Task.WaitAsync(options.FirstListWait, stopping.Token); }
        catch (TimeoutException) { }
    }

    async Task<JsonElement?> InitializeAsync(Connection c, AhpJsonRpcRequest request, InitializeParams p)
    {
        if (!p.ProtocolVersions.Contains(AhpNames.Version))
            throw new AhpError(AhpErrorCodes.UnsupportedProtocolVersion, $"This host speaks AHP {AhpNames.Version} only",
                Json.SerializeToElement(new UnsupportedProtocolVersionErrorData { SupportedVersions = [AhpNames.Version] }));
        // Not the list: the sessions follow as they come, and listSessions waits for them.
        await AgentsReadyAsync();
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
            c.Send(Success(request, Json.SerializeToElement(new InitializeResult
            {
                ProtocolVersion = AhpNames.Version, ServerSeq = serverSeq,
                ServerInfo = new Implementation { Name = "gorynych", Title = "Gorynych", Version = AgentHost.Version },
                Meta = new Dictionary<string, JsonElement> { [GorynychProtocol.MetaServiceId] = AhpNames.Element(JsonValue.Create(serviceId)) },
                Snapshots = snapshots,
                DefaultDirectory = AhpNames.FileUri(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            })));
            return null;
        }
    }

    /// <summary>Opens the sessions of the chats among <paramref name="channels"/> that are not open yet.</summary>
    async Task LoadAsync(IEnumerable<string> channels)
    {
        List<FaceChat> load;
        // A session's own chat too: its snapshot has its agent's options once the face has it open.
        lock (gate) load = [.. channels.Select(ch => chats.GetValueOrDefault(ch) ?? sessions.GetValueOrDefault(ch)?.Chats[0])
            .OfType<FaceChat>().Where(ch => ch.Model is null).Distinct()];
        foreach (var chat in load)
            try { await EnsureLoadedAsync(chat); }
            catch (AhpError e) { ServiceLog.Warn($"ahp: {e.Message}"); }
    }

    async Task<JsonElement?> ReconnectAsync(Connection c, AhpJsonRpcRequest request, ReconnectParams p)
    {
        await AgentsReadyAsync();
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
            var seen = p.LastSeenServerSeq;
            var rootToo = c.Subscriptions.Contains(AhpNames.Root);
            // A replay only when the log has everything after what the client saw, its root notifications too,
            // and no channel's state was made afresh since.
            var replay = seen <= serverSeq
                && (log.First is null || log.First.Value.Seq <= seen + 1)
                && (!rootToo || rootTrimmed < seen)
                && present.All(ch => BaseSeqOf(ch) is { } since && since <= seen);
            ReconnectResult result = replay
                ? new ReconnectResult(new ReconnectReplayResult
                {
                    Type = ReconnectResultType.Replay,
                    Actions = [.. log.Where(e => e.Seq > seen && c.Subscriptions.Contains(e.Channel)).Select(e => Json.Deserialize<ActionEnvelope>(e.Envelope)!)],
                    Missing = missing,
                })
                : new ReconnectResult(new ReconnectSnapshotResult
                {
                    Type = ReconnectResultType.Snapshot,
                    Snapshots = [.. present.Select(SnapshotOf).OfType<Snapshot>()],
                });
            c.Send(Success(request, Json.SerializeToElement(result)));
            // The root notifications it may have missed, again: they carry no number, and each says how a session is
            // now, so one it had already does no harm.
            if (replay && rootToo)
                foreach (var (stamp, message) in rootLog)
                    if (stamp >= seen) c.Send(message);
            return null;
        }
    }

    /// <summary>Under the gate: whether a channel exists for a client; a chat whose session the face does not have open yet does.</summary>
    bool Exists(string channel) => channel == AhpNames.Root || sessions.ContainsKey(channel) || chats.ContainsKey(channel) || terminals.ContainsKey(channel);

    /// <summary>Under the gate: the number since which the channel's state has been as it is (built afresh then); null when it has none now.</summary>
    long? BaseSeqOf(string channel) =>
        channel == AhpNames.Root ? 0
        : sessions.TryGetValue(channel, out var s) ? s.BaseSeq
        : chats.TryGetValue(channel, out var chat) && chat.Model is not null ? chat.BaseSeq
        : terminals.TryGetValue(channel, out var terminal) ? terminal.BaseSeq
        : null;

    /// <summary>Under the gate: a channel's state now; null for one with none (a chat whose session is not open).</summary>
    Snapshot? SnapshotOf(string channel)
    {
        SnapshotState state;
        if (channel == AhpNames.Root) state = new SnapshotState { Root = root };
        else if (sessions.TryGetValue(channel, out var s)) state = new SnapshotState { Session = s.State };
        else if (chats.TryGetValue(channel, out var chat) && chat.Model is { } model) state = new SnapshotState { Chat = model.State };
        else if (terminals.TryGetValue(channel, out var terminal)) state = new SnapshotState { Terminal = terminal.State };
        else return null;
        return new Snapshot { Resource = channel, State = state, FromSeq = serverSeq };
    }

    /// <summary>The sessions, a page of them; once the first list is read, or as many as came before it waited too long.</summary>
    async Task<JsonElement?> ListSessionsAsync(Connection c, AhpJsonRpcRequest request)
    {
        var p = Params<ListSessionsParams>(request.Params);
        await FirstListAsync();
        lock (gate)
        {
            // Not one still being made, or that could not be: the root says a created session once it is ready.
            var all = sessions.Values.Where(s => s.SessionId.Length > 0).Select(s => s.Sent ?? Summary(s))
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
            c.Send(Success(request, Json.SerializeToElement(new ListSessionsResult
            {
                Items = page,
                NextCursor = more ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{page[^1].ModifiedAt}\n{page[^1].Resource}")) : null,
            })));
            return null;
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

    /// <summary>
    /// A subscription, taken in the connection's order when it arrives: an <c>unsubscribe</c> after it, even one that
    /// comes while its session opens, takes it back. Answered once the channel has its state.
    /// </summary>
    void Subscribe(Connection c, AhpJsonRpcRequest request)
    {
        string channel;
        try { channel = Params<SubscribeParams>(request.Params).Channel; }
        catch (AhpError e)
        {
            _ = AnswerAsync(c, request, () => Task.FromException<JsonElement?>(e));
            return;
        }
        var pending = new object();
        lock (gate) c.Pending[channel] = pending;
        _ = Task.Run(() => AnswerAsync(c, request, () => SubscribeAsync(c, request, channel, pending)));
    }

    void Unsubscribe(Connection c, string channel)
    {
        lock (gate)
        {
            c.Subscriptions.Remove(channel);
            c.Pending.Remove(channel);
        }
    }

    async Task<JsonElement?> SubscribeAsync(Connection c, AhpJsonRpcRequest request, string channel, object pending)
    {
        try
        {
            // A channel the first list has not brought yet may come with it (VS Code subscribes again to what it
            // had open, right after a restart).
            bool known;
            lock (gate) known = Exists(channel);
            if (!known) await FirstListAsync();
            FaceChat? chat;
            FaceSession? session;
            lock (gate)
            {
                session = sessions.GetValueOrDefault(channel);
                // A session's own chat is opened first, so that the session's snapshot has its agent's options.
                if (!chats.TryGetValue(channel, out chat) && session?.Chats[0] is not { Model: null } own)
                {
                    if (!Subscribed(c, request, channel, pending)) throw NoChannel(channel);
                    return null;
                }
                chat ??= session!.Chats[0];
            }
            if (session is null) await EnsureLoadedAsync(chat);
            else
                try { await EnsureLoadedAsync(chat); }
                catch (AhpError e) { ServiceLog.Warn($"ahp: {e.Message}"); } // the session's snapshot goes without its options
            lock (gate)
                if (!Subscribed(c, request, channel, pending)) throw NoChannel(channel);
            return null;
        }
        finally
        {
            lock (gate)
                if (c.Pending.TryGetValue(channel, out var still) && still == pending) c.Pending.Remove(channel);
        }
    }

    static AhpError NoChannel(string channel) => new(AhpErrorCodes.SessionNotFound, $"No channel {channel} on this host");

    /// <summary>
    /// Under the gate: answers a subscription with the channel's state now, queued with it, and keeps the
    /// subscription unless the client took it back meanwhile; false for a channel with no state.
    /// </summary>
    bool Subscribed(Connection c, AhpJsonRpcRequest request, string channel, object pending)
    {
        if (SnapshotOf(channel) is not { } snapshot) return false;
        if (c.Pending.TryGetValue(channel, out var still) && still == pending)
        {
            c.Pending.Remove(channel);
            c.Subscriptions.Add(channel);
            if (chats.TryGetValue(channel, out var chat)) chat.Unwatched = null;
        }
        c.Send(Success(request, Json.SerializeToElement(new SubscribeResult { Snapshot = snapshot })));
        return true;
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
            if (terminals.TryGetValue(p.Channel, out var terminal))
            {
                // A terminal with no process: what a client changes in it is kept, its input goes nowhere.
                Broadcast(p.Channel, p.Action, origin);
                Reducers.ApplyToTerminal(terminal.State, p.Action);
                return;
            }
            if (sessions.TryGetValue(p.Channel, out var s))
            {
                switch (action)
                {
                    case SessionConfigChangedAction config:
                        ConfigChanged(c, s, p.Action, config, origin);
                        return;
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
                case ChatTurnStartedAction started:
                    TurnStarted(c, chat, p.Action, started, origin);
                    return;
                case ChatTurnCancelledAction cancelled:
                    TurnCancelled(c, chat, p.Action, cancelled, origin);
                    return;
                case ChatToolCallConfirmedAction confirmed:
                    Confirmed(c, chat, p.Action, confirmed, origin);
                    return;
                case ChatInputAnswerChangedAction answer:
                    InputAnswered(c, chat, p.Action, answer, origin);
                    return;
                case ChatInputCompletedAction completed:
                    InputCompleted(c, chat, p.Action, completed, origin);
                    return;
                case ChatPendingMessageSetAction set:
                    PendingSet(c, chat, p.Action, set, origin);
                    return;
                case ChatPendingMessageRemovedAction removed:
                    PendingRemoved(c, chat, p.Action, removed, origin);
                    return;
                case ChatQueuedMessagesReorderedAction when chat.Model is not null:
                    ApplyChat(chat, action, origin);
                    return;
                default:
                    Broadcast(p.Channel, p.Action, origin, InteractivityOf(chat) == ChatInteractivity.ReadOnly
                        ? "This chat is read-only" : "This chat takes no such change", c);
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

    /// <summary>
    /// Stops: each client's queue is sent out and its socket closed, then ended, before the endpoint stops, which
    /// would otherwise wait out its shutdown timeout for every open WebSocket.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<Connection> all;
        lock (gate) all = [.. connections];
        await Task.WhenAll(all.Select(async c =>
        {
            c.Complete();
            try { await c.Sending.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            try { await c.Transport.CloseAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }));
        stopping.Cancel();
        lock (gate) all = [.. connections];
        foreach (var c in all) c.Abort();
        if (acp is not null) await acp.DisposeAsync();
        if (serving is not null) try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
    }
}
