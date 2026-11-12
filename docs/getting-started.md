# Getting started

Gorynych is early work in progress. This guide sets it up on Windows with Zed as the GUI.

On Linux and macOS the service builds and Kimi Code runs. Claude Code's default, headless
transport uses nothing Windows-only but has not been tried there; its TUI fallback needs
Windows (ConPTY). Codex and DeepSeek Harness have not been tried there either, and nothing is
published for those systems yet.

## What you need

- Windows 10 1809 or later, x64.
- The agents you want, installed, on your `PATH` and logged in with your own account. Any
  subset works:
  - Claude Code: `claude`.
  - Kimi Code: `kimi`, after `kimi login`.
  - Codex CLI: `codex`, after `codex login`.
  - DeepSeek Harness: `dsh`, from `npm install -g @deepseek-ai/dsh` (it needs Node.js on your
    `PATH`), with your own DeepSeek API key set in DSH, for example on the Models page of
    `dsh web`. DSH is a developer preview, and every turn spends your DeepSeek API balance.
- An ACP client. This guide uses Zed.

Gorynych runs these agents as they are, under your login, and never edits their own
configuration (`~/.claude`, `~/.codex`, `~/.kimi-code`, `~/.dsh`). The first time Gorynych starts
DSH, DSH creates its own `acp` profile in `~/.dsh/profiles/acp`, as any `dsh acp` does.

## Install Gorynych

