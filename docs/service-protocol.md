# Service protocol

The service speaks [ACP v1](https://agentclientprotocol.com/protocol/v1/initialization)
to its clients (the GUI, `gorynych-test`) as an ordinary ACP agent with many sessions,
and to its adapters as an ACP client; what an adapter must provide is in
[adapter-contract.md](adapter-contract.md). That is the conductor role of the
[proxy-chains RFD](https://agentclientprotocol.com/rfds/proxy-chains.md); the service does
not use `proxy/*` methods yet because the RFD is unstable. Types: [acp-types.md](acp-types.md).

## Endpoint

- One service per user and endpoint, enforced by a named mutex
  (`Gorynych.Service.<pipe>`, current user, all logon sessions).
- Transport: a named pipe, default `gorynych-acp-<user name>`, carrying the ACP stdio
  framing (JSON-RPC 2.0, one message per line). On Windows its ACL allows only the
  current user and denies the NETWORK group; clients connect with
  `PipeOptions.CurrentUserOnly`, which also checks the server's owner.
- The service is not a Windows service (session 0 could not start agents under the
  user's login). The GUI, the test client or a logon task starts it; it keeps running
  after clients disconnect.
- Data: `%LOCALAPPDATA%\Gorynych` (`--data` overrides): `service.json` (the persistent
  service ID), `sessions.json` (the overlay), `journal/`, `claude/`, `logs/`.

Command line: `Gorynych.Service [--pipe <name>] [--data <dir>] [--default-agent claude|kimi]
[--claude-model <model>] [--no-channel] [--no-peers] [--console]`. The same executable is
also the `gorynych` MCP server agents start over stdio:
`peers [--pipe … --token …] [--channel-url …]` (see Opinions below), and the stdio bridge
for external ACP clients: `acp --agent claude|kimi` (see [external-clients.md](external-clients.md)).

## Standard methods

| Method | What the service does |
| --- | --- |
| `initialize` | Answers as agent `gorynych`: `loadSession`, `sessionCapabilities.list/resume/close/delete`, `_meta["gorynych/sessionsChanged"]: true` (see [List changes](#list-changes)) and `_meta["gorynych/archive"]: true` (see [Archive](#archive)). |
| `session/list` | Asks every adapter that can list (following its cursors), merges with the overlay, sorts by `updatedAt`, pages with its own cursor. Each `SessionInfo` carries `_meta["gorynych/agent"]`, and a spawned session also `_meta["gorynych/parentSessionId"]` (absent for a session a client created). A connection scoped to one agent sees only that agent's sessions. Drafts and deleted sessions are left out, and so are archived sessions unless the request has `_meta["gorynych/includeArchived"]: true`; then an archived one carries `_meta["gorynych/archived"]: true`. |
| `session/new` | Picks the adapter from `_meta["gorynych/agent"]` (else the connection's agent, else `--default-agent`, else the first), adds the peers MCP server to `mcpServers`, forwards, records the session as a draft, attaches the client. |
| `session/load` | Opens the session in the agent (`session/resume`, or `session/load` with the agent's replay dropped), replays the service journal as `session/update`, then attaches the client. A session the journal has never seen is loaded from the agent instead, and journaled. Returns the session state (below). |
| `session/resume` | Opens the session in the agent and attaches the client, without replay. Returns the session state (below). |
| `session/prompt` | Waits for the session's running turn (one turn at a time, opinion turns included), journals the prompt as `user_message_chunk` (shown to the session's other clients), sets the first prompt as a provisional title, forwards. The turn is not cancelled when the client disconnects; stopping it is `session/cancel`. |
| `session/cancel` | Forwarded to the agent. A cancel that arrives while its prompt is still on the way (the session being reopened, say) ends that prompt with `cancelled` before it reaches the agent; otherwise prompt and cancel pass one per-session gate, so the agent always sees the cancel after the prompt it stops. |
| `session/close` | Detaches the client. Only when no other client has the session open is it closed in its agent, or, for a draft, discarded (see below). |
| `session/delete` | Closes the session in its agent if it is open, then deletes it there if the agent can (`sessionCapabilities.delete`); otherwise the overlay keeps a tombstone. Drops the journal. A session only the agent knows is found through its list; an unknown or already deleted session succeeds. |
| anything else with a `sessionId` | Forwarded to the session's agent. |

The **session state** is what clients draw as selectors: `configOptions` (Kimi's model,
thinking and mode), `modes` and, where an agent has the unstable field, `models`. When the
service asks the agent to open the session, `session/load` and `session/resume` return the
agent's answer. When the session is already open (another client has it, or Zed restarted and
loads its threads again), they return the state the agent last reported, kept per session
from its `session/new`, `session/load` and `session/resume` responses, its
`config_option_update` and `current_mode_update` notifications, and the responses to forwarded
`session/set_config_option` and `session/set_mode`. A load whose session the agent cannot take
returns the history with no state.

**Folders** are spelled one way wherever the service lists or keeps them (`SessionInfo.cwd`,
the overlay, `list_agents`): on Windows with backslashes, an upper-case drive letter and no
`.` or `..` segments, and everywhere without a trailing separator (except on a root). Agents
differ (Kimi may report `C:/Users/x` for the folder Zed calls `C:\Users\x`), and a client
that compares folders as text would see two projects. Other casing is kept as the agent gave
it.

Agent → client requests go to the most recently attached client of the session:
`session/request_permission` (an approval) to any client; `elicitation/create` (a form,
such as Claude's questions) preferably to a client that declared
`clientCapabilities.elicitation.form`. A client without forms gets each choice field as a
`session/request_permission` with the answers as options plus `Skip`; a form with free text
is declined for it. Toward its adapters the service declares form elicitation, since it
relays or translates them. If none is attached the request waits; if
the client disconnects before answering it goes to the next one. So an approval raised
while the GUI is closed appears when the GUI loads the session again, right after the
replay. When the agent withdraws the request (`$/cancel_request`), it is withdrawn from the
client too.

A client that stops reading for 15 seconds is disconnected, so one stuck GUI cannot stall
the agent whose updates it is not taking. Notifications that belong to no session
(`_gorynych/sessions_changed`) go through a queue per client, so a slow or broken client
delays only its own.

## Extensions

Per ACP's [extensibility rules](https://agentclientprotocol.com/protocol/v1/extensibility):
own fields only under `_meta` with namespaced keys, own methods prefixed with `_`.

| Name | Where | Meaning |
| --- | --- | --- |
| `gorynych/agent` | `session/new` params, `SessionInfo._meta`, `session/new` result | the adapter that owns the session |
| `gorynych/agent` | `initialize` params `_meta` | scopes the connection to one adapter: its default for `session/new` and the only one in `session/list` (the stdio bridge sets it for plain clients) |
| `gorynych/serviceId` | `initialize` result `_meta` | the service's persistent ID; a GUI keys sessions as service/session |
| `gorynych/agents` | `initialize` result `_meta` | the adapters this service offers (`name`, `displayName`) |
| `gorynych/sessionsChanged` | `initialize` result `_meta` | `true`: the service sends `_gorynych/sessions_changed` |
| `gorynych/archive` | `initialize` result `_meta` | `true`: the service keeps an archive flag per session, with everything in [Archive](#archive) |
| `gorynych/archived` | `SessionInfo._meta` in `session/list`; `session_info_update._meta` | `true` for an archived session in a list (absent otherwise); in a `session_info_update`, `true` or `false` whenever the flag changes |
| `gorynych/includeArchived` | `session/list` params `_meta` | `true` lists archived sessions too |
| `gorynych/parentSessionId` | `SessionInfo._meta` in `session/list`; `session_info_update._meta`; the `_meta` of a spawned session's first `user_message_chunk` | the session that started this one with `spawn_session`; absent for a session a client created |
| `gorynych/origin` | `user_message_chunk._meta` | where a user message came from when the client did not send it: `peer` (an opinion request delivered as a turn), `channel` (one that reached Claude through its channel), `spawn` (a spawned session's first prompt), `report` (a child's report delivered as a turn) or `terminal` |
| `gorynych/sender` | `user_message_chunk._meta` | who asked, for example `peer_claude`, `parent_claude` for a spawn, `child_kimi` for a report: a label for GUIs only; the text the agent gets names the session by its ID instead (see [What agents are told](#what-agents-are-told)) |
| `gorynych/fromSessionId` | `user_message_chunk._meta` of an opinion request or a report delivered as a turn | the session that asked, or the child that reports |
| `gorynych/opinion` | `_meta` of the request message and of every update of its turn | the request's `message_id`, so a GUI can show the exchange apart from the user's own turns |
| `_gorynych/status` | method | service ID, process, data folder, clients, sessions, pending requests, adapters |
| `_gorynych/shutdown` | method | asks the service to exit |
| `_gorynych/opinion` | method, `{sessionId, text, sender?}` → `{text}` | asks the agent of `sessionId` for an opinion; for Claude it goes through the channel and the result is Claude's `reply`. From the peers server it carries `_meta["gorynych/peerToken"]`, and the service names the sender (`peer_kimi`) and passes `fromSessionId`. The service puts a first line naming the asking session in front of the text it forwards. An agent that answers method not found, or `-32600` because it is not running, is asked in a turn instead ([adapter-contract.md](adapter-contract.md#64-the-live-note-path)) |
| `_gorynych/peers` | method, `{token, includeArchived?}` → `{sessions}` | the sessions the session behind `token` can ask: the others that have history (not drafts, not deleted, and titled or with a turn in the journal), open ones first, then the newest; with agent, title, folder, `open`, `parent_session_id`. Archived ones only with `includeArchived: true`, and then every entry has `archived`. With a known token, `caller` names that session as `session_id <id> (<agent>, "<title>")`. Without `includeArchived`, `hiddenArchived` counts the archived sessions left out (never the caller) |
| `_gorynych/archive` | method, `{sessionId, archived}` → `{}` | sets the session's archive flag (see [Archive](#archive)); from the peers server, with its token, the result also has `text`, what the agent is told |
| `_gorynych/reply` | method, `{messageId, text}` → `{delivered}` | the asked session answers a request it got as a turn; accepted only from that session's token |
| `_gorynych/spawn` | method, `{agent, prompt, cwd?, title?, report?}` → `{sessionId}` | starts a child of the calling session after asking the person (see [Child sessions](#child-sessions)); accepted only with a session's peer token |
| `_gorynych/note` | method, service → adapter, `{sessionId, text, sender, fromSessionId?}` → `{}` | tells a session's agent something that needs no answer (a child's report); the Claude adapter pushes it through the channel, an agent that answers method not found, or `-32600` because it is not running, gets it as a turn |
| `_gorynych/sessions_changed` | notification to clients, `{sessionId, change}` | a session was `added` to, `updated` in or `removed` from the lists (see [List changes](#list-changes)) |
| `gorynych/peerToken` | `_meta` of `_gorynych/opinion`, `_gorynych/reply`, `_gorynych/spawn` and `_gorynych/archive` | which session's `gorynych` MCP server is calling |

## Opinions between agents

Every session gets, in `mcpServers` (the ACP way to give an agent tools), a stdio MCP server
named `gorynych`: `Gorynych.Service peers --pipe <pipe> --token <token>`. The token is
random per session and tells the service who is calling. Its tools:

- `list_agents {include_archived?}` — the user's other sessions (`_gorynych/peers`), archived
  ones only with `include_archived: true`, marked `archived`; a first line,
  `You are session_id <id> (<agent>, "<title>").`, names the caller itself, which the list
  leaves out, and a last line says how many archived sessions it left out;
- `ask_agent {session_id, question}` — starts a new question to one, by its exact
  `session_id`, and waits for the answer (`_gorynych/opinion`); archived sessions too;
- `spawn_session {agent, prompt, cwd?, title?, report?}` — starts a child session
  (`_gorynych/spawn`, see [Child sessions](#child-sessions)) and returns `{"session_id": …}`;
- `archive_session {session_id}` and `restore_session {session_id}` — set or clear a
  session's archive flag (`_gorynych/archive`, see [Archive](#archive)), any session's, the
  caller's own included;
- `reply {message_id, text}` — only answers a request this session was asked, by the
  `message_id` in its header. Nothing waiting for that ID fails with how to recover (below).

Each description says when to use the tool rather than its neighbour: `ask_agent` starts a
question and points to `reply` for answering one; `reply` only answers and points to
`ask_agent`; `session_id` is the exact value from `list_agents` or a header, never an agent
name, a title or a sender label; `list_agents` says its first line names this session, the ID
to pass for it, that IDs differ by agent (Claude's a bare UUID, Kimi's `session_…`) and are
passed exactly as listed, and what `open` means: the agent has the session loaded right now,
unrelated to `archived`; a session that is not open is started when asked, so it answers
more slowly (for every agent); `spawn_session` says the child's turns are reported back by
themselves; `archive_session` says archiving is not deleting (the history stays, `ask_agent`
still reaches it), and `restore_session` that it only changes where the session is shown, so
asking an archived session needs no restore.

How a request reaches the asked agent depends on what it has:

- **Claude** has a channel. The Claude adapter adds `--channel-url` to the same `gorynych`
  server in its `--mcp-config`, so one server is both its peer tools and its development
  channel: while its Claude process runs, the request arrives as a channel message
  (`meta.kind: "opinion"` and its `message_id`, which starts with `c`), and `reply` goes back
  through the channel. While the process is not running, the adapter refuses (`-32600`) and the
  request becomes a turn as for Kimi, which starts Claude.
- **Kimi** (any agent without a channel) gets the request as an ordinary `session/prompt`
  turn, marked in the text with the asking session and `message_id` and asking for `reply`. ACP
  cannot inject into a running turn, so the request waits for the session's current turn
  (the user's prompts wait for it in turn). The answer is the `reply` tool call
  (`_gorynych/reply`); if the turn ends without one, the turn's own text. The request and
  every update of its turn carry `_meta["gorynych/opinion"]`. The peers server sends `reply`
  to the channel first when it has one, then to the service, so Claude answers a request it
  got as a turn the same way.

A session that is not loaded in its agent is loaded for the request, and one whose agent is
not running is started by the turn. A session the service woke only for such turns (opinion
requests and reports) is closed again after the last of them, unless a client opened it
meanwhile; a session that was loaded and running stays as it was. An archived session is woken
the same way and stays archived. The rules for adapters are in
[adapter-contract.md](adapter-contract.md#6-service-turns-opinions-and-reports).

A session that is answering a request cannot ask another agent meanwhile, so two agents
cannot wait on each other.

### What agents are told

Everything the service puts in front of an agent is that agent's interface. A message it
injects names the sending session by its real ID, labelled `session_id` so the agent can pass
it to `ask_agent`, with the session's agent and title: `session_id <id> (<agent>, "<title>")`
(a title is cut at 80 characters, its double quotes become single ones; without a title only
the agent). Labels such as `peer_claude` stay in `_meta["gorynych/sender"]` for GUIs and are
never in the text. The journal keeps the text exactly as the agent got it.

- An opinion request delivered as a turn:

  ```
  [Opinion request · from session_id <id> (<agent>, "<title>") · message_id m3]
  <the question>

  This message comes from another of the user's coding agents, not from the user. Give your
  honest opinion briefly, then send it with the reply tool of the gorynych MCP server, passing
  message_id "m3" (reply answers this request; ask_agent would start a new question instead).
  Do not run other tools, edit files or take any other action for this request.
  ```

  Through Claude's channel the content is the first line without the `message_id` (the
  channel message carries it in `meta`) and the question; Claude's appended system prompt
  says the rest. A request a client sends without a peer token names the client's `sender`,
  or `a Gorynych client`.
- A spawned session's first prompt and a report to the parent: see
  [Child sessions](#child-sessions) and [Reports](#reports).

Errors from the peer tools say how to recover. The peers server returns them to the agent as
`Gorynych could not do that: <message>`:

| Case | Message |
| --- | --- |
| `ask_agent` with an unknown or deleted `session_id` (`-32002`) | `No session "<x>". Pass an exact session_id from list_agents.` and the first five sessions it can ask, one per line as `- session_id <id> (<agent>, "<title>")`, or `There is no other session to ask now.` |
| `ask_agent` with its own ID (`-32602`) | `"<x>" is this session's own session_id; ask_agent asks another session.` and the same list |
| `ask_agent` while answering a request as a turn (`-32600`) | `This session is answering opinion request m3, so it cannot ask another session during this turn. Send your answer with the reply tool, passing message_id "m3"; if another opinion would help, say so in the answer.` |
| `ask_agent` to a session that could not be loaded or started, or whose turn failed right after the service started it (the error's own code) | `session_id <id> (<agent>, "<title>") could not be started to answer: <reason>. Ask another session from list_agents, or ask the user to open that session and check it.` A session that is merely not loaded or not running is started instead ([above](#opinions-between-agents)) |
| `reply` with a `message_id` nothing waits for (the tool's own error) | `No open request with message_id "<x>" for this session; it may have been answered already or withdrawn. reply only answers a request you received, by the message_id in its header. To ask a session something, use ask_agent with its session_id.` |
| `spawn_session` with an unknown agent (`-32602`) | `No agent "<x>". Pass one of: claude, kimi.` |
| `spawn_session` with a folder that does not exist (`-32602`) | `No such folder: <path>. Pass an existing folder, or leave cwd out to work in this session's folder, <folder>.` |
| `archive_session` with an unknown or deleted `session_id` (`-32002`) | `No session "<x>". Pass an exact session_id from list_agents.` and the first five sessions it can archive, the caller's own first and marked `(this session)`, under `Sessions you can archive:`, or `There is no session to archive now.` |
| `restore_session` with an unknown or deleted `session_id` (`-32002`) | `No session "<x>". Pass an exact session_id from list_agents with include_archived: true.` and the first five archived sessions under `Archived sessions:`, or `No session is archived now.` |

The hint lists of `ask_agent` and `archive_session` leave archived sessions out, as
`list_agents` does.

What the tools answer when they succeed. `list_agents` names the caller first: the list leaves
it out, and an agent has no other way to learn its own `session_id` (which `archive_session`
takes for the session itself). `archive_session` and `restore_session` say what happened;
`(this session)` follows the session when it is the caller's own:

| Case | Text |
| --- | --- |
| `list_agents` | first line `You are session_id <id> (<agent>, "<title>").`, then the list as JSON |
| `list_agents` without `include_archived`, leaving N > 0 archived sessions out (the caller not counted) | a last line `N archived sessions are not shown; pass include_archived: true to list them.`, or for one `1 archived session is not shown; pass include_archived: true to list it.`; nothing when none is left out. The hint is in the result as well as in the schema, because an agent whose conversation was resumed may hold an older schema |
| archived now | `Archived session_id <id> (<agent>, "<title>"). It is now hidden from list_agents and from the user's session list; nothing in it is lost. ask_agent still reaches it, and restore_session brings it back.` |
| already archived | `session_id <id> (<agent>, "<title>") was already archived, so nothing changed. ask_agent still reaches it, and restore_session brings it back.` |
| restored now | `Restored session_id <id> (<agent>, "<title>"). It is back in list_agents and in the user's session list.` |
| not archived | `session_id <id> (<agent>, "<title>") is not archived, so nothing changed. It is in list_agents and in the user's session list.` |

A client calling `_gorynych/opinion` or `_gorynych/archive` without a peer token gets the
plain `Unknown session <id>`, since it has no `list_agents`.

Approvals are the same for every agent: `list_agents` (read only) and `reply` (an answer the
user's own agent was asked for) run without asking — the Claude adapter allows them in its
PermissionRequest hook, the service answers Kimi's `session/request_permission` for them —
while `ask_agent` always asks the person, because it hands context to another agent and
spends its turns. `spawn_session` is also allowed without the agent's own approval, because
the service itself asks the person before every spawn (below), so it is asked once.
`archive_session` and `restore_session` go through the agent's normal approval, with no
card of the service's own: both can be undone.

Verified live on 7 November 2026: Claude called `list_agents` and, after approval,
`ask_agent`; Kimi got the marked turn, answered with `reply`, and Claude used the answer.
Kimi asking Claude goes through Claude's channel the same way.

## Child sessions

A session can start another: a coordinator hands work to coder sessions, and the person can
open and prompt any of them directly. ACP's subagents RFD hides a subagent from
`session/list` and forbids prompting it, so a Gorynych child is an ordinary top-level ACP
session that names its parent in `_meta["gorynych/parentSessionId"]`; a stock client shows
it as one more session, a Gorynych GUI can draw the tree.

The agent calls the peers server's `spawn_session {agent, prompt, cwd?, title?}`, which sends
`_gorynych/spawn` with its peer token. The service then

1. shows the spawn in the calling session as a `tool_call` (kind `other`, the prompt and the
   folder as content) and asks the person with `session/request_permission` on that session:
   `Start` (`allow_once`) or `Don't start` (`reject_once`), never "always", so every spawn is
   asked. Without a client attached the question waits for one, like an agent's approval;
2. on `Start`, checks that the caller still exists (one deleted while the person decided
   starts nothing), then creates the session in the chosen agent with `session/new` (the
   folder is the caller's unless `cwd` names another; a relative path is taken from the
   caller's), with the peers server like any session;
3. takes the session's turn, records the caller as its parent, journals a
   `session_info_update` with the parent in `_meta` (and the title, if given), and only then
   lists the session (`added`, with its parent) and returns `{sessionId}`. Until it is listed
   the session is a draft in the overlay: if anything fails before that, or the spawn is
   withdrawn while the agent creates it, the session is closed and deleted in its agent again
   (an agent that cannot delete is only closed: the Claude adapter keeps nothing before a
   first prompt). The first prompt runs in the background: the agent gets the task as one text
   block and, as a second, what it needs to know about where the task came from (below); the
   journal keeps both as one `user_message_chunk` with `_meta["gorynych/origin"]: "spawn"`,
   `gorynych/sender` (`parent_claude`) and the parent. A question with `ask_agent` or a prompt
   that comes meanwhile, even one sent right after the `added` notice, waits for it;
4. ends the tool call `completed` with the new session's ID, or `failed` when nothing was
   started.

What follows the task in the child's first prompt:

```
[About this session · started by session_id <parent> (<agent>, "<title>") with spawn_session, approved by the user]
The task above comes from that session. When each of your turns ends, Gorynych sends your
final message to that session automatically, so you need not send it yourself. To ask it
something, use ask_agent with session_id "<parent>". The user can also prompt you here
directly.
```

With `report: false` the sentence about reports reads `Your turns are not reported to it.`
It follows the task rather than preceding it because Kimi titles a session with its first
prompt (cut at 200 characters): the task stays the title. The provisional title is the task
alone.

The spawn fails with

- `-32600` for `Don't start` ("The user declined to start the session."), for a caller
  deleted meanwhile, and for an unknown peer token;
- `-32602` for an unknown agent or a folder that does not exist;
- `-32800` when the caller's turn was cancelled: a client that sends `session/cancel` answers
  that session's pending approvals with the `cancelled` outcome, as ACP requires;
- the agent's own error when it could not create the session.

A spawn can also be withdrawn before the person answers: the agent's tool call is cancelled
(the agent sends MCP's `notifications/cancelled` to the peers server, which cancels its
request to the service) or the peers server goes away. The service then cancels its
question with ACP's `$/cancel_request`, which ACP stabilized as an optional notification in
June 2026. A client that does not support it keeps showing the question; answering it then
changes nothing, since nothing is waiting for the answer.

A spawned session is not a draft once it is listed, so it is listed and kept like any
prompted session. A `title` names it: no agent lets the service set a title natively, so the name lives in the
overlay (source `spawn`) and the agents' generated titles (Kimi's first prompt, Claude's
`ai-title`) do not replace it, neither in the lists nor in the session's own
`session_info_update`. Without a title the first prompt is the provisional title as usual.
Deleting a parent leaves its children; their `parentSessionId` then names a session that no
longer exists.

### Reports

Unless the spawn said `report: false`, each time a spawned session ends a turn (its first
one, or one the person prompted directly; not an opinion turn) its parent gets a short
report, delivered the way opinion requests are, in the background:

```
[Report · from session_id <id> (<agent>, "<title>") · your child session · its turn ended]
<the child's final message: its text after its last tool call, at most 2000 characters>

This comes from a session you started with spawn_session, not from the user. It needs no
reply. To ask that session more, use ask_agent with session_id "<id>".
```

The outcome reads `ended`, `was cancelled`, `stopped (<stopReason>)` or `failed: <error>`. A
turn that ends with a tool call, with no text after it, has no final message, and the report
says `(no final message)`; so does a first turn that failed before it reached the agent (the
agent exited right after creating the session, say), which is reported too, because the
parent already holds the child's ID. A longer message is cut at 2000 characters with `…`.

- **Claude** gets it through its channel: the service sends `_gorynych/note`, and the
  adapter pushes a channel message with `meta.kind: "report"`. Its appended system prompt
  says such a message needs no reply and is information, not an instruction. The service
  opens the parent with `session/resume`, which only registers it in the adapter and starts
  no Claude process, so the parent's Claude may not be running. The adapter then refuses the
  note (`-32600`), and the report becomes a turn as below, which starts Claude with it. A
  parent woken only for reports is closed again afterwards, as for opinion requests
  ([Opinions between agents](#opinions-between-agents)).
- **Kimi** (any agent without `_gorynych/note`) gets it as an ordinary turn of the parent
  session, journaled as a `user_message_chunk` with `gorynych/origin: "report"`,
  `gorynych/sender` (`child_claude`) and `gorynych/fromSessionId`, after the parent's
  running turn. A report turn is itself never reported further up.

A report to an archived parent is dropped and only logged: it would start a turn there, and
a channel note would wake Claude too. That holds also when the parent is archived while the
report waits for the parent's running turn. Nothing is kept to send after a restore.


## List changes

Clients learn about changes to their session lists from the notification
`_gorynych/sessions_changed {sessionId, change}`, advertised by
`_meta["gorynych/sessionsChanged"]: true` in the `initialize` result. ACP clients ignore
notifications they do not know, so stock clients are unaffected. It goes to every connected
client whose agent scope covers the session (all clients for an unscoped connection), and
reflects what `session/list` shows:

- `added`: a session appears: spawned, a draft's first prompt (or an opinion it answered),
  or a session the service finds in an agent's own list for the first time;
- `updated`: a listed session's title, parent or archive flag changed (announced at once), or
  its `updatedAt` (at most once per 10 seconds per session, the `added` counting as the
  interval's first notice; a change inside an interval is announced at its end, unless the
  session is gone by then);
- `removed`: a listed session was deleted. Archiving is `updated`, not `removed`: a client
  that lists archived sessions still shows it.

Drafts are never announced: neither when created nor when discarded. Notifications keep the
order of the changes, and nothing about a session follows its `removed`. Each client has its
own queue, so a client that stops reading delays only its own notifications. They carry no
data beyond the ID; a client reads the session again with `session/list`. In a rare race a
list that runs while a session is spawned can find it in its agent first; it is then
`added` without its parent and `updated` with it a moment later.

## Archive

An archived session is out of sight, not locked: it is left out of the lists but works as
before, so the archive can serve as a knowledge base that agents still ask. ACP's stable schema
has no archive, so this is Gorynych's extension, advertised by
`_meta["gorynych/archive"]: true` in the `initialize` result. ACP has a draft RFD,
[Session Archive and Unarchive](https://agentclientprotocol.com/rfds/session-archive) (revised
30 October 2026), whose archive closes the session and lets an agent refuse to resume it;
whether Gorynych follows it is open ([adapter-contract.md](adapter-contract.md#11-archive)). Neither agent offers one through
ACP either: Claude Code has no archive, and Kimi has one in its own UI that `kimi acp` does not
expose ([adapters.md](adapters.md#kimi-code)). The flag therefore lives in the overlay for
every agent, and the service never writes an agent's own files for it.

- `_gorynych/archive {sessionId, archived}` → `{}` sets or clears it. It is idempotent, and an
  unknown or deleted session fails with `-32002`. It is only a flag: nothing is closed,
  cancelled or unloaded, and a turn under way goes on.
- Nothing clears it on its own: not a prompt from the user, not a question from another
  session, not a new title from the agent. Only `_gorynych/archive` (from a client, or from the
  peer tools `archive_session` and `restore_session`) changes it.
- `session/list` leaves archived sessions out, as Codex and Kimi do in their own lists; with
  `_meta["gorynych/includeArchived"]: true` in the request it includes them, each with
  `_meta["gorynych/archived"]: true`. The key is absent for a session that is not archived.
- A change is announced at once, outside the activity throttle, as
  `_gorynych/sessions_changed {sessionId, change: "updated"}`, and published to the session's
  clients as a `session_info_update` whose `_meta["gorynych/archived"]` is `true` or `false`.
  The update is journaled, so a later `session/load` replays it, except for a session the
  service has never journaled: its first load takes the history from its agent, which a
  journal holding only this update would prevent.
- An archived session is asked, prompted, opened and spawned from as before. `list_agents`
  leaves it out unless `include_archived` is true, and so do the hint lists in the peer tools'
  errors; `ask_agent` reaches it all the same and leaves it archived.
- A report to an archived parent is dropped ([Reports](#reports)).

## Drafts and deletion

ACP clients may create sessions before the person types anything: Zed sends `session/new`
whenever a thread view opens and quits without `session/close`
([external-clients.md](external-clients.md)). A session made by `session/new` is a **draft**
until its first `session/prompt` or an opinion it answers (as a turn, or through Claude's
channel). Ending a draft is written to `sessions.json` before anything reaches the agent; if
that write fails the prompt is refused, because a draft left in the file is swept at the next
start.

A draft is discarded when

- `session/close` arrives for it and no other client has it open, or
- the last client attached to it disconnects (in the background, so the disconnect is not
  held up), or
- the service starts and finds it marked in the overlay from an earlier run.

A native delete cannot be undone, so a discard keeps the session whenever it is unsure:

1. Every session has a gate. Load, resume, prompt, opinions and forwarded requests register
   under it; a discard holds it throughout and goes ahead only if no client is attached and no
   operation is registered. A client that comes back meanwhile waits for the outcome, and then
   finds the session gone or kept.
2. A draft whose journal holds part of a turn (a message, a thought, a tool call, a plan) is
   kept, and its mark cleared.
3. An agent that can delete must list the session without a title. Kimi titles a session from
   its first prompt and lists an empty one with `title: null` (Kimi Code 2.4.1). A titled
   session is kept; one the agent does not list is hidden with a tombstone, not deleted.
4. Only then is the session closed in its agent and deleted there. An agent that cannot
   delete (Claude) has nothing native to delete: the Claude adapter records a session at its
   first prompt. The record and the journal go.

A discard that fails leaves the mark, and the next start tries again.

`session/delete` from a client closes the session in its agent if it is open (stopping its
turn) and deletes it there when the agent can. A session the agent no longer has counts as
deleted: ACP's not-found (`-32002`), or any error while the agent no longer lists it (Kimi
2.4.1 answers `-32602 Unknown sessionId`). An agent that cannot delete (Claude: the service
never touches `~/.claude`) keeps the session, and so does one that cannot be reached; the
overlay then keeps a tombstone (`deleted`), and the session disappears from `session/list`,
`_gorynych/peers`, `session/load` and `session/prompt` even though the agent still lists it.
ACP allows such a soft delete.

Nothing that arrives after a delete brings the session back: late updates from the agent are
dropped, a prompt queued behind the deleted session's turn fails with not found, and late
writes to the overlay are ignored.

## Journal

Every `session/update` an adapter sends, and every prompt a client sends, is appended to
`journal/<session>.<hash>.jsonl` as the raw update object, so variants this schema
version does not know are kept and replayed unchanged. Past the limit (default 5000
entries) the journal merges consecutive text chunks of one message and then drops the
oldest entries down to three quarters of the limit. A line torn by a crash is dropped
when the journal is read back.

## Overlay

`sessions.json` holds, per session: the owning adapter, `cwd`, `title` and its source
(`agent` when the harness gave it, `prompt` for the provisional first-prompt title, `spawn`
for a name given to `spawn_session`), `createdAt`, `updatedAt`, `parentSessionId` and
`reportToParent` for a spawned session, and `draft`, `deleted` or `archived` when set. Native state wins:
a title or `updatedAt` from the harness replaces the overlay's, except a `spawn` name, which
no harness can store yet.

The file is replaced atomically (a temporary file moved over it). An antivirus scanner or
the indexer can hold it open for a moment, so the move is retried for up to about 400 ms,
outside the lock on the records. A write that still fails is logged and the change stays in
memory for the next write; ending a draft is the exception (see above). The activity stamp of
a streaming turn tries a write at most once a second after the last attempt, so a file that
cannot be written does not slow every update down. A session removed in this run is not
recreated by a late write, and an activity stamp never creates a record: an update an agent
sends before its `session/new` answer (Kimi announces its commands) must not list the
session ahead of time.
