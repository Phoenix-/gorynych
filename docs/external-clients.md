# External ACP clients (Zed)

Any ACP client that starts an agent over stdio can use Gorynych through the bridge:

```
Gorynych.Service acp --agent claude|kimi|codex [--pipe <name>] [service options]
```

The bridge connects to the service's pipe (starting the service if it is not running) and
passes ACP between its stdin/stdout and the pipe. It changes one thing: it adds
`_meta["gorynych/agent"]` to the client's `initialize`, which scopes that connection to one
agent. New sessions then go to that agent and `session/list` shows only its sessions, so the
client needs no Gorynych extensions. `codex app-server proxy` follows the same idea.

Service options after `--agent` (`--data`, `--claude-model`, `--default-agent`,
`--unload-after <minutes>`, `--claude-channels ask|accept|decline`, `--no-channel`,
`--no-peers`) apply only when the bridge has to start the service; a running
service keeps its own. The service outlives the bridge and the client: it is started
without inherited handles, without a console and, when the client's job object allows it,
outside that job.

## What a plain client gets

Checked by `BridgeTests` (a client that sends no `_meta` and declares no forms, fs or
terminal) and by the live `PlainClient_ThroughTheBridge_…` test against real Claude:

- `initialize`: agent `gorynych`, title `Gorynych: Claude Code`, `Gorynych: Kimi Code`, `Gorynych: Codex` or `Gorynych: DeepSeek Harness`,
  `loadSession`, `sessionCapabilities.list/resume/close/delete`, no auth methods, text
  prompts only.
- The feed is standard `session/update`: message and thought chunks, `tool_call`,
  `tool_call_update`, `plan`, `usage_update`, `session_info_update`. Gorynych's own
  `_meta` keys can be ignored.
