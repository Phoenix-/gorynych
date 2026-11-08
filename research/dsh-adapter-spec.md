# DeepSeek Harness adapter: specification

**Draft, first part.** Nothing is built. This part rests on reading DSH's packages and on probes
that ran without a key, so no model call and nothing in `~/.dsh`. The live probes (contract
steps 8–15 and the sandbox) are written but not run yet: see
[Live probes still to run](#live-probes-still-to-run). Sections that depend on them say so.

Written 9 November 2026 from:

- **DeepSeek Harness (DSH) 0.2.2-rc.1**, the latest on npm (4 November), installed in a scratch npm
  prefix. Also **0.2.1-rc.2**, the global install. Both are the npm package `@deepseek-ai/dsh`,
  MIT, a developer preview that warns it will break compatibility.
- **Its package READMEs and built code.** These are `dsh-acp` (the ACP server), `dsh-acp-app`
  (the `acp` profile bundle), `dsh-base`, `dsh-app-boot` and the launcher (`lib/bin.js`,
  `lib/profile-boot-*.js`), plus the sandbox, approval, MCP client, session persistence and
  system-prompt packages.
- **Keyless probes** of `node lib/bin.js --profile acp` with `DSH_HOME` in a scratch folder, in
  [probes/dsh](probes/dsh).

Decided already by the user (9 November 2026): DSH runs on the user's real `~/.dsh`, like the
other harnesses (a system-wide install, our settings only per launch). DSH may create its own
shipped `profiles/acp` template on first boot. Gorynych never writes into `~/.dsh` itself.

## Summary

- **DSH speaks ACP v1 natively** (`dsh --profile acp`), so this is a Kimi-like external agent,
  not a translator like Codex.
- **But not a pure passthrough.** Five things need a thin in-process ACP proxy between the
  service and DSH's stdio (D1):
  - `session/list` leaves out the sessions open in the same process, so the proxy merges its
    live sessions back in;
  - permission requests carry only a `toolCallId`, so the proxy fills in the tool call's title
    and input;
  - every tool call is `kind: other` with no locations, so the proxy maps kinds and locations
    (and diffs where the input allows);
  - the one-writer error is an opaque `-32603`, so the proxy turns it into a sentence a person
    can act on;
  - `initialize` gets our `_meta` flag `gorynych/unloadAfter`.
- **What DSH lacks, and the service covers:**
  - no `session/load`: history comes from our journal;
  - no delete: a tombstone;
  - no archive: the overlay;
  - no titles over ACP: the provisional title;
  - no forms, no live-note path.
- **What is configured per launch:** one `--patch` file in Gorynych's data dir (the appended
  system prompt, the sandbox), the launch folder and two environment variables. Nothing goes
  into `~/.dsh`.
