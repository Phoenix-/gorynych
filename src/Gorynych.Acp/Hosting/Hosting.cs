namespace Gorynych.Acp.Hosting;

/// <summary>
/// An adapter is an ACP agent. The service starts it and talks ACP to it as a client over
/// the returned streams, whether the agent is an external process (<c>kimi acp</c>) or an
/// in-process implementation joined by a <see cref="JsonRpc.DuplexLink"/>.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>A stable short name, used as <c>_meta["gorynych/agent"]</c> and for routing.</summary>
    string Name { get; }

    /// <summary>A human-readable name for lists.</summary>
    string DisplayName { get; }

    Task<AgentInstance> StartAsync(CancellationToken ct);
}

/// <summary>A running agent: the ACP byte streams and a way to stop it.</summary>
public sealed class AgentInstance(Stream input, Stream output, Task exited, Func<ValueTask> stop) : IAsyncDisposable
{
    /// <summary>Messages from the agent.</summary>
    public Stream Input { get; } = input;

    /// <summary>Messages to the agent.</summary>
    public Stream Output { get; } = output;

    /// <summary>Completes when the agent exits.</summary>
    public Task Exited { get; } = exited;

    public ValueTask DisposeAsync() => stop();
}

/// <summary>Gorynych's own ACP extensions: <c>_meta</c> keys and <c>_</c>-prefixed methods.</summary>
public static class GorynychProtocol
{
    /// <summary>Which adapter owns a session; in <c>session/new</c> params it selects one.</summary>
    public const string MetaAgent = "gorynych/agent";

    /// <summary>Internal host prompt and terminal correlation for a pre-admitted report.</summary>
    public const string MetaReportId = "gorynych/reportId";
    /// <summary>Trusted queued parent-delivery correlation, separate from producer capture.</summary>
    public const string MetaReportDelivery = "gorynych/reportDelivery";
    /// <summary>Internal host capture capability and diagnostic extension.</summary>
    public const string MetaReportCapture = "gorynych/reportCapture";
    /// <summary>Trusted host-only error data proving a report prompt was refused before dispatch.</summary>
    public const string MetaReportRefused = "gorynych/reportRefused";
    /// <summary>Host-generated kind of an in-flight turn for adoption.</summary>
    public const string MetaHostTurnKind = "gorynych/hostTurnKind";


    /// <summary>
    /// The shared ACP v1 steering extension: a request <c>{sessionId, prompt, _meta?}</c> into a
    /// running turn (docs/steering.md). It is not an ACP-defined method.
    /// </summary>
    public const string MethodSteering = "_session/steering";

    /// <summary>
    /// Top-level <c>_meta</c> of an adapter's <c>initialize</c> result: <c>{"supported": true}</c>
    /// when it takes <see cref="MethodSteering"/>. Next to <c>agentCapabilities</c>, not inside it.
    /// </summary>
    public const string MetaSteering = "steering";

    /// <summary>
    /// The <c>_meta</c> of <c>session/new</c>, <c>session/load</c> and <c>session/resume</c>: whether
    /// this session's agent steers (docs/steering.md).
    /// </summary>
    public const string MetaSessionSteering = "gorynych/steering";

    /// <summary>On a steered <c>user_message_chunk</c> in the journal: this message joined the turn.</summary>
    public const string MetaSteered = "gorynych/steered";

    /// <summary>In the <c>data</c> of a steering refusal: the reason (<c>notSupported</c>, <c>turnNotSteerable</c>).</summary>
    public const string MetaSteerRefused = "gorynych/steerRefused";

    /// <summary>Host → conductor, numbered and kept: an accepted steer to journal before its turn's end.</summary>
    public const string MethodSteerAccepted = "_gorynych/steer_accepted";

    /// <summary>
    /// Internal, on a steering request toward an adapter host: which client sent it, so the conductor
    /// journals the acceptance without echoing it to that client. Never sent to clients.
    /// </summary>
    public const string MetaSteerOrigin = "gorynych/steerOrigin";

    /// <summary>The persistent ID of the service, in the <c>initialize</c> result.</summary>
    public const string MetaServiceId = "gorynych/serviceId";

    /// <summary>The adapters a service offers, in the <c>initialize</c> result.</summary>
    public const string MetaAgents = "gorynych/agents";

    /// <summary>
    /// In a client's <c>initialize</c> <c>_meta</c>: <c>false</c> when the client takes no agent → client
    /// requests (approvals, forms): the service routes none to it (the AHP face, step 1).
    /// </summary>
    public const string MetaRequests = "gorynych/requests";

    /// <summary>In the AHP face's <c>SessionSummary._meta</c>: the Gorynych session ID of an AHP session (docs/ahp-face.md).</summary>
    public const string MetaSessionId = "gorynych/sessionId";

    /// <summary>Where a user message came from when it was not typed by the user (for example <c>channel</c>).</summary>
    public const string MetaOrigin = "gorynych/origin";

    /// <summary>Who sent an agent-to-agent message.</summary>
    public const string MetaSender = "gorynych/sender";

