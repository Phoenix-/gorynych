# Agent instructions

Gorynych is a cockpit for several native AI coding agents: a per-machine **service**
plus a **GUI** that talk ACP. The design (decisions, probe results, protocol mapping,
stage one scope) lives in the private design doc
https://claude.ai/artifact/XuNPxC4mX7eLNrJFFS1yFZ; read it before changing the
architecture. Keep this file short: it is loaded into every agent session.

## Ground rules

- **Standards first.** Before inventing a format or protocol, name the closest standard
  and conform to it or extend it through its documented extension points. Check the
  standard's current version on the web, not from memory.
  - GUI <-> service is ACP v1. Own fields go only in `_meta`, own methods are
    `_`-prefixed, as ACP's extensibility rules say.
  - The service is an ACP conductor (see the proxy-chains RFD).
  - Adapters are ACP agents.
- **Never edit an agent's own configuration** (`~/.claude`, `~/.codex`, `~/.kimi-code`).
  - Claude is integrated only through per-launch flags (`--settings` with http hooks,
    `--mcp-config`, channels, `--append-system-prompt`) and its PTY.
  - Codex is integrated through `codex app-server`, Kimi through `kimi acp`.
- **Subscriptions.** Use only the user's own login on each machine, run the unmodified
  agent binaries, never copy credentials between machines and never serve other people.
- **Native state is the source of truth.** Where a harness has a native attribute
  (title, archive), read and write it there; keep only the rest in the service's own
  overlay.
- **Scale.** Expect a handful of interactive sessions per machine, not thousands.
  Still page lists and bound journals.

## Code

- C# on .NET 10. A build-time assembly per adapter; no runtime assembly loading, so the
  service stays Native AOT compatible.
- Windows first; the service must also build for Linux and macOS.
- Repository text is English. Commits use the author name Mikhail Kazakov and the GitHub
  noreply address configured in this clone.
