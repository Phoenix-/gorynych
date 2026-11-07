# Gorynych

One body, many heads. Gorynych is a desktop cockpit and a per-machine service that run
several AI coding agents (Claude Code, Codex, Kimi Code, DeepSeek Harness) in their
native harnesses and bring their conversations, approvals and session lists into one
place. The agents can also ask each other for opinions.

Status: first MVP slice. The service runs Claude Code and Kimi Code sessions, and Kimi can
ask a running Claude session for an opinion; a console test client stands in for the GUI.

## Try it

```
dotnet build Gorynych.slnx
dotnet test --project tests/Gorynych.Tests             # contract tests, no live agent
GORYNYCH_LIVE=1 dotnet test --project tests/Gorynych.Tests   # also runs Claude (haiku) and Kimi
```

`tools/Gorynych.TestClient` (`gorynych-test`) starts the service if it is not running:

```
gorynych-test new --agent claude --cwd <folder> --wait-title 20 "first prompt"
gorynych-test list
gorynych-test prompt <session> "next prompt"
gorynych-test load <session> [--follow]      # replays the history from the service journal
gorynych-test opinion --to <claude session> "a question from another agent"
gorynych-test status | shutdown
```

Approvals are asked on the console; `--approve allow|deny` answers them automatically.

Specifications: [service protocol](docs/service-protocol.md), [adapters](docs/adapters.md),
[ACP types](docs/acp-types.md). Probe programs and what they proved:
[research/probes](research/probes/README.md).

## Shape

- **Service.** A per-user background process on each machine. It owns the agent
  processes, keeps a bounded journal for every session and speaks
  [ACP](https://agentclientprotocol.com) v1 to the GUI.
- **GUI.** Windows, C# and WinUI. It connects to one or more services and shows a
  combined session list, the feed, the composer and approval cards.
- **Adapters.** Each agent plugs in as an ACP agent:
  - Kimi through `kimi acp`;
  - Codex through `codex app-server`;
  - DeepSeek through its host API;
  - Claude as its interactive TUI in a pseudo console, driven only by per-launch flags.

Stage one is a single local machine. The service/GUI split is there from day one.

## License

[MIT](LICENSE)