    /// <summary>Identifies the asking session in calls from the peers MCP server the service gave it.</summary>
    public const string MetaPeerToken = "gorynych/peerToken";

    /// <summary>Asks a session's agent for an opinion: <c>{sessionId, text, sender?}</c> → <c>{text}</c>.</summary>
    public const string MethodOpinion = "_gorynych/opinion";

    /// <summary>Durably delegate already authorized work to an immediate child.</summary>
    public const string MethodDelegateTask = "_gorynych/delegate_task";
    /// <summary>Advertises support for <see cref="MethodDelegateTask"/>.</summary>
    public const string CapDelegateTask = "gorynych/delegateTask";
    /// <summary>Read an immutable report, or list report metadata as an authenticated peer.</summary>
    public const string MethodReadReport = "_gorynych/read_report";
    public const string MethodListReports = "_gorynych/list_reports";
    public const string CapReportRetrieval = "gorynych/reportRetrieval";
    /// <summary>Headless native report-note submission and receipt query (extension fields live in _meta).</summary>
    public const string MethodReportNote = "_gorynych/report_note";
    public const string MethodReportNoteStatus = "_gorynych/report_note_status";
    public const string MethodReportNoteAck = "_gorynych/report_note_ack";
    public const string MetaReportNote = "gorynych/reportNote";
    public const string CapReportNote = "gorynych/reportNoteReceipt";
    public const string CapReportNoteAck = "gorynych/reportNoteAck";
    public const string MethodHostReportNote = "_gorynych/host_report_note";
    public const string MethodHostReportNoteStatus = "_gorynych/host_report_note_status";
    public const string MethodHostReportNoteAck = "_gorynych/host_report_note_ack";
    public const string MethodHostReportNoteReceipt = "_gorynych/host_report_note_receipt";
    public const string MetaHostNoteTracking = "gorynych/hostNoteTracking";
    public const string CapHostReportNote = "gorynych/hostReportNoteReceipt";
    /// <summary>Correlation ID for a delegated or spawned assignment.</summary>
    public const string MetaAssignmentId = "gorynych/assignmentId";
    /// <summary>Durably enqueue an inter-session message without waiting for a reply.</summary>
    public const string MethodSendMessage = "_gorynych/send_message";
    public const string MethodRequestReply = "_gorynych/request_reply";
    public const string CapCompletionOwnerTrigger = "gorynych/completionOwnerTrigger";
    public const string CapCompletionOwnerRecovery = "gorynych/completionOwnerRecovery";
    public const string MethodObserveCompletionGroup = "_gorynych/observe_completion_group";
    public const string CapCompletionGroupObservation = "gorynych/completionGroupObservation";
    public const string MethodRecoverCompletionReminder = "_gorynych/recover_completion_reminder";
    public const string MethodRequestCompletionReminder = "_gorynych/request_completion_reminder";
    public const string MethodListCompletionNotices = "_gorynych/list_completion_notices";
    public const string MethodAckCompletionNotice = "_gorynych/ack_completion_notice";
    public const string CapCompletionNotices = "gorynych/completionNotices";
    public const string MethodListAssignments = "_gorynych/list_assignments";
    public const string MethodReadAssignment = "_gorynych/read_assignment";
    public const string MethodUpdateAssignment = "_gorynych/update_assignment";
    public const string CapAssignments = "gorynych/assignments";
    public const string MethodUpdateContinuation = "_gorynych/update_continuation";
    public const string CapAssignmentContinuations = "gorynych/assignmentContinuations";
    public const string CapAssignmentDiagnostics = "gorynych/assignmentDiagnostics";
    public const string MethodListBackgroundTasks = "_gorynych/list_background_tasks";
    public const string CapAssignmentBackgroundContinuations = "gorynych/assignmentBackgroundContinuations";
    public const string MethodListReplyWaits = "_gorynych/list_reply_waits";
    public const string MethodCancelReplyWait = "_gorynych/cancel_reply_wait";
    public const string CapReplyWaits = "gorynych/replyWaits";
    public const string MetaExpectsReply = "gorynych/expectsReply";
    public const string MetaReplyReminder = "gorynych/replyReminder";
    public const string MethodReplyMessage = "_gorynych/reply_message";
    public const string MetaMessageRequestId = "gorynych/messageRequestId";
    public const string MetaMailboxMessage = "gorynych/mailboxMessage";
    public const string MetaOutsideMailbox = "gorynych/outsideMailbox";

    /// <summary>An answer to an opinion request that was delivered as a turn: <c>{messageId, text}</c>.</summary>
    public const string MethodReply = "_gorynych/reply";

    /// <summary>The opinion request a message or update belongs to (its <c>message_id</c>).</summary>
    public const string MetaOpinion = "gorynych/opinion";

    /// <summary>
    /// The asker's own name for a question (<c>ask_agent</c>), a random one it makes: in the <c>_meta</c>
    /// of <c>_gorynych/opinion</c> from the peers server and of <c>_gorynych/mcp_message</c> from the
    /// outside server. The service passes it on to the host with the question, so the next service, after
    /// a restart, can give the answer to the asker that comes back for it (<see cref="MetaReclaim"/>).
    /// </summary>
    public const string MetaAskId = "gorynych/askId";

