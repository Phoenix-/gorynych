# Adapter contract

What an agent adapter must do so that every Gorynych feature behaves the same for every
agent, and what the service does with it. A new adapter (Codex, DeepSeek) is checked
against this document; [adapters.md](adapters.md) keeps what is particular to each adapter
that exists. Codex was the first adapter written from it rather than alongside it; what that
changed is marked "(Codex)", and what DeepSeek Harness changed after it, "(DSH)".

The contract is a profile of [ACP v1](https://agentclientprotocol.com/protocol/overview): an
adapter is an ACP agent, and the service is its ACP client. The profile names the ACP methods
and capabilities Gorynych relies on, and adds Gorynych's own extensions through ACP's
extension points: fields under `_meta` with `gorynych/` keys and methods with a `_gorynych/`
prefix ([service-protocol.md](service-protocol.md#extensions)). Types come from the schema in
[acp-types.md](acp-types.md) (`protocolVersion` 1; for now ACP's main branch at 7441d10, which
is release v1.29.1 plus the completed client-system-prompt RFD); ACP features that are still
drafts or unstable are named as such where they matter. Checked against
agentclientprotocol.com and ACP's repository on 8 November 2026.

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
  configuration (`~/.claude`, `~/.codex`, `~/.kimi-code`, `~/.dsh`). Everything it needs goes through
  per-launch flags, the agent's own protocol, hooks or a terminal.
- The adapter's name (`IAgentAdapter.Name`, such as `claude`) is what the service records per
  session and what `spawn_session` takes; it MUST be stable.
- An adapter MUST start its agent in a folder of its own, never in a session's folder: an agent
  may read its launch folder's configuration (DSH loads its `.env` into its environment), which
  would then hold for every session. (DSH)
- An adapter MUST start its agent as a descendant of the service process, so that the MCP
  servers the agent starts from the user's own configuration are recognised as inside
  Gorynych: the `gorynych-outside` server, which the user registers for agents outside
  Gorynych, hides its tools when a Gorynych service is among its ancestors
  ([service-protocol.md](service-protocol.md#hiding-from-sessions-inside-gorynych)). An
  in-process adapter starts its agent as the service's child; an external adapter is one
  itself. An agent that starts its MCP servers through a separate long-lived process (a daemon
  that outlives the session) breaks this; its conformance row says so. The walk exists only on
  Windows yet.
- An adapter MUST NOT drive its agent through a daemon shared with other clients (Codex's
  `codex app-server daemon`): it starts MCP servers outside the service's process tree. (Codex)
- Where the agent lets a session turn off one of the user's own MCP servers, the adapter MAY
  turn off `gorynych-outside` for its sessions, which hides it on every OS (Codex's per-thread
  `config`). (Codex)

## 2. Initialization

- The adapter MUST answer `initialize` with `protocolVersion` 1 and the capabilities it has.
  The service reads `agentCapabilities.loadSession` and
  `agentCapabilities.sessionCapabilities.list`, `.resume`, `.close`, `.delete` and
  `.systemPrompt`; each feature below depends on one of them.
- The service reads no Gorynych flag from an adapter: it finds the live-note path by calling
  it ([§6.4](#64-the-live-note-path)). A flag that an adapter's extension would need goes in
  `agentCapabilities._meta` under a `gorynych/` key, as ACP's extensibility rules (advertising
  custom capabilities) say, not in the result's own `_meta`; the service advertises its own
  flags that way ([service-protocol.md](service-protocol.md#methods)).
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
  exactly, so they MAY look different per agent (Claude, Codex: a bare UUID, Kimi: `session_…`).
- Clients create sessions eagerly ([external-clients.md](external-clients.md#zed)), so the
  service keeps a session a *draft* until its first prompt and deletes unused drafts. An
  adapter SHOULD keep nothing durable before the first prompt (Claude records a session at its
  first prompt). An adapter that does create a durable session at once MUST list it with no
  title until its first prompt (Kimi), so the service can tell it is empty before deleting it.
  Fallback: a draft the service cannot prove empty is hidden with a tombstone, never deleted.
- An adapter that can delete but whose agent keeps nothing durable before the first prompt
  SHOULD say so with `agentCapabilities._meta["gorynych/draftsNotDurable"]: true`; the service
  then closes an unused draft and drops it, with no delete and no tombstone. (Codex, which
  writes a thread at its first turn)

**List.** The adapter SHOULD implement `session/list` (`sessionCapabilities.list`) with the
`cwd` filter, `title` and `updatedAt`, and MUST NOT list a session it deleted.

- The service merges the agents' lists with its overlay, keeps titles and folders in one
  spelling, sorts and pages with its own cursor. A session the service knows that a running
  agent no longer lists is taken to be gone and left out.
- So an adapter MUST list the sessions it has loaded. Where its agent leaves them out of its
  own `session/list`, the adapter adds them to the first page, in the requested folder. DSH
  leaves out the sessions open in its own process. (DSH)
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
  sends the same server again. An agent that keeps a session's MCP servers only while it is
  loaded (Codex) gets them from the request that loads it. An adapter that advertises `sessionCapabilities.systemPrompt`
  MUST honour the request's `systemPrompt` too ([§6.2](#62-the-gorynych-mcp-server)).
- The service prefers `session/resume`. With `session/load` it drops the agent's replay,
  because the history comes from its own journal; only for a session the journal has never
  seen does it let the agent replay and journals it.
- Opening SHOULD be cheap and start no model call. It MAY start no process either (Claude
  only registers the session; its process starts with the next prompt).
- Fallback without either: the session cannot be reopened after a restart, and using it fails
  with `-32002`.
- An agent may let one process at a time hold a session (Codex: one writer per thread). An
  adapter that can tell which other app holds one SHOULD mark it in `session/list` with
  `_meta["gorynych/openElsewhere"]` (a string naming the holder), SHOULD refuse its load or
  resume at once, before asking the agent, with that string in the error's `data` under the same
  key and a message that says to close it there, and MAY advertise `gorynych/openElsewhere` and
  answer `_gorynych/open_elsewhere {sessionIds}` ([service-protocol.md](service-protocol.md#sessions-open-in-another-app)).
  A guess on the agent's insides MUST fail open: when it cannot tell, it marks nothing and lets
  the agent answer. (Codex)

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

**Message IDs.** ACP's stable [message-id](https://agentclientprotocol.com/rfds/message-id)
field names the message that each `agent_message_chunk`, `agent_thought_chunk` and
`user_message_chunk` belongs to.

- The adapter SHOULD give every such chunk a `messageId`: the same on all chunks of one
  message, never the same for two messages of the session, and taken from the harness where it
  has one (its native state is the source of truth), generated otherwise. Claude's are in
  [adapters.md](adapters.md#claude-code).
- A thought and an answer are two messages. An adapter whose harness gives both one ID derives a
  distinct, stable one for the thought (DSH: `<id>:thought`). (DSH)
- The service passes an adapter's IDs on unchanged. Fallback: a chunk without one gets one from
  the service as it publishes it. Consecutive chunks of one kind are one message, as a client
  would infer without IDs, and any other part of the turn (a chunk of another kind or with its
  own ID, a tool call, a plan) ends it; a title or usage update does not.
- Messages the service writes itself (the client's prompt, an opinion request or a report
  asked as a turn, a spawned session's first prompt) get the service's IDs, one per message.
  Every `session/prompt` the service sends carries that ID in `_meta["gorynych/messageId"]`. An
  adapter whose harness can keep a client's ID with the message SHOULD store it there, so
  history replayed from the harness has the journal's IDs (Codex's `clientUserMessageId`).
  (Codex)
- The journal keeps the updates as they were published, so a replay on `session/load` has the
  live stream's IDs. When it compacts, it merges text chunks only when they share a `messageId`.

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
- A session the service opened or started for service turns is closed again
  (`session/close`) like any session with no client: once it has been idle (no turn running,
  no operation registered, the next queued service turn included) for the service's idle time
  (`--unload-after`, 10 minutes by default), so a follow-up turn within it finds the session
  loaded. A client that opens it meanwhile keeps it while it has it open
  ([service-protocol.md](service-protocol.md#unloading-idle-sessions)).
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
- All six run without the agent's approval ([service-protocol.md](service-protocol.md#approvals)).
  An adapter whose approvals reach the service as `session/request_permission` SHOULD title
  them `mcp__gorynych__<tool>`, as Kimi does, so the service can answer them itself. An adapter
  that approves tools on its own MUST allow them without asking (Claude: a per-launch allow
  rule and its own approvals; Codex: the server's `default_tools_approval_mode`), and MUST
  leave the person's own deny rules in force. Fallback: the person is asked for each.
- The service asks the person itself where the person must decide: before a spawn the user
  did not ask for in their own turn, and before a session that is not in full access asks one
  that is. A child starts with no mode in its `session/new`, so in its agent's default. For that an adapter whose agent
  has a mode in which it runs anything without asking MUST mark that mode's value with
  `_meta["gorynych/fullAccess"]: true` in its mode option (category `mode`) or `SessionMode`,
  in every state it reports (Claude's `bypassPermissions`, DSH's and Codex's `full-access`).
  Fallback: an unmarked mode counts as not full access, so a question into it is not asked.
- Fallback without the server: the agent cannot ask, answer, spawn or archive; an opinion
  asked as a turn is answered with the turn's text.

**The instructions as a system prompt.** ACP's
[client-system-prompt](https://agentclientprotocol.com/rfds/client-system-prompt) RFD (completed
6 November 2026) lets a client add to the agent's system prompt.

- With the server, the service sends what it tells agents about these tools as
  `systemPrompt {mode: "append", content: [text]}` on every `session/new`, `session/load` and
  `session/resume` (agents need not keep it) to an adapter that advertises
  `sessionCapabilities.systemPrompt`. The text is in
  [service-protocol.md](service-protocol.md#what-agents-are-told); it names no transport.
- An adapter that advertises the capability MUST support `append` and SHOULD give the content
  system-level precedence (Claude: `--append-system-prompt`). It MUST apply the latest
  request's value, so a resume without one leaves none, unless its agent keeps the instructions
  with the session: then the session keeps those it was given, and the adapter SHOULD replace
  them where the agent allows (Codex keeps the instructions a thread was created with and does
  not replace them on resume). (Codex) It MUST fail the request with
  `-32602`, instead of creating, loading or resuming the session, for a mode it does not
  advertise and for content it cannot apply (an empty list, a block that is not text). It MAY
  add text of its own for its own delivery (Claude's channel authorization comes first).
- Fallback without the capability: the agent learns the tools from the server's MCP
  `instructions` and the tools' descriptions, and each request's or report's text says what to
  do (Kimi).
- An adapter whose agent takes instructions only per process (DSH: a `--patch` with
  `personaSuffix`) applies the service's text at launch, from its options, and does not advertise
  the capability. It MAY add text of its own for that harness (DSH names the gorynych tools in
  full, since the harness has a `list_agents` of its own). (DSH)

### 6.3 Answering with reply

- An agent answers an opinion request with `reply {message_id, text}`. The peers server tries
  the live-note path first, when it has one, then the service (`_gorynych/reply`), so a
  request asked as a turn is answered even by an agent that has a live-note path.
- If the turn ends without `reply`, its text is the answer.
- The service's `message_id`s start with `m`. An adapter's own live-note `message_id`s MUST
  NOT start with `m`, so neither path takes the other's answer (Claude's channel uses `c`).
  Both are `<letter><run tag>-<count>` (`mk7q2-3`, `c9xw4-1`): a count kept in memory starts
  again after a restart, and an adapter SHOULD tag its own the same way, so a late reply to a
  request of an earlier run does not answer a new one.

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
- The request's `_meta` holds the keys the journal keeps on a request asked in a turn
  (`gorynych/sender`, and `gorynych/fromSessionId` or `gorynych/outside`). When the agent
  reports the delivered message as a `user_message_chunk`, the adapter SHOULD put those keys on
  it, with `gorynych/origin` saying how it arrived and, for an opinion, `gorynych/opinion` set
  to its own `message_id`; the GUI and `read_session` tell requests and reports apart by them.
- Today only Claude has it: on Claude's input when it runs headless ([adapters.md](adapters.md#live-notes-opinions-and-reports-on-stdin)),
  through its development channel in the TUI ([adapters.md](adapters.md#mcp-servers-and-the-channel-opinions-both-ways)).

## 7. Closing and unloading

- The adapter SHOULD implement `session/close` (`sessionCapabilities.close`): it stops the
  session's turn and frees what the session holds (Claude's process), and the session stays
  listed and can be opened again.
- A prompt still running when its session is closed, deleted or unloaded by the agent MUST
  end (`cancelled`), even when the agent never reports the turn's end; a cancel that comes
  before the agent has named the turn MUST still stop it. (Codex)
- The service sends it to unload a session that has been idle with no client attached for its
  idle time (`--unload-after`): one a client let go of, with `session/close` or by
  disconnecting without it (Zed when it quits), and one the service woke for service turns
  ([§6.1](#61-waking-a-session-and-closing-it-again)). It sends it at once only before a
  delete. It never sends it while a turn of the session runs, or while a request of the agent
  waits for a client (an approval or a form). A client's `session/close` cancels a running turn
  that a client's prompt started with `session/cancel` instead, and nothing else
  ([service-protocol.md](service-protocol.md#unloading-idle-sessions)).
- **An adapter's idle time.** An adapter whose loaded sessions lock out its agent's own apps
  MAY ask for a shorter idle time with `agentCapabilities._meta["gorynych/unloadAfter"]`
  (seconds); the service takes the shorter of it and `--unload-after`. Codex asks for 120: a
  loaded thread cannot be continued in Codex Desktop, and loading it again costs about two
  seconds. (Codex)
- **Turns of the agent's own.** An adapter whose agent can run a turn the service did not start
  (a message through the agent's own channel, the person typing in its terminal) SHOULD mark
  it: `_meta["gorynych/agentTurn"]: "started"` on the turn's first `session/update` (Claude:
  the `user_message_chunk` of the prompt), and `"ended"` on an update when it ends (Claude: a
  `session_info_update` after `Stop`, `StopFailure` or the process exiting). The service holds
  the session loaded in between. A start marked again before its end counts once; a missed end
  keeps the session loaded until the agent's connection closes. Fallback without the marks: the
  service unloads a session only once the idle time has passed since the agent's last update,
  so only a turn quiet for longer than that can be stopped.
- Fallback without it: before a delete the service sends `session/cancel` instead, and nothing
  is ever unloaded until the adapter restarts.

## 8. Approvals and forms

- Approvals MUST be `session/request_permission` with ACP's option kinds (`allow_once`,
  `allow_always`, `reject_once`, `reject_always`) and distinct option IDs; Zed rejects anything
  else ([external-clients.md](external-clients.md#what-zed-does-with-sessions)).
- An adapter MUST NOT offer an approval option whose effect is written to the agent's own
  configuration or kept beyond the session (Codex's exec-policy and network amendments, which
  go to `~/.codex/rules/default.rules`, and MCP's `persist: always`). `allow_always` then means
  "for this session". (Codex) An option ID the adapter did not offer MUST count as a refusal,
  never as an approval. (Codex) The service
  relays each to the client that attached to the session last, waits while none is attached,
  and asks the next client if one goes away before answering.
- ACP lets a permission request name its tool call by `toolCallId` alone. The adapter SHOULD
  send the tool call's title and input with the request (DSH's adapter fills them from the
  earlier `tool_call`). The service's pre-allow also matches a bare request by the title the call
  was published with. (DSH)
- When the agent abandons a question, the adapter SHOULD withdraw it with `$/cancel_request`;
  the service withdraws it from the client too. Where the agent drops its own question without
  withdrawing it (DSH on a cancel), the adapter withdraws it when the tool call ends or the
  prompt does, and drops a late answer. (DSH)
- Forms MAY be `elicitation/create` in form mode. The service relays them to a client that
  declared forms; for one that did not, it asks each choice field as a
  `session/request_permission` and declines a form with free text. The adapter SHOULD handle a
  decline (Claude then asks in plain text).

## 9. Titles

- The adapter SHOULD send `session_info_update` with `title` when its harness titles a session,
  and SHOULD list the title in `session/list`. The harness's title is the source of truth.
- The service shows the first prompt as a provisional title until the harness sends one. A name
  given to `spawn_session` stays in the overlay and wins over the harness's titles.
- An adapter whose harness can set a native title MAY take `_gorynych/set_title {sessionId,
  title}` → `{}` and advertise it with `agentCapabilities._meta["gorynych/setTitle"]: true`
  (ACP has no method for it). The service then writes explicit names natively: a
  `spawn_session` name, and later a rename. It never writes the provisional title, since the
  harness shows a nameless session by its first message anyway. Codex has `thread/name/set`;
  headless Claude has the `rename_session` control request; the TUI's `/rename` would need the
  terminal. (Codex, Claude headless)

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
  has, so an archive made in the harness's own UI is not mirrored (Kimi today, and Codex).
- Codex's native archive refuses to resume an archived thread, as the RFD allows, so mirroring
  Gorynych's archive into it would make archived sessions unusable; the overlay stays. (Codex)

## 12. Child sessions

- Spawning needs nothing beyond §3–§6: the service creates the child with `session/new` (with
  the `gorynych` server), and starts its first prompt at once, as two text blocks: the task,
  then where it came from. An adapter whose harness titles a session from its first prompt
  SHOULD title it from the first block (Kimi does).
- Reports to the parent are service turns or live notes ([§6](#6-service-turns-opinions-and-reports)).
- The approval card, the parent link and the reports are the service's; the adapter does
  nothing for them.

## 13. Conformance

Claude (both transports: headless by default, the TUI as the fallback), Kimi, Codex and
DeepSeek Harness as built. The Codex column is Codex 0.167.0 as built
and probed on 8 November 2026 ([adapters.md](adapters.md#codex)); `codex app-server` speaks its
own JSON-RPC, not ACP, so its adapter translates, like Claude's. The DSH column is DeepSeek
Harness 0.2.2-rc.1 as built and probed on 9 November 2026 ([adapters.md](adapters.md#deepseek-harness));
DSH speaks ACP, and its adapter is a thin proxy.

| Feature | Claude Code, headless (default: in-process adapter over `claude -p` with stream-json) | Claude Code, TUI (fallback, `--claude-transport tui`: the TUI in a pseudo console) | Kimi Code 2.5.2 (`kimi acp`, native ACP) | Codex 0.167 (in-process adapter over `codex app-server`) | DeepSeek Harness 0.2.2-rc.1 (`dsh --profile acp`, native ACP, in-process proxy; one process per permission mode) |
| --- | --- | --- | --- | --- | --- |
| Capabilities | list, resume, close, systemPrompt; `gorynych/setTitle`; image prompts | list, resume, close, systemPrompt | load, list, resume, close, delete (and fork) | load, list, resume, close, delete, systemPrompt; `gorynych/setTitle`, `gorynych/unloadAfter` 120, `gorynych/draftsNotDurable`, `gorynych/openElsewhere` | list, resume, close; `gorynych/unloadAfter` 120 |
| New ([§3](#3-new-sessions-the-list-and-discovery)) | UUID from the adapter; recorded at the first prompt | UUID from the adapter; recorded at the first prompt | `session_…`; a real session at once, listed untitled | `thread/start`; nothing on disk before the first turn | UUID from DSH; durable at once, never titled, so an unused draft gets a tombstone |
| List | the adapter's own list of the sessions it started | the adapter's own list of the sessions it started | Kimi's list; leaves natively archived sessions out; ignores `cursor` | `thread/list` (`cursor`, `limit`, exact `cwd`); archived left out; title = the thread's `name` | the read-only process's `{sessionId, cwd}` with cursor and `cwd`; the sessions open in either process added back by the proxy and the router; no title or `updatedAt` |
| Open not loaded ([§4](#4-opening-a-session-that-is-not-loaded)) | `session/resume` only registers; the process starts with the next prompt | `session/resume` only registers; the process starts with the next prompt | `session/resume` | `thread/resume`, `excludeTurns`; `session/load` replays `thread/items/list`; refused at once, naming the holder, while another Codex app has the thread (one writer; Windows), else by app-server; fails when it is archived in Codex | `session/resume` in the process of the session's mode, about 0.2 s; fails while `dsh web` or another DSH process has the session (one writer); no `session/load` |
| Turns ([§5](#5-prompts-and-turns)) | a user message on stdin; the `result` ends the turn; `interrupt` for `session/cancel` | bracketed paste into the TUI; the `Stop` hook ends the turn | native | `turn/start`; `turn/interrupt` for `session/cancel` | native; subagents run inside the prompt |
| Service turns and waking ([§6.1](#61-waking-a-session-and-closing-it-again)) | turns start the process; unloaded after the idle time | turns start the process; unloaded after the idle time | turns; resumed, then unloaded after the idle time | turns; resumed, then unloaded after its 2-minute idle time | turns; resumed, then unloaded after its 2-minute idle time |
| `gorynych` MCP server ([§6.2](#62-the-gorynych-mcp-server)) | per-launch `--mcp-config` | per-launch `--mcp-config` | through ACP `mcpServers` | per-thread `config.mcp_servers` on `thread/start` and every `thread/resume` (kept only while loaded) | through ACP `mcpServers` on new and every resume |
| System prompt ([§6.2](#62-the-gorynych-mcp-server)) | `sessionCapabilities.systemPrompt: {}` (append only): `--append-system-prompt`, after the adapter's live-note authorization; other modes and non-text content fail with `-32602` | as headless | not advertised by 2.5.2: the MCP instructions and the requests' texts | append only: `developerInstructions`; Codex keeps those a thread was created with | per launch: `personaSuffix` in the `--patch`, not advertised |
| Hidden from `gorynych-outside` ([§1](#1-building-and-starting-an-adapter)) | yes: `claude` is the service's child and starts MCP servers as its own children (probe); the adapter's `--settings` also denies `mcp__gorynych-outside` | as headless | yes: `kimi acp` is the service's child and starts MCP servers as its own children (probe) | yes: `codex.exe` is the service's child and starts MCP servers as its own children (probe); also turned off per thread when the user registered it | yes: DSH (node) is the service's child and starts MCP servers as its own children (probe) |
| … on Linux and macOS | runs there (not tried yet); only the deny rule would hide it | runs on Windows only (ConPTY) | not hidden: the parent walk is Windows only yet, so every caller counts as outside | hidden: turned off per thread | not hidden, as for Kimi |
| Message IDs ([§5](#5-prompts-and-turns)) | answers: the API's `msg_…`; live notes: the `uuid` the adapter wrote them with; texts Claude gives no ID: a new UUID | answers: `MessageDisplay`'s `message_id`; typed and channel messages: the hook's `prompt_id` (the transcript's `promptId`), a new UUID for a channel message within a turn; texts Claude gives no ID: a new UUID | none sent by 2.5.2: the service assigns them | item IDs (`msg_…`, `rs_…`, UUIDv7 user messages); the service's prompt ID kept as `clientUserMessageId` | DSH's on answers; thoughts as `<id>:thought` |
| Live-note path ([§6.4](#64-the-live-note-path)) | user messages on stdin, `reply` routed by `--reply-url`; `-32600` while not running | the development channel; `-32600` while not running | none (`-32601`) | none (`-32601`); `turn/steer` could carry notes into a running turn later | none (`-32601`) |
| Turns of its own marked ([§7](#7-closing-and-unloading)) | yes: from the first update of a turn the client did not start (a live note's, or one Claude began itself) to its `result` or the exit | yes: channel and terminal turns, from `UserPromptSubmit` to `Stop` or exit | none to mark: Kimi runs no turn of its own | none: one writer, so the adapter's process drives every turn | none marked: autonomous work after a cancel falls back to idle since the last update |
| Approvals ([§8](#8-approvals-and-forms)) | `can_use_tool` control requests; Allow / Deny only; the gorynych tools allowed by a per-launch rule and pre-allowed; in Full access nothing asks; `bypassPermissions` marked `gorynych/fullAccess` | the PermissionRequest hook; Allow / Deny only; the gorynych tools allowed by a per-launch rule and pre-allowed | Kimi's own `session/request_permission`; the service pre-allows the gorynych tools | command, file change, permissions and MCP tool approvals; no option that writes `~/.codex`; the gorynych tools run unasked (`default_tools_approval_mode: "approve"`); `full-access` marked | sandbox escalations only: a shell escalation refused by the proxy, a file write asks the person; in full access nothing asks; MCP tools unasked; `full-access` marked |
| Forms | `AskUserQuestion` as `elicitation/create`, through `can_use_tool`; MCP elicitations declined | `AskUserQuestion` as `elicitation/create`; MCP elicitations shown only in the terminal | none seen | `item/tool/requestUserInput` and MCP form elicitations as `elicitation/create`; URL elicitations declined | none (no ask tool, no elicitation in the acp profile) |
| Titles ([§9](#9-titles)) | `custom-title` from the transcript; `_gorynych/set_title` → `rename_session`, which writes `custom-title` | `ai-title` / `custom-title` from the transcript; no way to set one | the first prompt, cut at 200 characters; no way to set one | `thread/name/updated`; `_gorynych/set_title` → `thread/name/set` for spawn names | none over ACP: the provisional title |
| Close ([§7](#7-closing-and-unloading)) | ends the process | ends the process | `session/close`, still listed | `turn/interrupt`, up to 5 s for the turn to end, then `thread/unsubscribe` (unloads an idle thread at once) | `session/close`: cancels a running prompt, drains DSH's subagents |
| Delete ([§10](#10-delete)) | none: a tombstone | none: a tombstone | `session/delete` (`-32602` for an unknown one) | `thread/delete` (also deletes Codex's own sub-agent threads); a thread Codex lacks: `-32002` | none: a tombstone (`dsh web` can delete) |
| Archive ([§11](#11-archive)) | none: the overlay | none: the overlay | native in Kimi's UI, not in `kimi acp`: the overlay | native, but an archived thread cannot be resumed: the overlay; Codex-archived threads left out | none: the overlay |

## 14. Checking a new adapter

1. Go through §1–§12 and fill in a column of the table above, marking every MUST it cannot meet.
2. The service side of each feature is covered by contract tests against scripted agents
   (`tests/Gorynych.Tests`: `SessionLifecycleTests`, `OpinionTurnTests`, `SessionTreeTests`,
   `ArchiveTests`, `WakeTests`, `StableAcpTests`). A new adapter needs tests of its own translation, as
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
- **Codex's `turn/steer`.** Whether to carry a child's report into the parent's running turn
  with it, as a live-note path.
- **Permissions across harnesses.** Whether the service should pre-allow some tools, and how, is
  left for one pass over Claude, Kimi and Codex together. Codex's MCP tool approvals reach the
  person today.
- **Titles.** Neither Kimi, DSH nor the Claude TUI lets the service set a native title; Codex and headless Claude do.
- **Kimi's cursor.** Kimi 2.5.2 ignores `session/list`'s `cursor`
  ([adapters.md](adapters.md#kimi-code)).
- **A client's system prompt.** The service does not advertise `sessionCapabilities.systemPrompt`
  to its clients yet (Zed sends none) and refuses one with `-32602`. If it does, it has to
  combine the client's prompt with its own peer instructions. In `append` mode both could go to
  the agent in one `systemPrompt`, but in which order, and what reaches an agent that does not
  advertise the capability, is not decided. `override` replaces the agent's own instructions,
  so it would need the agent's `mode.override` and a rule for where the peer instructions go.
- **IDs of the client's own prompts.** The service names the client's prompt with an ID of its
  own, because ACP does not echo it; Claude's transcript has a `promptId` for it that the
  service learns only once the turn starts. ACP's v2 prompt-lifecycle RFD would let the agent
  return the ID it gave the prompt.
- **IDs for agents that send none.** Kimi 2.5.2 sends no `messageId`. The service's IDs group
  its chunks as a client would, so two of its messages with nothing between them stay one.
  Whether Kimi keeps message IDs of its own that it could send is not known.
