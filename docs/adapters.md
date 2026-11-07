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
- On 7 November 2026 every model answered `session/prompt` with `-32000 Authentication
  required: 403 Your current subscription does not have access to Kimi Code right now`.
  The service relays that error to the client unchanged.

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
| `PermissionRequest` for `AskUserQuestion` | one `session/request_permission` per question, its answers as options; the hook replies `allow` with `updatedInput.answers` |
| `PostToolUse` / `PostToolUseFailure` | `tool_call_update` `completed` / `failed`, `rawOutput`, Bash output as content |
| `MessageDisplay` | `agent_message_chunk` (`messageId` = `message_id`) |
| `Stop` | ends the turn; if no `MessageDisplay` came, `last_assistant_message` is sent as the answer |
| transcript `ai-title` / `custom-title` | `session_info_update` with the title (a chosen name wins) |

The transcript is only read, from the path the hooks report, for the title.
`AskUserQuestion` would fit ACP `elicitation/create` better; the approval-shaped mapping
works with every ACP client today.

### Channel (opinions from other agents)

With a channel (the default; `--no-channel` turns it off), Claude starts
`Gorynych.Service channel --url …/channel/<session>/<token>` over stdio. It is an MCP
server declaring `capabilities.experimental["claude/channel"]`; it long-polls the adapter
for messages, pushes them as `notifications/claude/channel` with
`meta: {sender, message_id}`, and posts `reply` tool calls back. `--append-system-prompt`
authorizes opinions from the user's agents and forbids actions on their behalf; the
adapter's PermissionRequest hook allows the channel's own `reply` tool without asking.
`_gorynych/opinion` on the service is the entry point.

### Not done yet

- A Unix PTY host; the adapter reports "not supported" off Windows, the service still builds.
- Listing Claude sessions the adapter did not start (`~/.claude/projects`).
- Subagent events, `/rename` from the GUI, resizing the pseudo console.
