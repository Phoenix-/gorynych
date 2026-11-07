# Service protocol

The service speaks [ACP v1](https://agentclientprotocol.com/protocol/v1/initialization)
to its clients (the GUI, `gorynych-test`) as an ordinary ACP agent with many sessions,
and to its adapters as an ACP client. That is the conductor role of the
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
| `initialize` | Answers as agent `gorynych`: `loadSession`, `sessionCapabilities.list/resume/close/delete`. |
| `session/list` | Asks every adapter that can list (following its cursors), merges with the overlay, sorts by `updatedAt`, pages with its own cursor. Each `SessionInfo` carries `_meta["gorynych/agent"]`. A connection scoped to one agent sees only that agent's sessions. Drafts and deleted sessions are left out. |
| `session/new` | Picks the adapter from `_meta["gorynych/agent"]` (else the connection's agent, else `--default-agent`, else the first), adds the peers MCP server to `mcpServers`, forwards, records the session as a draft, attaches the client. |
| `session/load` | Opens the session in the agent (`session/resume`, or `session/load` with the agent's replay dropped), replays the service journal as `session/update`, then attaches the client. A session the journal has never seen is loaded from the agent instead, and journaled. |
| `session/resume` | Opens the session in the agent and attaches the client, without replay. |
| `session/prompt` | Waits for the session's running turn (one turn at a time, opinion turns included), journals the prompt as `user_message_chunk` (shown to the session's other clients), sets the first prompt as a provisional title, forwards. The turn is not cancelled when the client disconnects; stopping it is `session/cancel`. |
| `session/cancel` | Forwarded to the agent. A cancel that arrives while its prompt is still on the way (the session being reopened, say) ends that prompt with `cancelled` before it reaches the agent; otherwise prompt and cancel pass one per-session gate, so the agent always sees the cancel after the prompt it stops. |
| `session/close` | Detaches the client. A draft no other client has open is deleted (see below); any other session is closed in its agent. |
| `session/delete` | Closes the session in its agent if it is open, then deletes it there if the agent can (`sessionCapabilities.delete`); otherwise the overlay keeps a tombstone. Drops the journal. An unknown or already deleted session succeeds. |
| anything else with a `sessionId` | Forwarded to the session's agent. |

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
the agent whose updates it is not taking.

## Extensions

Per ACP's [extensibility rules](https://agentclientprotocol.com/protocol/v1/extensibility):
own fields only under `_meta` with namespaced keys, own methods prefixed with `_`.

| Name | Where | Meaning |
| --- | --- | --- |
| `gorynych/agent` | `session/new` params, `SessionInfo._meta`, `session/new` result | the adapter that owns the session |
| `gorynych/agent` | `initialize` params `_meta` | scopes the connection to one adapter: its default for `session/new` and the only one in `session/list` (the stdio bridge sets it for plain clients) |
| `gorynych/serviceId` | `initialize` result `_meta` | the service's persistent ID; a GUI keys sessions as service/session |
| `gorynych/agents` | `initialize` result `_meta` | the adapters this service offers (`name`, `displayName`) |
| `gorynych/origin` | `user_message_chunk._meta` | where a user message came from when the client did not send it: `peer` (an opinion request delivered as a turn), `channel` (one that reached Claude through its channel) or `terminal` |
| `gorynych/sender` | `user_message_chunk._meta` | who asked, for example `peer_claude` |
| `gorynych/opinion` | `_meta` of the request message and of every update of its turn | the request's `message_id`, so a GUI can show the exchange apart from the user's own turns |
| `_gorynych/status` | method | service ID, process, data folder, clients, sessions, pending requests, adapters |
| `_gorynych/shutdown` | method | asks the service to exit |
| `_gorynych/opinion` | method, `{sessionId, text, sender?}` → `{text}` | asks the agent of `sessionId` for an opinion; for Claude it goes through the channel and the result is Claude's `reply`. From the peers server it carries `_meta["gorynych/peerToken"]`, and the service names the sender (`peer_kimi`) and passes `fromSessionId` |
| `_gorynych/peers` | method, `{token}` → `{sessions}` | the sessions the session behind `token` can ask: the others that have history (a title; no drafts or deleted sessions), open ones first, then the newest; with agent, title, folder, `open` |
| `_gorynych/reply` | method, `{messageId, text}` → `{delivered}` | the asked session answers a request it got as a turn; accepted only from that session's token |
| `gorynych/peerToken` | `_meta` of `_gorynych/opinion` and `_gorynych/reply` | which session's `gorynych` MCP server is calling |

## Opinions between agents

Every session gets, in `mcpServers` (the ACP way to give an agent tools), a stdio MCP server
named `gorynych`: `Gorynych.Service peers --pipe <pipe> --token <token>`. The token is
random per session and tells the service who is calling. Its tools:

- `list_agents` — the user's other sessions (`_gorynych/peers`);
- `ask_agent {session_id, question}` — asks one and waits for the answer (`_gorynych/opinion`);
- `reply {message_id, text}` — answers a request this session was asked.

How a request reaches the asked agent depends on what it has:

- **Claude** has a channel. The Claude adapter adds `--channel-url` to the same `gorynych`
  server in its `--mcp-config`, so one server is both its peer tools and its development
  channel: the request arrives as a channel message, `reply` goes back through the channel.
- **Kimi** (any agent without a channel) gets the request as an ordinary `session/prompt`
  turn, marked in the text with the sender and `message_id` and asking for `reply`. ACP
  cannot inject into a running turn, so the request waits for the session's current turn
  (the user's prompts wait for it in turn). The answer is the `reply` tool call
  (`_gorynych/reply`); if the turn ends without one, the turn's own text. The request and
  every update of its turn carry `_meta["gorynych/opinion"]`.

A session that is answering a request cannot ask another agent meanwhile, so two agents
cannot wait on each other.

Approvals are the same for every agent: `list_agents` (read only) and `reply` (an answer the
user's own agent was asked for) run without asking — the Claude adapter allows them in its
PermissionRequest hook, the service answers Kimi's `session/request_permission` for them —
while `ask_agent` always asks the person, because it hands context to another agent and
spends its turns.

Verified live on 7 November 2026: Claude called `list_agents` and, after approval,
`ask_agent`; Kimi got the marked turn, answered with `reply`, and Claude used the answer.
Kimi asking Claude goes through Claude's channel the same way.

## Drafts and deletion

ACP clients may create sessions before the person types anything: Zed sends `session/new`
whenever a thread view opens and quits without `session/close`
([external-clients.md](external-clients.md)). A session made by `session/new` is a **draft**
until its first `session/prompt` or an opinion request delivered to it as a turn. A draft is
deleted when

- `session/close` arrives for it and no other client has it open, or
- the last client attached to it disconnects, or
- the service starts and finds it in the overlay from an earlier run.

Deleting a draft closes it in its agent and, for an agent that can delete (Kimi, whose
`session/new` makes a real empty session), sends `session/delete`; then the record and the
journal go. A draft the agent fails to delete stays marked and hidden, and the next start
tries again.

`session/delete` from a client works the same way for any session, except that an agent
that cannot delete (Claude: the service never touches `~/.claude`) keeps the session, so the
overlay keeps a tombstone (`deleted`) and the session disappears from `session/list`,
`_gorynych/peers`, `session/load` and `session/prompt` even though the agent still lists it.
ACP allows such a soft delete.

## Journal

Every `session/update` an adapter sends, and every prompt a client sends, is appended to
`journal/<session>.<hash>.jsonl` as the raw update object, so variants this schema
version does not know are kept and replayed unchanged. Past the limit (default 5000
entries) the journal merges consecutive text chunks of one message and then drops the
oldest entries down to three quarters of the limit. A line torn by a crash is dropped
when the journal is read back.

## Overlay

`sessions.json` holds, per session: the owning adapter, `cwd`, `title` and its source
(`agent` when the harness gave it, `prompt` for the provisional first-prompt title),
`createdAt`, `updatedAt`, and `draft` or `deleted` when set. Native state wins: a title or
`updatedAt` from the harness replaces the overlay's.

The file is replaced atomically (a temporary file moved over it). An antivirus scanner or
the indexer can hold it open for a moment, so the move is retried for up to about 400 ms; a
write that still fails is logged and the change stays in memory for the next write. The
activity stamp of a streaming turn is written at most once a second.