Every push to `main` that passes CI publishes a Windows build on the
[nightly](https://github.com/Phoenix-/gorynych/releases/tag/nightly) prerelease. In
PowerShell:

```powershell
$zip = "$env:TEMP\gorynych-windows-x64.zip"
Invoke-WebRequest https://github.com/Phoenix-/gorynych/releases/download/nightly/gorynych-windows-x64.zip -OutFile $zip
Unblock-File $zip
Expand-Archive $zip "$env:LOCALAPPDATA\Programs" -Force
```

This gives `%LOCALAPPDATA%\Programs\Gorynych`, which holds `Gorynych.Service.exe` and
`outside\Gorynych.Outside.exe`. Any other folder works too; the paths below then change with it.

**There is nothing to start by hand.** The first thread you open in Zed starts the service
through the bridge ([Set up Zed](#set-up-zed)), and it keeps running after Zed quits. Agents
outside Gorynych start the outside server themselves.

The binaries are not signed. A zip downloaded with a browser carries the Mark of the Web, and
Explorer's "Extract All" copies it to every file. `Expand-Archive` and `tar` do not. Files with
the mark may get a SmartScreen warning when you start them yourself. Clear the mark with
`Unblock-File`, on the zip before extracting or afterwards:

```powershell
Get-ChildItem "$env:LOCALAPPDATA\Programs\Gorynych" -Recurse -File | Unblock-File
```

### Or build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for Native AOT, Visual
Studio or its Build Tools with the "Desktop development with C++" workload. Native AOT finds
the C++ linker through `vswhere.exe`, which is not on `PATH` by default. In a clone of the
repository, with the service stopped ([Update](#update) says how):

```powershell
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
dotnet publish src/Gorynych.Service -c Release -r win-x64 -p:PublishAot=true -o "$env:LOCALAPPDATA\Programs\Gorynych"
dotnet publish src/Gorynych.Outside -c Release -r win-x64 -p:PublishAot=true -o "$env:LOCALAPPDATA\Programs\Gorynych\outside"
```

## Set up Zed

Two choices:

- **The Zed fork.** Its [nightly](https://github.com/Phoenix-/zed/releases/tag/nightly)
  (`zed-gorynych-windows-x64.zip`) picks up sessions that agents start and shows child sessions
  under their parent. Unzip it and run `zed-gorynych\zed.exe`. It is Zed's dev channel: it does
  not update itself, it keeps its own database beside an installed Zed, and it shares that
  Zed's settings, `%APPDATA%\Zed\settings.json`.
- **Stock [Zed](https://zed.dev).** It works through the same bridge, but without the automatic
  pickup and without the tree: sessions made by agents show up only through "Import threads".

In Zed run `zed: open settings file` from the command palette and add the agents:

```json
{
  "agent_servers": {
    "Gorynych: Claude": {
      "type": "custom",
      "command": "~\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "claude"],
      "auto_import_sessions": true
    },
    "Gorynych: Kimi": {
      "type": "custom",
      "command": "~\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "kimi"],
      "auto_import_sessions": true
    },
    "Gorynych: Codex": {
      "type": "custom",
      "command": "~\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "codex"],
      "auto_import_sessions": true
    },
    "Gorynych: DeepSeek": {
      "type": "custom",
      "command": "~\\AppData\\Local\\Programs\\Gorynych\\Gorynych.Service.exe",
      "args": ["acp", "--agent", "dsh"],
      "auto_import_sessions": true
    }
  }
}
```

- Leave out the agents you do not use.
- Zed turns a leading `~` in `command` into the home folder of the machine where the agent
  runs; it expands nothing else, neither `%LOCALAPPDATA%` nor anything in `args` or `env`. So
  the same entries work on another machine in a remote project
  ([external-clients.md](external-clients.md#in-a-remote-project)).
- `auto_import_sessions` exists only in the fork. Stock Zed does not know it and ignores it.
- `default_config_options` is a stock Zed setting. It sets an agent's selectors for new
  threads, for example `"default_config_options": { "mode": "auto" }` for Codex.
- Arguments after `--agent` such as `--unload-after <minutes>`, `--claude-transport headless|tui`
  or `--claude-channels ask|accept|decline` configure the service. They apply only when the bridge
  starts it ([external-clients.md](external-clients.md)).

`Gorynych.Service.exe acp --agent …` is a small bridge. It starts the service if it is not
running, then passes ACP between Zed and the service. The service keeps running after Zed
quits and serves every client.

## Your first session

1. Open the folder you want to work in (File > Open Folder…).
2. In the Agent Panel's new-thread menu, pick "Gorynych: Claude", "Gorynych: Kimi",
   "Gorynych: Codex" or "Gorynych: DeepSeek".
3. Write the first prompt.

Approvals, questions and the agent's answers come into the thread as with any ACP agent.

**Claude** runs headless by default: the unmodified `claude -p` under your own plan login, which
Anthropic's terms allow today ([adapters.md](adapters.md#claude-code)). Its thread has three
selectors: **mode** (`Ask`, `Accept edits`, `Plan`, `Auto` where the model has it, and `Full
access`), **model** and **reasoning effort**. A change applies at once and stays with the session.
**Claude's `Full access` asks you nothing:** any command, any file, outside the folder too; you
choose it per thread, between turns. The first time Claude works in a folder you have not
trusted in Claude Code, Gorynych asks in the thread whether to trust it.

Should headless Claude stop working on your plan, start the service with
`--claude-transport tui`: Gorynych then drives the interactive Claude Code instead, and your
sessions carry over.

**Only with `--claude-transport tui`: Claude asks once about development channels.** In the TUI,
other agents' questions reach a running Claude session through Claude Code's development
channels, a research preview. With them on, Claude Code shows a notice at every launch that
needs Enter, so before the first such launch Gorynych asks in the thread:

- **Allow:** Gorynych starts Claude with the channel and confirms the notice on each launch.
- **Don't allow:** Claude runs without the channel. Other agents' questions and child sessions'
  reports reach it as ordinary turns.

The answer is kept in `%LOCALAPPDATA%\Gorynych\claude\channels.json`. To be asked again, delete
that file and restart the service (`Gorynych.Service.exe shutdown`, then reopen Zed): a running
service keeps the answer it read. If you dismiss the question, that launch runs without the channel and Gorynych asks
again later ([adapters.md](adapters.md#consent-to-development-channels)). The headless transport
needs no channel and never asks this.

**Codex** has three selectors in the thread:

- **mode:** `read-only`, `auto`, `auto-review` ("Approve for me", Codex's automatic reviewer) or
  `full-access`.
- **model.**
- **reasoning effort.**

A new thread starts with your own Codex defaults, and a change applies from the next prompt
([adapters.md](adapters.md#modes-models-and-effort)). **Kimi** has its own model, thinking and
mode selectors.

**DeepSeek Harness** has three selectors: **mode**, **model** and **reasoning effort**. The mode
is `read-only` for every new thread, or `full-access`, which you choose per thread.

In `read-only` its shell is read-only, so DSH's Windows sandbox never changes a folder's
permissions. `read-only` protects your files from changes; it does not stop the agent from
reading secrets or sending them over the network.

- Commands that only read run without asking: searches, `git status`, `git diff`, `git log`.
- Commands that write cannot run, so builds, tests and package installs are not available in a
  DeepSeek session. Gorynych refuses them without asking you, and the agent is told so up front.
- Every file the agent writes or edits with its file tools asks you first. The card names the
  file, shows the change, and says why the agent wants to write it.

**`full-access` asks you nothing.** The agent runs any command and writes any file, anywhere
on your disk, without a single question, as Codex's `full-access` does: builds, tests and
installs work. It changes no folder's permissions. Switch the mode between turns; a thread
made in `dsh web`, or in Gorynych before full access existed, keeps the permissions DSH gave it
and shows them instead.

- Its sessions are DSH's own and live in `~/.dsh`, so `dsh web` shows them too. A session can be
  open in only one of them at a time: Gorynych lets go of an idle one after two minutes. DSH
  cannot delete a session through Gorynych; deleting one in Gorynych hides it, and `dsh web`
  can delete it for good. Even a thread you open and never prompt leaves an empty session there.

More in [adapters.md](adapters.md#deepseek-harness).

## Sessions that talk to each other

Every session gets a `gorynych` MCP server with these tools:

- `list_agents`: see the other sessions.
- `ask_agent`: ask one for its opinion, without asking you. The one exception is a session
  that is not in full access asking one that is (Claude's, Codex's or DeepSeek's
  `Full access`): that session could act on the question without asking you, so Gorynych asks
  you first, in the asking session's thread.
- `archive_session` and `restore_session`: hide a session from the lists or bring it back,
  without asking you; nothing in it is lost.
- `spawn_session`: start a child session in another agent. When you ask for it yourself, in
  your own message, it starts right away; any other spawn (one the agent proposes, or one in a
  turn that another session's question or report came into) you approve first. The child starts in its agent's default
  mode, never in full access, and by default the child reports each finished turn to its
  parent.

A child is an ordinary session you can open and prompt yourself. How it works:
[design.md](design.md#sessions-that-talk-to-each-other).

## Optional: agents outside Gorynych

Claude Code in a terminal or in Claude Desktop, Codex, and Kimi Code can list Gorynych's
sessions, read them and ask one for its opinion through the `gorynych-outside` MCP server.
Registering it changes that agent's own configuration, so Gorynych never does it for you. Run
the command yourself, and keep the name `gorynych-outside`:

- **Claude Code** (Claude Desktop's Code tab reads the same configuration):
  ```powershell
  claude mcp add --scope user gorynych-outside -- "$env:LOCALAPPDATA\Programs\Gorynych\outside\Gorynych.Outside.exe"
  ```
  To undo: `claude mcp remove --scope user gorynych-outside`.
- **Codex:**
  ```powershell
  codex mcp add gorynych-outside -- "$env:LOCALAPPDATA\Programs\Gorynych\outside\Gorynych.Outside.exe"
  ```
  To undo: `codex mcp remove gorynych-outside`.
- **Kimi Code:** add this entry to the `mcpServers` object of `~/.kimi-code/mcp.json`; to undo,
  remove it. Not tried yet:
  ```json
  { "mcpServers": { "gorynych-outside": { "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\Gorynych\\outside\\Gorynych.Outside.exe" } } }
  ```

The server starts the service from its parent folder when the service is not running. Details,
timeouts and how to try it without registering:
[external-clients.md](external-clients.md#agents-outside-gorynych).

## Optional: another machine

With Gorynych set up on a second machine that this one reaches with `ssh` and a key, open a
folder there as a Zed remote project (*Open Remote Project*). Zed runs that machine's agents
there, in that folder, through the same entries: `~` in them is that machine's home folder. The
fork's nightly brings the server Zed needs on that machine with it.

Setting up the other machine (its logon task, its Zed entries, the ssh key) and the other way,
a Zed entry that runs the bridge there over ssh, are in
[external-clients.md](external-clients.md#a-remote-machine).

## Update

Zed can stay open: its threads go on with the new service
([service-protocol.md](service-protocol.md#the-resumable-link)). On Windows the agents run in
*adapter hosts* of their own, which a restart leaves running, so a turn that runs goes on and its
answer arrives whole ([service-protocol.md](service-protocol.md#adapter-hosts)).

1. Windows will not overwrite an exe that runs (the service, Zed's bridges, an outside server),
   but it allows a rename. The stamp keeps the name free while a copy from an earlier update
   still runs; delete the `*.old.exe` files once nothing runs them.
   ```powershell
   $dir = "$env:LOCALAPPDATA\Programs\Gorynych"; $stamp = Get-Date -Format yyyyMMddHHmmss
   Rename-Item "$dir\Gorynych.Service.exe" "Gorynych.Service.$stamp.old.exe"
   Rename-Item "$dir\outside\Gorynych.Outside.exe" "Gorynych.Outside.$stamp.old.exe"
   ```
2. Install the new build as above. `Expand-Archive -Force` writes the new files in place.
3. Restart the service. It exits within seconds, and Zed's bridges, which held the prompts sent
   meanwhile, start the new service and carry on. The running turns go on in their hosts, and the
   new service takes them back:
   ```powershell
   & "$dir\Gorynych.Service.exe" restart
   ```
   The bridges themselves keep running the old copy until Zed restarts them, and an agent outside
   Gorynych keeps its outside server until it restarts its MCP servers. The hosts of the old copy
   go on with their sessions: each session moves to a host of the new build as soon as it is idle
   (at once, or after its running turn), and an old host exits when it has none left. New sessions
   open in hosts of the new build at once. To see what still waits:
   ```powershell
   & "$dir\Gorynych.Service.exe" status
   ```
   It lists each host with its build, sessions and running turns, and under a host of an earlier
   build what keeps each of its sessions there: a turn that runs, a question of the agent's that
   waits for an answer, an empty Codex draft (Zed makes one whenever its agent panel opens: it
   moves after its first prompt, or goes when the panel closes), or a close that failed (it moves
   at a later idle moment). A restart with other adapter options (`--claude-model`, …) moves the
   sessions the same way.
   An agent that was waiting for another session's answer (`ask_agent`) during the restart gets it
   all the same, from the next service; only a question that had not reached the other session
   yet is lost, and the agent is told to ask again.
4. **Without hosts** (`--hosts off`, the default on macOS) a restart still stops the
   agents, so it starts no new turn and waits up to five minutes for the running ones
   (`--timeout <minutes>` changes that). **If a turn still runs after the wait**, the restart is
   called off: the command lists the sessions still at work and exits with 4, and the service goes
   on as before, the held prompts included. Run `restart` again once they end, give it a longer
   `--timeout`, or add `--force`, which cancels them and restarts all the same. `--no-wait`
   restarts only if nothing runs at that moment; `--no-wait --force` restarts at once.

**The first update onto hosts** still stops the agents: the service it replaces runs them in its
own process. So that restart waits for the running turns, as in step 4; the restarts after it
leave them running.

**The first update onto this scheme**, from a build without `restart`: the command asks the old
service to exit instead, which cancels its running turns, and the bridges Zed runs are old too,
so they end with it. Quit Zed before it and start it again after.

**The old copies.** A renamed `Gorynych.Service.<stamp>.old.exe` can be deleted once no process
runs it: no old bridge (Zed restarted them), and no old host (`status` says nothing waits to move,
as every session moved off them and they exited). What `status` lists under an old host keeps its
copy: a long turn until it ends, an empty Codex draft until it is prompted or its Zed panel closes.
Gorynych never deletes them itself. To see which ones still run:
```powershell
Get-Process Gorynych.Service, Gorynych.Outside | Select-Object Id, Path
```
The adapter hosts are `Gorynych.Service` processes too (`host --adapter …` on their command line).
A file that is still in use belongs to one of them.

## Where things are

- **Programs:** `%LOCALAPPDATA%\Programs\Gorynych`, or wherever you put them.
- **Data:** `%LOCALAPPDATA%\Gorynych`. Only you (and SYSTEM) can read it: the service closes it at
  each start, also to the commands Codex's sandbox runs, which can read the rest of your profile.
  - `sessions.json`: the service's list of sessions, with titles, parents and archive flags.
  - `journal\`: what each session said, as the service relayed it.
  - `claude\`: the Claude adapter's session list (with each session's selector choices),
    `trusted.json` (folders you trusted in Gorynych), `channels.json` (TUI only), and per session
    the launch files and `stderr.log` (headless) or `terminal.log` (TUI).
  - `dsh\`: `gorynych.patch.yml` and `gorynych.full-access.patch.yml`, the overlays Gorynych gives
    its two DSH processes, rewritten at every start; `modes.json`, the threads in full access.
  - `host.token`: what proves an adapter host to the service; the service writes it.
  - `logs\`: `service-<date>.log`, `host-<adapter>-<date>.log` for each adapter host, and
    `outside-<date>.log`.
- **The agents' own state** stays in their folders (`~/.claude`, `~/.codex`, `~/.kimi-code`,
  `~/.dsh`).
- **Elevation.** A service started by Zed in a plain session serves the user's plain and
  elevated clients alike, an SSH login included. A service started from an elevated
  (administrator) terminal serves only elevated clients, so a plain process can never drive an
  elevated service, which runs agents. A bridge started over SSH never starts a service
  itself: the logon task does ([external-clients.md](external-clients.md#a-remote-machine),
  [service-protocol.md](service-protocol.md#endpoint)).

## Uninstall

1. Undo any outside registrations (`claude mcp remove …`, `codex mcp remove …`, the Kimi entry).
2. Quit Zed and run `Gorynych.Service.exe shutdown`, and `Gorynych.Service.exe task uninstall`
   if you registered the logon task.
3. Remove the `Gorynych: …` entries from Zed's `agent_servers`.
4. Delete `%LOCALAPPDATA%\Programs\Gorynych`, and `%LOCALAPPDATA%\Gorynych` if you do not want
   its journals and logs. The agents' own transcripts stay where they are.

## Troubleshooting

Logs: Zed's `dev: open acp logs` shows the messages between Zed and the bridge. The service
writes `%LOCALAPPDATA%\Gorynych\logs\service-<date>.log`.

- **"claude was not found on PATH"**, likewise for `kimi`, `codex`, `dsh` and `node`. The service looks agents up
  on the `PATH` it inherited from whoever started it, usually Zed through the bridge. Install the
  agent, then restart the service so it picks up the new `PATH`: quit Zed, run
  `Gorynych.Service.exe shutdown`, and start Zed again from a fresh shell or the Start menu.
- **"gorynych acp: cannot reach the service on pipe …"**, with a note about elevation. An
  elevated service runs, and Zed is not elevated. Stop it from an elevated terminal with
  `Gorynych.Service.exe shutdown`, and let Zed start a plain one. When the message says instead
  that the pipe "is not this user's service", another user's process holds the pipe's name.
- **"gorynych acp: no service runs on pipe …, and this process runs outside the user's Windows
  session"**. A bridge over SSH found no service and no logon task. Run
  `Gorynych.Service.exe task install` on that machine once
  ([external-clients.md](external-clients.md#a-remote-machine)).
- **"Claude Code started without your login …"**. Run `claude` in a terminal and sign in. If you
  are signed in, `claude -p` may have started to run bare by default, which ignores the login:
  start the service with `--claude-transport tui`.
- **"Claude Code would run on …, billed outside your plan"** or **"Claude Code reports no plan"**.
  Claude Code found a key or token other than your plan login, such as an `apiKeyHelper` in its
  settings (Gorynych already leaves `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` out). Remove
  it, or sign in with your plan, then prompt again.
- **"Claude cannot run on your plan (…)"**. Claude Code reported an auth or billing error. If
  Anthropic no longer lets `claude -p` run on your plan, start the service with
  `--claude-transport tui`.
- **"Claude did not start the turn within 90 s. Last screen: …"** (TUI transport). Claude Code waited on a screen
  Gorynych cannot answer, such as a login. The error quotes the screen, and the session's
  `terminal.log` under `%LOCALAPPDATA%\Gorynych\claude\sessions\<session>\` has all of it. Run
  `claude` once in a terminal in that folder, deal with what it asks, then prompt again.
- **"llm-deepseek: no API key for provider route …"** in a DeepSeek thread. DSH has no DeepSeek
  API key. Set yours in DSH, for example on the Models page of `dsh web`, then prompt again.
- **"This DeepSeek Harness session is open in another DSH app …"**. `dsh web` or another `dsh`
  holds that session. Close it there, then open it again in Gorynych.
- **The update cannot overwrite a file.** A client or an agent outside Gorynych still runs it.
  See [Update](#update).
