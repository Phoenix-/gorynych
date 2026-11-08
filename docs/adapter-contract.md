# Adapter contract

What an agent adapter must do so that every Gorynych feature behaves the same for every
agent, and what the service does with it. A new adapter (Codex, DeepSeek) is checked
against this document; [adapters.md](adapters.md) keeps what is particular to each adapter
that exists.

The contract is a profile of [ACP v1](https://agentclientprotocol.com/protocol/overview): an
adapter is an ACP agent, and the service is its ACP client. The profile names the ACP methods
and capabilities Gorynych relies on, and adds Gorynych's own extensions through ACP's
extension points: fields under `_meta` with `gorynych/` keys and methods with a `_gorynych/`
prefix ([service-protocol.md](service-protocol.md#extensions)). Types come from the schema
release in [acp-types.md](acp-types.md) (v1.29.1, `protocolVersion` 1); ACP features that are
still drafts or unstable are named as such where they matter. Checked against
agentclientprotocol.com on 8 November 2026.

The key words MUST, MUST NOT, REQUIRED, SHALL, SHALL NOT, SHOULD, SHOULD NOT, RECOMMENDED,
NOT RECOMMENDED, MAY and OPTIONAL are to be interpreted as described in BCP 14
([RFC 2119](https://www.rfc-editor.org/rfc/rfc2119), [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174))
when, and only when, they appear in all capitals.

Each feature below says what the adapter provides, what the service does with it, and the
fallback when the adapter does not provide it.

## Contents

1. [Building and starting an adapter](#1-building-and-starting-an-adapter)
2. [Initialization](#2-initialization)
3. [New sessions, the list and discovery](#3-new-sessions-the-list-and-discovery)
4. [Opening a session that is not loaded](#4-opening-a-session-that-is-not-loaded)
5. [Prompts and turns](#5-prompts-and-turns)
6. [Service turns: opinions and reports](#6-service-turns-opinions-and-reports)
7. [Closing and unloading](#7-closing-and-unloading)
8. [Approvals and forms](#8-approvals-and-forms)
9. [Titles](#9-titles)
10. [Delete](#10-delete)
11. [Archive](#11-archive)
12. [Child sessions](#12-child-sessions)
13. [Conformance](#13-conformance)
14. [Checking a new adapter](#14-checking-a-new-adapter)
15. [Open questions](#15-open-questions)

## 1. Building and starting an adapter

- An adapter MUST implement `IAgentAdapter` (`Gorynych.Acp.Hosting`) in an assembly the
  service references at build time. Nothing is loaded at run time, so the service stays
  Native AOT compatible.
- `StartAsync` MUST return the byte streams of one ACP connection: an external process's
  stdio (Kimi), or a `DuplexLink` to an in-process agent (Claude). The service starts the
  adapter on first use and again after its connection closes; every session is then not
  loaded ([§4](#4-opening-a-session-that-is-not-loaded)).
- An adapter MUST run the unmodified agent binary or API client under the user's own login on
  that machine, MUST NOT copy credentials between machines, and MUST NOT edit the agent's own
  configuration (`~/.claude`, `~/.codex`, `~/.kimi-code`). Everything it needs goes through
  per-launch flags, the agent's own protocol, hooks or a terminal.
- The adapter's name (`IAgentAdapter.Name`, such as `claude`) is what the service records per
  session and what `spawn_session` takes; it MUST be stable.

## 2. Initialization

- The adapter MUST answer `initialize` with `protocolVersion` 1 and the capabilities it has.
  The service reads `agentCapabilities.loadSession` and
  `agentCapabilities.sessionCapabilities.list`, `.resume`, `.close` and `.delete`; each
  feature below depends on one of them.
- The service declares no file system and no terminal (`fs.readTextFile`, `fs.writeTextFile`
  and `terminal` false), so the adapter MUST NOT call `fs/*` or `terminal/*`. It declares
  form elicitation, which it relays ([§8](#8-approvals-and-forms)).
- An adapter MAY advertise `authMethods`. The service does not call `authenticate`: the user
  logs in on the machine, and an authentication error from a turn reaches the client unchanged
  (Kimi's `-32000 Authentication required`).

## 3. New sessions, the list and discovery

**New.** The adapter MUST implement `session/new` with `cwd` and `mcpServers`, and MUST give
the agent the `mcpServers` it gets ([§6.2](#62-the-gorynych-mcp-server)).

- A session ID MUST stay the same across adapter restarts: the service keys its overlay, its
  journal and the peer token by it. The service treats IDs as opaque and passes them on
  exactly, so they MAY look different per agent (Claude: a bare UUID, Kimi: `session_…`).
- Clients create sessions eagerly ([external-clients.md](external-clients.md#zed)), so the
  service keeps a session a *draft* until its first prompt and deletes unused drafts. An
  adapter SHOULD keep nothing durable before the first prompt (Claude records a session at its
  first prompt). An adapter that does create a durable session at once MUST list it with no
  title until its first prompt (Kimi), so the service can tell it is empty before deleting it.
  Fallback: a draft the service cannot prove empty is hidden with a tombstone, never deleted.

**List.** The adapter SHOULD implement `session/list` (`sessionCapabilities.list`) with the
`cwd` filter, `title` and `updatedAt`, and MUST NOT list a session it deleted.

- The service merges the agents' lists with its overlay, keeps titles and folders in one
  spelling, sorts and pages with its own cursor. A session the service knows that a running
  agent no longer lists is taken to be gone and left out.
- The adapter SHOULD honour `cursor`. The service follows `nextCursor` for at most 20 pages.
- An adapter whose harness has a native archive SHOULD leave archived sessions out of the
  default list, as ACP's draft archive RFD does ([§11](#11-archive)).
- Fallback without `list`: the service lists only the sessions it recorded itself; sessions
  made outside Gorynych are not discovered.

## 4. Opening a session that is not loaded

A session is *loaded* when the adapter's current instance has it open. After `session/new`
it is; after an adapter restart, or a `session/close`, it is not.

- The adapter MUST implement `session/resume` (`sessionCapabilities.resume`) or
  `session/load` (`loadSession`) for every session it lists, also after a restart. It MUST
  honour the `mcpServers` of that request: the peer token is per session, and the service
  sends the same server again.
- The service prefers `session/resume`. With `session/load` it drops the agent's replay,
  because the history comes from its own journal; only for a session the journal has never
  seen does it let the agent replay and journals it.
- Opening SHOULD be cheap and start no model call. It MAY start no process either (Claude
  only registers the session; its process starts with the next prompt).
- Fallback without either: the session cannot be reopened after a restart, and using it fails
  with `-32002`.

## 5. Prompts and turns

- The adapter MUST implement `session/prompt` with text blocks, including a prompt of several
  text blocks (a spawned session's first prompt has two, [§12](#12-child-sessions)), and MUST
  end the turn with an ACP `stopReason`.
- A prompt for a session that is loaded but whose agent is not running MUST start the agent
  and run the turn (Claude after a resume).
- The service sends one turn at a time per session: a prompt, an opinion turn or a report turn
  waits for the running one. The adapter need not queue.
- `session/cancel` MUST stop the running turn, which then ends with `cancelled`.
- The adapter MUST NOT echo the client's own prompt as `user_message_chunk` (ACP does not);
  the service journals and shows it. A user message that did not come from `session/prompt`
  (typed in a terminal) SHOULD be sent as `user_message_chunk` with `_meta["gorynych/origin"]`.
- The adapter SHOULD stream `agent_message_chunk`, `tool_call` and `tool_call_update` as they
  happen: a report carries the child's final message, its text after its last tool call.
- The adapter MAY ignore a prompt's `_meta`; it MUST pass the text to the agent unchanged.

## 6. Service turns: opinions and reports

A *service turn* is a turn the service runs on a session on another session's behalf: an
opinion request asked as a turn (`ask_agent`), or a child's report to its parent. A user's
prompt and a spawned session's first turn are not service turns.

- The service sends it as `session/prompt` with one text block and `_meta`:
  `gorynych/origin` (`peer` or `report`), `gorynych/sender` (a label for GUIs),
  `gorynych/fromSessionId`, and for an opinion `gorynych/opinion` (its `message_id`). The
  text's first line names the sending session by its ID, and the rest says what to do
  ([service-protocol.md](service-protocol.md#what-agents-are-told)).
- The turn waits for the session's running turn ([§5](#5-prompts-and-turns)).

### 6.1 Waking a session, and closing it again

- A service turn for a session that is not loaded opens it first ([§4](#4-opening-a-session-that-is-not-loaded)),
  and a turn whose agent is not running starts it ([§5](#5-prompts-and-turns)). An agent that
  refuses the live-note path because it is not running ([§6.4](#64-the-live-note-path)) gets
  the request as a turn instead, which starts it.
- A session the service opened or started only for service turns is closed again
  (`session/close`) after the last of them, unless a client opened, prompted or otherwise used
  it meanwhile (it then stays with that client), or another service turn waits for it (the
  last one closes it). A session that was already loaded and running stays as it was.
- The adapter SHOULD implement `session/close` so this frees the agent's resources
  ([§7](#7-closing-and-unloading)). Fallback: the session stays loaded.
- An archived session is woken the same way and stays archived.
- If the session cannot be opened or started, `ask_agent` fails with
  `session_id <id> (<agent>, "<title>") could not be started to answer: <reason> Ask another
  session from list_agents, or ask the user to open that session and check it.` A report that
  cannot be delivered is logged.

### 6.2 The gorynych MCP server

- The service adds a stdio MCP server named `gorynych` to the `mcpServers` of every
  `session/new`, `session/resume` and `session/load` (`Gorynych.Service peers --pipe … --token …`).
  The adapter MUST give it to the agent for that session, on every launch, with its command and
  arguments unchanged, and MUST NOT give it to another session. It MAY add arguments of its own
  (Claude adds `--channel-url`).
- Its tools: `list_agents`, `ask_agent`, `spawn_session`, `archive_session`,
  `restore_session` and `reply`. Their texts are in
  [service-protocol.md](service-protocol.md#opinions-between-agents).
- `list_agents`, `reply` and `spawn_session` run without the agent's approval; the others ask
  the person. An adapter whose approvals reach the service as `session/request_permission`
  SHOULD title them `mcp__gorynych__<tool>`, as Kimi does, so the service can answer the
  first three itself. An adapter that approves tools on its own MUST allow exactly those three
  without asking (Claude's PermissionRequest hook). Fallback: the person is asked for all.
- Fallback without the server: the agent cannot ask, answer, spawn or archive; an opinion
  asked as a turn is answered with the turn's text.

### 6.3 Answering with reply

- An agent answers an opinion request with `reply {message_id, text}`. The peers server tries
  the live-note path first, when it has one, then the service (`_gorynych/reply`), so a
  request asked as a turn is answered even by an agent that has a live-note path.
- If the turn ends without `reply`, its text is the answer.
- The service's `message_id`s start with `m`. An adapter's own live-note `message_id`s MUST
  NOT start with `m`, so neither path takes the other's answer (Claude's channel uses `c`).

### 6.4 The live-note path

OPTIONAL. Lets the service reach an agent that is running without starting a turn, also
in the middle of one.

- `_gorynych/opinion {sessionId, text, sender, fromSessionId?}` → `{text}`: asks for an
  opinion and returns the agent's `reply`.
- `_gorynych/note {sessionId, text, sender, fromSessionId?}` → `{}`: tells the agent something
  that needs no answer (a report).
- An adapter without the path MUST answer both with `-32601` (method not found); the service
  then uses a turn. An adapter that has the path but whose agent is not running MUST answer
  `-32600`; the service then uses a turn that starts the agent ([§6.1](#61-waking-a-session-and-closing-it-again)).
- The adapter MUST pass the text unchanged, whose first line names the sending session, and
  SHOULD NOT show the agent the `sender` label, which an agent could take for a session ID.
- Today only Claude has it, through its development channel ([adapters.md](adapters.md#mcp-servers-and-the-channel-opinions-both-ways)).

## 7. Closing and unloading

- The adapter SHOULD implement `session/close` (`sessionCapabilities.close`): it stops the
  session's turn and frees what the session holds (Claude's process), and the session stays
  listed and can be opened again.
- The service sends it when the last client lets go of a session, after the service turns of
  a session it woke ([§6.1](#61-waking-a-session-and-closing-it-again)), and before a delete.
- Fallback without it: before a delete the service sends `session/cancel` instead, and nothing
  is ever unloaded until the adapter restarts.

## 8. Approvals and forms

- Approvals MUST be `session/request_permission` with ACP's option kinds (`allow_once`,
  `allow_always`, `reject_once`, `reject_always`) and distinct option IDs; Zed rejects anything
  else ([external-clients.md](external-clients.md#what-zed-does-with-sessions)). The service
  relays each to the client that attached to the session last, waits while none is attached,
  and asks the next client if one goes away before answering.
- When the agent abandons a question, the adapter SHOULD withdraw it with `$/cancel_request`;
  the service withdraws it from the client too.
- Forms MAY be `elicitation/create` in form mode. The service relays them to a client that
  declared forms; for one that did not, it asks each choice field as a
  `session/request_permission` and declines a form with free text. The adapter SHOULD handle a
  decline (Claude then asks in plain text).

## 9. Titles

- The adapter SHOULD send `session_info_update` with `title` when its harness titles a session,
  and SHOULD list the title in `session/list`. The harness's title is the source of truth.
- The service shows the first prompt as a provisional title until the harness sends one. A name
  given to `spawn_session` stays in the overlay and wins over the harness's titles, because no
  adapter can set a native title yet.
- An adapter MAY offer a way to set the native title later (Codex has `thread/name/set`;
  Claude's `/rename` needs the terminal). There is no ACP method for it.

## 10. Delete

- When the harness can delete, the adapter SHOULD implement `session/delete`
  (`sessionCapabilities.delete`) and MUST NOT list the session afterwards. For a session it
  does not have it SHOULD answer `-32002`; the service also accepts any error while the agent no
  longer lists the session (Kimi answers `-32602`).
- The service closes the session first ([§7](#7-closing-and-unloading)) and drops its journal.
- Fallback without it: the service keeps a tombstone in its overlay that hides the session
  everywhere, while the harness keeps it (Claude).

## 11. Archive

Gorynych's archive is a flag in the service's overlay
([service-protocol.md](service-protocol.md#archive)): an archived session is left out of the
lists but works as before, and archiving closes nothing. No adapter is asked to do anything for
it, and the service never writes a harness's files for it.

ACP has a draft RFD, [Session Archive and Unarchive](https://agentclientprotocol.com/rfds/session-archive)
(revised 30 October 2026, to be implemented behind `unstable_session_archive`):
`session/archive` and `session/unarchive`, the capability `sessionCapabilities.archive`, a
`session/list` filter `archived` (`exclude`, `include`, `only`) and `SessionInfo.archived`. Its
archive closes an active session, and an agent MAY refuse to load or resume an archived one.

- An adapter MAY advertise `sessionCapabilities.archive`; if it does, it MUST follow that RFD.
  The service does not use it yet ([§15](#15-open-questions)).
- An adapter whose harness has a native archive SHOULD leave natively archived sessions out of
  the default `session/list`. The service then takes them for sessions the agent no longer
  has, so an archive made in the harness's own UI is not mirrored (Kimi today).

## 12. Child sessions

- Spawning needs nothing beyond §3–§6: the service creates the child with `session/new` (with
  the `gorynych` server), and starts its first prompt at once, as two text blocks: the task,
  then where it came from. An adapter whose harness titles a session from its first prompt
  SHOULD title it from the first block (Kimi does).
- Reports to the parent are service turns or live notes ([§6](#6-service-turns-opinions-and-reports)).
- The approval card, the parent link and the reports are the service's; the adapter does
  nothing for them.

## 13. Conformance

Claude and Kimi as built; Codex and DeepSeek as expected, nothing built. The Codex column is
from the app-server documentation (https://learn.chatgpt.com/docs/app-server, read
8 November 2026) and a probe of Codex 0.167 on 6 November 2026; `codex app-server` speaks its
own JSON-RPC, not ACP, so its adapter translates, like Claude's.

| Feature | Claude Code (in-process adapter, TUI in a pseudo console) | Kimi Code 2.5.2 (`kimi acp`, native ACP) | Codex (`codex app-server`), expected | DeepSeek, expected |
| --- | --- | --- | --- | --- |
| Capabilities | list, resume, close | load, list, resume, close, delete (and fork) | list, resume, close, delete | list, load, close, delete over its own store |
| New ([§3](#3-new-sessions-the-list-and-discovery)) | UUID from the adapter; recorded at the first prompt | `session_…`; a real session at once, listed untitled | `thread/start` | its own store: the adapter keeps the history, since DeepSeek has no session store; ID scheme to be chosen |
| List | the adapter's own list of the sessions it started | Kimi's list; leaves natively archived sessions out; ignores `cursor` | `thread/list` (`cursor`, `limit`, `archived`; archived left out by default) | its own store |
| Open not loaded ([§4](#4-opening-a-session-that-is-not-loaded)) | `session/resume` only registers; the process starts with the next prompt | `session/resume` | `thread/resume` (loads the thread) | `session/load` from its own store, which MUST keep the whole history |
| Turns ([§5](#5-prompts-and-turns)) | bracketed paste into the TUI; `Stop` hook ends the turn | native | `turn/start`; `turn/interrupt` for `session/cancel` | API calls |
| Service turns and waking ([§6.1](#61-waking-a-session-and-closing-it-again)) | turns start the process; closed again afterwards | turns; resumed, then closed again | expected the same | expected the same |
| `gorynych` MCP server ([§6.2](#62-the-gorynych-mcp-server)) | per-launch `--mcp-config` | through ACP `mcpServers` | unknown: how to give one thread an MCP server without editing `~/.codex` | unknown: depends on its MCP client |
| Live-note path ([§6.4](#64-the-live-note-path)) | development channel; `-32600` while not running | none (`-32601`) | none planned; `turn/steer` appends only to an active turn | none |
| Approvals ([§8](#8-approvals-and-forms)) | PermissionRequest hook; pre-allows the three tools | Kimi's own `session/request_permission`; the service pre-allows | `item/commandExecution/requestApproval`, `item/fileChange/requestApproval`, `item/permissions/requestApproval` | unknown |
| Forms | `AskUserQuestion` as `elicitation/create` | none seen | `tool/requestUserInput` (experimental), `mcpServer/elicitation/request` | unknown |
| Titles ([§9](#9-titles)) | `ai-title` / `custom-title` from the transcript; no way to set one | the first prompt, cut at 200 characters; no way to set one | `thread/name/set`, `thread/name/updated`; the first user message otherwise | its own |
| Close ([§7](#7-closing-and-unloading)) | ends the process | `session/close`, still listed | `thread/unsubscribe` (unloads after a grace period, `thread/closed`) | frees nothing native |
| Delete ([§10](#10-delete)) | none: a tombstone | `session/delete` (`-32602` for an unknown one) | `thread/delete` (also deletes spawned descendants) | its own store |
| Archive ([§11](#11-archive)) | none: the overlay | native in Kimi's UI, not in `kimi acp`: the overlay | `thread/archive`, `thread/unarchive`; not used: the overlay | none: the overlay |

## 14. Checking a new adapter

1. Go through §1–§12 and fill in a column of the table above, marking every MUST it cannot meet.
2. The service side of each feature is covered by contract tests against scripted agents
   (`tests/Gorynych.Tests`: `SessionLifecycleTests`, `OpinionTurnTests`, `SessionTreeTests`,
   `ArchiveTests`, `WakeTests`). A new adapter needs tests of its own translation, as
   `ClaudeAdapterTests` and `ClaudeHooksTests` do for Claude.
3. Add a live smoke test under `GORYNYCH_LIVE=1` (`LiveSmokeTests`): a first prompt, an
   approval, a title, a reload, an `ask_agent` both ways, and a service turn that wakes a
   session that is not loaded.

## 15. Open questions

- **The ACP archive RFD.** Gorynych's archive keeps a session usable and closes nothing; the
  draft RFD closes the session and lets an agent refuse to resume it. Whether the service
  should mirror its flag into a native archive that an adapter advertises (Codex
  `thread/archive`) is not decided.
- **Claude's "loaded".** For Claude, loaded means registered in the adapter, not that its
  process runs. The service learns that the process is not running only from the live-note
  path's `-32600`; without a channel it cannot tell, so it then closes a Claude session only
  when it opened it itself.
- **Codex.** How to give one thread the `gorynych` MCP server per launch without touching
  `~/.codex` is not known yet, nor whether `turn/steer` is worth using as a live-note path.
- **Titles.** Neither Claude nor Kimi lets the service set a native title.
- **Kimi's cursor.** Kimi 2.5.2 ignores `session/list`'s `cursor`
  ([adapters.md](adapters.md#kimi-code)).
