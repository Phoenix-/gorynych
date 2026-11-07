# External ACP clients (Zed)

Any ACP client that starts an agent over stdio can use Gorynych through the bridge:

```
Gorynych.Service acp --agent claude|kimi [--pipe <name>] [service options]
```

The bridge connects to the service's pipe (starting the service if it is not running) and
passes ACP between its stdin/stdout and the pipe. It changes one thing: it adds
`_meta["gorynych/agent"]` to the client's `initialize`, which scopes that connection to one
agent. New sessions then go to that agent and `session/list` shows only its sessions, so the
client needs no Gorynych extensions. `codex app-server proxy` follows the same idea.

Service options after `--agent` (`--data`, `--claude-model`, `--default-agent`,
`--no-channel`, `--no-peers`) apply only when the bridge has to start the service; a running
service keeps its own. The service outlives the bridge and the client: it is started
without inherited handles, without a console and, when the client's job object allows it,
outside that job.

## What a plain client gets

Checked by `BridgeTests` (a client that sends no `_meta` and declares no forms, fs or
terminal) and by the live `PlainClient_ThroughTheBridge_…` test against real Claude:

- `initialize`: agent `gorynych`, title `Gorynych: Claude Code` or `Gorynych: Kimi Code`,
  `loadSession`, `sessionCapabilities.list/resume/close/delete`, no auth methods, text
  prompts only.
- The feed is standard `session/update`: message and thought chunks, `tool_call`,
  `tool_call_update`, `plan`, `usage_update`, `session_info_update`. Gorynych's own
  `_meta` keys can be ignored.
- Approvals are `session/request_permission`.
- Questions (Claude's `AskUserQuestion`) are forms (`elicitation/create`) for clients that
  declare `elicitation.form`; for others the service asks each choice field as a
  `session/request_permission` whose options are the answers plus `Skip`. A form with free
  text is declined, and Claude then asks in plain text.
- The service and its agents never call the client's `fs/*` or `terminal/*`.
- `session/load` replays the history from the service journal. It and `session/resume`
  return the agent's config options and modes with their current values, also for a session
  the service already has open, so the selectors (Kimi's model, thinking, mode) survive a Zed
  restart.
- Sessions created but never prompted (drafts) are deleted when the client closes them or
  goes away, and are not listed ([service-protocol.md](service-protocol.md#drafts-and-deletion)).

## Zed

### What Zed does with sessions

From Zed's source (main `b5cd310`, `crates/agent_servers/src/acp/v1.rs`, `crates/agent_ui`),
and what the service does about it:

- `session/new` is eager: it fires when a thread view is created (the panel opens, `+`),
  before any prompt. Zed does not keep the IDs of such drafts, so every restart makes
  another one, and quitting kills the bridge without `session/close`. The service deletes
  these drafts when the bridge disconnects, and sweeps any left over at its next start.
- `session/close` is sent, because the service advertises `close`, when Zed drops a thread:
  a thread or draft removed or replaced, idle threads beyond the five it keeps, an
  abandoned new or load. Archiving and quitting send nothing. The service closes a session in
  its agent only when no other client has it open.
- Archiving is local to Zed. Deleting a thread from its history sends `session/delete` only
  if the agent advertises `sessionCapabilities.delete`, which the service does; a
  "not found" error (`-32002`) counts as success. Kimi itself answers `-32602` for a session
  it no longer has; the service turns that into success when Kimi no longer lists it.
- `session/list` is called only from the "Import threads" dialog. It passes no `cwd`,
  follows `nextCursor`, and imports the sessions as archived threads.
- Forms (`elicitation/create`) need no flag. Zed renders string (with `minLength`,
  `maxLength`, `pattern`, `format`), single choice (`enum` or titled `oneOf`, never both),
  number, integer, boolean, and multi-select arrays of strings or titled `anyOf` (with
  `minItems`/`maxItems`). A required property it cannot render, or a string with both
  `enum` and `oneOf`, fails the whole form with `-32602`.
- Approvals accept only the option kinds `allow_once`, `allow_always`, `reject_once` and
  `reject_always`, and distinct option IDs; anything else fails the request with `-32602`.
  An unknown `toolCallId` makes Zed create the tool call.

`SessionLifecycleTests.ZedRules` checks the forms the Claude adapter builds and every
approval the service and the Claude adapter emit against these rules.

### Settings

Zed registers custom agents under `agent_servers` in its `settings.json`
([docs](https://zed.dev/docs/ai/external-agents)). Open it from Zed with
`zed: open settings` and add (paths for the build published to
`%LOCALAPPDATA%\Programs\Gorynych`):

```json
{
  "agent_servers": {
    "Gorynych: Claude": {
      "type": "custom",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "claude"],
      "env": {}
    },
    "Gorynych: Kimi": {
      "type": "custom",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "kimi"],
      "env": {}
    }
  }
}
```

Publish that build with (the service must not be running, its exe is locked):

```
dotnet publish src/Gorynych.Service -c Release -r win-x64 -p:PublishAot=true -o %LOCALAPPDATA%\Programs\Gorynych
```

For debugging: Zed's `dev: open acp logs` shows the messages between Zed and the bridge; the
service writes `%LOCALAPPDATA%\Gorynych\logs\service-<date>.log`.