- Approvals are `session/request_permission`.
- Once, before the first Claude launch that would load Gorynych's channel, the person is asked
  on that session whether Gorynych may start Claude Code with development channels (a Claude
  Code research preview) and confirm the notice Claude shows for them on each launch
  (tool call `development-channels`, options "Allow" and "Don't allow"). The answer is kept in
  the service's data; declining runs Claude without the channel, and other agents' questions
  reach it as turns. `--claude-channels accept|decline` gives the answer up front
  ([adapters.md](adapters.md#consent-to-development-channels)).
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
- A session an agent spawned is an ordinary session in `session/list`, with its parent in
  `_meta["gorynych/parentSessionId"]`. The service asks the person to approve each spawn with
  a `session/request_permission` on the parent session
  ([service-protocol.md](service-protocol.md#child-sessions)). If the spawn is withdrawn
  first, the service sends `$/cancel_request` for that question; a client that ignores it
  keeps the card, and its answer changes nothing. Whether Zed handles `$/cancel_request` for
  an agent's request is not confirmed.
- Folders in `session/list` are spelled one way (on Windows: backslashes, no trailing
  separator), whatever spelling the agent used.
- `_gorynych/sessions_changed` tells a client that a session appeared, changed or went
  away; a plain client ignores it and sees new sessions the next time it lists them
  ([service-protocol.md](service-protocol.md#list-changes)).
- `session/list` leaves out sessions archived in the service (by an agent's
  `archive_session`, or by a client's `_gorynych/archive`), as Codex and Kimi leave theirs out.
  A client that sends `_meta["gorynych/includeArchived"]: true` gets them too, each with
  `_meta["gorynych/archived"]: true` ([service-protocol.md](service-protocol.md#archive)). A
  plain client sends no `_meta`, so it never sees them in a list; it can still open one whose
  ID it already holds.

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
  abandoned new or load. Archiving and quitting send nothing. When no other client has the
  session open, the service cancels a running turn that a client's prompt started, as ACP asks
  of a close (another agent's question or a child's report in it goes on), and unloads it
  from its agent once it has been idle for `--unload-after` minutes (10 by default). Quitting
  sends nothing, so the service takes the bridge going away as letting go of every session Zed
  had open: each is unloaded after the same idle time, counted from the end of its running
  turn if it has one. Nothing is cancelled, and a session whose approval waits for an answer
  stays loaded ([service-protocol.md](service-protocol.md#unloading-idle-sessions)).
- Archiving is local to Zed: stock Zed sends nothing, so the service and its agents see the
  session as merely closed and keep listing it (`list_agents` too). A client that wants the
  service to know calls `_gorynych/archive` ([service-protocol.md](service-protocol.md#archive)).
  Deleting a thread from its history sends `session/delete` only
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
approval the service and the Claude adapter emit against these rules, the spawn approval
included.

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
    },
    "Gorynych: Codex": {
      "type": "custom",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "codex"],
      "env": {}
    },
    "Gorynych: DeepSeek": {
      "type": "custom",
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "dsh"],
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

## Agents outside Gorynych

An agent outside Gorynych, such as Claude Code in Claude Desktop or in a terminal, Codex, or
Kimi Code, can list Gorynych's sessions, read what they said and ask one for its opinion
through the `gorynych-outside` MCP server
([service-protocol.md](service-protocol.md#agents-outside-gorynych)). Gorynych never registers
it by itself: each command below changes the agent's own configuration, and the user runs it.

### Publishing the server

The server is its own small exe with its own folder, because a host keeps it running for hours
and Windows cannot overwrite a running exe:

```
dotnet publish src/Gorynych.Outside -c Release -r win-x64 -p:PublishAot=true -o %LOCALAPPDATA%\Programs\Gorynych\outside
```

It starts the service from the parent folder (`%LOCALAPPDATA%\Programs\Gorynych\Gorynych.Service.exe`)
when the service is not running. To replace it while hosts run it, first rename the old
`Gorynych.Outside.exe` aside (`Gorynych.Outside.old.exe`); Windows allows renaming a running exe
on the same volume. The hosts keep the old copy until they restart their servers, and its
answers still come from the new service. Its log is
`%LOCALAPPDATA%\Gorynych\logs\outside-<date>.log`.

### Registering the server

- **Claude Code.** Checked on 8 November 2026: the Code tab of Claude Desktop reads the same
  user configuration. For its first seconds the server shows as still connecting, and
  ToolSearch waits for it.
  ```
  claude mcp add --scope user gorynych-outside -- "C:\Users\<you>\AppData\Local\Programs\Gorynych\outside\Gorynych.Outside.exe"
  ```
  To undo it: `claude mcp remove --scope user gorynych-outside`.
- **Codex:**
  ```
  codex mcp add gorynych-outside -- "C:\Users\<you>\AppData\Local\Programs\Gorynych\outside\Gorynych.Outside.exe"
  ```
  To undo it: `codex mcp remove gorynych-outside`.
- **Kimi Code:** an entry in the `mcpServers` object of `~/.kimi-code/mcp.json`, from Kimi's
  docs. *Unverified:* the docs also name a TUI command, `/mcp-config`, that edits that file.
  Neither has been tried with Kimi Code 2.5.2.
  ```json
  { "mcpServers": { "gorynych-outside": { "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\outside\\Gorynych.Outside.exe" } } }
  ```
- Keep the name `gorynych-outside`: the Claude adapter's deny rule matches it. Any other name
  still works, and the server still hides itself inside Gorynych.
- Optional: allow `mcp__gorynych-outside__list_sessions` and
  `mcp__gorynych-outside__read_session` in the agent's own permission settings.
- Timeouts (Claude Code): a question waits as long as the asked session needs, and Gorynych
  keeps the call alive with progress notifications. Leave `MCP_TOOL_TIMEOUT` (about 28 hours
  by default) and `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT` (30 minutes for stdio servers) as they
  are. A lower `MCP_TOOL_TIMEOUT`, or a per-server `timeout`, cuts long questions off,
  because progress does not extend it.
- To try it without changing any configuration, give the server to one run of Claude with
  `--mcp-config` (a file with `{"mcpServers": {"gorynych-outside": {"type": "stdio", "command": "<path to Gorynych.Outside.exe>"}}}`).
