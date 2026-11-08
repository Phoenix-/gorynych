# Claude Code headless transport: specification

Written and built 9 November 2026 on branch `claude/claude-headless`, with the user's go-ahead
("headless now, the TUI as the fallback"); the user confirmed headless as the default through the
coordinator. The adapter as built is described in [docs/adapters.md](../docs/adapters.md#claude-code);
the build-phase answers are at the end.
It adds a second way to drive Claude Code: the unmodified `claude` binary in its documented
non-interactive mode, `claude -p` with stream-json input and output, kept running for the
session's life. The interactive TUI in a pseudo console stays as a transport for the case
where Anthropic restricts headless use on subscriptions.

It rests on:

- **Claude Code 2.1.302** on Windows 11, with the user's own Max login, in a scratch folder,
  never writing `~/.claude`. Thirteen short probe runs on Haiku 5.5, kept in
  [probes/claude-headless](probes/claude-headless).
- **The protocol types of the official Agent SDK** (`@anthropic-ai/claude-agent-sdk` 0.3.319,
  `sdk.d.ts`), which describe every message and control request the CLI speaks on stdio.
  The SDK drives the same binary with the same flags (`--input-format stream-json
  --output-format stream-json --verbose --permission-prompt-tool=stdio`, then an `initialize`
  control request); the adapter does what the SDK does, in C#.
- **Anthropic's current terms**, read on 9 November 2026. The help article "Use the Claude
  Agent SDK with your Claude plan" (updated 15 June and 7 October 2026) says the Agent SDK and
  `claude -p` signed in with a plan draw from the plan's limits; the Claude Code legal page
  does not prevent "an end user from signing in to the unmodified Claude Code binary with
  their own Claude subscription". Gorynych runs the unmodified binary under each person's own
  login, which the binary itself obtained, and never touches the credentials.

## Summary

- **One adapter, two transports.** `--claude-transport headless|tui` on the service picks how
  the Claude adapter runs its sessions; `headless` is the default. The ACP face (agent id
  `claude`, session IDs, the session list) is the same, and both transports use Claude's own
  transcripts, so a session started in one resumes in the other.
- **No terminal, no screen.** One `claude -p` process per loaded session, kept running across
  turns, JSON lines on stdin and stdout. The PTY plumbing goes away for this transport: no
  paste timing, no idle-composer detection, no dialogs answered by key, no startup timeout on
  a screen.
- **No hooks and no channel.** The stream carries everything the hooks did, and more: token
  deltas, thinking, tool calls with their results, user messages with IDs. Approvals come as
  `can_use_tool` control requests. Opinions and reports go in on stdin, also in the middle of
  a turn, so the development channel, its consent question and its notice are not needed.
- **New with it:** streamed answers and thoughts, model / effort / mode selectors, a native
  rename (`rename_session` writes `custom-title`), images in prompts, and the adapter runs on
  Linux and macOS too.
- **Lost with it:** Claude's generated `ai-title` (headless mode writes none); the service's
  provisional title (the first prompt) stays until a name is given.

## What the probes showed

| Question | Result |
| --- | --- |
| Login | `claude -p` uses the person's subscription: `initialize` reports `subscriptionType: "Claude Max"`, `system/init` reports `apiKeySource: "none"`. |
| One process, many turns | Yes. Each user message on stdin starts a turn when Claude is idle; each turn ends with one `result` message. Closing stdin ends the process after `SessionEnd` (about 0.5 s when idle). |
| `initialize` control request | Answers in under a second without a model call: `models` (value, display name, `supportedEffortLevels`, `supportsAutoMode`, …), `commands`, `agents`, `account`, `capabilities`, `current_permission_mode`, `claude_code_version`. Optional: without it the session still works. |
| Capabilities (2.1.302) | `interrupt_receipt_v1`, `interrupt_cancel_queued_v1`, `interrupt_send_now_v1`, `msg_lifecycle_v1`, `request_marker_lists_v1`, `sdk_mcp_*`, `mcp_read_resource_v1`, `mcp_tool_ui_meta_v1`, `ui_surface_v1`, `mcp_call_result_not_for_model_v1`. |
| Hooks in `-p` | Our http hooks run (without `--bare`), but the adapter no longer needs them. |
| Approvals | With `--permission-prompt-tool=stdio`, a tool that needs approval arrives as a `can_use_tool` control request (`tool_name`, `input`, `tool_use_id`, `title`, `display_name`, `description`, `decision_reason`, `permission_suggestions`, `blocked_path`, …); `{behavior: "allow", updatedInput}` or `{behavior: "deny", message}` answers it. |
| `AskUserQuestion` | Absent from `-p` unless a permission host is declared: neither the PermissionRequest hook nor `CLAUDE_CODE_ENABLE_ASK_USER_QUESTION_TOOL=1` brought it back. With `--permission-prompt-tool=stdio` it comes as `can_use_tool` with `requires_user_interaction: true`, and `updatedInput.answers` (`{question: label}`) answers it, as with the hook. |
| A message while Claude works | Default priority (and `next`): merged into the running turn after the current tool, one `result` for both. `later`: waits for the turn to end, then runs as its own turn with its own `result`. `now`: ends the running turn after the current tool (`terminal_reason: aborted_tools`, empty result), then runs as a new turn. |
| Message IDs | A `uuid` the client puts on a user message comes back on its replay (`--replay-user-messages`, `isReplay: true`) and in `system/status.user_message_uuids`. Assistant messages carry the API's `msg_…` ID (`stream_event message_start`, `assistant.message.id`). |
| Interrupt | `control_request {subtype: "interrupt"}` answers `{still_queued: []}` at once; the turn ends with `result` `error_during_execution`, `terminal_reason: aborted_tools`; the next message runs normally. A pending `can_use_tool` is withdrawn with `control_cancel_request`. |
| `origin` on a user message | `{kind: "peer", …}` is accepted but the model sees no wrapper and the transcript keeps no `origin`; not useful to us. |
| Titles | No `ai-title` in the transcript a minute after the first answer. `rename_session {title, source: "host"}` writes `custom-title` and `agent-name`, as `/rename` does in the TUI. |
| Resume | `--resume <id>` continues the conversation. Transcripts record `entrypoint: "sdk-cli"`. |
| Model, effort, mode | `set_model`, `apply_flag_settings {settings: {effortLevel}}` and `set_permission_mode` answer at once and apply from the next turn (`system/init` shows the new model and mode). |
| Folder trust | `-p` asks nothing and records nothing: Claude's own trust flag (`projects[<folder>].hasTrustDialogAccepted` in `~/.claude.json`) stays as it was. Per the headless docs, a `-p` session runs the folder's project hooks and `.mcp.json` servers even in a folder never trusted. |
| `--bare` | Reads no OAuth credentials and runs no hooks. The headless docs say it "will become the default for -p in a future release"; 2.1.302 has no opt-out flag yet. |

## Design

### Transport choice

- `ClaudeAdapterOptions.Transport`: `Headless` (default) or `Tui`; the service maps
  `--claude-transport headless|tui`. Everything the TUI transport does today stays as it is,
  including its hooks, channel and consent.
- The TUI transport stays built and tested: its contract tests keep running, and the live
  smoke test runs with either transport (`GORYNYCH_CLAUDE_TRANSPORT`). Claude Code changes
  weekly; a transport nobody runs would not work on the day it is needed.
- **When to switch back to the TUI:** Anthropic moves `-p` and the SDK off the plan's limits
  (the paused separate credit), restricts them in the legal page, or makes `--bare` the
  default without an opt-out. Watch the help article named above.

### Launch

```
claude -p --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages --replay-user-messages --permission-prompt-tool=stdio
       --settings <session>/settings.json --permission-mode <mode> [--model <m>] [--effort <e>]
       [--mcp-config <session>/mcp.json] [--append-system-prompt <authorization>\n\n<ACP systemPrompt>]
       (--session-id <id> | --resume <id>)
```

- `settings.json` keeps only the deny rule for `mcp__gorynych-outside`; no hooks.
- `--permission-mode default` unless the person picked another mode (below); a global
  `defaultMode: auto` would otherwise decide approvals without asking.
- The environment is cleaned as for the TUI (`CLAUDECODE`, `CLAUDE_CODE_*`,
  `CLAUDE_AGENT_SDK*`, `CLAUDE_PREVIEW_*`, `CLAUDE_PID`, `CLAUDE_EFFORT`, and inside another
  Claude Code session the desktop's `ANTHROPIC_BASE_URL` and MCP variables).
- Right after the start the adapter sends `initialize` (no fields) and keeps the answer:
  models and capabilities for the selectors, the version for the log.
- The process starts with the session's first prompt, or the first live note's turn; it is
  not started at `session/new` or `session/resume`, as today.
- Stdout is read on its own thread, line by line, as UTF-8; stdin is written as UTF-8 lines
  under one lock. Stderr goes to `<session>/stderr.log` (bounded), shown in an error when the
  process exits early.

### Prompts and turns

- `session/prompt` waits while a turn runs (a turn of Claude's own included), then writes one
  `user` message with a fresh `uuid` and the prompt's content; the turn it starts ends with
  the next `result`.
- `result` → `stopReason`: `success` → `end_turn`; `stop_reason: "max_tokens"` →
  `max_tokens`; `error_max_turns` → `max_turn_requests`; `stop_reason: "refusal"` → `refusal`;
  any result after `session/cancel` → `cancelled`; any other error → a note with its
  `errors`/`result` text, then `end_turn`.
- The process exiting during a turn fails the prompt with the exit code and the end of stderr;
  the next prompt starts a new process with `--resume`.
- Content: text blocks as text; `image` blocks as Anthropic image blocks (`source.type:
  "base64"`), so the adapter advertises `promptCapabilities.image`; `resource_link` as
  `@<uri>` text, as today.

### Stream → ACP

| Claude stream | ACP |
| --- | --- |
| `stream_event` `message_start` | remembers the message ID (`msg_…`) for its chunks |
| `stream_event` `content_block_delta` `text_delta` | `agent_message_chunk`, `messageId` = the message's ID |
| `stream_event` `content_block_delta` `thinking_delta` | `agent_thought_chunk`, `messageId` = the message's ID |
| `assistant` with `tool_use` blocks | `tool_call` (`pending`), `toolCallId` = the block's `id`, title, kind and locations as for the hooks today |
| `assistant` text a turn streamed no delta for | `agent_message_chunk` with the whole text (a fallback; partial messages normally cover it) |
| `user` with `tool_result` blocks | `tool_call_update` `completed` or `failed` (`is_error`), `rawOutput` = `tool_use_result`, content as for the hooks today (Bash output, the error text) |
| `user` replay of a message the adapter delivered for the service | `user_message_chunk`, `messageId` = its `uuid`, with the note's `_meta` and `gorynych/origin: "live"` (below) |
| `user` replay of the client's own prompt | nothing (ACP does not echo) |
| `system` `init` | the current model and permission mode, for the selectors |
| `result` | ends the turn (above); a turn of Claude's own ends with `_meta["gorynych/agentTurn"]: "ended"` |
| messages with a `parent_tool_use_id` (a subagent's) | not shown yet; the `Task` tool call stays `pending` until its result |
| `system` `task_*`, `status`, `rate_limit_event`, `thinking_tokens`, `api_retry` | logged only |

### Approvals and questions

- `can_use_tool` → `session/request_permission` (Allow / Deny), as the PermissionRequest hook
  does today; the reply is `{behavior: "allow", updatedInput: <input>}` or `{behavior: "deny",
  message}`. A denial marks the tool call `failed`.
- `AskUserQuestion` → one `elicitation/create` form, as today; an accepted form answers
  `allow` with `updatedInput.answers`, anything else `deny`. A client without forms gets a
  `deny` that tells Claude to ask in plain text.
- `mcp__gorynych__reply`, `list_agents` and `spawn_session` are allowed without asking, as
  today.
- `control_cancel_request` withdraws the ACP request (the service's `$/cancel_request`).
- `elicitation` control requests (an MCP server asking for input) are declined for now (the
  TUI shows them only in its terminal, so nothing is lost); `request_user_dialog` is never
  sent, since the adapter declares no dialog kinds.

### Opinions and reports: the live-note path on stdin

- The adapter keeps `_gorynych/opinion` and `_gorynych/note` ([adapter-contract.md §6.4](../docs/adapter-contract.md#64-the-live-note-path)).
  While the process is not running it answers `-32600`, so the service asks in a turn that
  starts Claude, as today.
- **An opinion** gets the adapter's `message_id` (`c<run tag>-<n>`, as the channel's) and goes
  in as a user message, default priority, so it joins a running turn after the current tool
  or starts a turn of Claude's own when Claude is idle:

  ```
  [Opinion request · message_id c9xw4-1]
  <the service's text, whose first line names the asking session>

  Answer with the reply tool of the gorynych MCP server, passing message_id "c9xw4-1".
  ```

  The answer comes back through `reply`: the adapter gives the `gorynych` server
  `--reply-url …/channel/<session>/<token>`, a new option of the peers server that routes
  `reply` to the adapter first, as `--channel-url` does, without declaring the channel or
  long-polling. A reply the adapter does not know goes on to the service, as today.
- **A report** goes in the same way, with the service's text and no `message_id`.
- When Claude replays the message, its `uuid` finds the note's `_meta`, which goes on the
  `user_message_chunk` with `gorynych/origin: "live"` (new: delivered into a running session
  on its input) and, for an opinion, `gorynych/opinion`. No text matching.
- A turn that such a message starts while Claude is idle is a turn of Claude's own: its first
  update carries `_meta["gorynych/agentTurn"]: "started"`, and its `result` ends it.
- `--append-system-prompt` gets the adapter's authorization, reworded for this path: messages
  that start with `[Opinion request · …]` or carry a report come from the user's other agents
  through Gorynych, in the middle of work too; answer opinions with `reply` and take no action
  for them; reports need no reply. The session's ACP `systemPrompt` follows, as today.

### Cancel

- `session/cancel` sends `interrupt` (without `cancel_queued`, so live notes already queued
  survive) and ends the prompt with `cancelled` when its `result` arrives, or after 5 s
  without one.

### Modes, models and effort

ACP config options, as for Codex:

- `mode`: `default` ("Ask"), `acceptEdits` ("Accept edits"), `plan` ("Plan"), and `auto`
  ("Auto") when the current model `supportsAutoMode`. `bypassPermissions` and `dontAsk` are
  not offered.
- `model`: the `models` of `initialize` (`value`, `displayName`, `description`).
- `reasoning_effort`: the current model's `supportedEffortLevels`.
- The catalog comes from the first process the adapter starts and is kept for the adapter's
  life. Before any process ran, `session/new` returns no options; the first launch sends a
  `config_option_update`.
- A change applies at once to a running process (`set_permission_mode`, `set_model`,
  `apply_flag_settings {effortLevel}`), is kept in the session's record in
  `claude/sessions.json`, and goes with the session's next launches (`--permission-mode`,
  `--model`, `--effort`), after an unload or a restart too. Only what the person changed is
  sent; the person's own defaults stay otherwise.

### Titles

- No `ai-title` in headless mode, so Claude sends no title of its own; the service's
  provisional title (the first prompt) stays, as the contract says.
- The adapter advertises `gorynych/setTitle` and maps `_gorynych/set_title` to
  `rename_session {title, source: "host"}`, which writes Claude's own `custom-title`: a
  `spawn_session` name becomes native. While the process is not running, the name waits for
  the next launch.
- The transcript watcher stays: a `custom-title` written elsewhere (a later TUI `/rename`, or
  `claude --resume` in a terminal) still reaches the client.

### Folder trust

The TUI asks before Claude works in a folder never trusted; `-p` does not, and runs that
folder's project hooks and `.mcp.json` servers at once. So before the first launch in a
folder the adapter checks Claude's own flag, read only: `projects` in `~/.claude.json`, the
folder or one of its parents with `hasTrustDialogAccepted: true`. Otherwise it asks the
person with the same `session/request_permission` the TUI's trust dialog becomes today. An
allow is kept in the adapter's state (`claude/trusted.json`), never in Claude's configuration;
a deny fails the prompt. A file it cannot read counts as not trusted.

### Close, unload and platforms

- `session/close` interrupts a running turn, closes stdin, waits up to 5 s for the process to
  exit, then kills its tree.
- The headless transport needs no PTY, so the adapter runs on Linux and macOS; the TUI
  transport stays Windows only and refuses to start elsewhere, as today.

## Contract and doc changes

- [docs/adapters.md](../docs/adapters.md): the Claude section describes both transports, the
  headless one first.
- [docs/adapter-contract.md](../docs/adapter-contract.md): the Claude column of §13 (live notes on
  stdin, `gorynych/setTitle`, approvals by `can_use_tool`, titles); §6.4 names both paths.
- [docs/service-protocol.md](../docs/service-protocol.md): `gorynych/origin` gains `live`;
  `read_session` labels it as it does `channel`.
- [docs/design.md](../docs/design.md) and [AGENTS.md](../AGENTS.md): Claude is driven headless by
  default; the TUI in a pseudo console is the fallback transport.
- The peers server gains `--reply-url`.

## The default and its trade-off

Headless is the default because it is far simpler and richer than driving the TUI, and the
terms allow it today. The TUI stays the more durable footing: the paused separate credit would
hit exactly `-p` and the SDK, and `--bare` is to become the default for `-p`. So:

- the default is one switch, `--claude-transport` (`ClaudeAdapterOptions.Transport`);
- the TUI transport stays built, with its contract tests on `Gorynych.FakeClaude` (its TUI mode)
  and its live tests (`GORYNYCH_CLAUDE_TRANSPORT=tui`);
- switching needs no migration (verified live, below);
- headless never falls back silently (next section).

## Failing loudly

- **No login** in `initialize` (no plan, `tokenSource: "none"`): what `--bare` gives (probed:
  `claude -p --bare` answers `account: {tokenSource: "none", apiProvider: "firstParty"}` and its
  first turn fails "Not logged in"). Claude is stopped; the prompt fails, telling the person to
  sign in or to use `--claude-transport tui`.
- **An API key** in `initialize` (`account.apiKeySource`, probed with a dummy
  `ANTHROPIC_API_KEY`: `{tokenSource: "claude.ai", apiKeySource: "ANTHROPIC_API_KEY"}`, no plan):
  in `-p` the key wins over the login, so the session would be billed outside the plan. Claude
  is stopped; the prompt fails, naming the variable to remove.
- **Auth or billing errors** during a turn (an assistant message's `error`:
  `authentication_failed`, `billing_error`, `account_on_hold`, `oauth_org_not_allowed`,
  `verification_required`): a note in the feed and the client's prompt fails, both naming
  `--claude-transport tui`.

## Build-phase answers

- **Tests.** `ClaudeHeadlessTests` (7, on `Gorynych.FakeClaude -p`) and `StreamJsonTests` (7);
  the TUI's `ClaudeAdapterTests` run pinned to `Transport = Tui`. The whole suite: 240 tests, 231
  passed, 9 live skipped.
- **Live, headless** (Haiku 5.5, Claude Code 2.1.302, each on its own pipe and temp data):
  first prompt, Write approval, reload and a live-note opinion answered through `reply`
  (`Claude_FirstPrompt_Approval_Title_Reload_AndOpinion`); a session the service woke for a
  question (`Claude_NotRunning_…`); the stdio bridge with a plain client; `gorynych-outside`
  hidden inside a session. All passed.
- **Live, TUI**: the same first-prompt and bridge tests passed with
  `GORYNYCH_CLAUDE_TRANSPORT=tui`.
- **Switching transports**: `Claude_SessionsMoveBetweenTransports_BothWays` started a session
  headless and asked it in the TUI (it remembered PELICAN), then the other way round (MARMOT).
  Both use `--resume <id>` on the same transcript; the adapter's session list is shared.
- **`Claude_NotRunning_…` failed on both transports** before this branch: it asserted that a
  client's `session/close` unloads at once, which the idle-unload rule (755280c) changed. The
  test now runs its conductor with `UnloadAfter = 0` and waits for the unload.
- **Client `uuid`s** come back on replays and in `system/status`, but Claude's transcript keeps
  its own IDs; history is replayed from the service's journal, so the IDs agree there.

## Not verified, open (after the build)

- Whether `claude --resume`'s picker in a terminal lists `sdk-cli` sessions (resuming by ID
  works, above).
- Subagent messages, background tasks and `/compact` are not mapped; MCP elicitations are
  declined.
- An opt-out from `--bare` once it becomes the default for `-p`; until then the loud failure
  points to the TUI.
- Headless on Linux and macOS.