    /// <summary>
    /// <c>true</c> next to <see cref="MetaAskId"/>: the asker lost its connection while the question was
    /// answered (a restart) and asks for the answer, not again. A service that does not know the question
    /// fails with <see cref="MetaAskLost"/> in the error's <c>data</c>; it never asks the session anew.
    /// </summary>
    public const string MetaReclaim = "gorynych/reclaim";

    /// <summary>In the <c>data</c> of the error a reclaim gets for a question no host kept: it went with the service; ask again.</summary>
    public const string MetaAskLost = "gorynych/askLost";

    /// <summary>The sessions a peer can ask: <c>{token}</c> → <c>{sessions: [...]}</c>.</summary>
    public const string MethodPeers = "_gorynych/peers";

    /// <summary>
    /// The session that started this one with <c>spawn_session</c>: in every <c>SessionInfo._meta</c>
    /// of <c>session/list</c>, and in a <c>session_info_update._meta</c> when it is set.
    /// </summary>
    public const string MetaParentSessionId = "gorynych/parentSessionId";

    /// <summary>
    /// The AHP URI a session created through the AHP face goes by (<c>ahp-session:/&lt;id&gt;</c>, its
    /// client's choice): in <c>session/new</c>'s <c>_meta</c>, kept in the overlay, and in
    /// <c>SessionInfo._meta</c> of <c>session/list</c> (docs/ahp-face.md, "Identifiers").
    /// </summary>
    public const string MetaAhpUri = "gorynych/ahpUri";

    /// <summary>
    /// In <c>session/new</c> params <c>_meta</c>, client → service: an object of native config
    /// option IDs to string values to set on the new session, for example
    /// <c>{"model": "[\"deepseek-official\",\"deepseek-flash\"]"}</c>. Only model and model-related
    /// options (ACP categories <c>model</c>, <c>model_config</c> and <c>thought_level</c>) are
    /// accepted; permission modes use the request's own <c>mode</c> surface, never this map.
    /// </summary>
    public const string MetaConfigOptions = "gorynych/configOptions";

    /// <summary>
    /// In <c>session/new</c> params <c>_meta</c>, service → adapter: the config option IDs the
    /// service is about to set (docs/service-protocol.md, "Default session configuration"). An
    /// adapter whose catalog of options arrives asynchronously (Claude's model catalog) may wait
    /// for an already running fetch, but must not start a model call for it, before it answers.
    /// </summary>
    public const string MetaRequiredConfigOptions = "gorynych/requiredConfigOptions";

    /// <summary>
    /// Starts a child of the calling session, after asking the person:
    /// <c>{agent, prompt, cwd?, title?, report?}</c> with the caller's peer token → <c>{sessionId}</c>.
    /// </summary>
    public const string MethodSpawn = "_gorynych/spawn";

    /// <summary>
    /// Tells a session's agent something that needs no answer, such as a child's report:
    /// <c>{sessionId, text, sender, fromSessionId?}</c> → <c>{}</c>. An agent without a way in
    /// (no channel) answers method not found, and the service delivers it as a turn.
    /// </summary>
    public const string MethodNote = "_gorynych/note";

    /// <summary>The session a peer request or a report came from, in the <c>_meta</c> of the message it became.</summary>
    public const string MetaFromSessionId = "gorynych/fromSessionId";

    /// <summary>A notification to clients: <c>{sessionId, change}</c>, change <c>added</c>, <c>updated</c> or <c>removed</c>.</summary>
    public const string MethodSessionsChanged = "_gorynych/sessions_changed";
    public const string MethodAttributesGet = "_gorynych/session_attributes/get";
    public const string MethodAttributesPatch = "_gorynych/session_attributes/patch";
    public const string MethodGroupsList = "_gorynych/groups/list";
    public const string MethodGroupsSet = "_gorynych/groups/set";
    public const string MethodGroupsDelete = "_gorynych/groups/delete";
    public const string MethodGroupsChanged = "_gorynych/groups_changed";
    public const string MetaAttributes = "gorynych/attributes";
    public const string MetaGroup = "gorynych/group";
    public const string CapSessionAttributes = "gorynych/sessionAttributes";
    public const string CapGroups = "gorynych/groups";

