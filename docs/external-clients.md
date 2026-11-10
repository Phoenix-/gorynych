# External ACP clients (Zed)

Any ACP client that starts an agent over stdio can use Gorynych through the bridge:

```
Gorynych.Service acp --agent claude|kimi|codex|dsh [--pipe <name>] [service options]
```

The bridge connects to the service's pipe and passes ACP between its stdin/stdout and the pipe.
When the service is not running, the bridge starts it: with the logon task when there is one,
and never from an ssh login without it ([A remote machine](#a-remote-machine)). It changes one thing: it adds
`_meta["gorynych/agent"]` to the client's `initialize`, which scopes that connection to one
agent. New sessions then go to that agent and `session/list` shows only its sessions, so the
client needs no Gorynych extensions. `codex app-server proxy` follows the same idea.

Service options after `--agent` (`--data`, `--claude-model`, `--default-agent`,
`--unload-after <minutes>`, `--claude-transport headless|tui`, `--claude-channels ask|accept|decline`, `--no-channel`,
`--no-peers`) apply only when the bridge has to start the service as a process. A running
service keeps its own options, and the logon task has its own. The service outlives the bridge
and the client: it is started without inherited handles, without a console and, when the
client's job object allows it, outside that job.

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
- With the TUI transport (`--claude-transport tui`) only: once, before the first Claude launch that would load Gorynych's channel, the person is asked
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
  `_meta["gorynych/parentSessionId"]`. The service asks the person to approve a spawn with
  a `session/request_permission` on the parent session, unless the user asked for it in their
  own turn
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
([docs](https://zed.dev/docs/ai/external-agents)). The entries for Gorynych, installing or
publishing the build they point to, and the fork's `auto_import_sessions` are in
[getting-started.md](getting-started.md#set-up-zed).

For debugging: Zed's `dev: open acp logs` shows the messages between Zed and the bridge; the
service writes `%LOCALAPPDATA%\Gorynych\logs\service-<date>.log`.

## A remote machine

Zed reaches the agents of another Windows machine through ssh. It starts `ssh -T` to that
machine and runs the bridge there, and the bridge talks to that machine's service. This is the
first step of [Several machines](design.md#several-machines). The hub, which shows every
machine in one list, is planned.

### Setting up the remote machine

Do this once, as the person who works there.

1. Install Gorynych there and sign in to its agents, as in [getting-started.md](getting-started.md).
   Logins never move between machines.
2. Register the logon task, from a terminal or from an ssh login:

   ```
   %LOCALAPPDATA%\Programs\Gorynych\Gorynych.Service.exe task install
   ```

   - Service options go after it (for example `--claude-model sonnet`) and become the task's
     arguments. A relative `--data` is made absolute.
   - `task uninstall` removes the task, and leaves a running service running.
   - Run `task install` again after you move the installation: the task runs the executable
     that registered it.
   - *Unverified:* whether a plain (not elevated) terminal may register the task. If schtasks
     answers "Access is denied", run it from an elevated one: the task still runs with the
     limited token.

What the task does:

- **Where the service runs.** The task `Gorynych.Service.<pipe>` starts the service at the
  person's logon, in their own session and with the limited token. So the service never runs
  elevated or in session 0, although an administrator's ssh login is elevated. The pipe lets
  such an elevated client of the same user in ([service-protocol.md](service-protocol.md#endpoint)).
- **When it runs.** The bridge and the outside server run the task whenever the service's
  pipe is missing. It can run only while the person is signed in to Windows on that machine; a
  locked session will do, and after a reboot they sign in once.
- **Without the task**, a bridge started from ssh starts no service and says to run
  `task install`. A service started from ssh would run in session 0, and elevated for an
  administrator.
- **Console window.** Task Scheduler gives the service a console window of its own; the
  service lets it go at once, so the window only flashes at logon. *Unverified* on a real
  logon.

### The Zed entry

On the machine with Zed, add an entry to `agent_servers` for each remote agent. `work-pc` is a
host from your `~/.ssh/config` that logs in with a key:

```json
"Gorynych: Claude on the work PC": {
  "type": "custom",
  "command": "ssh",
  "args": ["-T", "-o", "BatchMode=yes", "work-pc",
           "%LOCALAPPDATA%\\Programs\\Gorynych\\Gorynych.Service.exe", "acp", "--agent", "claude"]
}
```

*Unverified:* this entry has not been tried with Zed yet.

- **The options.**
  - `-T`: no pseudo terminal, so ACP passes through as it is.
  - `-o BatchMode=yes`: ssh fails at once instead of waiting for a password that Zed cannot
    type.
- **The remote command** runs in the remote user's shell. OpenSSH for Windows uses `cmd.exe`
  by default, which expands `%LOCALAPPDATA%`; with another default shell, give the full path.
- **Service options** given to a bridge over ssh never apply: the task's do.
- **Paths** in such a thread, such as folders and links to files, are the remote machine's.
- **A dropped connection** ends only the bridge. The remote service and its agents work on, and
  Zed can open the session again.

## VS Code's Agents window

VS Code's Agents window (a preview since VS Code 1.120) speaks Microsoft's Agent Host Protocol,
not ACP ([design.md](design.md#the-gui)). Until the service has an AHP face of its own,
[ahp-acp-gateway](https://github.com/formulahendry/ahp-acp-gateway) (MIT, Node 24 or newer)
stands in between. To VS Code it is an AHP host; to Gorynych it is an ACP client that runs the
bridge, one bridge per VS Code chat. Checked on 10 November 2026 with VS Code 1.145.2 and the
gateway 0.2.2: Claude, Codex, Kimi and DeepSeek each answered and wrote a file after an
approval, and their sessions appeared in the service's list.

### Setting it up

1. Install the gateway at a fixed version:

   ```
   npm install ahp-acp-gateway@0.2.2 --prefix "%LOCALAPPDATA%\Programs\ahp-acp-gateway"
   ```

2. Write its configuration to `%LOCALAPPDATA%\Gorynych\ahp-gateway\gateway.json`, and let only
   yourself read it, since it holds the token:

   ```json
   {
     "version": 1,
     "listener": { "host": "127.0.0.1", "port": 8765, "path": "/ahp",
                   "allowAnyOrigin": false, "allowedOrigins": ["vscode-file://vscode-app"] },
     "auth": { "token": "<a long random hex string>" },
     "database": { "path": "C:\\Users\\<you>\\AppData\\Local\\Gorynych\\ahp-gateway\\gateway.db" },
     "providerDefaults": { "requestTimeoutMs": 86400000 },
     "providers": [
       { "id": "copilot", "enabled": false },
       { "id": "gorynych-claude", "displayName": "Gorynych: Claude",
         "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
         "args": ["acp", "--agent", "claude"], "startupTimeoutMs": 60000 }
     ]
   }
   ```

   - Turn off every built-in provider the same way as `copilot`, so the gateway never fetches
     an agent with npx: `copilot`, `claude`, `gemini`, `qwen`, `auggie`, `qoder`, `codex`,
     `opencode`, `openclaw`, `kiro`, `hermes`, `kimi`, `pi`.
   - Add `gorynych-codex`, `gorynych-kimi` and `gorynych-dsh` like `gorynych-claude`, with
     `--agent codex`, `kimi` and `dsh`.
   - `requestTimeoutMs` is how long the gateway lets a prompt run before it fails it and stops
     its bridge. Its default, five minutes, is shorter than many turns.
   - `--print-config` shows what the gateway makes of the file, with the token hidden.

3. Start it at logon with no window. Write `run-gateway.cmd` next to the configuration; it runs
   `"C:\Program Files\nodejs\node.exe" "%LOCALAPPDATA%\Programs\ahp-acp-gateway\node_modules\ahp-acp-gateway\dist\src\cli.js" --config gateway.json`
   and appends the output to `gateway.log`. Then register a task, `Gorynych.AhpGateway`, for
   your logon, with your limited token, no time limit and a restart on failure, that runs:

   ```
   %SystemRoot%\System32\conhost.exe --headless %SystemRoot%\System32\cmd.exe /d /c "%LOCALAPPDATA%\Gorynych\ahp-gateway\run-gateway.cmd"
   ```

   `conhost --headless` gives node a console that is never shown.

4. Add the host to VS Code's user settings:

   ```json
   "chat.remoteAgentHosts": [
     { "name": "Gorynych", "address": "127.0.0.1:8765/ahp?token=<the token>" }
   ]
   ```

   Then open the Agents window (`code --agents`, or **Chat: Open Agents Window**), choose
   **New**, the environment **Gorynych**, a folder and an agent.

### What to expect

- A session started in VS Code is an ordinary Gorynych session, which Zed sees too. Sessions
  started elsewhere do not show in VS Code: the gateway keeps a list of its own.
- The model picker starts at "Auto" for Codex and Kimi, and **Send** stays off until a model is
  picked. The gateway sets the picked model before every prompt, so pick one the account may
  use: on 10 November Kimi's `kimi-code/*` models failed with "Your current subscription does
  not have access to Kimi Code", while its default, under the picker's second tab
  (`moonshot-ai/kimi-k3.1-pro`), worked.
- Claude asks to trust a new folder first, as it does in Zed.
- VS Code's setting warns that a connected host receives your GitHub credentials. In the traffic
  checked on 10 November VS Code sent the gateway no `authenticate` and no GitHub token, and
  the gateway asks for none.
- With `chat.agentHost.ahpJsonlLoggingEnabled` on (by default only in Insiders), VS Code
  writes the host's address, token included, into the names of its AHP log files under
  `%APPDATA%\Code\logs`.
- Left out by the gateway: more than one chat in a session, forks, and Gorynych's own `_meta`
  (archive, child sessions, questions between sessions).

## Agents outside Gorynych

An agent outside Gorynych, such as Claude Code in Claude Desktop or in a terminal, Codex, or
Kimi Code, can list Gorynych's sessions, read what they said and ask one for its opinion
through the `gorynych-outside` MCP server
([service-protocol.md](service-protocol.md#agents-outside-gorynych)). Gorynych never registers
it by itself: each command below changes the agent's own configuration, and the user runs it.

### Publishing the server

The server is its own small exe with its own folder (`outside\` in the published build,
[getting-started.md](getting-started.md#install-gorynych)), because a host keeps it running for
hours and Windows cannot overwrite a running exe.

It starts the service from the parent folder (`%LOCALAPPDATA%\Programs\Gorynych\Gorynych.Service.exe`)
when the service is not running. To replace it while hosts run it, first rename the old
`Gorynych.Outside.exe` aside (`Gorynych.Outside.old.exe`); Windows allows renaming a running exe
on the same volume. The hosts keep the old copy until they restart their servers, and its
answers still come from the new service. Its log is
`%LOCALAPPDATA%\Gorynych\logs\outside-<date>.log`.

### Registering the server

The commands for Claude Code (`claude mcp add --scope user`), Codex (`codex mcp add`) and Kimi
Code (an entry in `~/.kimi-code/mcp.json`), and how to undo each, are in
[getting-started.md](getting-started.md#optional-agents-outside-gorynych). What is known about them:

- **Claude Code.** Checked on 8 November 2026: the Code tab of Claude Desktop reads the same
  user configuration. For its first seconds the server shows as still connecting, and
  ToolSearch waits for it.
- **Kimi Code.** The `mcp.json` entry follows Kimi's docs. *Unverified:* the docs also name a
  TUI command, `/mcp-config`, that edits that file. Neither has been tried with Kimi Code 2.5.2.
- Keep the name `gorynych-outside`: the Claude adapter's deny rule matches it. Any other name
  still works, and the server still hides itself inside Gorynych.
- Optional: allow `mcp__gorynych-outside__list_sessions`, `mcp__gorynych-outside__read_session`
  and `mcp__gorynych-outside__ask_agent` in the agent's own permission settings, so they run
  without asking, as the gorynych tools do inside Gorynych. Gorynych adds no approval of its own
  for an agent outside it, so the host's approval is the only gate: leave `ask_agent` out to
  approve each question yourself.
- Timeouts (Claude Code): a question waits as long as the asked session needs, and Gorynych
  keeps the call alive with progress notifications. Leave `MCP_TOOL_TIMEOUT` (about 28 hours
  by default) and `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT` (30 minutes for stdio servers) as they
  are. A lower `MCP_TOOL_TIMEOUT`, or a per-server `timeout`, cuts long questions off,
  because progress does not extend it.
- To try it without changing any configuration, give the server to one run of Claude with
  `--mcp-config` (a file with `{"mcpServers": {"gorynych-outside": {"type": "stdio", "command": "<path to Gorynych.Outside.exe>"}}}`).
