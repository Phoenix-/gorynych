# Agent instructions

Gorynych is a cockpit for several native AI coding agents: a per-machine **service**
plus a **GUI** that talk ACP. The design (decisions, probe results, protocol mapping,
stage one scope) lives in [docs/design.md](docs/design.md); read it before changing the
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
  - Claude is integrated only through per-launch flags (`--settings`, `--mcp-config`,
    `--append-system-prompt`, …) and its own protocols: headless `claude -p` with stream-json by
    default; the TUI in a PTY, with http hooks and channels, as the fallback
    (`--claude-transport tui`).
  - Codex is integrated through `codex app-server`, Kimi through `kimi acp`.
- **Subscriptions.** Use only the user's own login on each machine, run the unmodified
  agent binaries, never copy credentials between machines and never serve other people.
- **Native state is the source of truth.** Where a harness has a native attribute
  (title, archive), read and write it there; keep only the rest in the service's own
  overlay.
- **Scale.** Expect a handful of interactive sessions per machine, not thousands.
  Still page lists and bound journals.

## Code

- Specifications live in `docs/` (design, service protocol, the adapter contract, adapters,
  ACP types); keep them in step with the code. A new adapter is checked against
  `docs/adapter-contract.md`. ACP types are generated: change `tools/Gorynych.AcpGen`, not `Acp.g.cs`;
  Codex's app-server types likewise: `tools/Gorynych.CodexGen`, not `Codex.g.cs`.

- C# on .NET 10. A build-time assembly per adapter; no runtime assembly loading, so the
  service stays Native AOT compatible.
- Windows first; the service must also build for Linux and macOS.
- Repository text is English. Commits use the author name Mikhail Kazakov and the GitHub
  noreply address configured in this clone.