    /// <summary>
    /// In <c>agentCapabilities._meta</c> of the <c>initialize</c> result: the service sends
    /// <see cref="MethodSessionsChanged"/>. (Also in the result's own <c>_meta</c>, deprecated.)
    /// </summary>
    public const string MetaSessionsChanged = "gorynych/sessionsChanged";
    /// <summary>Version 1 live aggregate session activity extension and snapshot key.</summary>
    public const string MetaSessionState = "gorynych/sessionState";
    public const string MethodSessionState = "_gorynych/session_state";
    /// <summary>Adapter's authoritative foreground state, never inferred from a pending permission.</summary>
    public const string MetaForegroundState = "gorynych/foregroundState";
    public const string MetaCodexLoadedIdle = "gorynych/_codexLoadedIdle";
    public const string MethodCodexProvisionalIdle = "_gorynych/codex_provisional_idle";
    public const string MethodHostCodexProvisionalIdle = "_gorynych/host_codex_provisional_idle";
    public const string CapHostCodexProvisionalIdle = "gorynych/hostCodexProvisionalIdle";
    public const string MethodCompletionGuardedSubmit = "_gorynych/completion_guarded_submit";
    public const string MethodCompletionGuardedStatus = "_gorynych/completion_guarded_status";
    public const string MethodCompletionGuardedAck = "_gorynych/completion_guarded_ack";
    public const string MethodCompletionGuardedRetire = "_gorynych/completion_guarded_retire";
    public const string MethodCompletionGuardedAbort = "_gorynych/completion_guarded_abort";
    public const string MethodHostCompletionGuardedSubmit = "_gorynych/host_completion_guarded_submit";
    public const string MethodHostCompletionGuardedStatus = "_gorynych/host_completion_guarded_status";
    public const string MethodHostCompletionGuardedAck = "_gorynych/host_completion_guarded_ack";
    public const string MethodHostCompletionGuardedRetire = "_gorynych/host_completion_guarded_retire";
    public const string MethodHostCompletionGuardedAbort = "_gorynych/host_completion_guarded_abort";
    public const string CapCompletionGuardedReceiver = "gorynych/completionGuardedReceiver";
    public const string CapCompletionGuardedAdapter = "gorynych/completionGuardedAdapter";
    public const string CapHostCompletionGuarded = "gorynych/hostCompletionGuarded";
    public const string MethodClaudePromptObservation = "_gorynych/claude_prompt_observation";
    public const string MethodHostClaudePromptObservation = "_gorynych/host_claude_prompt_observation";
    public const string CapClaudeObservationReceiver = "gorynych/claudeObservationReceiver";
    public const string CapClaudePromptObservation = "gorynych/claudePromptObservation";
    public const string CapHostClaudePromptObservation = "gorynych/hostClaudePromptObservation";
    public const string CapCodexIdleHostReceiver = "gorynych/codexIdleHostReceiver";
    public const string CapCodexIdleAdapter = "gorynych/codexIdleAdapter";
    /// <summary>Trusted adapter confirms a full background change stream for its current process.</summary>
    public const string MetaBackgroundCompleteStream = "gorynych/backgroundCompleteStream";
    public const string MetaBackgroundWork = "gorynych/backgroundWork";
    /// <summary>A partial live observation; it does not establish the complete background snapshot.</summary>
    public const string MetaBackgroundKnown = "gorynych/backgroundKnown";
    /// <summary>The native process ended; releases its resource pin without claiming a known-empty snapshot.</summary>
    public const string MetaBackgroundProcessExited = "gorynych/backgroundProcessExited";
    public const string MetaHostBackgroundEvidence = "gorynych/hostBackgroundEvidence";
    public const string CapHostBackgroundEvidence = "gorynych/hostBackgroundEvidenceCapability";

    /// <summary>
    /// In <c>agentCapabilities._meta</c> of the <c>initialize</c> result: the service keeps an archive
    /// flag per session (<see cref="MethodArchive"/>). (Also in the result's own <c>_meta</c>, deprecated.)
    /// </summary>
    public const string MetaArchive = "gorynych/archive";

    /// <summary>
    /// <c>true</c> in <c>SessionInfo._meta</c> for an archived session (absent otherwise), and an
    /// explicit <c>true</c> or <c>false</c> in a <c>session_info_update._meta</c> when the flag changes.
    /// </summary>
    public const string MetaArchived = "gorynych/archived";

    /// <summary>In <c>session/list</c> params <c>_meta</c>: <c>true</c> lists archived sessions too.</summary>
    public const string MetaIncludeArchived = "gorynych/includeArchived";

    /// <summary>
    /// An MCP request of the <c>gorynych-outside</c> server for agents outside Gorynych, in the
    /// shape of <c>mcp/message</c> from ACP's draft MCP-over-ACP RFD: <c>{serverId, requestId,
    /// method, params}</c> → <c>{result}</c> or <c>{error}</c>; as a notification from the service,
    /// a notification that belongs to that request (progress).
    /// </summary>
    public const string MethodMcpMessage = "_gorynych/mcp_message";

    /// <summary>Reads a session's history from the journal: <c>{sessionId, cursor?, limit?, thoughts?}</c>.</summary>
    public const string MethodRead = "_gorynych/read";

    /// <summary>
    /// The agent outside Gorynych behind a <c>_gorynych/mcp_message</c>: <c>{client, folder,
    /// hostSessionId}</c>; also in the <c>_meta</c> of the opinion request it asked.
    /// </summary>
    public const string MetaOutside = "gorynych/outside";

    /// <summary>
    /// On a <c>session/update</c> from an adapter: <c>"started"</c> on the first update of a turn
    /// the agent runs that the service did not start (a message through the agent's own channel,
    /// the person typing in its terminal), <c>"ended"</c> on an update when it ends. The service
    /// keeps the session loaded in between (docs/adapter-contract.md, §7).
    /// </summary>
    public const string MetaAgentTurn = "gorynych/agentTurn";

