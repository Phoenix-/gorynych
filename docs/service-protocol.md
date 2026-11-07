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
[--claude-model <model>] [--no-channel] [--no-peers] [--console]`. The same executable also
runs the two stdio helpers agents start: `channel --url …` (Claude's development channel)
and `peers --pipe … --token …` (the peers MCP server).

## Standard methods

| Method | What the service does |
| --- | --- |
| `initialize` | Answers as agent `gorynych`: `loadSession`, `sessionCapabilities.list/resume/close`. |
| `session/list` | Asks every adapter that can list (following its cursors), merges with the overlay, sorts by `updatedAt`, pages with its own cursor. Each `SessionInfo` carries `_meta["gorynych/agent"]`. |
| `session/new` | Picks the adapter from `_meta["gorynych/agent"]` (default: `--default-agent`, else the first), adds the peers MCP server to `mcpServers`, forwards, records the session, attaches the client. |
| `session/load` | Opens the session in the agent (`session/resume`, or `session/load` with the agent's replay dropped), replays the service journal as `session/update`, then attaches the client. A session the journal has never seen is loaded from the agent instead, and journaled. |
| `session/resume` | Opens the session in the agent and attaches the client, without replay. |
| `session/prompt` | Journals the prompt as `user_message_chunk` (shown to the session's other clients), sets the first prompt as a provisional title, forwards. The turn is not cancelled when the client disconnects; stopping it is `session/cancel`. |
| `session/cancel` | Forwarded to the agent. A cancel that arrives while its prompt is still on the way (the session being reopened, say) ends that prompt with `cancelled` before it reaches the agent; otherwise prompt and cancel pass one per-session gate, so the agent always sees the cancel after the prompt it stops. |
| `session/close`, anything else with a `sessionId` | Forwarded to the session's agent. |

Agent → client requests go to the most recently attached client of the session that can
take them: `session/request_permission` (an approval) to any client, `elicitation/create`
(a form, such as Claude's questions) only to a client that declared
`clientCapabilities.elicitation.form`. Toward its adapters the service declares form
elicitation, since it relays them. If none is attached the request waits; if
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
| `gorynych/serviceId` | `initialize` result `_meta` | the service's persistent ID; a GUI keys sessions as service/session |
| `gorynych/agents` | `initialize` result `_meta` | the adapters this service offers (`name`, `displayName`) |
| `gorynych/origin` | `user_message_chunk._meta` | where a user message came from when the client did not send it: `channel` (another agent) or `terminal` |
| `_gorynych/status` | method | service ID, process, data folder, clients, sessions, pending requests, adapters |
| `_gorynych/shutdown` | method | asks the service to exit |
| `_gorynych/opinion` | method, `{sessionId, text, sender?}` → `{text}` | asks the agent of `sessionId` for an opinion; for Claude it goes through the channel and the result is Claude's `reply`. From the peers server it carries `_meta["gorynych/peerToken"]`, and the service names the sender (`peer_kimi`) and passes `fromSessionId` |
| `_gorynych/peers` | method, `{token}` → `{sessions}` | the sessions the session behind `token` can ask (all others, with agent, title, folder) |
| `gorynych/peerToken` | `_meta` of `_gorynych/opinion` | which session's peers server is asking |

## Opinions between agents

Every session gets, in `mcpServers` (the ACP way to give an agent tools), a stdio MCP server
named `gorynych`: `Gorynych.Service peers --pipe <pipe> --token <token>`. The token is
random per session and tells the service who is asking. Its tools:

- `list_agents` — the user's other sessions (`_gorynych/peers`);
- `ask_agent {session_id, question}` — waits for the other agent's answer (`_gorynych/opinion`).

The agent asks for approval before calling them, like any MCP tool. Verified live on
7 November 2026: Kimi called `list_agents` and `ask_agent`, the question reached a running
Claude session through its channel from `peer_kimi`, and Claude's `reply` came back as the
tool result. Asking Kimi (or any agent without a channel) is not supported yet; the plan is
an ordinary turn marked with the sender.

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
`createdAt`, `updatedAt`. Native state wins: a title or `updatedAt` from the harness
replaces the overlay's.
