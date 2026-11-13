# The AHP face

The service speaks [AHP](https://github.com/microsoft/agent-host-protocol) (Microsoft's Agent
Host Protocol) to clients such as VS Code's Agents window, next to ACP for Zed, over the same
conductor ([design.md](design.md#two-faces)). This file is the face's specification. **Status:**
step 1 (reading) built on 13 November 2026 (`src/Gorynych.Service/Ahp`) and checked live in VS Code
1.145.2; step 2 (working in sessions) built on 13 November 2026, its live check to come; the face is
off by default; steps 3 to 5 specified, not built. Each step is closed by a live check.

What the face is, in one paragraph: a translator inside the service. Toward AHP clients it is
a host: it keeps AHP's state tree, sequences actions, serves snapshots and replays. Toward the
conductor it is one more ACP client, on an in-process connection, which uses the methods and
extensions of [service-protocol.md](service-protocol.md) like any other client. The conductor,
the adapters and Zed's path do not change for it, except for the few additions listed under
[What the conductor adds](#what-the-conductor-adds). This is the layering AHP's own guide
describes: a host speaks AHP to its clients and ACP to its agents.

## Version and library

- **AHP 1.0.0**, the current release (`spec/v1.0.0`, 2 October 2026; checked again on 13
  November 2026: no newer release, `main` at `a768e74` says 1.1.0 unreleased). VS Code 1.145.2
  offers `["1.0.0", "0.10.0", "0.9.0"]`, and 1.1.0 only behind the setting
  `chat.agentHost.multipleChatProviders.enabled`.
- The face speaks 1.0.0 and nothing else: an `initialize` that does not offer it gets
  `UnsupportedProtocolVersion` (`-32005`) with `data.supportedVersions: ["1.0.0"]`. A later
  version is taken up as a change of its own.
- **Types and reducers come from Microsoft's NuGet package**
  `Microsoft.VisualStudioCode.AgentHostProtocol` 1.0.0 (MIT), pinned, and with it
  `Microsoft.VisualStudioCode.AgentHostProtocol.Abstractions` 1.0.0. Unlike ACP's and Codex's
  types, which are generated here, these are taken whole, for the reducers: the host must reduce
  state exactly as its clients do, and the package's reducers are the ones the clients run.
  Checked on 13 November 2026 in a Native AOT console (net10.0, win-x64): no trim or AOT
  warnings, and the reducers, the serializer and an action notification worked.
  - Used: the wire and state types; `Reducers.ApplyToRoot`, `ApplyToSession`, `ApplyToChat`
    (they change the state in place and return `Applied`, `NoOp` or `OutOfScope`);
    `Reducers.IsClientDispatchable`; `SystemTextJsonAhpSerializer` and `AhpJson.Options` (the
    source-generated metadata; never the `Serialize(object, Type)` overloads, which are not AOT
    safe); `ITransport` with `TransportMessage`; `AhpErrorCodes`.
  - Not used in the service: `AhpClient`, `MultiHostClient`. The tests use `AhpClient` as the
    client, so the face is checked against Microsoft's own client code.
  - The package has no host side: routing, `serverSeq`, subscriptions, snapshots and the replay
    log are the face's.
  - The states are mutable classes. All reducing happens under the face's one lock, and a
    snapshot is serialized under it, never handed out as an object. Sending is not: each client
    has its own queue, so a slow client delays only itself, and one that takes nothing for 15
    seconds is disconnected, as on the ACP side: forgotten at once, so nothing more is queued for
    it and it watches no session, and its connection ended.
  - The package's `SessionState` requires all its lists (`chats`, `activeClients`,
    `customizations`, `changesets`, `serverTools`, `inputNeeded`; AHP's TypeScript requires only
    the first two), so they are always set, empty if nothing else.
  - Its dependencies, `Microsoft.Extensions.Options` and `…DependencyInjection.Abstractions`
    8.0.2, are already in the service through ASP.NET Core.

## Endpoint

- **Option.** `serve --ahp off|on|<port>`; `on` is port 8766. **Off by default**; whether it is on by
  default, as [design.md](design.md#two-faces) has it, is decided after step 2's live check. `task install`
  takes the option like any service option.
- **Transport.** WebSocket on Kestrel, which the executable has already (the Claude adapter's
  hook server), at `ws://127.0.0.1:<port>/ahp`: loopback only, one JSON-RPC message per text
  frame. Another machine reaches it through ssh ([step 5](#step-5-another-machine)).
- **Access.** AHP leaves the gate to the transport. The face takes the upgrade only with the
  token, as `?tkn=` (what VS Code sends for an entry's `connectionToken`) or
  `Authorization: Bearer`, compared in constant time; otherwise 401 before any AHP message. Any
  other path than `/ahp` is 404, and a request that is no WebSocket upgrade 400.
  - The token is 32 random bytes in hex, in `ahp.token` in the data folder, which is the user's
    alone ([service-protocol.md](service-protocol.md#endpoint)). It is made when the face first
    starts; deleting the file and restarting the service makes a new one.
  - A request whose `Origin` is a web page (`http:` or `https:`) is refused with 403, whatever its token:
    a browser lets any page open a WebSocket to localhost. VS Code sends
    `vscode-file://vscode-app`; other clients send none.
  - A port that another process holds leaves the face off with a warning in the log and in
    `status`; the ACP side runs on. Two users on one machine need two ports.
- **Address.** `Gorynych.Service ahp address [--pipe …]` prints the entry for VS Code's user
  setting `chat.remoteAgentHosts`, `{"name": "Gorynych", "address": "127.0.0.1:<port>/ahp",
  "connectionToken": "<token>"}`, with the port the running service reports. Changing the user's
  VS Code settings stays the user's step.
- **Who it serves.** Whoever has the token gets what the pipe gives: every session, prompting
  every agent, and, through `resource*`, reading the user's files and making folders. The token is the user's, like
  the pipe. The face advertises no `protectedResources`, so VS Code sends it no GitHub
  credentials.

## Identifiers

- **A session** is `ahp-session:/<id>`, where `<id>` is the Gorynych session ID as a
  percent-encoded path segment. A session created through AHP keeps the URI its client chose:
  the face passes it to `session/new` as `_meta["gorynych/ahpUri"]`, the overlay keeps it, and
  `session/list` returns it in `SessionInfo._meta` ([What the conductor adds](#what-the-conductor-adds)),
  so the face names it so after a restart too.
- **A chat** is `ahp-chat:/<id>`, with the same `<id>` as its session's URI for the session's
  own conversation, and the child's session ID for a child ([Children](#children)).
- Only root sessions are AHP sessions. A child session (one with
  `_meta["gorynych/parentSessionId"]` whose parent exists) is a chat in its root's AHP session,
  however deep it is; it never has an `ahp-session:` URI of its own.
- **A turn** is the `gorynych/promptId` of its prompt when it has one, else the `messageId` of
  its user message, else `agent-<position>` for a turn the agent began itself, `<position>`
  being the `gorynych/position` of its first update.
- **Folders** are `file:` URIs (RFC 8089; AHP 1.0.0 says nothing more of them). (AHP's
  `environment.pathStyle` is newer than 1.0.0.)
  - The face writes the service's `C:\Users\x` as `file:///C:/Users/x` (`/home/x` as `file:///home/x`),
    each segment percent-encoded as UTF-8 but its unreserved characters, `%` among the rest: .NET's `Uri`
    leaves a `%41` in a path as it is, which a reader takes for `A`. VS Code parses a host's URI and
    compares URIs as URIs, its drive letter and encoding normalized, so this matches its own form.
  - It reads a client's URI by RFC 8089 itself, not with .NET's `Uri`, which sees no drive in VS
    Code's `c%3A`. `file:///c%3A/Users/x` (VS Code's form) and `file:///C:/Users/x` are both
    `C:\Users\x`: the path percent-decoded as UTF-8, the drive letter upper-cased as Windows writes
    it (an agent keeps a folder's sessions by its path), no trailing separator but a root's.
    `file://localhost/…` is local.
  - Refused: another machine's `file://host/share/…`, a URI with a query or a fragment, and on
    Windows one with no drive. A host serves its own files, and an agent's working folder is a
    local one: Windows tools take no UNC working folder, and opening one sends the user's
    credentials to that machine.
  - A file a message attaches goes to the agent in the face's form, as Zed's do.

## Connection

- `initialize`: version as above; `serverSeq`, the face's current number; `serverInfo`
  `{name: "gorynych", version: <build>}`; `_meta["gorynych/serviceId"]`; `defaultDirectory`, the
  user's profile folder; `snapshots` for `initialSubscriptions`. The client's `clientId` is
  remembered for `reconnect` while the service runs. It does not wait for the list, which is as
  slow as the slowest agent's (about 16 s live): the face answers it once it knows the agents, and
  the sessions follow as `root/sessionAdded` as the first list comes. `listSessions` waits for that
  list, 30 s at most, then answers what the face has; it does not hold up what follows it.
- `reconnect`: from the replay log, when it still holds everything after `lastSeenServerSeq`
  for the client's subscriptions, else snapshots; `missing` lists subscriptions whose channel is
  gone. A `clientId` the face does not know (the service restarted) gets `NotFound` (`-32008`),
  on which VS Code initializes afresh (seen in the probe). The root's notifications carry no
  number, and AHP replays none: the face keeps the last 10,000 with the number of the action
  before each, and a replay sends again, after its answer, those from `lastSeenServerSeq` on (each
  says how a session is now, so one the client had does no harm). When it no longer has them all,
  the answer is snapshots.
- The **replay log** holds the last 10,000 action envelopes. The states are not persisted: after
  a restart the face builds them again from the conductor.
- `ping` answers `{}` before `initialize` too. `subscribe` answers a snapshot, `unsubscribe`
  drops the channel. `serverSeq` is one counter across all channels.
- Unknown methods, and those of what the face does not serve ([Not in the face](#not-in-the-face)):
  `-32601`. Before `initialize` or `reconnect`, any method but `ping` gets `-32600`. A connection's
  requests are answered in order, except `subscribe`, which may wait for its session to open, and
  `listSessions` before the first list, which do not hold up what follows them. A `subscribe` counts
  from when it arrives: an `unsubscribe` after it takes it back, even while its session opens (its
  answer still comes). An answer that carries state (a snapshot, a replay, the list) is queued
  with that state under the face's lock, so no action newer than it reaches the client before it.
  `disposeSession` does not hold up what follows it either. The commands it does not carry out are
  answered as [step 1](#step-1-reading) and [step 2](#step-2-working-in-sessions) say. Unknown
  channels: `SessionNotFound` (`-32001`) for a session, chat, terminal or other URI the face does not
  serve (VS Code subscribes to `<session>/annotations` and takes this calmly). The notification
  `setClientManagedSettingsPermissions` that VS Code sends is not AHP and is ignored.
- Client actions arrive by `dispatchAction`. The face checks `IsClientDispatchable`, then the
  channel's rules (below), applies an accepted action through the reducer and echoes it with its
  `origin`; a refused one is echoed with `rejectionReason`. An action for a channel that does not
  exist is dropped without an echo, as AHP says. A refused action takes a `serverSeq` and goes into
  the replay log like an applied one.

## The internal client

- One ACP connection for the whole face, through `Conductor.ServeClientAsync` over an in-memory
  duplex stream, named `ahp`. Every AHP client shares it, as they share AHP's state.
- Its `initialize` `_meta`: `gorynych/link: 1`, so its prompts carry `gorynych/promptId` and
  their ends are marked. From step 2 it takes approvals and forms, and declares
  `clientCapabilities.elicitation.form` (in step 1 it said `gorynych/requests: false`, so the conductor
  sent it none) and `clientCapabilities.session.configOptions.boolean`, as it sets boolean options.
- **Sessions it opens.** It loads a session (`session/load`) when an AHP client first subscribes
  to one of its chats, or to the session's own channel (so that the session's snapshot has its agent's
  options), builds the chat state from the replay, and answers the subscription when the load answers. It closes the session (`session/close`) when no AHP client has subscribed to
  any of its chats for two minutes, so a reconnect finds it still open and gets a replay, and
  never while any turn of the session runs, nor while its agent waits for an answer here. When the face is the session's last client, a close
  cancels whatever client's turn runs ([service-protocol.md](service-protocol.md#standard-methods)),
  Zed's too, if Zed has quit meanwhile; so the face waits for the turn's end. Its own chat state
  says whether a turn runs, since it sees every start and end ([Turns](#chats)). The same holds for
  a session the face lets go of for another reason (it left the list, or became a child): if a
  turn of it runs, it stays open until that turn ends. The face's loads and closes of a session
  reach the conductor in the order it decided them, so a quick reload never comes before the
  close it follows.
- **Stopping.** The face sends out each client's queue and closes its socket, then ends it, before
  the endpoint stops; the endpoint waits at most 2 s for what still runs. A window that keeps its
  connection open so holds up no restart (it held one up for Kestrel's 30 s in the live check).
- **The list.** It reads `session/list` with `_meta["gorynych/includeArchived"]: true`, every
  page, from its start on, without holding up `initialize`; on each `_gorynych/sessions_changed` it
  reads it again (at most every 250 ms) and turns the difference into AHP notifications and
  actions.

## Root channel

- `agents`: one `AgentInfo` for each entry of the conductor's `gorynych/agents`: `provider` is
  the adapter's name (`claude`, `codex`, `kimi`, `dsh`), `displayName` its display name,
  `description` "`<displayName>` through Gorynych", `models` the models the face has seen for that
  agent ([step 2](#step-2-working-in-sessions)), with `root/agentsChanged` when they change, and
  `capabilities.multipleChats: {}` without `fork` or `sideChat`, as the probe's host advertised
  when the window showed its child chats. VS Code 1.145.2 needs it: its sessions bundle shows a
  session's chats beyond the default one only when the agent has `multipleChats`, or when a chat's
  `origin` is `tool` or `sideChat`, and children are `user` chats until step 4 ([Children](#children)).
  The capability also makes the window offer New Nested Session (Ctrl+T) and Show Chat Tabs on
  each session; the face refuses `createChat`. Once children are `tool` chats the capability can
  go, and the offer with it. Step 1 also checks whether the window treats the names `claude` and
  `codex` as its own built-in providers; if it does, they become `gorynych-<name>`.
- `config`: clients may dispatch `root/configChanged` (VS Code pushes its settings there); the
  face keeps the values in memory and acts on none.
- `terminals`: the face's terminals (below), kept with `root/terminalsChanged`. No `activeSessions`.
- **Terminals.** VS Code 1.145.2 opens a terminal on the host for the session it shows
  (`workbench.contrib.sessionsTerminal` calls `createTerminal` for every connected remote host, with
  no capability or setting in between), and AHP 1.0.0 has no way to say a host has none; in step 1 the
  window showed "The terminal process failed to launch: Method not found: createTerminal" for each
  session it opened. From step 2 the face serves a terminal that runs nothing:
  - `createTerminal` at the URI the client chose (VS Code's is `agenthost-terminal:/<uuid>`) makes one
    whose state is one `unclassified` line, "This host has no shell: Gorynych runs agents here, not
    terminals.", `lifecycle: {status: "exited"}` with no exit code from the start (no process ever ran),
    `isPty: false`, and the client's claim. `root/terminalsChanged` adds it to the catalog (and says a
    title or claim a client changes), `disposeTerminal` takes it out (`NotFound` for one the face does
    not have); a terminal with no URI is `InvalidParams`. Past 32 terminals the oldest no client
    subscribes to goes, but never one younger than 30 s (its client subscribes only once its creation
    is answered), the one being made above all; one a client shows stays until it is disposed. A
    client's terminal actions go through the reducer; `terminal/input` reaches nothing.
  - What VS Code makes of it (its bundle's `AgentHostPty`): it creates the terminal, subscribes, writes
    the content and signals ready. It ends a terminal on a `terminal/exited` action, not on the state
    it subscribed to, so the tab stays with the line and no toast; an exit with no code (or 0) never
    shows an error (`parseExitResult`), and an exit action would close the tab at once, unseen.
  - No loop: the window makes a terminal when the session it shows changes (keyed by folder, host and
    session), not when a terminal goes; closing the tab sends `disposeTerminal`.

## Sessions

`listSessions` serves root sessions, newest `modifiedAt` first, with a keyset cursor; `limit` is
honoured up to 200. A `SessionSummary`:

| Field | From |
| --- | --- |
| `resource` | the session's URI ([Identifiers](#identifiers)) |
| `provider` | `_meta["gorynych/agent"]` |
| `title` | `title`; for a session without one (DSH lists none, a Kimi session before its first prompt) `<the agent's display name> session <id8>`, `<id8>` the first eight characters of its ID after a word that only names its kind (Kimi's `session_`), the same each time: VS Code shows an empty title as `Session <id8>` at first and as a blank row after later updates (live check) |
| `status` | activity bits (below), plus `IsRead` and `IsArchived` |
| `workingDirectories` | `[cwd]` |
| `createdAt`, `modifiedAt` | until the conductor reports a creation time: `createdAt` the first `updatedAt` the face saw (it keeps it, rather than move it with each change), `modifiedAt` the latest of `updatedAt` and its chats' times. A session whose agent says no time is as old as can be (the Unix epoch), not as new as the moment the service first saw it, which made old sessions look new and sort first (live check); DSH's adapter says the time DSH last wrote the session |
| `chats`, `defaultChat` | the session's own chat first, then its descendants' chats; the own chat is the default |
| `_meta` | `gorynych/sessionId`, `gorynych/agent` |

- **Activity bits** (`Idle` 1, `Error` 2, `InProgress` 8, `InputNeeded` 24): from the chats the
  face has open, aggregated as AHP's session channel says (`InputNeeded` and `Error` promoted from
  any chat). A session the face does not have open is `Idle`; whether `session/list` should say
  that a turn runs is left for later. AHP 1.0.0 has no action that sets a session's activity, and the
  package's session reducer does not aggregate it from the chats, so the activity bits are in the
  summaries (`listSessions`, `root/sessionSummaryChanged`) and in the chat catalog only; the session
  channel's own `status` keeps the read and archive bits.
- **`IsArchived`** (64) is `gorynych/archived`.
- **`IsRead`** (32) lives in the face's memory, for each session and each chat, and is not
  persisted. Everything is read when the face starts. AHP's chat reducer clears a chat's bit
  when a turn starts (and on `chat/inputRequested`); the face clears the session's bit when a turn
  starts and when the agent asks something of the face (an approval or a form). A replay would clear it for every past turn, so after building a chat from a replay
  the face sets the bit back to what it had.
- **Notifications on `ahp-root://`.** A new root session: `root/sessionAdded` (one a client creates,
  once it is ready: nothing is said of it, nor listed, while it is made). A deleted one:
  `root/sessionRemoved`. A changed one (title, archive, `modifiedAt`, its chats): one
  `root/sessionSummaryChanged` with the changed fields only. A new child: `session/chatAdded` on
  its root's channel and a `root/sessionSummaryChanged` with the new `chats`.
- **The chat catalog stays in step with the chats**, as AHP requires of the host: whenever a
  chat's summary fields change (title, `status` with its read, archived and activity bits,
  `activity`, `modifiedAt`, `interactivity`), the face sends `session/chatUpdated` on the
  session's channel and the compact `chats` in `root/sessionSummaryChanged`. A partial update
  cannot remove a field, so a cleared activity goes as the empty string. The chat reducer moves
  `modifiedAt` only when a turn starts or ends, not with each update. AHP 1.0.0 has no
  action for a chat's own title, so a `ChatState.title` already sent stays until the client
  subscribes again; the catalog has the new one at once.

**The session channel.** Its `SessionState` repeats the summary and adds `lifecycle: "ready"`,
the full `chats` catalog, `defaultChat`, the clients' `activeClients` (from
`session/activeClientSet`; the tools they offer are not offered to agents), the agent's options as
`config` and what the agent waits for in `inputNeeded` ([step 2](#step-2-working-in-sessions)), and
empty customizations, changesets and server tools. A session a client creates is `creating` until its
agent has it.

Client actions on a session:

| Action | What the face does |
| --- | --- |
| `session/isArchivedChanged` | echoed at once, then `_gorynych/archive {sessionId, archived}` in an order of the session's archives own (an archive and an unarchive reach the service as they came, and no prompt or cancel waits for one), its session's ID taken then; one the service refuses goes back by the host (see below) |
| `session/isReadChanged` | applied in memory |
| `session/titleChanged` | refused (`Renaming is not supported yet`) until renaming reaches the native side ([design.md](design.md#sessions-catalog-titles-archive), planned) |
| `session/activeClientSet`, `session/activeClientRemoved` | applied |
| `session/configChanged` | echoed at once, then set in the agent; a value the agent refuses goes back by the host ([step 2](#step-2-working-in-sessions)) |
| anything else | refused |

A change that waits for the service or an agent is echoed at once rather than once done: an AHP client
matches the echoes of its pending actions in the order it sent them, and a later action of the same
client must not be echoed first. One the service or the agent then refuses goes back with an action of
the host's own (no `origin`), which the client takes as any server change; the refusal is logged.

## Chats

A chat's `ChatState` is built from the session's journal and kept live from its updates. The
conductor sends the face ACP `session/update`s; the face turns each into AHP actions, reduces
them, and broadcasts them to the chat's subscribers. While a load replays, the actions are reduced
into a fresh state without `serverSeq`; only live actions are numbered.

**Turns.**

- A `user_message_chunk` with a new `messageId` begins a turn: `chat/turnStarted` with the turn's
  ID, the message's text, and the update's `gorynych/origin`, `gorynych/sender`,
  `gorynych/fromSessionId` and `gorynych/opinion` in the message's `_meta`. AHP cannot add to a
  message once its turn started, so the face holds the chunks of one message until another kind
  of update comes, the replay ends, or 100 ms pass. Non-text blocks are left out of the message
  in step 1.
- The message's `origin.kind` says who wrote it, from `gorynych/origin`: `agent` for another
  agent's text (`peer`, `channel`, `live`, `report`) and for a parent's task (`spawn`);
  `systemNotification` for the service's own entry (`notice`); `user` otherwise (a client's
  prompt, `terminal`, `native`).
- `startedAt` is the time live. The journal keeps no times, so a replayed turn gets the session's
  `updatedAt`, and its `duration` is 0: the times of past turns are unknown, and a chat built from
  a replay has its session's time as `modifiedAt`.
- An update of the agent with no turn open, or `session_info_update` with
  `gorynych/agentTurn: "started"`, begins a turn of the agent's own: an empty message with
  `origin.kind: "agent"`. One an update began, with no mark of its start (a late chunk, an agent's
  work after a cancel), has no mark to end it either: it ends once the session has been quiet for
  10 s.
- The end mark (`session_info_update` with `gorynych/agentTurn: "ended"`) ends the turn it is
  for: a prompt's by the same `gorynych/promptId`, a turn of the agent's own by a mark without
  one; a late mark of a turn a newer one ended already ends nothing. It ends it so: its
  `gorynych/stopReason` `cancelled` as `chat/turnCancelled`, `gorynych/error` as `chat/error`
  with an error part (`errorType` the code, `message`), any other as `chat/turnComplete`. A new
  user message ends a turn still open as complete; so does a load whose answer says
  `gorynych/turn: "idle"`. At a turn's end the face clears the chat's `activity`; AHP's reducer
  itself cancels a tool call still running then.
- Every turn the face sees begin has a mark of its end: a client's prompt (Zed's bridge gives each
  an ID, and the conductor gives a prompt without one an ID of its own), the agent's own turns, and
  the service's own turns, which the conductor marks from step 1 on.

**Within a turn.**

| ACP update | AHP action |
| --- | --- |
| `agent_message_chunk`, text, a message not yet open | `chat/responsePart` with a `markdown` part whose `id` is the `messageId` (with `#n` when that ID comes back after another part) |
| `agent_message_chunk`, the open message | `chat/delta` |
| `agent_thought_chunk` | `chat/responsePart` with a `reasoning` part, then `chat/reasoning` |
| `tool_call` | `chat/toolCallStart` (`toolName` the ACP `kind`, `displayName` the title), then `chat/toolCallReady` with `invocationMessage` the title, `toolInput` the `rawInput` as JSON and `confirmed: "not-needed"`, so the call is running |
| `tool_call_update` with `content` | `chat/toolCallContentChanged` with the whole content, as ACP's update replaces it |
| `tool_call_update`, status `completed` or `failed` | `chat/toolCallComplete` with `success`, `pastTenseMessage` the title, and the content |
| `plan` | `chat/activityChanged` with the entry in progress (AHP has no plan); the plan itself is not shown |
| `available_commands_update` | nothing in the chat: the face keeps the list for `completions` |
| `current_mode_update`, `config_option_update` | nothing in the chat: the session's `config` and the agent's models ([step 2](#step-2-working-in-sessions)) |
| `usage_update` | `chat/usage` for the open turn ([step 2](#step-2-working-in-sessions)) |
| `session_info_update`, title | for the session's own chat `session/titleChanged` and `session/chatUpdated`; for a child's, `session/chatUpdated` |
| `session_info_update`, `gorynych/archived` | `session/isArchivedChanged`, or for a child `chat/isArchivedChanged` |
| `session_info_update`, `gorynych/interactivity` | `session/chatUpdated` with `interactivity`; the reason in the chat state's `_meta["gorynych/interactivityReason"]`, for later snapshots, since `PartialChatSummary` has no `_meta` |

Tool content: ACP `content` blocks with text become `text` results; a `terminal` becomes text
naming the terminal, as the face serves no terminal of an agent's; a resource link a `resource`. A
`diff` is text (`Edited <path>`, then its lines with `-` and `+`, or their counts past 200
lines): AHP's `fileEdit` names its two sides by `ContentRef` URIs a client reads with
`resourceRead`, and a diff's old text is in no file. It becomes a `fileEdit` once the face has a URI
scheme of its own for the two sides (not in step 2).

Client actions on a chat: `chat/isReadChanged` and `chat/draftChanged` are applied;
`chat/isArchivedChanged` on a child's chat archives the child; from step 2 the turns, queued
messages, confirmations and answers of [step 2](#step-2-working-in-sessions); everything else is
refused (`This chat is read-only` in a read-only chat, else `This chat takes no such change`). A chat
is `full`, except a child's (until step 4) and a read-only session's (`gorynych/interactivity`); in
step 1 every chat was read-only.

## Children

A child is a chat in its root's session (AHP's tool-spawned worker chat); the probe showed the
window opens such a chat from its card and prompts it when it is not read-only.

- Its `ChatSummary` has the child's title and status; its `origin` is `{kind: "tool", chat,
  toolCallId}` once the conductor records the spawn's tool call (step 4), and `{kind: "user"}`
  until then. The probe saw only `tool` chats, which stay out of the session's tabs until opened
  from their card. A `user` child shows nested under its session in the list and as a second,
  read-only tab (live check of step 1), because the agents advertise `multipleChats`
  ([Root channel](#root-channel)).
- The spawn's tool call in the parent's chat gets a `subagent` result whose `resource` is the
  child's chat, once that tool call is known.
- A child of another agent than its root: AHP 1.0.0 has no per-chat `provider` (1.1.0 adds it,
  and VS Code honoured it even at 1.0.0 in the probe). Settled in step 4.

## What the conductor adds

Small additions to the ACP side, each in [service-protocol.md](service-protocol.md) when built:

- **Step 1** (built). `gorynych/requests: false` in a client's `initialize` `_meta`: the conductor
  routes no agent → client request (approval, form) to that client. And the end mark for service
  turns too (an opinion, a report, a spawn's first prompt delivered as a turn): each gets a prompt ID
  of the service's own, `service-<messageId>`, on its user message and its prompt, and its end is
  marked with it as a client's prompt's is; one that goes on in a host a restart leaves running is
  marked by the next service, from the host's report. A client's prompt without an ID gets one of
  the service's own too, `prompt-<uuid>`, so every prompt's end is marked.
- **Step 2** (built). `gorynych/ahpUri` on `session/new`, kept in the overlay and returned in
  `SessionInfo._meta` and the `session/new` answer. And a change of a session's options one client
  makes (`session/set_config_option`, `session/set_mode`) reaches its other clients as a
  `config_option_update` (`current_mode_update`), not journaled: the agent answers only the client that
  asked, so Zed showed the old value of a change made in VS Code.
- **Step 3.** An approval or form goes to every attached client; the first answer wins and the
  others get `$/cancel_request`.
- **Step 4.** The spawn's tool call ID recorded with the child (`gorynych/parentToolCallId`)
  where the conductor can tell it.

## Steps

Each step is a branch, reviewed and checked live in an isolated VS Code (`--user-data-dir`, the
face on its own port and data folder) before the next one starts.

### Step 1: reading

- **Scope.** The endpoint, the token and `ahp address`; `initialize`, `reconnect`, `ping`;
  `listSessions` and the root notifications; `subscribe` and `unsubscribe` for root, sessions and
  chats; chat states from the journal and live from the mapping above; children as read-only
  chats; the client actions listed as applied. The conductor's step-1 additions.
- **Commands it does not carry out yet**, which the window may send:

  | Command | Answer in step 1 |
  | --- | --- |
  | `createSession`, `disposeSession` | `PermissionDenied` (`-32009`), `Creating and deleting sessions comes in a later version` |
  | `createChat`, `disposeChat`, `moveChat` | `PermissionDenied`, `This host has one chat per session and its children` (in every step) |
  | `resolveSessionConfig` | an empty schema and the values given, as the probe's host answered |
  | `completions`, `sessionConfigCompletions` | no items |
  | `fetchTurns` | nothing to load: the face never sets `turnsNextCursor` |
  | `resource*`, `createResourceWatch` | `PermissionDenied` (step 2 serves the reading ones) |
  | `authenticate` | accepted and ignored: no agent needs it |

- **Limits of this step.** A snapshot holds the chat's whole history (a journal keeps at most
  5000 entries; the window asks for no `view.turns`). While the face takes no requests, an
  approval waits for an ACP client (Zed) to attach, and VS Code shows its tool call as running.
  Opening a session shows the terminal error ([Root channel](#root-channel)), and each session
  offers New Nested Session, which the face refuses.
- **Tests.** The mapping as unit tests (`AhpChatModelTests`): sequences of ACP updates in,
  actions and reduced state out. The face end to end (`AhpFaceTests`) with `AhpClient` over a
  WebSocket against a service with the test adapters: the list and its pages, subscribing to a
  session and a chat built from its history (read as it was), a live turn with a tool call and its
  catalog and summary, an archive both ways, a dropped connection replayed, an unknown client after
  a restart, a web page and a missing or wrong token refused, a session never closed while its turn
  runs, the commands of later steps, the client actions, a child as a read-only chat, a service
  turn's end, and a client that takes no requests. After the review: an answer with state never
  behind a newer action, an unsubscribe while its session opens, a client that takes nothing let
  go of, a prompt without an ID ending its turn, a close and a reload at once, a session that
  leaves the list under its turn, the root's notifications on a reconnect, a turn no mark ends,
  the face stopping at once with clients on it, untitled and timeless sessions, and the first list
  holding up neither `initialize` nor `ping`. Built and checked on 13 November 2026, the AOT build
  too, live on a port of its own.
- **Live check.** VS Code lists the same sessions as Zed (count, titles, archived); a Claude
  session with tool calls reads well; a turn run from Zed streams into VS Code as it runs; an
  archive in either shows in the other; a service restart leaves VS Code working after it
  initializes afresh. Also checked: whether the window takes `claude` and `codex` as its own.

### Step 2: working in sessions

Built on 13 November 2026. Its live check (VS Code 1.145.2, the same day) and an independent review
found what the second round fixed: VS Code's form of a `file:` URI (L1), the model picker (L3), and
the review's items below; an approved tool call VS Code no longer shows once done (L2) is VS Code's
way, explained under "Limits". A re-check and a third review (13 November) found the rest fixed in a
third round: the model choice, the terminal limit, file URIs, asks tied to their turn, a new session's
early updates. A fourth round: agents never seen selectable (L5), a new session's later updates after
its answer, a queued message's model, and smaller items. A fifth: a client's draft model is its user's
pick (the fourth round's re-offer of a "stale" one went), a state file it cannot read, smaller items.

- **Creating and deleting.** `createSession`: the session at the URI its client chose, `creating` at
  once, with its own chat (`ahp-chat:/<id>`, the same `<id>`) open from the start; `session/new` in the
  agent its `provider` names, in the first of its working directories the face serves (none named, the
  user's profile folder; named but none served, the command fails with `InvalidParams`), with
  `gorynych/ahpUri`; then the options its client chose (below); then `session/ready` and
  `root/sessionAdded`; or `session/creationFailed` with the error. A provider the face does not have is
  `ProviderNotFound`, a URI it has `SessionAlreadyExists`. A turn a client starts while the session is
  being made waits for it. `listSessions` and the root leave out a session until it is ready. Until the
  conductor lists it the session is a draft: the face keeps it. The conductor lists it once its first
  prompt has made it a session (its promotion); a list read before that does not take it away, nor its
  chat, nor what its agent asks. Closing a draft never prompted (two minutes with no AHP client on it)
  discards it, as a draft Zed made. What the conductor passes on of a new session after its answer but
  before the face has taken the answer (its commands; its title; its options: Claude's adapter says the
  model Claude reports so, Codex's its settings) is kept for it, and taken after the answer's own
  options, being newer. (What an agent says before its answer the conductor passes on to no client.) `disposeSession` is `session/delete`, which
  deletes the session in its agent where the agent can, as a delete from Zed does, then
  `root/sessionRemoved`; the children the face had open, which a delete leaves, are closed.
- **Folders.** On `file:` URIs: `resourceList` (folders first, then files, each by name),
  `resourceResolve` (a file, a folder, or a link when asked not to follow it; followed, the real path,
  each link on the way resolved, a link's target and its parents too, is its URI), `resourceRead` (UTF-8 text when the file is valid UTF-8
  and base64 was not asked for, else base64; at most 16 MB) and `resourceMkdir` (with its parents). They
  run off the client's connection loop, at most four at once for a client, so a slow disk holds up none
  of its other messages. A `file:`
  URI the face does not serve (see "Identifiers"; on Windows a path must be a drive's, never a device's
  or another machine's) is `PermissionDenied`; another scheme is `NotFound`.
  `resourceWrite`, `resourceCopy`, `resourceMove`, `resourceDelete`, `resourceRequest` and
  `createResourceWatch` are `PermissionDenied`.
- **Configuration.**
  - A session's `config` is its agent's ACP config options: each select or boolean option by its ACP ID,
    with its name as `title`, its choices as `enum` (groups flattened) with their names and descriptions,
    and `sessionMutable`; but not the option of category `model`. An agent with ACP modes and no config
    option of category `mode` has its modes as `mode`.
  - `session/configChanged` from a client is echoed at once (see [Sessions](#sessions)), then sets each
    value in the agent (`session/set_config_option`, or `session/set_mode` for modes); a key or value
    the session does not have, or a `replace`, is refused; a value the agent refuses goes back. The options the agent
    says later (`config_option_update`, `current_mode_update`, the answer to a change) change the values
    with `session/configChanged`. AHP 1.0.0 has no action for a config's schema: one that changes reaches
    a client when it subscribes again.
  - The model is AHP's own: the choices of the model option are the agent's `AgentInfo.models` (`id`
    the ACP value, its name, its description in `_meta`), from the latest session of that agent the face
    has seen, and `root/agentsChanged` says when they change.
  - **Never none.** VS Code disables an agent whose `models` is empty ("No models are available for this
    agent"; live check, right after a start, for every agent but the one the window had made a draft
    of). So the face keeps each agent's models, and its options' schema, in its own state file in the
    data folder (`ahp-face.json`), written a second after they change (off the face's lock; changes close
    together are written once: Claude's options change with its model) and read at its start, where a
    file it cannot read, or not as it writes one, is none and is written anew; and an agent it knows
    nothing of has one model, `gorynych/agent-default` ("Agent's default"), which is no value of any
    agent's model option, so the face never sets it on a session: the agent keeps its own default.
    ACP has no way to learn an agent's models without a session, and a session made only to ask would
    be a session in the agent's own list (Claude's and Codex's keep every one), so the face makes none.
  - The session's model is in its chat's draft (`chat/draftChanged`, the draft's text left alone), as
    VS Code's model picker takes a chat's model from its draft (else from its last turn's message, else
    its own default, its first model: "Default (recommended)" for Claude). The face puts it there when the
    chat has no draft and when the model changes, from any client.
  - VS Code's picker shows the draft's model and follows it live (its live check: a change made in Zed
    showed in the window at once), and it sends its model with every message: the session's own, unless
    its user picked another. A message's `model` is a pick when it is another than the session's model
    when the message was written: sent (`chat/turnStarted`), or queued (`chat/pendingMessageSet`, whose
    model is compared with the session's then, not when it starts, so a change made elsewhere while it
    waits stands). A pick is set with `session/set_config_option` before its prompt when it is one of
    the session's models and not its current one; a set that fails ends the turn with the error. While
    the session's model is not known (its agent said none) no model is set: the window's fallback is no
    pick. Nothing is remembered of a pick: once set it is the session's model, which the draft offers
    again; a set that failed leaves the session's, so the same pick again is set again. A window that
    reloads, or a second window, reads the same draft.
  - A client's draft (`chat/draftChanged`) keeps the model in it: AHP's draft carries what its user
    picked, the model offered before among the rest (a user who picks it again after a change elsewhere,
    or Claude's "Default" after Claude reported its model). A cleared draft (VS Code clears it on a send)
    gets the session's model again. Accepted: a draft a client built in the milliseconds before it took a
    new offer of the face's carries the model before; a message sent from it sets that model back.
  - **Before a session exists** (the open question of this step; where an agent's options depend on its
    model, as Claude's modes do, the schema of the last session seen, whatever its model): `resolveSessionConfig` answers the
    options of the latest session of that agent the face has seen (kept across restarts; none before
    it has ever seen one), with only the values the client gave that those options take. A new session gets
    its agent's own defaults, and the values the client chose are set on it after `session/new`, before
    `session/ready`. Not a draft session to ask: VS Code asks again at each change in its picker, and a
    draft would start the agent each time.
  - Usage: ACP's `usage_update` (how much of its context the agent's model holds; Codex sends it) is
    the open turn's `chat/usage`: the tokens in context as `inputTokens`, the context's size and the cost
    in `_meta` (`gorynych/contextSize`, `gorynych/cost`). Claude and Kimi report none, and ACP's stable
    answer to a prompt has no usage.
- **Prompts.**
  - `chat/turnStarted` from a client is applied and echoed, and becomes `session/prompt` with the turn's
    ID as its `gorynych/promptId`, whose end mark ends the turn. It is refused while a turn runs (the
    reducer would replace that turn: the message goes to the queue), for a message whose origin is not
    `user`, and in a read-only chat. A prompt that fails before its agent marks its end (a restart
    refuses it) ends its turn with the error. The prompt is the message's text, a `resource` attachment
    as a `resource_link`, and a `simple` one's `modelRepresentation`; other attachments are left out.
  - `chat/turnCancelled` is applied, and becomes `session/cancel`; what the agent still asks of the face
    is answered as cancelled, as ACP has a client do when it cancels. What the face sends the conductor
    for a session goes in the order it decided it (prompts, cancels, option changes), so a cancel never
    overtakes the prompt it follows (one waiting for its session to be made, or for its model to be set),
    which the conductor would then run: it cancels only the prompts that came before a cancel. The turn is over for its clients at
    once; what its agent still sends of it is left out until the end mark of its prompt comes (or
    another turn's user message, or 10 s of quiet).
  - Queued and steering messages (`chat/pendingMessageSet`) stay in the chat (with the session's model
    then, for their own model's sake, above), and the face starts the
    next when no turn runs: `chat/pendingMessageRemoved`, then `chat/turnStarted` with `queuedMessageId`,
    then the prompt; a steering message first (the face cannot put one into a running turn, so it goes
    next), then the queue in order, after a turn however it ended. The face holds them rather than send
    each at once for the conductor's queue: a prompt waiting there for the turn before it cannot be
    withdrawn, and a `session/cancel` cancels every prompt its client sent before it, so a cancel in
    VS Code would take its queued messages with it. `chat/pendingMessageRemoved` and
    `chat/queuedMessagesReordered` from a client change the queue.
  - Slash commands: `completions` on a chat whose text up to the cursor is `/` and the start of a word
    gives the agent's commands (its `available_commands_update`) whose names begin so: `insertText`
    `/<name> `, the range from the start to the cursor, and a `simple` attachment labelled `/<name>`
    with `gorynych/command` in its `_meta`, which the prompt leaves out, the command being in its text.
- **Approvals and forms.** The internal client takes them, and declares form elicitation.
  - An approval (`session/request_permission`) puts its tool call (started, if the agent had not said
    it yet) back to `pending-confirmation` (`chat/toolCallReady` without `confirmed`, the ACP options as
    `options`: `allow_*` approve, `reject_*` deny), and into the session's `inputNeeded` as a
    `toolConfirmation`. `chat/toolCallConfirmed` answers it with the option chosen, else the agent's
    first `allow_once` (`allow_always`) or `reject_once` (`reject_always`), as the client approved or
    denied; one for a tool call that waits for none is refused.
  - A form (`elicitation/create`) is `chat/inputRequested` with a question for each field (a string
    with `enum` or `oneOf` a single choice, another string text with its format and lengths, a number or
    an integer with its bounds, a boolean, an array of choices a multiple choice), and in `inputNeeded`
    as a `chatInput`. `chat/inputAnswerChanged` keeps a draft; `chat/inputCompleted` answers it:
    `accept` with each answered field's value (a skipped one left out; refused while a required one has
    no submitted answer), `decline` or `cancel`. A form of another kind (a URL to open), or of no
    session, is declined.
  - A request the agent withdraws is withdrawn here too: a tool call back to running
    (`confirmed: "not-needed"`), a form completed as `cancel`, and out of `inputNeeded`. What the agent
    asks about a session the face does not show (it let it go, or could not open it) is answered as
    cancelled, so the agent never waits for nobody; one that comes while the face opens the session is
    shown once it is open. What the agent asks while a turn a client cancelled settles is answered as
    cancelled at once, as ACP has a cancelling client do; no turn opens for it. An ask is shown as it
    arrives, on the internal connection's read loop and in the order of its stream (`RequestArrived`), not
    when its handler runs on the thread pool: it belongs to the turn it came in. The face does not close a
    session whose agent waits for an answer here.
  - Routing, until step 3: a request goes, as the conductor routes any, to the client attached to the
    session last: VS Code only when the face opened (or created) the session after any ACP client did,
    else Zed. Neither sees the other's approvals.
- **Chats** are `full`, except a child's (until step 4) and a session's its agent opened for reading
  only ([Chats](#chats)). `multipleChats` stays: the step-1 check showed children vanish without it.
- **The terminal**: one that says the host has no shell ([Root channel](#root-channel)).
- **Limits of this step.** A request goes to the client attached last (step 3). A diff is still text.
  - An approved tool call leaves VS Code's chat once it completes (L2). The window gives a tool call
    that waits for confirmation `presentation: "hiddenAfterComplete"` (its bundle's `rue`), and on
    completion clears it only for content it renders itself: a terminal, file edits (`fileEdit`), a
    subagent, an image. So once its card is answered, a completed Write with text content goes; the
    chat state has it, and a reload shows it (a completed call built from history is not hidden). The
    same holds for any host's approved tool with plain content. It shows once ACP's `diff` becomes a
    `fileEdit` and an `execute` tool's output a terminal, a step of its own.
  Queued messages live in the face's memory: a restart loses those not started. A config schema that
  changes reaches a client only when it subscribes again. Attachments other than files and a client's
  own text are left out of a prompt.
- **Tests.** The mapping as unit tests (`AhpChatModelTests`): a client's turn and the end mark of its
  prompt, a cancelled turn's tail, a prompt that failed, an approval pending and withdrawn, one for a
  tool call not said yet, a form and its withdrawal, usage, the options as config and the model as
  AHP's, modes as `mode`. End to end (`AhpFaceTests`), with `AhpClient` over a WebSocket: a session
  created in a folder, prompted, its answer streamed, and listed by Zed under its URI; an approval
  answered from AHP and one the agent withdraws; a form answered (refused without its required
  answer); a cancel and its tail; a cancel right after a message with another model, after its prompt;
  a draft kept while the list changes; a queued message; options, a change Zed sees, a message's model;
  options chosen before a session exists; a session disposed; slash commands; the terminal (made, its
  line, input taken, disposed); the folder methods and those refused. Every folder and file there is
  named as VS Code names it (`file:///c%3A/…`, spaces, `#` and non-ASCII percent-encoded): the session's
  folder, an attached file, each `resource*` method; another machine's is refused in each and in
  `createSession`. `AhpNamesTests` reads such URIs and the face's own back into paths. From the live
  check and the review: the session's model in its chat's draft, and VS Code's unchanged model no
  choice; a session in its first turn kept through a list read before its promotion; an approval
  asked while a cancelled turn settles; an approval making a read session unread; a deleted root's
  open children closed; a new session's early commands; a session being made on no list; a client's
  changes echoed in order, a refused one going back; the terminal's title in the catalog, a shown one
  kept past 32, one with no URI refused. From the third round: a model picked again after a change
  elsewhere, after a failed set, and a second window's followed model; an ask whose handler runs after
  its cancelled turn ended; a new terminal kept though every older one is shown; what an agent says of a
  new session right after its answer; a file URI's `%` escaped; a real path through links. From the
  fourth: an agent never seen has its default as its one model, never set; models and options kept across
  a restart; options said right after a new session's answer win over the answer's; a queued message's
  model against the model when it was queued;
  no file URI for a drive-relative or device path. From the fifth: the model offered before, picked again
  in a draft, is a pick; the model a new session's answer said stays pickable after its agent reported
  another; a state file the face cannot read is none and is written anew.
- **Live check.** In VS Code: a Claude session in a folder picked through the host; a prompt; a Write
  approved; a turn cancelled; a Kimi session with its model picked; each visible in Zed; no terminal
  toast.

### Step 3: one approval for both faces

The conductor's step-3 addition, and its AHP side: an approval stays one piece of state that
every AHP client sees, and the ACP clients get it too; the first answer from either face settles
it. The same change lets two Zed windows on one session both see an approval.

### Step 4: children

Children's chats become promptable (`full`, a prompt goes to the child's session), get their
`tool` origin and the subagent result on the spawn's card; a child of another agent is settled
here (the 1.1.0 `provider`, or the child as a session of its own).

### Step 5: another machine

The face on another machine is reached as the probe reached its host: `ssh -L` to its port and a
`chat.remoteAgentHosts` entry, documented in [external-clients.md](external-clients.md); or
VS Code's `chat.sshRemoteAgentHostCommand` (a development setting) with a command that prints the
face's address, if it proves worth it.

## Not in the face

A terminal that runs anything (the face's terminal only says it has none), changesets,
annotations, canvases, automations, resource watches, the MCP and telemetry channels (their methods
get `-32601`), protected resources, writing files, and chats beyond a session's own and its
children's (`createChat`, `disposeChat` and `moveChat` are refused). Each is a change of its own,
should a need come.