    /// <summary>
    /// A short random tag for one run, in the message IDs a component counts in memory (the
    /// service's <c>m…</c>, Claude's channel <c>c…</c>): after a restart the count starts again, and the
    /// tag keeps a late reply to a request of an earlier run from answering a new one. Four letters
    /// or digits from <see cref="RunTagAlphabet"/>, so the IDs stay easy for an agent to copy.
    /// </summary>
    public static string NewRunTag() => System.Security.Cryptography.RandomNumberGenerator.GetString(RunTagAlphabet, 4);

    /// <summary>The characters of a run tag: no look-alikes (0 1 i l o), since Kimi once copied an l as a 1.</summary>
    public const string RunTagAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>
    /// Sets a session's archive flag: <c>{sessionId, archived}</c> → <c>{}</c>. Only a flag: it
    /// closes, cancels and unloads nothing. A call with a peer token also gets <c>text</c>, what the agent is told.
    /// </summary>
    public const string MethodArchive = "_gorynych/archive";

    /// <summary>
    /// Sets a session's native title in its agent: <c>{sessionId, title}</c> → <c>{}</c>. The
    /// service calls it only on an adapter that advertises <see cref="CapSetTitle"/>
    /// (docs/adapter-contract.md, §9).
    /// </summary>
    public const string MethodSetTitle = "_gorynych/set_title";

    /// <summary>In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MethodSetTitle"/>.</summary>
    public const string CapSetTitle = "gorynych/setTitle";

    /// <summary>
    /// Asks an adapter to generate a concise native title for a session that still has only the
    /// service's provisional first-prompt title: <c>{sessionId, title, input, cwd}</c> → <c>{}</c>.
    /// The adapter does the bounded harness-specific generation and writes the native name; the
    /// answer says only that the request was accepted. The service calls it only on an adapter that
    /// advertises <see cref="CapAutoTitle"/> (docs/adapter-contract.md, §9).
    /// </summary>
    public const string MethodGenerateTitle = "_gorynych/generate_title";

    /// <summary>In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MethodGenerateTitle"/>.</summary>
    public const string CapAutoTitle = "gorynych/autoTitle";

    /// <summary>
    /// In <c>agentCapabilities._meta</c>: seconds; the agent's sessions are unloaded after this
    /// idle time when it is shorter than the service's own (docs/adapter-contract.md, §7).
    /// </summary>
    public const string CapUnloadAfter = "gorynych/unloadAfter";

    /// <summary>
    /// In <c>agentCapabilities._meta</c>: <c>true</c> when the agent keeps nothing durable before a
    /// session's first prompt, so a draft it does not list was never durable (docs/adapter-contract.md, §3).
    /// </summary>
    public const string CapDraftsNotDurable = "gorynych/draftsNotDurable";

    /// <summary>In <c>agentCapabilities._meta</c>: slash commands the adapter answers itself, without a native turn.</summary>
    public const string CapLocalCommands = "gorynych/localCommands";

    /// <summary>On a local command's updates: <c>true</c>, so they do not make a non-durable draft native history.</summary>
    public const string MetaLocalCommand = "gorynych/localCommand";

    /// <summary>
    /// In <c>session/resume</c> params <c>_meta</c>, service → adapter: <c>true</c> for a draft, a session
    /// created and never prompted. An adapter that keeps nothing before the first prompt and no longer
    /// knows the session (it restarted) opens it afresh under its ID (docs/adapter-contract.md, §3).
    /// </summary>
    public const string MetaDraft = "gorynych/draft";

    /// <summary>
    /// A string naming another app that has the session open, so its agent cannot load it here until
    /// it is closed there ("Codex Desktop", "a terminal codex (pid 4242)"): in <c>SessionInfo._meta</c>
    /// of <c>session/list</c> (absent: free, or not known) (docs/service-protocol.md, "Sessions open in another app").
    /// </summary>
    public const string MetaOpenElsewhere = "gorynych/openElsewhere";

    /// <summary>
    /// Which of some sessions another app has open now: <c>{sessionIds}</c> → <c>{sessions: {"&lt;id&gt;":
    /// "&lt;holder&gt;"}}</c>, the held ones only. Sent to an adapter that advertises <see cref="CapOpenElsewhere"/>.
    /// </summary>
    public const string MethodOpenElsewhere = "_gorynych/open_elsewhere";

    /// <summary>In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MethodOpenElsewhere"/>.</summary>
    public const string CapOpenElsewhere = "gorynych/openElsewhere";

    /// <summary>
    /// What a client may do with a session: <see cref="InteractivityFull"/> (absent means it) or
    /// <see cref="InteractivityReadOnly"/>, as AHP's <c>ChatInteractivity</c>. In <c>SessionInfo._meta</c>
    /// of <c>session/list</c>, the <c>_meta</c> of a <c>session/load</c> or <c>session/resume</c> answer,
    /// and the <c>_meta</c> of a <c>session_info_update</c>, where a change always says the value
    /// (docs/service-protocol.md, "Read-only sessions").
    /// </summary>
    public const string MetaInteractivity = "gorynych/interactivity";

