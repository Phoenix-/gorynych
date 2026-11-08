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
  session that is not loaded is resumed for an opinion request or a report and closed again
  after it ([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)).
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
| `UserPromptSubmit` | nothing for the client's own prompt (ACP does not echo it); otherwise `user_message_chunk` with `_meta["gorynych/origin"]` = `channel` or `terminal` and `messageId` = the hook's `prompt_id` (below) |
| `PreToolUse` | `tool_call` (`pending`), `toolCallId` = `tool_use_id`, title and kind from the tool, locations from `file_path` |
| `PermissionRequest` | `session/request_permission` with Allow / Deny; the hook replies `allow` or `deny`. A denial also marks the tool call `failed`. |
| `PermissionRequest` for `AskUserQuestion` | one `elicitation/create` form (scoped to the session and tool call) with a property `q1`, `q2`, … per question: a string with `oneOf` for a single choice, an array of titled items for `multiSelect`; an accepted form makes the hook reply `allow` with `updatedInput.answers` (`{question: label}`, several labels joined by ", "), anything else `deny` |
| `PostToolUse` / `PostToolUseFailure` | `tool_call_update` `completed` / `failed`, `rawOutput`, Bash output as content |
| `MessageDisplay` | `agent_message_chunk` (`messageId` = `message_id`, the same for all pieces of one message) |
| `Stop` | ends the turn; if no `MessageDisplay` came, `last_assistant_message` is sent as the answer, with a new `messageId` |
| `StopFailure` | ends the turn after a note with the error, with a new `messageId` |
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
message_id}` (a `message_id` starting with `c`, never the service's `m`), and posts `reply`
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
