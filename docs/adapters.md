# Adapters

What every adapter must provide, and how Claude, Kimi, Codex and DeepSeek compare, is in
[adapter-contract.md](adapter-contract.md). This file keeps what is particular to each
adapter that exists.

## Kimi Code

`kimi acp` is native ACP, so the adapter only runs the unmodified binary under the
user's own login and hands its stdio to the service. Observed with Kimi Code 2.4.1
(recorded in `tests/Gorynych.Tests/Fixtures/kimi`):

- `initialize`: `loadSession`, `sessionCapabilities` list, resume, close, delete, fork,
  additionalDirectories; one terminal auth method (`kimi login`). Kimi 2.5.2 advertises no
  `sessionCapabilities.systemPrompt`, so it gets no peer instructions as a system prompt
  (checked in its bundled ACP agent on 8 November 2026).
- Its chunks carry no `messageId` (2.5.2, same check): the service gives them IDs as it
  publishes them ([service-protocol.md](service-protocol.md#standard-methods)).
- `session/new` returns config options (model, thinking, mode) and modes; the first
  prompt immediately yields `session_info_update` with the prompt as the title.
- `session/list` returns titles and `updatedAt`. A folder may come with forward slashes
  (`C:/Users/x`); the service lists and keeps it in one spelling
  ([service-protocol.md](service-protocol.md#standard-methods)).
- Approvals arrive as `session/request_permission` with Kimi's own options (approve once,
  approve for this session, reject); tool calls stream their arguments as `tool_call_update`.
- `session/new` creates a real, empty native session. Kimi lists it with `title: null`
  until the first prompt titles it, and `session/close` keeps it listed. A `session/delete`
  of a session Kimi does not have answers `-32602 Invalid params: Unknown sessionId`, not
  ACP's `-32002`.
- The service deletes a draft with Kimi's own `session/delete` (after `session/close`) once
  Kimi lists it untitled, and forwards a client's `session/delete` the same way. Verified
  live on 7 November 2026 (`Kimi_DraftIsDeletedNatively_WhenItsClientLeaves`, and a probe of
  the list and delete answers).
- Kimi has no way for a client to set a session's title, so a name given to `spawn_session`
  lives in the service's overlay and wins over the title Kimi takes from the first prompt.
  The service answers Kimi's approval of `spawn_session` itself, since it asks the person
  before it spawns ([service-protocol.md](service-protocol.md#child-sessions)). Kimi does not
  know `_gorynych/note`, so a child's report reaches a Kimi parent as a marked turn. A Kimi
  session that is not loaded is resumed for an opinion request or a report and unloaded again
  after the service's idle time ([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)).
- **Archive: not mirrored.** Kimi Code 2.5.2 has a native archive (`"archived"` in a
  session's `state.json`, `kimi session list --archived`, set from Kimi's own UI), but
  `kimi acp` offers no method for it. Its `session/list` leaves natively archived sessions out:
  read from the 2.5.2 code (not tried, so as not to archive any of the user's sessions), it
  asks Kimi's engine for the session list with an empty query, which excludes archived ones,
  and its `SessionInfo` has no archive field. So the service's archive flag lives in its
  overlay for Kimi too ([service-protocol.md](service-protocol.md#archive)) and never touches
  Kimi's files. A session archived in Kimi's own UI is not seen as archived: while Kimi is
  running, the service takes it for a session Kimi no longer has and leaves it out of
  `session/list`, even with `includeArchived`; its ID still works for `session/load` and
  `ask_agent` as far as the service goes (whether Kimi resumes a natively archived session is
  not checked). Restoring it in Kimi brings it back.
- Also seen in the 2.5.2 code, not tried: Kimi's ACP `session/list` ignores the request's
  `cursor`, so if it ever returned a `nextCursor`, following it would repeat the first page.
- Verified live on 7 November 2026 after logging in: first prompt, a Write approval, the
  title, replay through the service journal, and `ask_agent` to a Claude session. Before
  the login every turn failed with `-32000 Authentication required: 403`, which the service
  relays to the client unchanged.

## Claude Code

The adapter (`Gorynych.Adapters.Claude`) is an in-process ACP agent that runs the
interactive `claude` TUI in a Windows pseudo console. It never touches `~/.claude`:
everything goes through per-launch flags, hook HTTP calls and the terminal.

### Launch

```
claude --settings <session>/settings.json --permission-mode default [--model <m>]
       [--mcp-config <session>/mcp.json [--dangerously-load-development-channels server:gorynych]]
       [--append-system-prompt <channel authorization>\n\n<ACP systemPrompt>]
       (--session-id <id> | --resume <id>) -- <first prompt>
```

- `settings.json` holds http hooks posting to
  `http://127.0.0.1:<port>/hook/<session>/<token>` (Kestrel on loopback, a random token
  per session), which add to the user's global hooks, and one permission rule,
  `"deny": ["mcp__gorynych-outside"]`: the server for agents outside Gorynych, which the user may
  have registered in their own configuration, is not for a session inside it. The server also
  hides its tools by its ancestry ([service-protocol.md](service-protocol.md#hiding-from-sessions-inside-gorynych)).
- `--permission-mode default`, because a global `defaultMode: auto` would decide
  approvals without asking.
- The session ID is chosen by the adapter at `session/new` and passed as `--session-id`;
  later launches use `--resume` and pass every flag again (resume does not restore them).
- `session/new` launches nothing. The adapter's own list (`claude/sessions.json` in the
  service data, used for `session/list` and `session/resume`) gets the session at its first
  prompt, so a draft that is never prompted leaves nothing behind.
- The adapter does not advertise `session/delete`: the transcript belongs to `~/.claude`.
  Deleting a Claude session closes it (the process exits) and the service hides it with a
  tombstone in its overlay; `claude --resume` in a terminal still finds it.
- Environment: `CLAUDECODE`, `CLAUDE_CODE_*`, `CLAUDE_AGENT_SDK*` and, when started from
  inside another Claude Code session, `ANTHROPIC_BASE_URL` and the desktop MCP variables
  are removed, so the child uses the user's own login.
- Screens that need a key: the development-channels notice is accepted with Enter. The
  folder-trust question is relayed to the client as an approval and answered with Enter
  only if the person allows it.

### Prompts and turns

- The first `session/prompt` is the launch argument (after `--`). Until Claude has taken
  a prompt the launch uses `--session-id`; after that, `--resume`.
- Later prompts wait until `Stop` says Claude is idle, then go in as a bracketed paste
  (`ESC[200~ … ESC[201~`) and, 400 ms later, Enter. That arrives as a real user turn.
- A turn ends with the `Stop` hook (`end_turn`), but only a turn Claude has acknowledged
  with `UserPromptSubmit`, so a late `Stop` of an interrupted turn cannot end the next one.
- `session/cancel` sends Esc and ends the turn with `cancelled`; a prompt still waiting for
  the composer is dropped with `cancelled`.
- A launch that does not reach `UserPromptSubmit` within 90 s is killed and fails the
  prompt with the last screen text, so a stuck dialog is visible instead of a hang; the
  next prompt starts a fresh process.
- An approval is withdrawn when Claude abandons the hook call (its timeout, an answer typed
  in the TUI, an interrupt).

### Hooks → ACP

| Hook | ACP |
| --- | --- |
| `UserPromptSubmit` | nothing for the client's own prompt (ACP does not echo it); otherwise `user_message_chunk` with `_meta["gorynych/origin"]` = `channel` or `terminal` and `messageId` = the hook's `prompt_id` (below); the first of a turn of Claude's own also carries `_meta["gorynych/agentTurn"]: "started"` |
| `PreToolUse` | `tool_call` (`pending`), `toolCallId` = `tool_use_id`, title and kind from the tool, locations from `file_path` |
| `PermissionRequest` | `session/request_permission` with Allow / Deny; the hook replies `allow` or `deny`. A denial also marks the tool call `failed`. |
| `PermissionRequest` for `AskUserQuestion` | one `elicitation/create` form (scoped to the session and tool call) with a property `q1`, `q2`, … per question: a string with `oneOf` for a single choice, an array of titled items for `multiSelect`; an accepted form makes the hook reply `allow` with `updatedInput.answers` (`{question: label}`, several labels joined by ", "), anything else `deny` |
| `PostToolUse` / `PostToolUseFailure` | `tool_call_update` `completed` / `failed`, `rawOutput`, Bash output as content |
| `MessageDisplay` | `agent_message_chunk` (`messageId` = `message_id`, the same for all pieces of one message) |
| `Stop` | ends the turn; if no `MessageDisplay` came, `last_assistant_message` is sent as the answer, with a new `messageId`; a turn of Claude's own ends with a `session_info_update` carrying `_meta["gorynych/agentTurn"]: "ended"`, which the exit of Claude's process sends too |
| `StopFailure` | ends the turn after a note with the error, with a new `messageId`, and marks a turn of Claude's own ended, as `Stop` does |
| transcript `ai-title` / `custom-title` | `session_info_update` with the title (a chosen name wins) |

**Message IDs** are Claude's own where it has them. An answer's is `MessageDisplay`'s
`message_id`, which Claude's transcript does not keep (the hook carries no transcript ID, and
mapping streamed pieces to transcript entries would hold the stream back). A typed or channel
message's is the hook's `prompt_id`, which the transcript keeps as the `promptId` of the
prompt that started the turn (checked live on 8 November 2026). A channel message that
arrives while Claude works on a turn carries that turn's `prompt_id` (recorded in
`hooks-channel.jsonl`), so an ID an earlier message of the session took gets a new UUID
instead; so does a message without one. The client's own prompts get the service's IDs.

The transcript is only read, from the path the hooks report, for the title. Approvals
and questions are kept apart: `session/request_permission` asks to allow an action,
`elicitation/create` asks for information. If the client cannot show forms, the question
is denied with a note so Claude asks in plain text.

### MCP servers and the channel (opinions both ways)

The `mcpServers` of `session/new` and `session/resume` go into the per-launch
`--mcp-config` (stdio, http and sse), so Claude gets the client's servers and the
service's `gorynych` server. With a channel (the default; `--no-channel` turns it off) the
adapter adds `--channel-url …/channel/<session>/<token>` to that `gorynych` server and starts
Claude with `--dangerously-load-development-channels server:gorynych`: the one server then
declares `capabilities.experimental["claude/channel"]`, long-polls the adapter for opinion
requests, pushes them as `notifications/claude/channel` with `meta: {kind: "opinion",
message_id}` (a `message_id` `c<run tag>-<n>`, never the service's `m…`), and posts `reply`
back; a `reply` the channel does not know goes on to the service. The content's first line names the asking session by
its `session_id` ([service-protocol.md](service-protocol.md#what-agents-are-told)); the meta
carries no sender label, which Claude could take for a session ID. `reply` reports an error
when the channel has no request with that `message_id`. Without a service-given server the adapter starts a channel-only
`gorynych` server itself.

`--append-system-prompt` holds two parts. With a channel, the adapter's own authorization
comes first: it authorizes opinions from the user's agents, forbids actions on their behalf,
says each channel message names the asking session by its `session_id` (or says it comes from
an agent outside Gorynych, which `ask_agent` cannot reach) and that such a message arrives as a
prompt when Claude was not running. Then comes the session's ACP
`systemPrompt`: the adapter advertises `sessionCapabilities.systemPrompt: {}` (append only,
since Claude Code can only add to its own prompt) and takes the text of the latest
`session/new` or `session/resume`. The service sends its peer instructions there, which tell
Claude about `list_agents`, `ask_agent` (by exact `session_id`; `reply` only answers),
`spawn_session` and reports ([service-protocol.md](service-protocol.md#what-agents-are-told)).
`override`, any other mode, an empty `content` and blocks other than text fail the request
with `-32602`, as ACP requires. Without a channel and without a `systemPrompt` there is no
`--append-system-prompt`. The PermissionRequest hook allows `mcp__gorynych__reply`,
`mcp__gorynych__list_agents` and `mcp__gorynych__spawn_session` without asking (the service
asks the person before every spawn); `mcp__gorynych__ask_agent`, `archive_session` and
`restore_session` become ACP approvals like any tool.

`_gorynych/note` (a child's report to its parent) becomes a channel message with
`meta: {kind: "report"}` and no `message_id`; the peer instructions say it needs no reply. The
adapter ignores the `sender` param of `_gorynych/opinion` and `_gorynych/note`: the text names
the session. It keeps the `_meta` each came with, and when Claude reports the channel message
as a prompt (`UserPromptSubmit`, `<channel … message_id="c1">` and the content), it finds it by
that `message_id`, or a report by its text, and puts those keys on the `user_message_chunk`
with `gorynych/origin: channel` and, for a request, `gorynych/opinion` = its `message_id`. So
the journal names the sender, a session or an agent outside Gorynych, as for a request in a
turn. It keeps at most 64 pushed messages that Claude has not reported yet. Without a channel the adapter answers method not found; while Claude's process is not
running (`session/resume` registers a session without starting one) it answers `-32600`, to
`_gorynych/opinion` as well. Either way the service sends the request or report as a turn
instead, which starts Claude, and closes the session again (ending the process) once its
service turns are over, unless a client opened it meanwhile
([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)). For
Claude, *loaded* therefore means registered in the adapter, not that its process runs.

A name given to `spawn_session` stays in the service's overlay: the adapter cannot write
Claude's own `custom-title` yet (that needs `/rename` through the PTY), and Claude's
generated `ai-title` does not replace the name.

Claude Code has no archive of its own, so the archive flag of a Claude session lives in the
service's overlay ([service-protocol.md](service-protocol.md#archive)); the adapter knows
nothing of it.

### Not done yet

- A Unix PTY host; the adapter reports "not supported" off Windows, the service still builds.
- Listing Claude sessions the adapter did not start (`~/.claude/projects`).
- Subagent events, `/rename` from the GUI (and so a spawn name written as Claude's own
  `custom-title`), resizing the pseudo console.

## Codex

Codex does not speak ACP (0.167 has no `codex acp`), so the adapter is a translator, like
Claude's. It is an in-process ACP agent that drives one `codex app-server`, the service's child,
over app-server's JSON-RPC. The design and the probes behind it are in
[research/codex-adapter-spec.md](../research/codex-adapter-spec.md); the probes are in
`research/probes/codex`. Built and checked against Codex 0.167.0 on 8 November 2026.

- **Types** are generated from the app-server schema that `codex app-server
  generate-json-schema` writes, kept in `schema/codex/0.167.0`, by `tools/Gorynych.CodexGen`.
  It works the way `Gorynych.AcpGen` does for ACP: open enums, tagged unions with an `Unknown`
  variant, and a source-generated `CodexJsonContext` for Native AOT. Only the types the adapter
  uses and those they reach are generated (the list is `Roots` in the generator). To move to a
  new Codex, generate the schema into a new folder and run the generator with its version.
- **Process.** `codex` is found on PATH. On Windows the native `codex.exe` that npm's
  `codex.cmd` shim would start through node is used directly. The adapter starts it as
  `codex app-server` (stdio), and app-server starts the threads' MCP servers as its own
  children, so they are inside Gorynych for the `gorynych-outside` walk. The shared daemon
  (`codex app-server daemon`) is not used: it starts MCP servers outside the service's process
  tree. When app-server exits, the adapter stops, the service starts it again on the next
  use, and every session is not loaded. Starting and initializing app-server takes about
  0.4 s.
- **Identity.** `initialize` sends `clientInfo {name: "gorynych", title: "Gorynych"}`. Codex
  records the name as the thread's `originator` and puts it in its user agent and OpenAI's
  compliance logs. The thread's `source` is `vscode`, as for Codex Desktop's threads, so
  Codex's own apps list Gorynych's threads too. The adapter turns off the notifications it
  does not use (`optOutNotificationMethods`): hooks, rate limits, MCP startup and the like.

### Sessions

| ACP | Codex |
| --- | --- |
| `session/new` | `thread/start {cwd, config, developerInstructions}`. Nothing is on disk before the first turn, and the thread is not listed until then (`gorynych/draftsNotDurable`: the service drops an unused draft instead of hiding it) |
| `session/list` | `thread/list {cwd, cursor, limit 50, archived: false, sortKey: updated_at, useStateDbOnly: true}`; `title` is the thread's `name` (none until a name is set or generated: the service shows its provisional title), `updatedAt` from unix seconds |
| `session/resume` | `thread/resume {threadId, excludeTurns: true, config, developerInstructions}` |
| `session/load` | the same, then the history from `thread/items/list`, page by page, as the updates below (only for a thread the journal has not seen: one made in Codex's own apps) |
| `session/close` | `turn/interrupt` when a turn runs (ACP's close cancels it; `thread/unsubscribe` alone lets it run on), then `thread/unsubscribe`, which unloads an idle thread at once |
| `session/delete` | `thread/delete`; a thread Codex no longer has answers `-32002` |
| `_gorynych/set_title` | `thread/name/set` (works before the first turn too) |

- **The `gorynych` server** goes in `config.mcp_servers` of `thread/start` and of every
  `thread/resume`. Codex keeps that config only while the thread is loaded, and ignores it for
  a thread that is already loaded. A stdio server is `{command, args, env}`, an HTTP one
  `{url, http_headers}`; SSE servers are left out.
- **`gorynych-outside` is off** in Gorynych's threads when the user's Codex config registers
  it (`config/read`): `config.mcp_servers["gorynych-outside"] = {enabled: false}`. Turning off
  a server the config does not have fails the thread (`invalid transport`), so it is checked
  first. Together with the ancestry walk this hides the outside tools on every OS. The user's
  other MCP servers, hooks (`~/.codex/hooks.json`) and `AGENTS.md` apply to Gorynych's threads
  as to any other.
- **The appended system prompt** is `developerInstructions` (`sessionCapabilities.systemPrompt`,
  append only; any other mode or a non-text block fails with `-32602`). Codex keeps the
  instructions a thread was created with: sending others on `thread/resume` does not replace
  them (checked live), so a thread keeps the text of the service that created it.
- **One writer.** A thread has one writer process. While Gorynych's app-server has a thread
  loaded, Codex Desktop, the CLI or VS Code cannot continue it, and Gorynych cannot resume a
  thread one of them has open. The adapter turns app-server's `thread <id> already has an
  active writer` into `This Codex thread is open in another Codex app (Codex Desktop, the CLI
  or VS Code); close it there, then try again.` Within its own process the adapter drives
  every turn, so there are no turns of Codex's own to mark (`gorynych/agentTurn`).
- **Idle time: 2 minutes** (`gorynych/unloadAfter: 120`; the service takes the shorter of it and
  `--unload-after`). A loaded thread locks Codex's own apps out, so it should go back soon after
  a conversation moves on. Loading it again is cheap (probe of 8 November 2026): `thread/resume`
  took 0.4 s, and the user's nine MCP servers were up 1.7 s after it, or up to about 11 s when
  one of them fails slowly. Two minutes still covers a quick follow-up question, which then
  finds the thread loaded.
- **Archive.** Codex refuses to resume an archived thread (`session <id> is archived`), while
  Gorynych's archive keeps a session usable, so the archive stays in the service's overlay.
  The list leaves Codex-archived threads out (as Kimi's does), so an archive made in Codex
  Desktop is not mirrored. A thread archived there after Gorynych knew it fails to open with
  `This Codex thread is archived in Codex. Unarchive it there (codex unarchive <id>), then try
  again.`

### Prompts and turns

- `session/prompt` is `turn/start {threadId, input}`, one `text` input per text block (a
  resource link as `@<uri>`). Its `clientUserMessageId` is the service's `messageId` for the
  prompt (`_meta["gorynych/messageId"]`), which Codex keeps as the message's `clientId`, so
  history replayed from Codex carries the journal's IDs.
- The turn ends with `turn/completed`: `completed` → `end_turn`, `interrupted` → `cancelled`,
  `failed` → the prompt fails with Codex's message (a context-window error → `max_tokens`).
  `session/cancel` is `turn/interrupt`; Codex then settles a pending approval itself
  (`serverRequest/resolved`), and the adapter withdraws it from the client.
- Codex's own user message items are not echoed (ACP).

| Codex | ACP `session/update` |
| --- | --- |
| `item/agentMessage/delta` | `agent_message_chunk`, `messageId` = the item ID (`msg_…`); the completed item's text only when no delta came |
| `item/reasoning/summaryTextDelta`, `textDelta`, `summaryPartAdded` | `agent_thought_chunk`, `messageId` = the item ID (`rs_…`); a new summary part starts a paragraph |
| `commandExecution` | `tool_call` titled from `commandActions` (`Read <file>`, `Search …`, else the command without Codex's shell wrapper), kind `read`, `search` or `execute`; on completion its output as text and `rawOutput {exitCode, output}` |
| `fileChange` | `tool_call` kind `edit` (`Create`, `Delete` or `Edit <file>`), locations, each file's diff as text |
| `mcpToolCall` | `tool_call` titled `<tool> (<server>)`, `rawInput` = its arguments, the result's text |
| `webSearch`, `imageView`, `dynamicToolCall`, `collabAgentToolCall`, `imageGeneration` | `tool_call` kinds `fetch`, `read`, `other` |
| `turn/plan/updated` | `plan` |
| `thread/tokenUsage/updated` | `usage_update {used: the last request's input + output tokens, size: the model's context window}` |
| `thread/name/updated` | `session_info_update {title}` |
| `thread/settings/updated` | `config_option_update` |

### Approvals and forms

All approvals are `session/request_permission` on the item's tool call:

| Codex | Options |
| --- | --- |
| `item/commandExecution/requestApproval` | of its `availableDecisions`: `accept` (allow once), `acceptForSession` (allow for this session), `decline` (deny), `cancel` (deny and stop the turn) |
| `item/fileChange/requestApproval` | the same four |
| `item/permissions/requestApproval` | allow for this turn, allow for this session, deny |
| `mcpServer/elicitation/request` with `_meta.codex_approval_kind: mcp_tool_call` | allow once, allow for this session (`_meta.persist: session`), deny |

- **No option writes Codex's configuration.** `acceptWithExecpolicyAmendment` and
  `applyNetworkPolicyAmendment` add rules to `~/.codex/rules/default.rules`, and MCP's
  `persist: always` keeps an approval beyond the session. Codex offers them; the adapter does
  not (adapter-contract §8).
- **The gorynych tools ask the person** like any other MCP tool: their approvals are titled
  `<tool> (<server>)`, which the service does not answer itself. Codex can also allow a
  server's tools without asking (`default_tools_approval_mode: "approve"`, verified per thread),
  and whether to use it is left for a pass over the permissions of all harnesses.
- `item/tool/requestUserInput` (Codex's own questions) and an MCP server's form elicitation are
  `elicitation/create` forms; a URL elicitation is declined.

### Modes, models and effort

ACP config options, applied with the next `turn/start`, which Codex keeps for later turns:

- `mode`: Codex's permission presets.
  - `read-only`: approval `on-request`, sandbox `read-only`.
  - `auto`: `on-request`, `workspace-write`.
  - `auto-review` ("Approve for me"): as `auto`, with Codex's automatic reviewer
    (`approvalsReviewer: auto_review`), a standard per-thread setting of app-server, so nothing
    is written to `~/.codex`. Its decisions were not seen live: the probe's command needed no
    approval.
  - `full-access`: `never`, `danger-full-access`.
  - Settings that match none of these (the user's own config) show as `custom` until changed.
- `model`: `model/list`'s visible models.
- `reasoning_effort`: the selected model's efforts.
- A new thread takes the user's own defaults (here `gpt-6.1-sol`, effort `xhigh`); the adapter
  overrides nothing until the person changes an option.

### Not done yet

- Images and embedded resources in prompts.
- A live-note path through `turn/steer` (a child's report into the parent's running turn).
- Pre-allowing tools, for every harness at once.
- `thread/settings/update` (experimental) to apply an option before the next turn.