- **Three properties of DSH need the user's decisions** (see
  [For the user to decide](#for-the-user-to-decide)):
  - **The Windows sandbox writes standing ACEs and a Low integrity label onto every session's
    folder tree.** That is the user's repositories.
  - **The only approvals DSH asks are sandbox escalations.** MCP tools, the gorynych tools
    included, run without asking.
  - **Every turn spends money on the user's API key**, unlike the subscription harnesses.

## What the probes showed

Keyless, DSH 0.2.2-rc.1, Windows 11, node 26.12.1, `DSH_HOME` in a scratch folder,
`DSH_TELEMETRY_DISABLED=1`. The script is [probes/dsh/acp_probe.py](probes/dsh/acp_probe.py),
checks `boot`, `sessions`, `other`, `kill` and `cost`.

1. **The first boot materializes the profile.** `loadProfile()` in `dsh-app-boot` creates
   `$DSH_HOME/profiles/acp/` from the shipped template when its `package.json` is missing.
   - It holds `package.json` (bundles `dsh-base`, `dsh-acp-app`), an empty `cordis.patch.yml`
     and `pnpm-workspace.yaml`.
   - The launcher rewrites `cordis.yml` (`[]`) on every boot.
   - `--help` and `--dump-config` do the same. Both versions behave alike.
2. **Boot.**
   - `initialize` was answered 3.6–3.8 s after the spawn. Stdin is read from the start, so the
     request just waits.
   - Stdout carried only protocol lines; stderr stayed empty.
   - Closing stdin ended the process in 0.07 s with code 0. DSH bounds its own shutdown at 5 s.
3. **initialize** (as sent):
   ```json
   {"protocolVersion": 1, "agentInfo": {"name": "deepseek-harness-acp", "version": "0.0.1"},
    "agentCapabilities": {"mcpCapabilities": {"http": true},
      "promptCapabilities": {"image": false, "audio": false, "embeddedContext": false},
      "sessionCapabilities": {"close": {}, "list": {}, "resume": {}}},
    "authMethods": []}
   ```
   - No `loadSession`, `delete` or `systemPrompt`.
   - `agentInfo.version` is a constant, not DSH's version.
   - Images depend on the route; they were off without a key.
4. **New.** `session/new {cwd, mcpServers}` took 0.20 s.
   - **The session is durable at once:**
     `sessions/<cwd slug>/<id>/session.v4.jsonl.zstd` and
     `storages/session_projcache/sessions/<id>.json` exist before any prompt.
   - IDs are bare UUIDs.
   - The result carries `configOptions`:
     - `model` (category `model`, values are JSON tuple strings such as
       `["deepseek-official","deepseek-v4-flash"]`; only flash was offered without a key);
     - `reasoning_effort` (category `thought_level`: `off`, `low` … `max`, default `high`).
5. **MCP.** The stdio server given as `gorynych` was started **twice** per new or resume, as a
   direct child of `node.exe` (DSH), and was gone after `session/close`. When node was killed,
   it died too.
6. **List.** `session/list` returns only `{sessionId, cwd}`: no `title`, no `updatedAt`.
   - Sessions come newest-created first, with a keyset cursor and an optional `cwd` filter by
     realpath.
   - `cwd` comes back as it was sent.
   - **A session open in the same DSH process is left out**: the list was empty while the
     probe session was open, and showed it after `session/close`.
   - Subagent sessions are always left out.
7. **Config.**
   - `session/set_config_option` returns the full list.
   - An unknown option or value fails with `-32602` (`unknown model option: …`,
     `unknown session config option: mode`).
   - The effort set on a session survived close and resume.
8. **Close** took 0.01–0.02 s.
9. **Resume** took 0.19–0.23 s with the MCP server and 0.01 s without.
   - It takes the request's `mcpServers` (a missing list is fine) and checks that `cwd` is the
     session's (realpath).
   - Errors:
     - a second resume in the same process: `-32602 session is already active`;
     - another `cwd`: `-32602 session cwd does not match`;
     - an unknown ID: `-32602 session is not resumable`.
   - The model and effort recorded with the session come back with it.
10. **One writer.** A second DSH process listed a session the first one had open, but could not
    resume it: `-32603 Internal error`, `data.details` = `session "<id>" is already owned by an
    active write handle`. DSH's persistence takes a kernel lock per session (a named semaphore
    on Windows) at the first write, so `dsh web` and our process exclude each other the same way.
11. **Not there.** `session/load`, `session/delete` and `_gorynych/note` answer `-32601`.
    `session/cancel` for an unknown session is ignored.
12. **No key.** A prompt fails at once with `-32603 Internal error: turn failed: llm-deepseek:
    no API key for provider route "deepseek-official"; store DEEPSEEK_API_KEY through the
    credentials service (the web Models page writes it), or export DEEPSEEK_API_KEY in the
    launching environment`. The user message is still recorded.
