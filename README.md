# Gorynych

One body, many heads. Gorynych is a desktop cockpit and a per-machine service that run
several AI coding agents (Claude Code, Codex, Kimi Code, DeepSeek Harness) in their
native harnesses and bring their conversations, approvals and session lists into one
place. The agents can also ask each other for opinions.

Status: design done, MVP not started yet.

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