    /// <summary>Why a session is read-only, in words for a person, next to <see cref="MetaInteractivity"/>.</summary>
    public const string MetaInteractivityReason = "gorynych/interactivityReason";

    /// <summary>A <see cref="MetaInteractivity"/>: the client may send prompts.</summary>
    public const string InteractivityFull = "full";

    /// <summary>A <see cref="MetaInteractivity"/>: the client may watch the session, not prompt it.</summary>
    public const string InteractivityReadOnly = "read-only";

    /// <summary>
    /// Service → adapter, <c>{sessionId, _meta: {"gorynych/replayFrom": …}}</c> → <c>{_meta: {"gorynych/replay": …}}</c>:
    /// replays the session's history after the mark, as a resume does (<see cref="MetaReplayFrom"/>),
    /// without opening or taking it: for a session open for reading only, whose history another app
    /// made meanwhile (docs/service-protocol.md, "Read-only sessions").
    /// </summary>
    public const string MethodCatchUp = "_gorynych/catch_up";

    /// <summary>In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MethodCatchUp"/>.</summary>
    public const string CapCatchUp = "gorynych/catchUp";

    /// <summary>Service → adapter: republish the observed states of this root's native subagents.</summary>
    public const string MethodRefreshSubagentStates = "_gorynych/refresh_subagent_states";

    /// <summary>Private update marker for a fresh subagent snapshot that should not be journaled again.</summary>
    public const string MetaSubagentSnapshot = "gorynych/subagentSnapshot";

    /// <summary>Internal adapter/service negotiation and marker for a live subagent state observation.</summary>
    public const string MetaSubagentLiveState = "gorynych/subagentLiveState";

    /// <summary>
    /// In <c>agentCapabilities._meta</c>: <c>true</c> when the adapter takes <see cref="MetaReplayFrom"/>
    /// on <c>session/resume</c> and reports <see cref="MetaHistoryMark"/> (docs/adapter-contract.md, §4).
    /// </summary>
    public const string CapReplayFrom = "gorynych/replayFrom";

    /// <summary>
    /// In <c>session/resume</c> params <c>_meta</c>: replay the agent's own history as
    /// <c>session/update</c>s before answering, from <c>{"type": "start"}</c> or after a mark,
    /// <c>{"type": "gorynych/after", "mark": "…"}</c>. The shape of the <c>replayFrom</c> of ACP v2's
    /// session-resume-replay draft.
    /// </summary>
    public const string MetaReplayFrom = "gorynych/replayFrom";

    /// <summary>The <c>type</c> of a <see cref="MetaReplayFrom"/> cursor that replays everything.</summary>
    public const string ReplayFromStart = "start";

    /// <summary>The <c>type</c> of a <see cref="MetaReplayFrom"/> cursor that replays what follows its <c>mark</c>.</summary>
    public const string ReplayAfterMark = "gorynych/after";

    /// <summary>
    /// In a <c>session/resume</c> result's <c>_meta</c>, answering <see cref="MetaReplayFrom"/>:
    /// <c>{"from": "mark" | "start", "mark": "…"}</c>. <c>from: "start"</c> after a request from a mark
    /// says the mark is no longer in the history (it was rewritten), so all of it was sent.
    /// </summary>
    public const string MetaReplay = "gorynych/replay";

    /// <summary>
    /// The point of the agent's own history that the session's journal holds up to, opaque to the
    /// service: in a <c>session/prompt</c> result's <c>_meta</c> at the end of every turn the service
    /// ran, and on the update that ends a turn of the agent's own (<see cref="MetaAgentTurn"/>).
    /// </summary>
    public const string MetaHistoryMark = "gorynych/historyMark";

    /// <summary>
    /// What an entry the service writes into a journal says, on a <c>user_message_chunk</c> with
    /// <c>gorynych/origin: "notice"</c>: <c>historyRewritten</c>, where the agent's history no longer
    /// held what the journal showed and was imported again.
    /// </summary>
    public const string MetaNotice = "gorynych/notice";

    /// <summary><c>spawn_session</c> of the service's <c>gorynych</c> MCP server, as agents name it.</summary>
    public const string SpawnTool = "mcp__gorynych__spawn_session";

    /// <summary>
    /// The tools of the service's <c>gorynych</c> MCP server, as agents name them. None needs the
    /// agent's own approval: the service asks the person itself where it must (docs/service-protocol.md,
    /// "Approvals"). The service answers an agent's approval of them (Kimi), and so does the Claude adapter.
    /// </summary>
    public static readonly IReadOnlyList<string> PeerTools =
    [
        "mcp__gorynych__list_agents", "mcp__gorynych__reply", "mcp__gorynych__ask_agent",
        "mcp__gorynych__send_message", "mcp__gorynych__reply_message",
        "mcp__gorynych__request_reply", "mcp__gorynych__list_reply_waits", "mcp__gorynych__cancel_reply_wait",
        "mcp__gorynych__observe_completion_group", "mcp__gorynych__recover_completion_reminder", "mcp__gorynych__request_completion_reminder", "mcp__gorynych__list_completion_notices", "mcp__gorynych__ack_completion_notice", "mcp__gorynych__list_assignments", "mcp__gorynych__read_assignment", "mcp__gorynych__update_assignment", "mcp__gorynych__update_continuation", "mcp__gorynych__list_background_tasks",
        "mcp__gorynych__read_report", "mcp__gorynych__list_reports",
        "mcp__gorynych__archive_session", "mcp__gorynych__restore_session", SpawnTool,
    ];

