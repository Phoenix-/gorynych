# Probes

Throwaway programs that checked, on the author's machine, how each harness can be driven
without editing its configuration. They ran on 6 November 2026 on Windows 11 with
Claude Code 2.1.302 (Haiku 5.5), Codex 0.167 and Kimi 2.4.1. After every Claude probe
the hashes of `~/.claude/settings.json`, `CLAUDE.md` and `~/.claude/.claude.json` were
unchanged. The C# files are .NET 10 file-based apps (`dotnet run file.cs`). Paths inside
`channel/mcp.json` and `pty/run.ps1` point to the probe's working folder, written here as
`C:\work\probe` (as in the recorded fixtures under `tests/Gorynych.Tests/Fixtures`); they are
kept as a record, not as something to run as is.

The production code that grew from them lives in `src/Gorynych.Adapters.Claude`.

| File | What it proved |
| --- | --- |
| `listener.cs` + `hooks.json` | `claude --settings hooks.json` with hooks of `type: http` posts every event to a local listener and adds to the user's global hooks instead of replacing them. Received: `UserPromptSubmit` (`prompt`), `PreToolUse`/`PostToolUse` (`tool_input`, `tool_response`), `PermissionRequest`, `MessageDisplay` (`delta`, `final`), `Stop` (`last_assistant_message`), `SessionEnd`. `SessionStart` did not arrive over http. Replying `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}` approves a tool; for `AskUserQuestion` the decision also carries `updatedInput` = `tool_input` plus `answers` as `{question text: option label}`. `--permission-mode default` is required because the user's global `defaultMode` is `auto`. |
| `channel/channel.cs` + `channel/mcp.json` | A stdio MCP server that declares `capabilities.experimental['claude/channel']` and pushes `notifications/claude/channel` with `{content, meta}` reaches a live session when Claude is started with `--mcp-config mcp.json --dangerously-load-development-channels server:<name>`. Meta keys must be identifiers. Without authorization Claude treats channel text as untrusted; with an `--append-system-prompt` that calls channel messages opinion requests to answer through the `reply` tool and forbids actions for them, it answered and refused actions. A push while Claude is busy is delivered right after the current tool result. `reply` is a deferred tool (loaded through ToolSearch) and asks for permission. |
| `pty/pty.cs` | A minimal ConPTY host in C#: runs `claude` in a pseudo console, logs raw output, writes keystrokes. Every launch with development channels shows a full-screen warning that needs Enter (`\r`). Claude enables bracketed paste (`ESC[?2004h`); after `Stop`, `ESC[200~text ESC[201~` followed by `\r` about 400 ms later arrives as a real user turn. Typing `/rename <name>` and Enter writes `custom-title` and `agent-name` transcript entries. |
| `pty/drive.ps1`, `pty/run.ps1` | PowerShell helpers that drove the ConPTY host and the channel together and printed a timeline for the channel-authorization probe. |
| `titles.py` | Prints the non-message entries of a Claude transcript. Showed that the generated title arrives as `{"type":"ai-title","aiTitle":...}` a few seconds after the first answer, and `/rename` as `custom-title`. |
| `outside/probe_server.py` + `claude_probe.py`, `claude_modern_probe.py`, `kimi_probe.py` | Probes of 8 November 2026 (Claude Code 2.1.302, Kimi Code 2.5.2, Python 3) for the `gorynych-outside` server ([service-protocol.md](../../docs/service-protocol.md#agents-outside-gorynych)). A throwaway stdio MCP server logs its environment, parent chain, folder and every request; Claude got it through `--mcp-config`, Kimi through `mcpServers` in ACP `session/new`, and the Kimi session was deleted afterwards. Both agents start their MCP servers as their own direct children and pass them their whole environment; the server's folder is the project folder (Claude) or the session's `cwd` (Kimi). Claude 2.1.302 probes with `server/discover` (MCP 2026-07-28), stays stateless with a server that answers it, and falls back to `initialize` with `2025-11-25` on an error; it sends a `progressToken` with every `tools/call` and loads the user's own MCP servers next to the given ones. `permissions.deny: ["mcp__<server>"]` in `--settings` takes the server's tools out of the model's list, although Claude still starts it. Kimi starts with `initialize` and offers no roots. The logs the scripts write hold the host's environment, tokens included, so they are not kept here. |
| `codex/codexprobe.cs` | Drives `codex app-server` over stdio. No `jsonrpc` field on the wire; `initialized` must follow `initialize`. `thread/start`, `turn/start`, `thread/name/set` (+ `thread/name/updated`), `thread/archive` (+ `thread/archived`) and `thread/list` (`archived: true` for archived threads) all work and land in the shared `~/.codex/state_5.sqlite`, which the desktop app reads. The exact schema comes from `codex app-server generate-json-schema --out <dir>`. |

Launching Claude from a process that itself runs inside a Claude Code session needs
`CLAUDECODE` and the desktop-session variables (`ANTHROPIC_BASE_URL`, `CLAUDE_CODE_*`)
stripped from the environment.
