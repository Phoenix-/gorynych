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
  `loadSession`, `sessionCapabilities.list/resume/close`, no auth methods, text prompts only.
- The feed is standard `session/update`: message and thought chunks, `tool_call`,
  `tool_call_update`, `plan`, `usage_update`, `session_info_update`. Gorynych's own
  `_meta` keys can be ignored.
- Approvals are `session/request_permission`.
- Questions (Claude's `AskUserQuestion`) are forms (`elicitation/create`) for clients that
  declare `elicitation.form`; for others the service asks each choice field as a
  `session/request_permission` whose options are the answers plus `Skip`. A form with free
  text is declined, and Claude then asks in plain text.
- The service and its agents never call the client's `fs/*` or `terminal/*`.
- `session/load` replays the history from the service journal.

## Zed

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