    /// <summary>
    /// <c>true</c> in the <c>_meta</c> of a mode's value (<c>SessionConfigSelectOption</c> of the
    /// option with category <c>mode</c>, or a <c>SessionMode</c>): in that mode the agent runs
    /// anything without asking the person. The service asks the person before a session that is
    /// not in such a mode asks one that is (docs/adapter-contract.md, §6.2).
    /// </summary>
    public const string MetaFullAccess = "gorynych/fullAccess";

    /// <summary>
    /// On a <c>session/prompt</c> the service sends: the <c>messageId</c> under which the service
    /// journals the prompt's text, for an agent that can keep it with the message (docs/adapter-contract.md, §5).
    /// </summary>
    public const string MetaMessageId = "gorynych/messageId";

    /// <summary>Returns service status: ID, process, adapters, sessions.</summary>
    public const string MethodStatus = "_gorynych/status";

    /// <summary>
    /// The management CLI's safe stop: <c>{agents?: [name], all?: bool, wait?: bool, timeoutSeconds?: number}</c>.
    /// The service first enters maintenance so no new work starts for those adapters, then enumerates
    /// their active work. Without <c>wait</c> it refuses if any exists; with it, waits up to the
    /// timeout and refuses (leaving the service usable) if some is left. On success it unloads idle
    /// sessions and stops the adapters' hosts, and the stopped set is kept in the data folder. With
    /// <c>all</c>, it then exits; bridges will not start it again until <c>_gorynych/start</c>.
    /// </summary>
    public const string MethodStop = "_gorynych/stop";

    /// <summary>
    /// Restores adapters a management stop disabled: <c>{agents?: [name], all?: bool}</c>. Availability
    /// only; a stopped agent starts again at its next use. With <c>all</c> it also clears the
    /// stopped-all marker that keeps bridges from starting the service.
    /// </summary>
    public const string MethodStart = "_gorynych/start";

    /// <summary>Asks the service to exit.</summary>
    public const string MethodShutdown = "_gorynych/shutdown";

    /// <summary>
    /// Asks the service to exit for an update without breaking its clients' work:
    /// <c>{wait?, timeoutSeconds?, force?}</c> → <c>{restarted, processId, waitedSeconds, cancelledTurns}</c>,
    /// or <c>{restarted: false, running: [{sessionId, agent, title}]}</c>. It starts no new turn, waits
    /// for the running ones (unless <c>wait</c> is false) up to the timeout, and exits; with turns
    /// still running it calls the restart off, unless <c>force</c> says to cancel them. A client on
    /// the resumable link reconnects to the next service (docs/service-protocol.md, "The resumable link").
    /// </summary>
    public const string MethodRestart = "_gorynych/restart";

    /// <summary>
    /// A notification to clients, <c>{restart}</c>: the service is restarting (the number of the
    /// restart, in this run). A client on the resumable link holds its new prompts from now on and
    /// sends them to the next service, or to this one when the restart is called off.
    /// </summary>
    public const string MethodRestarting = "_gorynych/restarting";

    /// <summary>
    /// A notification to clients, <c>{restart}</c>: the restart is called off (turns still ran, or it
    /// failed), and new turns start again. A client on the link sends this service what it held.
    /// </summary>
    public const string MethodRestartAborted = "_gorynych/restart_aborted";

    /// <summary>In the <c>data</c> of a <see cref="MetaRestarting"/> error: the number of the restart that refused the request.</summary>
    public const string MetaRestart = "gorynych/restart";

    /// <summary>
    /// In <c>agentCapabilities._meta</c> of the service's <c>initialize</c> result: the version of the
    /// resumable link it speaks (1): positions on updates, <c>session/resume</c> from a position,
    /// the turn state, <see cref="MethodRestarting"/>. A client of a service without it does not reconnect.
    /// </summary>
    public const string CapLink = "gorynych/link";

    /// <summary>The version of the resumable link this build speaks.</summary>
    public const int LinkVersion = 1;

    /// <summary>
    /// On every <c>session/update</c> the service journals, as a client gets it live or on
    /// <c>session/load</c>: the update's position in the session's journal, an opaque string. A
    /// <c>session/resume</c> with <c>replayFrom</c> <c>{"type": "gorynych/after", "mark": "&lt;position&gt;"}</c>
    /// sends what follows it. Also in the <c>session/load</c> and <c>session/resume</c> result's
    /// <c>_meta</c>: the position the client is at once the answer is written.
    /// </summary>
    public const string MetaPosition = "gorynych/position";

    /// <summary>
    /// In a <c>session/resume</c> and <c>session/load</c> result's <c>_meta</c>: <c>"running"</c> while a
    /// turn of the session runs or a prompt for it waits, else <c>"idle"</c>.
    /// </summary>
    public const string MetaTurn = "gorynych/turn";

