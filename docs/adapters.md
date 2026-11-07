# Adapters

Every adapter is an ACP agent (`IAgentAdapter` in `Gorynych.Acp.Hosting`) compiled into
the service; nothing is loaded at run time, so the service stays Native AOT compatible.
An adapter returns the byte streams of an ACP connection: an external process's stdio,
or a `DuplexLink` to an in-process agent.

## Kimi Code

`kimi acp` is native ACP, so the adapter only runs the unmodified binary under the
user's own login and hands its stdio to the service. Observed with Kimi Code 2.4.1
(recorded in `tests/Gorynych.Tests/Fixtures/kimi`):

- `initialize`: `loadSession`, `sessionCapabilities` list, resume, close, delete, fork,
  additionalDirectories; one terminal auth method (`kimi login`).
- `session/new` returns config options (model, thinking, mode) and modes; the first
  prompt immediately yields `session_info_update` with the prompt as the title.
- `session/list` returns titles and `updatedAt`.
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
  before it spawns ([service-protocol.md](service-protocol.md#child-sessions)).
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
       [--mcp-config <session>/mcp.json --dangerously-load-development-channels server:gorynych
        --append-system-prompt <channel authorization>]
       (--session-id <id> | --resume <id>) -- <first prompt>
```

- `settings.json` holds only http hooks posting to
  `http://127.0.0.1:<port>/hook/<session>/<token>` (Kestrel on loopback, a random token
  per session). They add to the user's global hooks.
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
| `UserPromptSubmit` | nothing for the client's own prompt (ACP does not echo it); otherwise `user_message_chunk` with `_meta["gorynych/origin"]` = `channel` or `terminal` |
| `PreToolUse` | `tool_call` (`pending`), `toolCallId` = `tool_use_id`, title and kind from the tool, locations from `file_path` |
| `PermissionRequest` | `session/request_permission` with Allow / Deny; the hook replies `allow` or `deny`. A denial also marks the tool call `failed`. |
| `PermissionRequest` for `AskUserQuestion` | one `elicitation/create` form (scoped to the session and tool call) with a property `q1`, `q2`, … per question: a string with `oneOf` for a single choice, an array of titled items for `multiSelect`; an accepted form makes the hook reply `allow` with `updatedInput.answers` (`{question: label}`, several labels joined by ", "), anything else `deny` |
| `PostToolUse` / `PostToolUseFailure` | `tool_call_update` `completed` / `failed`, `rawOutput`, Bash output as content |
| `MessageDisplay` | `agent_message_chunk` (`messageId` = `message_id`) |
| `Stop` | ends the turn; if no `MessageDisplay` came, `last_assistant_message` is sent as the answer |
| transcript `ai-title` / `custom-title` | `session_info_update` with the title (a chosen name wins) |

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
requests, pushes them as `notifications/claude/channel` with `meta: {sender, message_id}`,
and posts `reply` back. Without a service-given server the adapter starts a channel-only
`gorynych` server itself.

`--append-system-prompt` authorizes opinions from the user's agents, forbids actions on
their behalf, and, when the peer tools are there, tells Claude about `list_agents`,
`ask_agent` and `spawn_session`. The PermissionRequest hook allows `mcp__gorynych__reply`,
`mcp__gorynych__list_agents` and `mcp__gorynych__spawn_session` without asking (the service
asks the person before every spawn); `mcp__gorynych__ask_agent` becomes an ACP approval like
any tool.

A name given to `spawn_session` stays in the service's overlay: the adapter cannot write
Claude's own `custom-title` yet (that needs `/rename` through the PTY), and Claude's
generated `ai-title` does not replace the name.

### Not done yet

- A Unix PTY host; the adapter reports "not supported" off Windows, the service still builds.
- Listing Claude sessions the adapter did not start (`~/.claude/projects`).
- Subagent events, `/rename` from the GUI (and so a spawn name written as Claude's own
  `custom-title`), resizing the pseudo console.
