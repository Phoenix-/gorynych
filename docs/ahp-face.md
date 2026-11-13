# The AHP face

The service speaks [AHP](https://github.com/microsoft/agent-host-protocol) (Microsoft's Agent
Host Protocol) to clients such as VS Code's Agents window, next to ACP for Zed, over the same
conductor ([design.md](design.md#two-faces)). This file is the face's specification. **Status:**
step 1 (reading) built on 13 November 2026 (`src/Gorynych.Service/Ahp`), off by default; its first
live check in VS Code 1.145.2 passed, and what it and a review found is fixed or settled below; steps 2
to 5 specified, not built. Each step is closed by a live check.

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

- **Option.** `serve --ahp off|on|<port>`; `on` is port 8766. **Off by default** until step 2 is
  done (below), then on by default, as [design.md](design.md#two-faces) has it. `task install`
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
  every agent, and, through `resource*`, reading the user's files. The token is the user's, like
  the pipe. The face advertises no `protectedResources`, so VS Code sends it no GitHub
  credentials.

## Identifiers

- **A session** is `ahp-session:/<id>`, where `<id>` is the Gorynych session ID as a
  percent-encoded path segment. A session created through AHP keeps the URI its client chose:
  the face passes it to `session/new` as `_meta["gorynych/ahpUri"]`, the overlay keeps it, and
  `session/list` returns it in `SessionInfo._meta` ([What the conductor adds](#what-the-conductor-adds)).
- **A chat** is `ahp-chat:/<id>`, with the same `<id>` as its session's URI for the session's
  own conversation, and the child's session ID for a child ([Children](#children)).
- Only root sessions are AHP sessions. A child session (one with
  `_meta["gorynych/parentSessionId"]` whose parent exists) is a chat in its root's AHP session,
  however deep it is; it never has an `ahp-session:` URI of its own.
- **A turn** is the `gorynych/promptId` of its prompt when it has one, else the `messageId` of
  its user message, else `agent-<position>` for a turn the agent began itself, `<position>`
  being the `gorynych/position` of its first update.
- **Folders** are `file:` URIs; on Windows `file:///C:/Users/x` for the service's `C:\Users\x`,
  both ways. (AHP's `environment.pathStyle` is newer than 1.0.0.)

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
  The session and chat commands it serves only from a later step are answered as each
  step says ([step 1](#step-1-reading)). Unknown channels: `SessionNotFound` (`-32001`) for a
  session, chat or other URI the face does not serve (VS Code subscribes to
  `<session>/annotations` and takes this calmly). The notification `setClientManagedSettingsPermissions` that VS Code sends is not
  AHP and is ignored.
- Client actions arrive by `dispatchAction`. The face checks `IsClientDispatchable`, then the
  channel's rules (below), applies an accepted action through the reducer and echoes it with its
  `origin`; a refused one is echoed with `rejectionReason`. An action for a channel that does not
  exist is dropped without an echo, as AHP says. A refused action takes a `serverSeq` and goes into
  the replay log like an applied one.

## The internal client

- One ACP connection for the whole face, through `Conductor.ServeClientAsync` over an in-memory
  duplex stream, named `ahp`. Every AHP client shares it, as they share AHP's state.
- Its `initialize` `_meta`: `gorynych/link: 1`, so its prompts carry `gorynych/promptId` and
  their ends are marked; in step 1 also `gorynych/requests: false`, so the conductor sends it no
  approvals or forms it could not answer.
- **Sessions it opens.** It loads a session (`session/load`) when an AHP client first subscribes
  to one of its chats, builds the chat state from the replay, and answers the subscription when
  the load answers. It closes the session (`session/close`) when no AHP client has subscribed to
  any of its chats for two minutes, so a reconnect finds it still open and gets a replay, and
  never while any turn of the session runs. When the face is the session's last client, a close
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
  `description` "`<displayName>` through Gorynych", `models` empty until step 2, and
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
- No `terminals`, no `activeSessions`.
- **Terminals, open.** VS Code 1.145.2 opens a terminal on the host for the session it shows
  (`workbench.contrib.sessionsTerminal` calls `createTerminal` for every connected remote host, with
  no capability or setting in between), and AHP 1.0.0 has no way to say a host has none. The face
  serves no terminal in step 1, so the window shows "The terminal process failed to launch:
  Method not found: createTerminal" each time it opens a session. Settled before step 2: serve a
  terminal channel (one that only says the host has no shell, or a real shell), or live with it.

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
  when a turn starts (and on `chat/inputRequested`); the face clears the session's bit at the
  same time. A replay would clear it for every past turn, so after building a chat from a replay
  the face sets the bit back to what it had.
- **Notifications on `ahp-root://`.** A new root session: `root/sessionAdded`. A deleted one:
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
`session/activeClientSet`; the tools they offer are not offered to agents), and empty
customizations, changesets, server tools and `inputNeeded` until step 2.

Client actions on a session:

| Action | What the face does |
| --- | --- |
| `session/isArchivedChanged` | `_gorynych/archive {sessionId, archived}`; echoed when it succeeds, refused with its error when not |
| `session/isReadChanged` | applied in memory |
| `session/titleChanged` | refused (`Renaming is not supported yet`) until renaming reaches the native side ([design.md](design.md#sessions-catalog-titles-archive), planned) |
| `session/activeClientSet`, `session/activeClientRemoved` | applied |
| anything else | refused |

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
| `available_commands_update`, `current_mode_update`, `config_option_update`, `usage_update` | nothing in step 1 ([step 2](#step-2-working-in-sessions)) |
| `session_info_update`, title | for the session's own chat `session/titleChanged` and `session/chatUpdated`; for a child's, `session/chatUpdated` |
| `session_info_update`, `gorynych/archived` | `session/isArchivedChanged`, or for a child `chat/isArchivedChanged` |
| `session_info_update`, `gorynych/interactivity` | `session/chatUpdated` with `interactivity`. In step 1 nothing: every chat is read-only. Where the reason goes is settled in step 2: `PartialChatSummary` has no `_meta`, so it cannot go in `session/chatUpdated` |

Tool content: ACP `content` blocks with text become `text` results; a `terminal` becomes text
naming the terminal, as the face serves no terminal channel; a resource link a `resource`. A
`diff` is text in step 1 (`Edited <path>`, then its lines with `-` and `+`, or their counts past 200
lines): AHP's `fileEdit` names its two sides by `ContentRef` URIs a client reads with
`resourceRead`, which the face serves only from step 2, and a diff's old text is in no file. It
becomes a `fileEdit` in step 2, with a URI scheme of the face's own for the two sides.

Client actions on a chat in step 1: `chat/isReadChanged` and `chat/draftChanged` are applied;
`chat/isArchivedChanged` on a child's chat archives the child; everything else is refused
(`This chat is read-only in this version`). Every chat is `interactivity: "read-only"` in step 1,
so the window shows no input box in the first place.

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
- **Step 2.** `gorynych/ahpUri` on `session/new`, kept in the overlay and returned in
  `SessionInfo._meta`.
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

- `createSession`: `session/new` in the session's `provider` with the first working directory as
  `cwd` and `gorynych/ahpUri`; `lifecycle: "creating"`, then `session/ready`, or
  `session/creationFailed` with the error. `disposeSession` as `session/delete`, which deletes
  the session in its agent where the agent can, as a delete from Zed does.
- Folders: `resourceList`, `resourceResolve`, `resourceRead` and `resourceMkdir` on `file:` URIs;
  the other `resource*` methods are refused with `PermissionDenied`.
- Configuration: the agent's config options, modes and models as `SessionState.config` and
  `resolveSessionConfig`; `session/configChanged` as `session/set_config_option` or
  `session/set_mode`; `AgentInfo.models` from the models the face has seen for that agent;
  `Message.model` set before its prompt. How to describe an agent's options before it has a
  session is the open question of this step.
- Prompts: `chat/turnStarted` as `session/prompt` with `gorynych/promptId` the turn's ID;
  `chat/turnCancelled` as `session/cancel`; a message while a turn runs as a queued message,
  which the conductor's queue sends next; the prompt's usage as `chat/usage`; slash commands
  through `completions`.
- Approvals and forms: the internal client drops `gorynych/requests: false` and declares forms.
  An approval puts its tool call back to pending confirmation (`chat/toolCallReady` without
  `confirmed`, the ACP options as `options`) and into `session/inputNeeded`;
  `chat/toolCallConfirmed` answers it. A form is `chat/inputRequested`, answered by
  `chat/inputCompleted`. A request the agent withdraws is withdrawn here too. Until step 3 a
  request goes, as today, to the client attached last: VS Code only if the face opened the
  session after any ACP client did.
- Chats become `full`, except where `gorynych/interactivity` says read-only.
- **Live check.** In VS Code: a Claude session in a folder picked through the host; a prompt; a
  Write approved; a turn cancelled; a Kimi session with its model picked; each visible in Zed.

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

Terminals, changesets, annotations, canvases, automations, resource watches, the MCP and
telemetry channels (their methods get `-32601`), protected resources, and chats beyond a
session's own and its children's (`createChat`, `disposeChat` and `moveChat` are refused). Each
is a change of its own, should a need come.