13. **Per-session pins.** At the first prompt, failed or not, DSH records in the session:
    - the sandbox mode (`workspace-write`) and approval policy (`ask`);
    - the deterministic fallback title (the prompt's first words, `What is the probe word?`).
    These live in DSH's projection cache, not on the ACP wire.
14. **Overlays compose as documented** (`--dump-config`):
    - a `system-prompt` row with `personaPrefix` restated and our `personaSuffix`;
    - `session-title-llm` switched back on;
    - `sandbox-policy` and `approval` rows.

    A patch replaces a row's whole `config`, so every row we patch restates what it keeps.

## What the code says

From 0.2.2-rc.1's code and READMEs. These are confirmed only by the live probes.

- **Updates.**
  - Committed assistant messages arrive whole, not streamed: one `agent_message_chunk` per
    text block, and `agent_thought_chunk` for reasoning, both with DSH's `messageId`.
  - `tool_call` has `title` = the tool's name (`mcp__<server>__<tool>` for MCP tools),
    `kind: other`, `status: in_progress` and `rawInput` = the parsed arguments, with no
    `locations` or content.
  - `tool_call_update` is `completed` or `failed`, with the tool result as text content and no
    `rawOutput`.
  - `usage_update {used, size}` follows each assistant message; `config_option_update` follows
    a topology change.
  - Nothing else: no plans, no `session_info_update`, no `user_message_chunk`.
- **Stop reasons.** `completed`, `aborted`, `blocked` and `error` map to `end_turn`;
  `max-tokens` to `max_tokens`; `interrupted` to `cancelled`. A failed turn rejects the prompt
  (finding 12).
- **Permission requests:**
  `{sessionId, toolCall: {toolCallId}, options: [allow-once "Allow once" allow_once, reject-once "Reject" reject_once]}`.
  - Before asking, DSH drains its updates, so the `tool_call` comes first.
  - `cancelled` counts as cancelled; any option other than `allow-once` counts as a rejection.
- **What asks at all.** Only the shell and file tools' **sandbox escalation** asks. A command or
  write that the sandbox denied is retried by the model once with `sandbox_permissions` and a
  `justification`, which raises the approval.
  - Nothing asks inside the sandbox.
  - MCP tools never ask (`dsh-mcp-client` has no approval path).
  - Approval policy `never` rejects every escalation.
  - There is no "ask before every command" setting; only a DSH plugin hooking
    `tools/pre-execute` could do that.
- **Descendants.** A prompt stays in flight while DSH subagents run, while their results start
  later root turns, and while output drains.
  - `session/cancel` stops the root activity present then.
  - Later child messages may start autonomous root work that belongs to no prompt.
  - `session/close` cancels, drains and disposes the descendants.
- **ask_user_question** is mounted only by the `sdk` and `web` apps, not by `acp`. The acp
  profile has no elicitation either. So a DSH session cannot ask the person anything except
  through approvals.
- **System prompt.** The `system-prompt` row's `personaPrefix` is `You are a coding agent
  powered by the {{model}} model.` in the acp app. `personaSuffix` goes after DSH's own
  guidance. Both are templates (`{{name}}` interpolates). HMR is off, so the composition is
  fixed for the process's life.
- **Credentials:**
  - sources: `$DSH_HOME/.credentials.yaml`, then the process environment, the launch folder's
    `.env` and `$DSH_HOME/.env`;
  - the launch folder's `.env` is loaded into DSH's process environment;
  - web search uses the same `DEEPSEEK_API_KEY`.
- **Telemetry.** `session-telemetry-otel` runs in mode `FEEDBACK_ONLY`: it exports only after
  explicit user feedback, which ACP has no way to give. A non-empty `DSH_TELEMETRY_DISABLED`
  disables the row.
- **Windows sandbox.** In `workspace-write` (the default) and `read-only`, PowerShell commands
  run under a write-restricted, Low-integrity token (`dsh-sandbox-windows-acl`). For
  `workspace-write`, the first confined command in a workspace writes onto the workspace root,
  propagated through the whole tree:
  - a capability-SID allow ACE;
  - a `FILE_DELETE_CHILD` deny for Everyone, inherited by subfolders, after which a FullControl
    open of a subfolder fails, also for the user;
  - an inheritable **Low** mandatory label, after which any Low-integrity process of the user
    can write there.

  These edits are standing by design: they are never revoked, and icacls cannot remove them
  (`ERROR_NONE_MAPPED`). The propagation can take tens of seconds on a big tree. `read-only`
  grants nothing, and pwsh then runs in ConstrainedLanguage. File tools enforce the mode
  in-process, with no ACL. `danger-full-access` confines nothing.
- **0.2.1-rc.2 → 0.2.2-rc.1, in the ACP surface:**
  - a model or effort change applies from the next model step, also within a running turn
    (before: from the next turn);
  - resume restores the recorded selection;
  - ordinary forks (`parentSession`) are now listed and resumable.

  The acp-app and base bundle patches are unchanged.

## Decisions

Each has a recommendation; the coordinator and the user decide.

**D1. An in-process ACP proxy over `dsh --profile acp`.**

- Kimi's adapter hands the agent's stdio to the service unchanged. That is not enough here:
  findings 6 and 10 and the permission and tool-call shapes need small rewrites.
- A translator like Codex's is not needed either: DSH's methods and updates are ACP already.
- So the adapter is a `DuplexLink` agent that forwards every frame. It rewrites only:
  - `initialize`'s result: adds `agentCapabilities._meta["gorynych/unloadAfter"]` (D10);
  - `session/new` and `session/resume` results: records the session as live, with its `cwd`;
  - `session/close` results: drops it;
  - `session/list` results: merges the live sessions (D4);
  - `session/update` `tool_call`: records title, input and kind; maps kind, locations and
    content (D6);
  - `session/request_permission`: fills the tool call (D5);
  - error results of `session/resume`: rewrites the one-writer error (D11).
- Anything it does not know passes through, so a DSH update that adds a field reaches the service.

**D2. One `dsh --profile acp` per service, on the user's `~/.dsh`.**

- One process serves every session (DSH multiplexes sessions on one connection), and its MCP
  servers are its children (finding 5), so `gorynych-outside` stays hidden on Windows.
- **Command.** `node <dsh>/lib/bin.js --profile acp --patch <data>/dsh/gorynych.patch.yml`.
  - `<dsh>` is the global package that the `dsh` shim on PATH points to.
  - Running node directly keeps `cmd.exe` out of the tree. Either way, everything stays a
    descendant of the service.
- **Launch folder.** Gorynych's data dir (`<data>/dsh`), which holds no `.env`. DSH loads the
  launch folder's `.env` into its environment, so a repository's `.env` must not be the launch
  folder.
- **Environment.** No `DSH_HOME`. No `DSH_PERMISSION_MODE`: the sandbox goes through the patch
  (D7). `DSH_TELEMETRY_DISABLED=1` (D13).
- **Stop.** Close stdin, wait up to 6 s, then kill the tree.
- **Version.** Read `version` from the `package.json` next to `lib/bin.js`. Log it, and warn
  when it is not a version the adapter was checked against (0.2.2-rc.1). Do not refuse:
  `agentInfo.version` is a constant, and a preview moves fast.

**D3. Per-launch configuration in one patch file.**

- The adapter writes `<data>/dsh/gorynych.patch.yml` before each start:
  ```yaml
  - id: system-prompt
    config:
      personaPrefix: >-
        You are a coding agent powered by the {{model}} model.
      personaSuffix: >-
        <PeersServer.SystemPrompt>
  # plus the sandbox rows of D7
  ```
- The text is the service's peer instructions (`PeersServer.SystemPrompt`), handed to the
  adapter's options when the service builds it, so the adapter needs no reference to the service.
- The adapter checks that the text holds no `{{` (a template would try to resolve it), and
  fails its start if it does.
- **The adapter does not advertise `sessionCapabilities.systemPrompt`.** DSH applies the text
  per process, not per session; every session gets the same text anyway.
- DSH also applies the user's own layers, `~/.dsh/profiles/acp/cordis.patch.yml` and
  `~/.dsh/cordis.patch.yml`, before ours. Our two rows replace whatever those set for the same
  rows (see [Risks](#risks)).

**D4. List: merge the live sessions back in.**

- The proxy keeps `{sessionId → cwd}` for every session it created or resumed and has not
  closed.
- On a `session/list` answer without a `cursor` in the request, it adds the live sessions whose
  `cwd` matches the request's filter, or all of them without one. It compares paths
  case-insensitively after normalizing separators, as DSH compares by realpath. It does not
  duplicate.
- Later pages pass through unchanged.
- No title and no `updatedAt` exist for any session (finding 6):
  - the service shows its provisional title (D9);
  - for sessions it never saw (made in `dsh web`) it has no time and sorts them last.
- Reading DSH's projection cache for titles is possible but fragile (D9).

**D5. Permission requests get their tool call.**

- The proxy records each `tool_call`'s `title`, `kind`, `rawInput` and `locations` per
  `(sessionId, toolCallId)`, and copies them into `toolCall` of the matching
  `session/request_permission`. Records are dropped when the call completes or fails, and when
  the session closes.
- The person then sees, for example, `pwsh` and the command with its `sandbox_permissions` and
  `justification`, instead of a bare ID.
- The service's pre-allow of `mcp__gorynych__list_agents`, `reply` and `spawn_session` reads the
  title. DSH never asks for MCP tools, though (D8), so for DSH pre-allowing changes nothing.
- The options pass through unchanged. They are already distinct ACP kinds, and DSH counts an
  unknown option as a rejection.

**D6. Tool kinds, locations and diffs.**

- The proxy maps DSH's tool names to ACP kinds:
  - `pwsh` / `bash` → `execute`;
  - `read` → `read`;
  - `write`, `edit`, `apply_patch` → `edit`;
  - `grep`, `glob`, `ls` → `search`;
  - `web_fetch` → `fetch`;
  - `web_search` → `search`;
  - `subagent`, `subagent_fork`, `send_message` → `other`;
  - `mcp__*` → `other`.
- It takes `locations` from the input's path fields.
- For `edit` and `write` it adds ACP `diff` content from the input (`path`, old and new text)
  when the input has them.
- The exact tool names and argument names come from the live probe `basics`, which lists the
  tools (to confirm).
- Messages stay whole: DSH commits them that way, and the service needs nothing else. A report
  carries the child's final message, its text after its last tool call, as before.

**D7. The sandbox: to decide.** See [For the user to decide](#for-the-user-to-decide). It is
written into the patch as `sandbox-policy` and `approval` rows (examples in
[probes/dsh/overlays](probes/dsh/overlays)).

- DSH pins the mode into a session at its first turn (finding 13). So the choice applies to
  sessions created by Gorynych from then on. Sessions made earlier, or in `dsh web`, keep their
  own mode.

**D8. The gorynych tools run without asking.**

- §6.2 wants `ask_agent`, `archive_session` and `restore_session` to ask the person. DSH runs
  every MCP tool unasked, and nothing per launch changes that.
- **Recommendation:** a contract change (C3). An adapter flags
  `agentCapabilities._meta["gorynych/unaskedMcpTools"]: true`, and the peers server then asks
  the person itself before running those three for a session of that agent. It asks through
  `session/request_permission` to the session's client, as the service relays approvals today.
- Fallback until then: DSH sessions archive and ask freely, which the user should know.

**D9. Titles stay the service's provisional ones.**

- DSH's acp app switches its model titles off; DSH then keeps a deterministic title, the first
  words of the first prompt (finding 13), and `dsh web` shows it.
- Neither title reaches ACP. The service's provisional title is the same text, so nothing is lost.
- Switching model titles back on (a patch row, checked offline) costs a model call per session
  on the user's key. It would still need the adapter to read DSH's projection cache
  (`~/.dsh/storages/session_projcache/sessions/<id>.json`, versioned, written behind) and to
  send `session_info_update`. Not in stage one.
- No `_gorynych/set_title`: DSH has no title write over ACP.

**D10. Idle time: `gorynych/unloadAfter` 120.**

- As for Codex: while Gorynych holds a session, `dsh web` cannot open it, and the reverse also
  holds (finding 10). Opening again costs about 0.2 s (finding 9), and DSH's start (3.6 s)
  only matters after the adapter restarts.

**D11. The one-writer error.**

- The proxy turns `-32603` with `already owned by an active write handle` into `-32603` with
  this message:

  > This DeepSeek Harness session is open in another DSH app (dsh web or another dsh); close it there, then try again.

  `data` is kept as it came.

**D12. History comes from the journal.**

- With no `session/load`, the service resumes, and history comes from its journal (§4). That
  covers every session made in Gorynych.
- A session made in `dsh web` and opened in Gorynych shows no earlier history.
- Reading DSH's session log ourselves (zstd JSONL, format v4 with migrations) is out of scope.

**D13. `DSH_TELEMETRY_DISABLED=1`.**

- DSH's default exports nothing without explicit feedback, which ACP cannot give. Setting the
  variable per launch makes that certain, at no cost.

## Feature mapping

Section numbers are the contract's.

| Contract | DSH mechanism | Gap / what the adapter or service does |
| --- | --- | --- |
| §1 start | `node <dsh>/lib/bin.js --profile acp --patch …` child process, stdio; in-process proxy | D1, D2 |
| §1 hidden from outside | MCP servers are node's children (finding 5) | Windows walk as today; the user's own MCP servers are not in the acp profile unless they add them |
| §1 configuration | profile `acp` auto-created by DSH; our `--patch` from the data dir | decided: DSH may create its template; nothing else in `~/.dsh` |
| §2 initialize | as finding 3 | proxy adds `gorynych/unloadAfter` 120; no `authMethods`; the no-key error (finding 12) reaches the client unchanged |
| §3 new | `session/new {cwd, mcpServers}`, durable at once | drafts: no titles in the list and no delete, so an unused draft gets a tombstone and an empty DSH session stays in `~/.dsh` (Risks) |
| §3 list | `{sessionId, cwd}`, cursor, `cwd` | D4; no title, no `updatedAt` |
| §4 open | `session/resume` with `mcpServers`, about 0.2 s | D11 for the one-writer error; no load (D12) |
| §5 prompt | `session/prompt`: text, resource links, images when the route can | several text blocks are concatenated in one user message (the spawned session's two blocks arrive as one text) |
| §5 cancel | `session/cancel` | live: time to `cancelled`, what is left running |
| §5 stop reasons | as in [What the code says](#what-the-code-says) | `aborted`, `blocked` and `error` end as `end_turn` |
| §5 echo | none sent | as ACP |
| §5 message IDs | DSH's `messageId` on message and thought chunks | passed on; DSH does not keep `gorynych/messageId` |
| §5 streaming | whole committed messages | none needed (D6) |
| §6 service turns | ordinary prompts | as Kimi; each costs API tokens |
| §6.2 gorynych server | `mcpServers` on new and resume; tools `mcp__gorynych__*` | started twice per open (finding 5; harmless, to note in the peers server's log); tools unasked (D8) |
| §6.2 system prompt | per-launch `personaSuffix` | D3; capability not advertised |
| §6.4 live notes | none (`-32601`) | turns |
| §7 close | `session/close`: cancels, drains descendants, flushes | live: a close during a turn ends the prompt `cancelled`? |
| §7 own turns | autonomous root work after a cancel or from children | no marks; the service's fallback (idle since the last update) |
| §7 idle time | one writer vs `dsh web` | D10 |
| §8 approvals | sandbox escalations only; `allow-once` / `reject-once` | D5; D7; D8 |
| §8 forms | none (no elicitation, no ask tool in acp) | nothing to relay |
| config | `model`, `reasoning_effort` | passed through; no `mode` option (D7 is per launch) |
| §9 titles | none over ACP | D9 |
| §10 delete | none (`-32601`) | tombstone; the user can delete in `dsh web` |
| §11 archive | none in acp | overlay |
| §12 children | nothing beyond §3–§6 | DSH's own subagents are inside one prompt and not listed |
| §13 conformance | below | |

## Gaps and how the service covers them

| Gap | Covered by |
| --- | --- |
| No `session/load`, no replay | the service's journal (D12); none for sessions made in `dsh web` |
| No delete | the overlay's tombstone; DSH's own UI can delete |
| No archive | the overlay |
| No titles, no `updatedAt` | provisional titles; sessions the service never saw sort last |
| Own sessions missing from the list | the proxy's merge (D4) |
| No tool kinds or locations | the proxy's map (D6) |
| Bare permission requests | the proxy's fill (D5) |
| No forms, no ask tool | nothing to cover; DSH cannot ask the person anything but approvals |
| MCP tools unasked | contract change C3 (D8) |
| No system-prompt capability | the per-launch patch (D3) |

## Per-launch configuration

| What | Value |
| --- | --- |
| Command | `node <dsh>/lib/bin.js --profile acp --patch <data>/dsh/gorynych.patch.yml` |
| Launch folder | `<data>/dsh` (no `.env`) |
| Environment | inherited, minus `DSH_HOME` and `DSH_PERMISSION_MODE`, plus `DSH_TELEMETRY_DISABLED=1` |
| Patch | `system-prompt` (D3), `sandbox-policy` and `approval` (D7) |
| Per session | `cwd`, `mcpServers` with `gorynych` (new and every resume), config options as the person sets them |

DSH itself creates `~/.dsh/profiles/acp/` once and rewrites its `cordis.yml` on every boot. It
writes sessions, its projection cache and attachments under `~/.dsh`, as `dsh web` does.

## Risks

- **A developer preview.** Two release candidates a week apart already differ in the ACP
  surface (model changes mid-turn, forks in the list). DSH says it will break compatibility.
  `agentInfo.version` is a constant. Mitigations:
  - the adapter logs and checks the package version (D2);
  - the live smoke test runs against each new version before the user updates.
- **The API key costs money.** Every turn of a DSH session spends the user's DeepSeek balance.
  That includes `ask_agent` opinions, reports from children, DSH's own subagents, and
  autonomous work that continues after a cancel.
  - The acp profile's model is the cheap `deepseek-v4-flash`, but its default effort is `high`.
  - Model titles stay off (D9).
  - The service could show usage (`usage_update`) prominently for DSH sessions.
- **The Windows sandbox edits ACLs** of every folder DSH works in, standing and not removable
  with icacls (D7).
- **One writer.** A session cannot be open in Gorynych and in `dsh web` at once (D10, D11).
- **Durable empty drafts.** Clients that create sessions eagerly (Zed) leave an empty DSH session
  per draft in `~/.dsh`. The service hides it with a tombstone; `dsh web` may still show it (to
  check).
- **Our rows win.** The user's own `system-prompt` or sandbox settings in DSH's profile and home
  layers are replaced by ours, because a patch replaces a whole row.
- **Mixed versions on one store.** An older `dsh web` and a newer `dsh acp` on the same
  `~/.dsh` may disagree on the session format. The coordinator is handling the running
  0.2.1-rc.2 instance; the adapter can only warn about the version (D2).
- **Node.** DSH needs a Node runtime on PATH (26 here); the adapter finds `node` like `dsh`.

## For the user to decide

1. **The sandbox** (D7). The live probes check each option in a scratch tree.
   - **(a) DSH's default** (`workspace-write`, `ask`). Commands and writes inside the workspace
     run without asking; anything else is denied, then escalated with a question. The price is
     the standing ACL and Low label on every repository DSH works in.
   - **(b) No Windows sandbox, approvals kept** (patch: `sandbox-policy.mode:
     danger-full-access`, `approval.policy: ask`). From the code, this asks nothing at all:
     without a sandbox nothing is denied, so nothing escalates. It is full access without
     questions, like Codex's `full-access`. To confirm live.
   - **(c) `read-only`** (`ask`). Every write is denied, then escalated with a question. That
     costs a second model step per write, and pwsh runs in ConstrainedLanguage. An approved
     pwsh escalation to `workspace-write` still writes the ACL and label for that workspace;
     only an escalation straight to `danger-full-access` avoids it.

   **Recommendation (provisional, until the probes run):** (b) does not give "a question before
   every command and write". If that is what the person wants, only (c) comes close among
   DSH's settings, at the cost above. If the person accepts no questions for a coding agent in
   its own folder, (b) avoids touching ACLs. (a) is the right choice only if the ACL edits are
   acceptable on the user's repositories.
2. **The gorynych tools unasked** (D8): accept, or ask through the service (C3).
3. **Telemetry off** (D13): recommended.
4. **Model titles** (D9): off in stage one.

## Proposed contract changes

- **C1. §3: the agent's own loaded sessions.** Add: *An adapter MUST list the sessions it has
  loaded; where its agent leaves them out of `session/list` (DSH), the adapter adds them.*
  Otherwise the service takes them for gone.
- **C2. §8: the tool call of an approval.** ACP lets a permission request name the tool call by
  `toolCallId` alone. Add: *the adapter SHOULD send the tool call's title and input with the
  request (DSH's adapter fills them from the earlier `tool_call`).* Also make the service's
  pre-allow merge with the journaled tool call by ID, so a bare request still matches.
- **C3. §6.2: agents that run MCP tools unasked.** Add the flag
  `agentCapabilities._meta["gorynych/unaskedMcpTools"]`. With it, the peers server asks the
  person itself before `ask_agent`, `archive_session` and `restore_session` (D8).
- **C4. §6.2: a system prompt per process.** Add: *an adapter whose agent takes instructions
  only per process (DSH) applies the service's text at launch, from its options, and does not
  advertise the capability.*
- **C5. §1: the launch folder.** Add: *an adapter starts its agent in a folder of its own,
  never in a session's folder*, since DSH loads the launch folder's `.env`. Kimi's adapter
  already starts in the user profile.
- **C6. §13: the DSH column.** Replace "DeepSeek, expected" with these findings once the live
  probes run.

## Live probes still to run

Written in [probes/dsh/acp_probe.py](probes/dsh/acp_probe.py) and checked keyless up to the
first prompt. They will run with `--real-home` against the global `dsh` once the coordinator
says the old instance is stopped and the global install is updated. Each runs in a folder under
the scratch folder, on `deepseek-v4-flash` at effort `low`, and records its session IDs.

| Check | Contract steps | What it settles |
| --- | --- | --- |
| `basics` (with `overlays/basics.yml`) | 8, 11, 12, 13, 15 | the probe word from `personaSuffix`; DSH's tool names (D6); an MCP call unasked; whether an ask tool exists; a subagent's updates and how long the prompt stays in flight; the title DSH records |
| `plain` | 12 | a launch without the overlay resumes that session: is the probe word gone (per launch)? |
| `default` | 8, 9, 7, sandbox (a) | update mapping for a pwsh and a file-tool write; the tree's DACL and label before and after (SDDL), and a FullControl open of a subfolder; cancel mid-command and what is left running; a turn after the cancel; close during a turn |
| `readonly` (with `overlays/read-only.yml`) | 10, sandbox (c) | the permission request as sent; reject; a question held while the turn is cancelled (does DSH withdraw it with `$/cancel_request`?); allow, and the tree's ACL after an approved escalation |
| `off` (with `overlays/sandbox-off.yml`) | sandbox (b) | does anything ask before a pwsh write, a file write or a delete; the tree's ACL unchanged |

About 12 short turns in all, plus one subagent; 5 sessions. Probe 14 (`dsh web` on the same
store) is covered by finding 10 for the locking. Whether `dsh web` lists sessions made over
ACP, and empty ones, is checked by reading its list once a web instance runs. Probe 16 (load
cost) is finding 9.

## Leftovers

- The keyless probes ran only in a scratch `DSH_HOME`. Nothing was written to `~/.dsh`,
  checked after each run.
- The live probes will leave up to five sessions in `~/.dsh/sessions/` that DSH cannot delete
  over ACP. Their IDs go in the report, so the user can delete them in `dsh web`.
- Probe workspaces under the scratch folder keep the ACEs and label that sandbox (a) and an
  approved escalation in (c) write. They are temporary folders and go when the scratch
  folder is cleaned.