    /// <summary>
    /// On a <c>session/prompt</c>, client → service: an ID the client gives the prompt, so that after a
    /// reconnect it can tell whether the prompt is still in progress (<see cref="MetaPrompts"/>) and
    /// find the mark of its end. On that mark: the prompt the ended turn answered.
    /// </summary>
    public const string MetaPromptId = "gorynych/promptId";

    /// <summary>
    /// In a <c>session/resume</c> and <c>session/load</c> result's <c>_meta</c>: the
    /// <see cref="MetaPromptId"/>s of the session's prompts still in progress (running, or waiting for
    /// the turn before them).
    /// </summary>
    public const string MetaPrompts = "gorynych/prompts";

    /// <summary>
    /// On the mark that ends a turn (<see cref="MetaAgentTurn"/> <c>"ended"</c>): the turn's ACP stop
    /// reason. Absent means <c>end_turn</c>.
    /// </summary>
    public const string MetaStopReason = "gorynych/stopReason";

    /// <summary>On the mark that ends a turn whose prompt failed: the error, <c>{code, message}</c>.</summary>
    public const string MetaError = "gorynych/error";

    /// <summary>
    /// In the <c>data</c> of an error: <c>true</c> when the service did not carry the request out
    /// because it is restarting, so it may be sent again to the next service as it is.
    /// </summary>
    public const string MetaRestarting = "gorynych/restarting";

    /// <summary>
    /// In the <c>data</c> of an error: the adapter that a management command stopped or is stopping.
    /// No new work of that adapter starts until <c>_gorynych/start</c>.
    /// </summary>
    public const string MetaStopped = "gorynych/stopped";

    /// <summary>In the <c>data</c> of an error: <c>true</c> while a management stop drains the adapter.</summary>
    public const string MetaStopping = "gorynych/stopping";

    /// <summary>In <c>_gorynych/status</c>: the management state (stopped agents, stopped all, draining).</summary>
    public const string MetaMaintenance = "gorynych/maintenance";

    /// <summary>
    /// The version of the link between the conductor and its adapter hosts this build speaks; a
    /// conductor takes hosts from <see cref="MinHostLinkVersion"/> up to it (docs/service-protocol.md,
    /// "Adapter hosts").
    /// </summary>
    public const int HostLinkVersion = 1;

    /// <summary>The oldest host link a conductor of this build still takes.</summary>
    public const int MinHostLinkVersion = 1;

    /// <summary>
    /// A host's first message on a connection to the conductor's host pipe, a notification:
    /// <c>{token, hostId, adapter, link, pid, version}</c>. The token, in the data folder, proves it
    /// is a host of this service.
    /// </summary>
    public const string MethodHostHello = "_gorynych/host_hello";

    /// <summary>
    /// Conductor → host: <c>{}</c> → <c>{sessions, turns: [{sessionId, promptId?}], buffered, requests, seq}</c>:
    /// what the host has while the conductor was away.
    /// </summary>
    public const string MethodHostState = "_gorynych/host_state";

    /// <summary>
    /// Conductor → host: <c>{after: {sessionId: seq}}</c> → <c>{replayed, trimmed}</c>: the host sends
    /// the session updates it kept that follow each session's cursor, then makes the connection its
    /// conductor's and asks its pending questions again.
    /// </summary>
    public const string MethodHostReplay = "_gorynych/host_replay";

    /// <summary>Conductor → host: <c>{}</c> → <c>{}</c>: stop the agent and exit (the service shuts down).</summary>
    public const string MethodHostStop = "_gorynych/host_stop";

    /// <summary>
    /// Host → conductor, a notification: <c>{sessionId, promptId?, result | error}</c>: a prompt whose
    /// conductor went before its answer ended so in the agent.
    /// </summary>
    public const string MethodTurnEnded = "_gorynych/turn_ended";

    /// <summary>
    /// Host → conductor, a notification: <c>{sessionId, askId, result | error}</c>: the agent answered a
    /// question (<c>_gorynych/opinion</c>) a conductor sent with <see cref="MetaAskId"/>. Numbered and
    /// kept as <see cref="MethodTurnEnded"/> is, so the next conductor learns an answer whose conductor went.
    /// </summary>
    public const string MethodAskAnswered = "_gorynych/ask_answered";

    /// <summary>
    /// On what a host sends: <c>"&lt;hostId&gt;/&lt;seq&gt;"</c>, its place in the host's stream, in the
    /// <c>_meta</c> of a session update (journaled with it) or of a notification's params. The conductor
    /// takes each once.
    /// </summary>
    public const string MetaHostSeq = "gorynych/hostSeq";

    /// <summary>In a host's <c>initialize</c> result <c>_meta</c>: <c>{hostId, link, reattached}</c>.</summary>
    public const string MetaHost = "gorynych/host";

    /// <summary>The default local endpoint: one named pipe per user.</summary>
    public static string DefaultPipeName => "gorynych-acp-" + Sanitize(Environment.UserName);

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}
