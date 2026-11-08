# Codex adapter: specification

Approved 8 November 2026 and built on branch `claude/codex-adapter`. The adapter as built is
described in [docs/adapters.md](../docs/adapters.md#codex), and the contract changes are in
[docs/adapter-contract.md](../docs/adapter-contract.md). This file keeps the design and the
probe results it rests on, with the build-phase answers at the end. Changes made when it was
approved:

- Codex's idle time is 2 minutes, measured (answer 8).
- Only explicit names are written natively, never the provisional title.
- The gorynych tools are not pre-allowed; they ask the person until a pass over the permissions
  of all harnesses decides.

## Draft as reviewed

How Gorynych drives Codex, mapped feature by feature onto
[adapter-contract.md](../docs/adapter-contract.md). Nothing is built yet. Written 8 November 2026
from:

- **Codex 0.167.0** (`codex-cli 0.167.0`, the npm package `@openai/codex` with its Windows build).
- **Its protocol schema**, generated locally with `codex app-server generate-json-schema --out <dir>`
  (and `--experimental` for the experimental surface). Every method and field named below is in it.
- **The official app-server docs** (https://learn.chatgpt.com/docs/app-server) and the openai/codex
  README. They disagree with each other in places; the schema wins.
- **Four probes** against the real `codex app-server` with the user's own login, in a scratch
  folder, never writing `~/.codex`. They are kept in [probes/codex](probes/codex); their results
  are below.
- **Zed's adapters, as references for the mapping only.** The archived `zed-industries/codex-acp`
  linked Codex's Rust crates at `rust-v0.137.0`. Its successor `@agentclientprotocol/codex-acp`
  (2.6.0, 8 November 2026) is TypeScript over `codex app-server`.

## Summary

- **Codex does not speak ACP.** There is no `codex acp` in 0.167 or in releases up to
  0.169.0-alpha.3. So the adapter is a translator, like Claude's: an in-process ACP agent that
  drives one `codex app-server` child process over its JSON-RPC.
- **Everything per thread goes through the protocol, never through `~/.codex`.**
  - `thread/start` and `thread/resume` take `config` (the same overrides as `-c`). That is how
    the thread gets the `gorynych` MCP server, verified.
  - The same `config` can turn off one of the user's own MCP servers for one thread, verified.
  - `developerInstructions` carries the appended system prompt, verified.
- **The protocol covers every contract feature:** new, list (cursor, cwd), resume, load with
  history, close (`thread/unsubscribe`), delete, native titles (`thread/name/set`,
  `thread/name/updated`), turns and interrupt, approvals for commands, file changes, extra
  permissions and MCP tool calls, forms, plans, usage, and stable item IDs for `messageId`.
- **Three properties of Codex change the contract:**
  - **A thread has one writer process.** While our app-server has a thread loaded, Codex Desktop
    or the CLI cannot continue it, and the reverse also holds.
  - **An archived thread cannot be resumed.**
  - **Some approval options write the user's configuration.** These are the "always" options:
    an exec-policy amendment, and MCP `persist: always`.
- **Recommendation:** build our own C# adapter on `codex app-server`, with one app-server per
  service. Do not use the shared daemon or `@agentclientprotocol/codex-acp`. Decisions D1–D8
  below; contract changes in [Proposed contract changes](#proposed-contract-changes).

## What the probes showed

Codex 0.167.0 on Windows 11, 8 November 2026, model `gpt-6.1-sol` (the user's default), effort
`low` for every probe turn. There were seven short turns in all. Every probe thread was deleted
afterwards (see [Leftovers](#leftovers)).

1. **Wire.** Newline-delimited JSON over stdio without a `jsonrpc` field. `initialize` with
   `clientInfo {name, title, version}` is followed by the `initialized` notification.
   `initialize` returns `userAgent` (`gorynych_probe/0.167.0 (Windows …) (gorynych_probe;
   0.0.1)`), `codexHome`, `platformFamily` and `platformOs`.
2. **Identity.**
   - A thread records `originator` = our `clientInfo.name`, and `source` `vscode`, the same as
     Codex Desktop's threads.
   - Among the user's threads, the CLI's are `source cli, originator codex-tui`, and the desktop
     app's are `source vscode, originator Codex Desktop`.
   - The default `thread/list` (interactive sources) lists all of these. With
     `sourceKinds: ["appServer"]` our thread is not listed.
3. **Per-thread MCP server.**
   - `thread/start {config: {mcp_servers: {gorynych_probe: {command, args}}}}` connects the server
     for that thread, next to the user's own servers (9 of them, from `~/.codex/config.toml`).
   - Codex starts each MCP server as a **direct child of `codex.exe`**, in the thread's `cwd`. It
     speaks MCP `2025-06-18` to it, with `clientInfo codex-mcp-client` and `elicitation {form,
     url}`, and passes no `CODEX_*` variables.
   - The server's `tools/call` carries `_meta["x-codex-turn-metadata"]` with `thread_id` and
     `turn_id`.
4. **The config is not kept with the thread.** A `thread/resume` without `config` (in the same or
   another process) has no `gorynych_probe`. A `thread/resume` with `config` for a thread that is
   already loaded does not add it either. So the server has to be given whenever the thread gets
   loaded.
5. **Turning off a user's server.** `config: {mcp_servers: {<one of the user's servers>: {enabled: false}}}`
   (no command) made that server `disabled` for that thread only.
6. **Developer instructions.**
   - `developerInstructions` at `thread/start` worked: `PING` answered `PONG-7319`.
   - They *are* kept with the thread: a resume in a second process without them still answered
     `PONG-7319`.
   - Whether resending them replaces or adds to the old ones is not known (build-phase check).
7. **Nothing durable before the first turn.** `thread/start` names a rollout path under
   `~/.codex/sessions/…`, but writes no file, and `thread/list` does not show the thread.
8. **One writer.**
   - A second `codex app-server` process could not resume a thread the first had loaded, even
     when idle: `-32600 thread <id> already has an active writer`.
   - It could resume the thread once the first sent `thread/unsubscribe`. That unloaded the
     thread at once (`thread/loaded/list` empty).
   - A second process received none of the first one's notifications.
9. **Archive.**
   - `thread/archive` (emits `thread/archived`), `thread/unarchive` (`thread/unarchived`) and
     `thread/delete` (`thread/deleted`) work.
   - `thread/list` leaves archived threads out unless `archived: true`.
   - **An archived thread cannot be resumed:** `-32600 session <id> is archived. Run `codex
     unarchive <id>` to unarchive it first.`
10. **Turn events.**
    - The notifications arrive in this order: `turn/started`, `item/started` and
      `item/completed` per item, `item/agentMessage/delta`, `item/reasoning/*`,
      `item/commandExecution/outputDelta`, `thread/tokenUsage/updated`, `thread/status/changed`
      (`active` with `activeFlags: [waitingOnApproval]`, then `idle`), and `turn/completed`
      with `status`.
    - The user's own Codex hooks (`~/.codex/hooks.json`) run on our threads (`hook/started`,
      `hook/completed`), and so do the user's `AGENTS.md` instructions: the agent answered in
      Russian.
11. **Item IDs are stable.**
    - User messages have UUIDv7 IDs, agent messages `msg_…`, reasoning `rs_…`, commands
      `exec-…`.
    - The same IDs come back from `thread/read`, `thread/turns/list` and `thread/items/list`.
    - `turn/start {clientUserMessageId}` is kept as the user message's `clientId` in history,
      verified across processes.
12. **Command approval.**
    - `item/commandExecution/requestApproval` gives `itemId`, `command` (the full `pwsh.exe
      -Command …` line), `cwd`, `commandActions`, `reason?` and `availableDecisions`. Under
      `untrusted` these were `["accept", {acceptWithExecpolicyAmendment: …}, "cancel"]`.
    - The answer is `{decision}`. Codex then sends `serverRequest/resolved`.
    - On Windows the model also wrote a file through the shell (`Set-Content`), so
      `item/fileChange/requestApproval` was not seen live; it is in the schema.
13. **MCP tool approval.** Every call of a tool of the per-thread server asked
    `mcpServer/elicitation/request` with `mode: "form"`, an empty `requestedSchema`, and `_meta
    {codex_approval_kind: "mcp_tool_call", tool_name, tool_description, tool_params, persist:
    ["session", "always"]}`. `{action: "accept", content: {}}` ran the tool.
14. **Interrupt.**
    - `turn/interrupt {threadId, turnId}` sent while a command approval was pending made Codex
      resolve the pending request itself (`serverRequest/resolved`), fail the command item, and
      end the turn `interrupted`.
    - The interrupted turn's history keeps the user and agent messages, but not the failed
      command.
15. **Settings.**
    - The thread took the user's defaults: model `gpt-6.1-sol`, effort `xhigh`.
    - `turn/start {effort}` changes the thread's effort "for this turn and subsequent turns", so
      overrides persist.
    - `model/list` returned eight models, each with `supportedReasoningEfforts` (`low` … `ultra`)
      and `defaultReasoningEffort`.
16. **No daemon was running** (`codex app-server daemon version`: no control socket).

## Decisions

Each has a recommendation; the coordinator and the user decide.

**D1. Our own adapter, not `@agentclientprotocol/codex-acp`.**

- **For codex-acp:** it is an ACP agent over the unmodified `codex app-server`, maintained under
  the ACP organisation, and it would plug in like `kimi acp`.
- **Against it:**
  - It is a Node/TypeScript runtime the service would depend on.
  - By default it runs its own bundled Codex (`@openai/codex ^0.166.0`), not the user's 0.167,
    unless `CODEX_PATH` is set.
  - It is a fast-moving third-party layer: weekly releases, 79 open issues on 8 November.
  - It has its own extensions (`_session/archive`, `_session/rename`, JetBrains `air` keys),
    and it skips an ACP MCP server whose name exists in the user's config.
  - Its predecessor marked a project trusted when a mode trusted it, which Codex records in
    the user's own configuration. Whether the successor offers the configuration-writing
    approval options is not known.
- Our hard rules (never write `~/.codex`, which approval options to offer, item IDs as
  `messageId`, history replay, native titles) are easier to keep in our own translator.
- **Recommendation:** an in-process C# adapter (a `DuplexLink` agent, like Claude's) that starts
  `codex app-server` and speaks its JSON-RPC. It stays AOT-compatible, needs no runtime, and
  uses codex-acp as a reference for the mapping only. Types come from the generated schema, the
  way `Gorynych.AcpGen` does it for ACP.

**D2. One `codex app-server` per service, started by the adapter; no daemon.**

- One process serves all threads, and its MCP servers are its direct children, so the
  `gorynych-outside` ancestry walk holds.
- The shared daemon (`codex app-server daemon` + `proxy`) would:
  - start MCP servers outside our process tree, breaking §1;
  - run with the user's remote-control settings;
  - give nothing in return. The one-writer rule (finding 8) means another Codex client could
    not drive our loaded threads anyway, unless it shared that daemon.
- Out of scope.
- When the process dies, the connection closes, the service restarts the adapter, and every
  session is not loaded (§1, §4).

**D3. Approval options never write the agent's configuration.**

- Codex's `acceptWithExecpolicyAmendment` and `applyNetworkPolicyAmendment` add rules that
  allow similar commands or hosts later (the schema: "to allow similar commands without
  prompting"), and MCP `persist: "always"` saves an approval beyond the session. Each outlives
  the session in the user's Codex state; where exactly it is stored is to be confirmed, but
  none of them is needed.
- The adapter offers only `accept` (allow once), `acceptForSession` / `persist: "session"`
  (allow for the session), `decline` and `cancel`, and only those that `availableDecisions`
  lists. See [Approvals](#approvals-and-forms).

**D4. Archive stays in the overlay; natively archived threads are left out.**

- Codex refuses to resume an archived thread (finding 9). Gorynych's archive keeps a session
  usable (`ask_agent` reaches it), so mirroring it into `thread/archive` would break that.
- As for Kimi, the default list leaves natively archived threads out (`archived: false`), so an
  archive made in Codex Desktop is not mirrored. The user had 200+ archived threads there.
- A Codex-archived thread that Gorynych still knows fails to open with a clear message (D7).

**D5. No live-note path in stage one.**

- `_gorynych/opinion` and `_gorynych/note` answer `-32601`, so opinions and reports are turns, as
  for Kimi.
- `turn/steer {expectedTurnId, input}` appends input to a running turn. It could carry
  `_gorynych/note` (a child's report into the parent's running turn) later, with `-32600` when no
  turn runs.
- Opinions should stay turns: the asker's `reply` is matched by the service's `m…` IDs.

**D6. The appended system prompt is `developerInstructions`.**

- Advertise `sessionCapabilities.systemPrompt: {}` (append only). Send the text at `thread/start`,
  and at `thread/resume` once the build phase shows that resending replaces rather than adds
  (finding 6).
- Codex keeps it with the thread, so "a resume without one leaves none" (§6.2) cannot hold.
- `override` could map to `baseInstructions` later.

**D7. The one-writer rule.**

- While Gorynych holds a thread loaded (its idle time, 10 minutes by default), the user cannot
  continue it in Codex Desktop or the CLI. Gorynych cannot open a thread the user has open
  there.
- The adapter turns `already has an active writer` into an error the person and the asking
  agent understand:
  `This Codex thread is open in another Codex app (Codex Desktop, the CLI or VS Code); close it there, then try again.`
- **Recommendation:** let an adapter ask for a shorter idle time (contract change C6), for
  Codex about one minute, so a thread goes back to Codex's own apps soon after a turn.

**D8. Native titles are written.**

- Codex can set a thread's name (`thread/name/set`). The adapter should write the name a
  `spawn_session` gives, so Codex Desktop shows it, and report `thread/name/updated` as
  `session_info_update`.
- This needs a way for the service to ask for it (contract change C5).
- Our threads get no name of their own (the probe thread had none until it was set), so the
  service's provisional title from the first prompt could be written too. To decide.

## Feature mapping

Section numbers are the contract's.

| Contract | Codex mechanism | Gap / what the adapter or service does |
| --- | --- | --- |
| §1 start | `codex app-server` child process (stdio), in-process ACP agent | find `codex` like Kimi's `kimi` (on Windows the npm shim starts node, then `codex.exe`: all descendants) |
| §1 hidden from outside | MCP servers are `codex.exe`'s children (verified) | also turn `gorynych-outside` off per thread when the user's config has it (`config/read` names; finding 5): hidden on every OS |
| §2 initialize | `initialize {clientInfo: {name: "gorynych", title: "Gorynych", version}}`, `initialized` | advertise `loadSession`, `list`, `resume`, `close`, `delete`, `systemPrompt {}`; no `authMethods` (an auth error reaches the client) |
| §3 new | `thread/start {cwd, config: {mcp_servers}, developerInstructions}` | nothing durable before the first turn (finding 7), like Claude |
| §3 list | `thread/list {cwd, cursor, limit, archived: false, sortKey: updated_at, useStateDbOnly: true}` | `title` = `name` (none → the service's provisional title); `updatedAt` from unix seconds; `cwd` matches exactly, so spell it as Codex does (as for Kimi) |
| §4 resume | `thread/resume {threadId, excludeTurns: true, config, developerInstructions}` | `config` again every time (finding 4); errors: one writer (D7), archived (D4) |
| §4 load | `thread/resume`, then `thread/items/list` ascending (`thread/turns/list` for turns) | replay for threads the journal never saw (made in Codex Desktop or the CLI) |
| §5 prompt | `turn/start {threadId, input: [{type: text, text}…], overrides}` | one `text` input per block; images as `image`/`localImage`; resource links as text `@uri`; overrides only when the person changed a setting |
| §5 cancel | `turn/interrupt {threadId, turnId}` | ends `interrupted` → `cancelled`; pending approvals resolved by Codex → `$/cancel_request` to the service |
| §5 stop reasons | `turn/completed {turn: {status, error}}` | `completed` → `end_turn`; `interrupted` → `cancelled`; `failed` → JSON-RPC error with `turn.error.message` (context window: `max_tokens`, to check against `CodexErrorInfo`) |
| §5 echo | `item/*(userMessage)` of our own turn | not sent (ACP) |
| §5 message IDs | item IDs (finding 11) | agent message `msg_…`, reasoning `rs_…`, user message UUIDv7 |
| §6 service turns | ordinary turns | as Kimi |
| §6.2 gorynych server | `config.mcp_servers.gorynych {command, args, env}` (verified) | each load; every tool call asks an MCP approval (finding 13) titled `mcp__gorynych__<tool>`, so the service pre-allows three |
| §6.2 system prompt | `developerInstructions` (verified) | D6 |
| §6.4 live notes | none (`turn/steer` later) | `-32601` (D5) |
| §7 close | `turn/interrupt` if a turn runs, then `thread/unsubscribe` | unloads at once (finding 8) |
| §7 own turns | none: one writer, and our process drives every turn | no `gorynych/agentTurn` marks; Codex's own sub-agents run in separate threads (`subAgent` source, not listed) |
| §8 approvals | `item/commandExecution/requestApproval`, `item/fileChange/requestApproval`, `item/permissions/requestApproval`, MCP `mcp_tool_call` elicitation | below; D3 |
| §8 forms | `item/tool/requestUserInput`, `mcpServer/elicitation/request` (`form`, `url`) | `elicitation/create` form; `url` declined for now |
| config | `model/list`; overrides on `turn/start` (persist); `thread/settings/updated` | ACP config options `mode`, `model`, `reasoning_effort` (below) |
| §9 titles | `thread/name/updated`; list `name`; `thread/name/set` | D8, C5 |
| §10 delete | `thread/delete` (also deletes spawned descendants), `thread/deleted` | unknown thread → `-32002` |
| §11 archive | `thread/archive`/`unarchive`; archived not resumable | overlay only (D4) |
| §12 children | nothing beyond §3–§6 | first prompt's two text blocks as two `text` inputs |
| identity | `originator` = `clientInfo.name`, `source` `vscode` | name `gorynych`; it also reaches OpenAI's compliance logs and the user agent |

### Turn updates

| Codex | ACP `session/update` |
| --- | --- |
| `item/agentMessage/delta {itemId, delta}` | `agent_message_chunk`, `messageId` = `itemId`; the completed item's `text` only when no delta came |
| `item/reasoning/summaryTextDelta`, `textDelta` | `agent_thought_chunk`, `messageId` = `itemId`; `summaryPartAdded` → a paragraph break |
| `item/started` `commandExecution` | `tool_call` (`toolCallId` = item ID, title = `commandActions` text: `Read <path>`, `Search …`, else the command without the shell wrapper; kind `read`, `search` or `execute`; `rawInput {command, cwd}`) |
| `item/commandExecution/outputDelta` | `tool_call_update` content (text, buffered per item) |
| `item/completed` `commandExecution` | `tool_call_update` `completed` / `failed` (`declined` → `failed`), `rawOutput {exitCode, aggregatedOutput}` |
| `fileChange` (`changes: [{path, kind, diff}]`) | `tool_call` kind `edit`, `locations`, content: the unified diff as text (ACP's `diff` content needs old and new text) |
| `mcpToolCall {server, tool, arguments, result, error}` | `tool_call` titled `mcp__<server>__<tool>` (as Kimi, so `read_session` and pre-allowing read it), `rawInput` = arguments, result as content |
| `webSearch`, `imageView`, `imageGeneration`, `dynamicToolCall`, `collabAgentToolCall`, `subAgentActivity` | `tool_call` kinds `fetch`, `read`, `other` |
| `turn/plan/updated {plan: [{step, status}]}` | `plan` (`pending` / `in_progress` / `completed`) |
| `thread/tokenUsage/updated` | `usage_update {used: last input + output tokens, size: modelContextWindow}` |
| `thread/name/updated` | `session_info_update {title}` |
| `contextCompaction`, `warning`, `configWarning`, `error` | logged; `error` during a turn ends it as `failed` |
| `hook/*`, `account/*`, `remoteControl/*`, `mcpServer/startupStatus/updated`, `turn/moderationMetadata`, `thread/readState/changed` | dropped (`optOutNotificationMethods` where possible) |

### Approvals and forms

All approvals are `session/request_permission` with the item's `tool_call` (by `toolCallId`) and
distinct option IDs:

| Codex request | Options offered (when in `availableDecisions`) | Answer |
| --- | --- | --- |
| command | `accept` allow_once "Allow"; `acceptForSession` allow_always "Allow for this session"; `decline` reject_once "Deny"; `cancel` reject_once "Deny and stop the turn" | `{decision}` |
| file change | same four | `{decision}` |
| extra permissions (`item/permissions/requestApproval`) | allow_once "Allow for this turn" (`scope: turn`); allow_always "Allow for this session" (`scope: session`); reject_once "Deny" | `{permissions: <the requested profile, or none>, scope}` |
| MCP tool call (`mcp_tool_call` elicitation) | allow_once (accept); allow_always "Allow for this session" (accept with `persist: session`); reject_once (decline) | `{action, content: {}, _meta}` |

- Never offered: `acceptWithExecpolicyAmendment`, `applyNetworkPolicyAmendment`, MCP
  `persist: "always"` (D3).
- `item/tool/requestUserInput` (questions with options, `isOther`, `isSecret`) and other MCP form
  elicitations become `elicitation/create` in form mode, as Claude's `AskUserQuestion` does. A
  decline answers with no answers. URL elicitations are declined and logged.
- When Codex sends `serverRequest/resolved` for a request the client has not answered, the
  adapter withdraws it with `$/cancel_request`.

### Modes, models and config options

ACP config options, like Kimi's:

- **`mode`** (category `mode`): Codex's presets. `read-only` (approval `on-request`, sandbox
  `read-only`); `auto` (`on-request`, `workspace-write`); `full-access` (`never`,
  `danger-full-access`). A thread whose settings match no preset (the user's own config) shows
  them as a fourth value, `custom`, until changed.
- **`model`** (category `model`): `model/list`, visible models.
- **`reasoning_effort`** (category `thought_level`): the selected model's
  `supportedReasoningEfforts`.

A change goes out with the next `turn/start` (`approvalPolicy`, `sandboxPolicy`, `model`,
`effort`), where Codex keeps it for later turns. `thread/settings/update`, which applies it at
once, is experimental and not used. The response to `session/set_config_option` is the full list
with the new values. On Windows, `workspace-write` needs Codex's sandbox set up
(`windowsSandbox/readiness`); without it the adapter offers `read-only` and `full-access`.

## Proposed contract changes

Where adapter-contract.md describes Claude or Kimi rather than ACP, or misses what Codex shows:

- **C1. §8: no approval option that writes the agent's configuration.** Add: *An adapter MUST NOT
  offer an approval option whose effect is written to the agent's own configuration (Codex's
  exec-policy amendments, MCP `persist: always`); a session-scoped "allow always" is fine.*
  Today §1 forbids editing the configuration, but §8 offers `allow_always` without saying what
  it may mean.
- **C2. §6.2: instructions kept with the session.** "It MUST apply the latest request's value, so
  a resume without one leaves none" assumes an agent that forgets. Change it to: *An agent that
  keeps the instructions with the session keeps the last value given; the adapter SHOULD replace
  it with the latest one where the agent allows.*
- **C3. §4 and §6.2: the MCP server applies only when a session is loaded.** Add: *The adapter
  MUST give the agent the `mcpServers` of the request that loads the session (Codex ignores
  them for a session already loaded and does not keep them).* The service already sends them on
  every open, so nothing changes there.
- **C4. §1: a daemon is not an option.** §1 already says a separate long-lived MCP host breaks
  hiding. Add that an adapter MUST NOT drive the agent through a daemon shared with other clients
  (Codex's `app-server daemon`), and MAY additionally turn off `gorynych-outside` per session
  where the agent allows it (Codex `config`), which hides it on every OS.
- **C5. §9: setting a native title.** The contract says no adapter can set a native title, but
  Codex can. ACP has no method for it. Propose `_gorynych/set_title {sessionId, title}`, with
  the flag `gorynych/setTitle` in `agentCapabilities._meta`. The service would call it for a
  `spawn_session` name (and perhaps its provisional title) when the flag is there, and keep the
  overlay otherwise.
- **C6. §7: an adapter's idle time.** For an agent whose loaded sessions lock out its own apps
  (Codex's one writer), propose `agentCapabilities._meta["gorynych/unloadAfter"]` (seconds), a
  shorter idle time for that agent's sessions. The service takes the smaller of it and
  `--unload-after`.
- **C7. §3 drafts.** For an agent that can delete but keeps nothing durable before the first
  prompt (Codex), the discard finds the draft unlisted and hides it with a tombstone (Kimi's
  rule), one per Zed draft. Propose: *after `session/close`, a draft the agent does not list was
  never durable and is dropped*, when the adapter says so with `gorynych/draftsNotDurable` in
  `agentCapabilities._meta`.
- **C8. §11 and §15: native archive.** Record that Codex's native archive refuses to resume, like
  the ACP archive RFD, so the overlay stays. Mirroring waits for the RFD's `unstable` capability
  and a decision on what an archived session may still do.
- **C9. §13: the Codex column.** Replace the expected values with these findings. Close:
  `thread/unsubscribe` unloads at once, not "after a grace period". MCP server: per-thread
  `config` (verified). Hidden: yes (verified). Message IDs: item IDs. Own turns: none (one
  writer).
- **C10. §5: the prompt's own ID.** Optional: the service could pass its message ID for the
  client's prompt in `_meta["gorynych/messageId"]`. An adapter whose harness keeps a client ID
  (Codex `clientUserMessageId`) would store it, so history replayed from the harness has the
  journal's IDs. This also answers part of the open question "IDs of the client's own prompts".

## To check in the build phase

(Answered below, in [Build-phase answers](#build-phase-answers).)

1. Whether resending `developerInstructions` on `thread/resume` replaces or adds (D6, C2). One
   turn.
2. Whether `thread/unsubscribe` during a running turn stops it. The adapter interrupts first
   anyway.
3. How Codex Desktop shows a thread with `originator gorynych`, and whether `serviceName` or
   `threadSource` should be set.
4. `item/fileChange/requestApproval` live, with a prompt that makes Codex use `apply_patch`.
5. The `_meta` of an MCP approval answered "for this session".
6. Whether a per-server tool approval setting exists for MCP servers (the schema has
   `approval_mode` only for apps), which would spare an approval round trip per gorynych tool
   call.
7. `workspace-write` on Windows without the sandbox set up.
8. The cost of a load: each thread load starts the user's own MCP servers too (9 here).
9. Where Codex stores exec-policy and network amendments and an MCP `persist: "always"`
   approval (D3 assumes the user's Codex home).
10. Whether `config` overrides with dotted keys (`mcp_servers.gorynych-outside.enabled`) and
    nested objects behave the same, and what a nested override does to a server the user's
    config does not have.

## Build-phase answers

`research/probes/codex/build_checks_probe.py`, 8 November 2026, Codex 0.167.0, about seven
short turns, every thread deleted.

1. **Resending `developerInstructions` on `thread/resume`** replaces nothing. A thread created
   with "PONG-1111" still answered PONG-1111 after a resume that sent "PONG-2222". So a thread
   keeps the instructions it was created with (contract C2).
2. **`thread/unsubscribe` during a running turn** does not stop it. The turn ran on and
   completed, and the thread stayed loaded. So the adapter's close interrupts the turn first.
3. **How Codex Desktop shows a thread with `originator gorynych`**: not checked. It needs the
   desktop app and a person looking. `serviceName` and `threadSource` are not set.
4. **A file-change approval live**: `item/fileChange/requestApproval {threadId, turnId, itemId,
   startedAtMs, reason, grantRoot}`, with no `availableDecisions`. The item's change is `{path,
   kind: {type: add}, diff: "hi
"}`; for a new file, the diff is its content. Declined, the
   item ends `declined`.
5. **An MCP approval answered "for this session"** is `{action: "accept", content: {}, _meta:
   {persist: "session"}}`. Two calls of the tool in one turn asked once.
6. **A per-server approval setting exists**: `default_tools_approval_mode: "approve"` in a
   thread's `config.mcp_servers.<name>` ran the tool without asking. Codex's config reference
   also lists `tools.<tool>.approval_mode`. Not used: pre-allowing waits for the permissions
   pass.
7. **`workspace-write` on Windows**: `windowsSandbox/readiness` answered `ready` here, and a
   thread started with it.
8. **The cost of a load**:
   - `codex app-server` starts and initializes in 0.3–0.4 s.
   - `thread/resume` of a written thread takes 0.4 s, and the user's MCP servers (nine) are up
     1.7 s after it.
   - Listing their status, which waits for every server, took about 11 s. Two of the user's
     servers fail to start, one of them over HTTP with a timeout.
   - A thread that never had a turn cannot be resumed at all (`no rollout found`).

   So a reload costs about two seconds, or up to about ten with a slow server. Codex's idle
   time is 2 minutes (`gorynych/unloadAfter: 120`).
9. **Where the "always" options go**: Codex's docs and source say exec-policy and network
   amendments are appended to `~/.codex/rules/default.rules`. Where MCP's `persist: always` is
   kept is not documented. Neither is offered (D3).
10. **Config overrides**:
    - Dotted keys (`mcp_servers.<name>.enabled`) and nested objects both work for a server the
      user's config has.
    - For one it does not have, both fail `thread/start` with `invalid transport in
      mcp_servers.<name>`. So `gorynych-outside` is turned off only when `config/read` lists it.

Also checked:

- **The automatic reviewer.** `approvalsReviewer: "auto_review"` is a standard field of
  `thread/start` and `turn/start`, not an experimental one, and the thread took it. It is the
  "Approve for me" mode of Codex Desktop (docs: `approvals_reviewer = "auto_review"` with an
  interactive policy such as `on-request`). So it is a value of the adapter's `mode` option. Its
  decisions were not seen: the probe's command needed no approval.
- **The wire.** App-server accepts messages with `"jsonrpc": "2.0"`, so the service's own
  JSON-RPC connection is used.
- **Names.** `thread/name/set` works before the first turn, and the name stays after it.

## Leftovers

- None from these probes: four threads were made and deleted, and `thread/list` for the scratch
  folders is empty, archived included.
- The probe turns ran with `effort low`, kept only on those deleted threads.
- An archived thread named "zerus probe" (`originator zerus_probe`) from the probe of
  6 November 2026 is still in the user's Codex. It was not touched.
