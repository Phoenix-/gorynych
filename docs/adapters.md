# Adapters

What every adapter must provide, and how Claude, Kimi, Codex and DeepSeek compare, is in
[adapter-contract.md](adapter-contract.md). This file keeps what is particular to each
adapter that exists.

## Kimi Code

`kimi acp` is native ACP, so the adapter only runs the unmodified binary under the
user's own login and hands its stdio to the service. Observed with Kimi Code 2.4.1
(recorded in `tests/Gorynych.Tests/Fixtures/kimi`):

- `initialize`: `loadSession`, `sessionCapabilities` list, resume, close, delete, fork,
  additionalDirectories; one terminal auth method (`kimi login`). Kimi 2.5.2 advertises no
  `sessionCapabilities.systemPrompt`, so it gets no peer instructions as a system prompt
  (checked in its bundled ACP agent on 8 November 2026).
- Its chunks carry no `messageId` (2.5.2, same check): the service gives them IDs as it
  publishes them ([service-protocol.md](service-protocol.md#standard-methods)).
- `session/new` returns config options (model, thinking, mode) and modes; the first
  prompt immediately yields `session_info_update` with the prompt as the title.
- **Its list of commands** (`available_commands_update`, 31 commands in 2.5.2) comes after the
  `session/new` answer, and twice on every `session/load` and `session/resume`: before the
  replay and again after the answer (probed with 2.5.2 on 10 November 2026; 2.4.1 also sent it
  twice in a first prompt). The service keeps the latest as the session's state, not in the
  journal, and passes on only a list that differs from the one before
  ([service-protocol.md](service-protocol.md#standard-methods)); journaled, every open of a
  Kimi session had added two lists. The one list after `session/new` may reach the service
  before the client has its answer; the service sends it to that client after the answer, so a
  new Kimi thread in Zed has its slash commands.
- `session/list` returns titles and `updatedAt`. A folder may come with forward slashes
  (`C:/Users/x`); the service lists and keeps it in one spelling
  ([service-protocol.md](service-protocol.md#standard-methods)).
- Approvals arrive as `session/request_permission` with Kimi's own options (approve once,
  approve for this session, reject); tool calls stream their arguments as `tool_call_update`.
  The service answers those of the gorynych tools itself, approve once, as they ask nothing
  ([service-protocol.md](service-protocol.md#approvals)). Kimi marks no mode
  `gorynych/fullAccess` (it has no adapter of Gorynych's to do so), so a Kimi session never
  counts as in full access.
- `session/new` creates a real, empty native session. Kimi lists it with `title: null`
  until the first prompt titles it, and `session/close` keeps it listed. A `session/delete`
  of a session Kimi does not have answers `-32602 Invalid params: Unknown sessionId`, not
  ACP's `-32002`.
- The service deletes a draft with Kimi's own `session/delete` (after `session/close`) once
  Kimi lists it untitled, and forwards a client's `session/delete` the same way. Verified
  live on 7 November 2026 (`Kimi_DraftIsDeletedNatively_WhenItsClientLeaves`, and a probe of
  the list and delete answers).
- Kimi has no way for a client to set a session's title, so a name given to `spawn_session`
  lives in the service's overlay and wins over the title Kimi takes from the first prompt.
  The service answers Kimi's approval of `spawn_session` itself, since it asks the person
  itself where needed ([service-protocol.md](service-protocol.md#child-sessions)). Kimi does not
  know `_gorynych/note`, so a child's report reaches a Kimi parent as a marked turn. A Kimi
  session that is not loaded is resumed for an opinion request or a report and unloaded again
  after the service's idle time ([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)).
- **Archive: not mirrored.** Kimi Code 2.5.2 has a native archive (`"archived"` in a
  session's `state.json`, `kimi session list --archived`, set from Kimi's own UI), but
  `kimi acp` offers no method for it. Its `session/list` leaves natively archived sessions out:
  read from the 2.5.2 code (not tried, so as not to archive any of the user's sessions), it
  asks Kimi's engine for the session list with an empty query, which excludes archived ones,
  and its `SessionInfo` has no archive field. So the service's archive flag lives in its
  overlay for Kimi too ([service-protocol.md](service-protocol.md#archive)) and never touches
  Kimi's files. A session archived in Kimi's own UI is not seen as archived: while Kimi is
  running, the service takes it for a session Kimi no longer has and leaves it out of
  `session/list`, even with `includeArchived`; its ID still works for `session/load` and
  `ask_agent` as far as the service goes (whether Kimi resumes a natively archived session is
  not checked). Restoring it in Kimi brings it back.
- Also seen in the 2.5.2 code, not tried: Kimi's ACP `session/list` ignores the request's
  `cursor`, so if it ever returned a `nextCursor`, following it would repeat the first page.
- **History made in the Kimi CLI.** Kimi keeps no marks and `kimi acp` replays a session only
  whole, on `session/load`, without message IDs. So the service reopens a Kimi session with
  `session/load` (not `session/resume`) and aligns the replay with its journal by user text
  ([service-protocol.md](service-protocol.md#history-made-outside-gorynych)). Kimi 2.5.2 puts a
  `<system-reminder>` part of its own after every replayed user message; the service cuts such a
  leading reminder off a part, and a part that is only one is left out. The replay is Kimi's context, so after a compaction it is a summary and a kept
  tail; the journal's last user messages are found in that tail. An undo (`context.undone`)
  drops them, and the session is imported again. `/clear` in the Kimi CLI starts a new session.
  Verified live on 10 November 2026: a turn taken with `kimi -S <id> -p` showed once, marked
  `native`, and a second open added nothing.
  - The replay gives a tool call another ID than it had live: `1:Read_0_…` for a live
    `0:Read_0_…` (probed with 2.5.2 on 10 November 2026). So a Kimi turn's tool calls never say
    whether it is the journal's turn; before that was known, reopening any session with a tool
    call imported it again as rewritten. Verified live since: such a session opened twice is
    kept as it is.
- Verified live on 7 November 2026 after logging in: first prompt, a Write approval, the
  title, replay through the service journal, and `ask_agent` to a Claude session. Before
  the login every turn failed with `-32000 Authentication required: 403`, which the service
  relays to the client unchanged.

## Claude Code

The adapter (`Gorynych.Adapters.Claude`) is an in-process ACP agent that drives the unmodified
`claude` binary under the person's own login. It never touches `~/.claude`: everything goes
through per-launch flags and Claude's own protocols. It has two transports, picked with the
service option `--claude-transport`:

- **`headless`** (the default): `claude -p` with stream-json in and out, one process per loaded
  session. Anthropic's help center says `claude -p` and the Agent SDK signed in with a plan draw
  from the plan's limits (updated 15 June and 7 October 2026).
- **`tui`** (the fallback): the interactive TUI in a Windows pseudo console, with http hooks and
  the development channel ([below](#tui-transport)). It stays built and tested for the day
  Anthropic moves `-p` off the plans.

Both use Claude's own transcripts and session IDs and the adapter's one session list, so
switching is one flag with no migration: a session started with one transport continues with
the other (`LiveSmokeTests.Claude_SessionsMoveBetweenTransports_BothWays`). The design and the
probes behind the headless transport are in
[research/claude-headless-spec.md](../research/claude-headless-spec.md).

### Headless transport

#### Launch

```
claude -p --input-format stream-json --output-format stream-json --verbose
       --include-partial-messages --replay-user-messages --permission-prompt-tool=stdio
       --settings <session>/settings.json --permission-mode <mode> [--model <m>] [--effort <e>]
       [--mcp-config <session>/mcp.json] [--append-system-prompt <live-note authorization>\n\n<ACP systemPrompt>]
       (--session-id <id> | --resume <id>)
```

- `settings.json` holds only the deny rule for `mcp__gorynych-outside`; the transport needs no
  hooks.
- The process starts with the session's first prompt, not at `session/new` or
  `session/resume`, and lives until `session/close` or the service's idle unload. It gets the
  TUI's cleaned environment without `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN`, which in
  `-p` would win over the plan login.
- A `session/resume` with `_meta["gorynych/draft"]: true` for a session the adapter does not
  know (it restarted before the draft's first prompt, which is when it records a session)
  opens it afresh under its ID, so its first prompt launches with `--session-id`. Without the
  mark an unknown session is not found (both transports).
- The session handles only the lines of its current process: a process it gave up (stopped off
  the plan, or after an interrupt without a result) may still have output in the pipe, which is
  dropped, so it can neither open a turn nor answer an approval.
- Right after the start the adapter sends the `initialize` control request (no fields). Its
  answer brings the model catalog and the account, which the adapter checks
  ([below](#failing-loudly)).
- Stdout is read line by line on a thread of its own; stdin takes JSON lines under one lock;
  stderr goes to `<session>/stderr.log` (up to 1 MB) and its tail into the error when Claude
  exits early.

#### Prompts and turns

- `session/prompt` waits until no turn runs and nothing written waits, then writes one `user`
  message stamped with a fresh `uuid`. Text blocks go as text, `image` blocks as Anthropic image
  blocks (`promptCapabilities.image`), resource links as `@<uri>`.
- A turn starts with the replay of the first message it takes, or with output Claude produces
  on its own, and ends with its `result`. The client's prompt ends with the `result` of the turn
  that replayed its `uuid`, never with another's (a turn Claude began itself, say, whose result
  was already on its way).
- `result` → `stopReason`: `success` → `end_turn`, `stop_reason: max_tokens` → `max_tokens`,
  `refusal` → `refusal`, `error_max_turns` → `max_turn_requests`, any result after
  `session/cancel` → `cancelled`. Another error adds a note (`[Claude stopped with an error: …]`)
  and ends the turn.
- `session/cancel` sends `interrupt` with `cancel_queued`, after the prompt's own write (one
  gate orders them), so a prompt Claude has not started yet is dropped too. The turn ends with
  its `result`; without one within 5 s it ends as `cancelled` and Claude's process is stopped, so
  the next prompt resumes the conversation in a new one. Live notes the interrupt cancelled go
  back to the service ([below](#live-notes-opinions-and-reports-on-stdin)), and approvals still
  open are withdrawn. A cancel whose interrupt comes only after its turn ended sends nothing, so
  it never stops the next prompt.
- Messages that wait with no turn running while Claude stays silent for 30 s are let go: a
  prompt among them fails ("Claude did not take the prompt"), so nothing waits for ever.
- Claude exiting during a turn fails the prompt with the exit code and the end of stderr; the
  next prompt starts a new process with `--resume`.

#### Stream → ACP

| Claude stream | ACP |
| --- | --- |
| `stream_event` `message_start` | the message ID (`msg_…`) for the chunks that follow |
| `stream_event` `text_delta` / `thinking_delta` | `agent_message_chunk` / `agent_thought_chunk`, `messageId` = that ID |
| `assistant` with `tool_use` | `tool_call` (`pending`), `toolCallId` = the block's `id`, title, kind and locations as in the TUI |
| `assistant` text no delta carried | `agent_message_chunk` with the whole text |
| `user` with `tool_result` | `tool_call_update` `completed` / `failed`, `rawOutput` = `tool_use_result`, Bash output or the error as content |
| replay of the client's own prompt | nothing (ACP does not echo) |
| replay of a live note | `user_message_chunk`, `messageId` = the note's `uuid`, the note's `_meta`, `gorynych/origin: "live"` |
| `system` `init` | the current model and mode (`config_option_update` when they change) |
| `result` | ends the turn; a turn of Claude's own ends with `_meta["gorynych/agentTurn"]: "ended"` |
| a subagent's messages (`parent_tool_use_id`) | not shown yet; the `Task` tool call shows until its result |

Output that arrives while no turn was counted is a turn Claude began itself (a background
task ended): it is marked `gorynych/agentTurn: "started"` on its first update and `"ended"`
with its `result`, so the service holds the session meanwhile.

#### Approvals and questions

- `can_use_tool` control requests become `session/request_permission` with the TUI's two
  options, Allow (`allow_once`) and Deny (`reject_once`); the answer is `{behavior: "allow",
  updatedInput}` or `{behavior: "deny", message}`, and a denial marks the tool call `failed`.
  Nothing offered writes Claude's settings: the request's `permission_suggestions` are not
  used.
- `AskUserQuestion` (present in `-p` only because the adapter declares a permission host)
  becomes the same `elicitation/create` form as in the TUI; an accepted form answers `allow`
  with `updatedInput.answers`.
- The gorynych tools ask nothing ([below](#the-gorynych-tools)): `can_use_tool` for one of them
  is answered allow without asking the person, as in the TUI.
- `control_cancel_request` withdraws the ACP request.
- An MCP server's `elicitation` is declined for now; other control requests from Claude get an
  error.

#### Live notes: opinions and reports on stdin

`_gorynych/opinion` and `_gorynych/note` go into the running Claude as user messages, so they
join a running turn after its current tool, or start a turn of Claude's own when Claude is
idle. No development channel, consent question or notice is involved.

- An opinion keeps the service's text unchanged and gets a footer that names the adapter's
  `message_id` (`c<run tag>-<n>`) and says to answer with `reply`. The `gorynych` server gets
  `--reply-url …/channel/<session>/<token>`: it routes `reply` to the adapter first, without
  declaring a channel or polling, and on to the service for a `message_id` the adapter does not
  know.
- A report goes in as the service's text.
- The note's `uuid` finds its `_meta` when Claude replays it; the `user_message_chunk` carries
  it with `gorynych/origin: "live"` and, for an opinion, `gorynych/opinion`.
- While Claude's process is not running the adapter answers `-32600`, so the service asks in a
  turn that starts Claude. An opinion request Claude does not take after all (cancelled with the
  client's turn, or lost with the process) is answered `-32601`, and the service asks it in a turn
  instead.
- `--append-system-prompt` starts with the adapter's authorization for this path: messages
  whose first line starts with `[Opinion request` or `[Report` come from the user's other agents
  through Gorynych; opinions are answered with `reply` and no other action, reports need none.

#### Mode, model and effort selectors

ACP config options, like Codex's:

- `mode`: `default` ("Ask"), `acceptEdits`, `plan`, `auto` when the current model supports
  it, and `bypassPermissions` ("Full access", [below](#full-access-mode)); a mode set in the
  person's own settings shows as itself.
- `model`: the catalog of `initialize` (`value`, `displayName`, `description`).
- `reasoning_effort`: `default` (Claude's own) and the model's `supportedEffortLevels`.
- **The catalog** is the `models` of a headless `initialize`. Every launch that brings one that
  is new or changed keeps it in `claude/catalog.json` and sends `config_option_update` to every
  headless session, so open threads get the selectors without being reopened.
  - At start the adapter loads the kept catalog, so `session/new` and `session/resume` show all
    three selectors before any launch. A file that cannot be read counts as none.
  - With none kept, the adapter fetches one once, in the background (neither the service's start
    nor `session/new` waits; stopping the adapter stops it): Claude launched as every headless
    session is (the same flags and per-launch settings, the same cleaned environment, the same plan
    check), in `claude/catalog`, asked only `initialize`, then closed.
    - No prompt is sent, so it uses no tokens.
    - `--no-session-persistence`: no transcript and no project folder in `~/.claude`. Checked
      live (Claude Code 2.1.302): nothing named after the fetch's session is left in `~/.claude`
      (without the two flags below Claude still made an empty `session-env/<id>` folder, as it
      does for every run).
    - `--setting-sources local` (the folder has none) and `--strict-mcp-config` with an empty
      `--mcp-config`: none of the person's settings files, so none of their hooks, and none of
      their MCP servers run for it. Claude still reads the login, so the plan check holds.
    - Off the plan it keeps nothing and logs why. Until a catalog arrives, `session/new` returns
      only `mode`.
  - A model the person chose that a later catalog no longer has stays the session's model and is
    shown as such.
- A change applies to a running Claude at once (`set_permission_mode`, `set_model`,
  `apply_flag_settings {effortLevel}`), is kept in the session's record in `claude/sessions.json`,
  and is passed to later launches (`--permission-mode`, `--model`, `--effort`). Nothing the
  person did not change is sent. Full access is the exception below.

#### Full access mode

Claude's `bypassPermissions`, as the `mode` value "Full access": Claude asks nothing at all,
for any command and any file, outside the folder too. Only the person picks it, per session; the
adapter's own default is never Full access (a configured `bypassPermissions` is taken as
`default`).

- **Entering.** Claude cannot enter `bypassPermissions` in a process started without it; only
  `--allow-dangerously-skip-permissions` would allow that, and the adapter never gives it. So
  choosing Full access stops an idle Claude, and the next prompt starts it again with
  `--permission-mode bypassPermissions` (a resume: nothing is lost). During a turn the choice
  is refused until the turn ends. The choice is kept only once the old Claude has stopped.
- **Leaving** is `set_permission_mode` on the running Claude, which holds from its next tool
  call; a Claude that refuses it is stopped.
- Option changes go one at a time and never during a launch, each on the settings as they are
  then. After `initialize` the launch checks the mode Claude reports against the one it asked
  for, both ways: Full access the person did not choose (the person's own Claude settings may
  set it) stops the launch, and so does a Claude that did not take Full access (its settings may
  turn it off with `permissions.disableBypassPermissionsMode`); either fails the prompt with the
  reason.
- `claude -p` asks no one to accept bypass mode, and the adapter writes nothing for it.
- The value is marked `_meta["gorynych/fullAccess"]: true` in the `mode` option, so the service
  asks the person before a session that is not in full access asks this one
  ([service-protocol.md](service-protocol.md#approvals)); the gorynych tools themselves run
  unasked here as in every mode ([below](#the-gorynych-tools)).
- The mode is kept in the session's record like the other choices, so a resumed session starts
  in Full access until the person leaves it.

#### The gorynych tools

They run without asking the person, in every mode ([service-protocol.md](service-protocol.md#approvals)):

- Every launch's `--settings` allows `mcp__gorynych__list_agents`, `reply`, `ask_agent`,
  `archive_session` and `restore_session` (`permissions.allow`). Claude applies allow rules
  before auto mode's classifier, so `auto` does not judge them either. `spawn_session` is not in
  the rule, as it was not before: the service asks the person itself before a spawn they did
  not ask for, and Claude's own approval of it is answered yes.
- Claude may still ask for one through `can_use_tool`, and the adapter answers allow without
  asking the person: `plan` asks for MCP tools whatever the allow rules say, and so does an ask
  rule of the person's or a project's settings. `can_use_tool` does not say why it asks, so the
  two cannot be told apart.
- The person's deny rules still win: Claude evaluates deny rules before the mode and allow
  rules, and leaves a denied MCP tool out of its list, in Full access too. So does the rule that
  hides `gorynych-outside`.
- No hook is registered. A PreToolUse hook's allow would not override deny rules either
  (Claude Code's permissions page: "PreToolUse hook decisions don't bypass permission rules"),
  and nothing needs one.
- Checked live in `claude -p` 2.1.302 (`haiku`, `sonnet` for auto) with a stdio MCP server named
  `gorynych`: with the per-launch allow rule, `ask_agent` ran unasked in `default`,
  `acceptEdits` and `auto`, while `plan` and a project ask rule still went to `can_use_tool`; a
  project deny rule took the tool out of Claude's list, with the allow rule or with a hook's
  allow alike. Without the rule, `default` asked through `can_use_tool`. Through the adapter
  (live test, 9 November 2026, `haiku`): `ask_agent` ran with no question in Ask and in auto,
  and not at all in a folder whose `.claude/settings.json` denies it.

#### Titles

Headless Claude writes no generated `ai-title`, so the service's provisional title stays until
a name is given. The adapter advertises `gorynych/setTitle`: `_gorynych/set_title` becomes the
`rename_session` control request, which writes Claude's own `custom-title` (waiting for the next
launch when Claude is not running). The transcript is still followed, so a title written
elsewhere reaches the client.

#### Folder trust

`claude -p` asks nothing before working in a folder and runs that folder's project hooks and
`.mcp.json` servers at once. So before the first launch in a folder the adapter reads Claude's
own flag (`projects[<folder or a parent>].hasTrustDialogAccepted` in `~/.claude.json`, or in
`$CLAUDE_CONFIG_DIR`), and otherwise asks the person with the same approval the TUI's trust
dialog becomes. An allow is kept in `claude/trusted.json`, never in Claude's configuration; a
deny fails the prompt.

#### Failing loudly

The transport never runs on silently when Claude is not on the person's plan. The check fails
closed: a launch runs only when `initialize` answers with an `account` that has a plan
(`subscriptionType`) on `apiProvider: "firstParty"`; one whose `initialize` fails, times out or is
cancelled, or shows a token without a plan (an auth token, a gateway) or another provider, is
stopped.

- `initialize` shows no login (no plan, `tokenSource: "none"`), which is what `--bare` looks
  like: Claude is stopped and the prompt fails, telling the person to sign in or to start the
  service with `--claude-transport tui` (the headless docs say `--bare` will become the default
  for `-p`; it reads no OAuth login).
- `initialize` shows an API key (`apiKeySource`, which wins over the login in `-p`), or a turn's
  `system/init` does: Claude is stopped before it calls the API, and the prompt fails, naming the
  variable to remove.
- An assistant error `authentication_failed`, `billing_error`, `account_on_hold`,
  `oauth_org_not_allowed` or `verification_required` fails the client's prompt and leaves a
  note in the feed, both naming `--claude-transport tui`.

### History made outside Gorynych

Both transports advertise `gorynych/replayFrom`
([service-protocol.md](service-protocol.md#history-made-outside-gorynych)). The adapter reads
Claude's transcript, `~/.claude/projects/<folder>/<session>.jsonl` (append-only; read, never
written):

- **The mark** is `<uuid>@<offset>`: the last entry of the conversation and the byte offset
  where its line starts. The conversation is the chain from the file's last main entry (not a
  subagent's `isSidechain` one) back by `parentUuid`, and by `logicalParentUuid` across a
  compaction. It is not the file's order: a rewind or a checkpoint restore starts a new branch
  further down. Every prompt's result carries the mark, and so does the `ended` update of a
  turn of Claude's own.
- **A catch-up** reads the file once from the mark's line, which must be the first entry there.
  If the chain from the last entry reaches the mark, the entries after it are replayed.
  Otherwise another branch is the conversation now: the whole chain is replayed, as
  `from: "start"`, and the log says so. A mark whose line moved is looked for in the whole file.
  - Lines are read as a stream, and only their `uuid`, parents, `type` and sidechain flag are
    taken. An entry's body is parsed only when it is sent.
  - Past 512 MB read in one pass (from the mark, or the whole file) the catch-up gives up and
    fails open; a line over 64 MB is left out. A last line without its newline is still being
    written: neither the mark nor a replay takes it.
  - A missing or unreadable transcript, or any error, answers without `gorynych/replay`; an
    entry of an unexpected shape is left out.
- **The mark at a turn's end** is taken once two reads 50 ms apart agree (at most half a
  second): Claude may still be writing the turn's last entries when its result comes. It never
  fails the prompt.
- **Entries become updates** as the live stream maps them, through the same code (`StreamJson`):
  - user text by `uuid`;
  - assistant text and thinking by the API's `msg_…` ID;
  - `tool_use` as a tool call;
  - `tool_result` as its update.

  Left out: subagents' entries, slash commands and their output (`<command-…>`,
  `<local-command-…>`), meta notes (`isMeta`), `[Request interrupted by user]`, a compaction's
  summary, a torn last line, and the `<system-reminder>` blocks Claude Code writes into user
  messages (a block that is only a reminder; reminders a text starts with). A reminder inside
  text the person wrote stays.
- `/clear` continues in a new session ID, so this session's transcript does not change.
  `claude --resume <id>` keeps the ID (`--fork-session` would not).
- Verified live on 10 November 2026: a turn taken with `claude -p --resume <id>` showed once,
  marked `native`, and a second open added nothing.

### TUI transport

The fallback: the interactive `claude` TUI in a Windows pseudo console, fed by http hooks and
the development channel.

#### Launch

```
claude --settings <session>/settings.json --permission-mode default [--model <m>]
       [--mcp-config <session>/mcp.json [--dangerously-load-development-channels server:gorynych]]
       [--append-system-prompt <channel authorization>\n\n<ACP systemPrompt>]
       (--session-id <id> | --resume <id>) -- <first prompt>
```

- `settings.json` holds http hooks posting to
  `http://127.0.0.1:<port>/hook/<session>/<token>` (Kestrel on loopback, a random token
  per session), which add to the user's global hooks, and one permission rule,
  `"deny": ["mcp__gorynych-outside"]`: the server for agents outside Gorynych, which the user may
  have registered in their own configuration, is not for a session inside it. The server also
  hides its tools by its ancestry ([service-protocol.md](service-protocol.md#hiding-from-sessions-inside-gorynych)).
- `--permission-mode default`, because a global `defaultMode: auto` would decide
  approvals without asking.
- The session ID is chosen by the adapter at `session/new` and passed as `--session-id`;
  later launches use `--resume` and pass every flag again (resume does not restore them).
- `session/new` launches nothing. The adapter's own list (`claude/sessions.json` in the
  service data, used for `session/list` and `session/resume`) gets the session at its first
  prompt, so a draft that is never prompted leaves nothing behind.
- The adapter does not advertise `session/delete`: the transcript belongs to `~/.claude`.
  Deleting a Claude session closes it (the process exits) and the service hides it with a
  tombstone in its overlay; `claude --resume` in a terminal still finds it.
- Environment: `CLAUDECODE`, `CLAUDE_CODE_*`, `CLAUDE_AGENT_SDK*` and, when started from
  inside another Claude Code session, `ANTHROPIC_BASE_URL` and the desktop MCP variables
  are removed, so the child uses the user's own login.
- Screens that need a key: the development-channels notice is confirmed with Enter, only
  for a launch with the channel, which the person agreed to
  ([below](#consent-to-development-channels)). The folder-trust question is relayed to the
  client as an approval and answered with Enter only if the person allows it.

#### Consent to development channels

Claude Code loads the service's channel only with `--dangerously-load-development-channels`, a
research preview, and then shows a notice at every launch that needs Enter. Gorynych confirms
that notice for the person, so it asks first:

- The first time a launch would load the channel, the adapter asks the person once with a
  `session/request_permission` on that session, before Claude starts: tool call
  `development-channels` (kind `other`), a title, a text that explains the preview and what
  each answer does, and the options `allow` ("Allow", `allow_always`) and `deny`
  ("Don't allow", `reject_always`). A `tool_call_update` then marks it `completed` (allowed)
  or `failed`.
- The answer is kept in the service's own data, `claude/channels.json`
  (`{"accepted": …, "answeredAt": …, "sessionId": …}`), never in Claude's configuration, and
  holds for every later launch, after a restart too. To be asked again, delete the file and
  restart the service, which reads it once at its start.
- Allowed: Claude starts with the channel, and the adapter confirms the notice on each launch.
  Declined: Claude starts without the channel and without its part of
  `--append-system-prompt`, as with `--no-channel`; opinion requests and reports go in as
  service turns ([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)).
- A question left unanswered (the client answers `cancelled`, or no client can take it) runs
  that launch without the channel, keeps nothing and asks again at a later launch. Like
  every approval, it waits for a client while none has the session open. A cancelled prompt
  withdraws its question (the tool call ends `failed`) and starts nothing.
- One question at a time, and it holds up no other session: while it is open, other sessions
  launch without the channel and are not asked; launches after the answer follow it.
- `--claude-channels accept|decline` answers up front, for a service nobody watches; `ask` is
  the default. `--no-channel` is the same as `decline`.

#### Prompts and turns

- The first `session/prompt` is the launch argument (after `--`). Until Claude has taken
  a prompt the launch uses `--session-id`; after that, `--resume`.
- Later prompts wait until `Stop` says Claude is idle, then go in as a bracketed paste
  (`ESC[200~ … ESC[201~`) and, 400 ms later, Enter. That arrives as a real user turn.
- A turn ends with the `Stop` hook (`end_turn`), but only a turn Claude has acknowledged
  with `UserPromptSubmit`, so a late `Stop` of an interrupted turn cannot end the next one.
- `session/cancel` sends Esc and ends the turn with `cancelled`; a prompt still waiting for
  the composer is dropped with `cancelled`.
- A launch that does not reach `UserPromptSubmit` within 90 s is killed and fails the
  prompt with the last screen text, so a stuck dialog is visible instead of a hang; the
  next prompt starts a fresh process.
- An approval is withdrawn when Claude abandons the hook call (its timeout, an answer typed
  in the TUI, an interrupt).

#### Hooks → ACP

| Hook | ACP |
| --- | --- |
| `UserPromptSubmit` | nothing for the client's own prompt (ACP does not echo it); otherwise `user_message_chunk` with `_meta["gorynych/origin"]` = `channel` or `terminal` and `messageId` = the hook's `prompt_id` (below); the first of a turn of Claude's own also carries `_meta["gorynych/agentTurn"]: "started"` |
| `PreToolUse` | `tool_call` (`pending`), `toolCallId` = `tool_use_id`, title and kind from the tool, locations from `file_path` |
| `PermissionRequest` | `session/request_permission` with Allow / Deny; the hook replies `allow` or `deny`. A denial also marks the tool call `failed`. |
| `PermissionRequest` for `AskUserQuestion` | one `elicitation/create` form (scoped to the session and tool call) with a property `q1`, `q2`, … per question: a string with `oneOf` for a single choice, an array of titled items for `multiSelect`; an accepted form makes the hook reply `allow` with `updatedInput.answers` (`{question: label}`, several labels joined by ", "), anything else `deny` |
| `PostToolUse` / `PostToolUseFailure` | `tool_call_update` `completed` / `failed`, `rawOutput`, Bash output as content |
| `MessageDisplay` | `agent_message_chunk` (`messageId` = `message_id`, the same for all pieces of one message) |
| `Stop` | ends the turn; if no `MessageDisplay` came, `last_assistant_message` is sent as the answer, with a new `messageId`; a turn of Claude's own ends with a `session_info_update` carrying `_meta["gorynych/agentTurn"]: "ended"`, which the exit of Claude's process sends too |
| `StopFailure` | ends the turn after a note with the error, with a new `messageId`, and marks a turn of Claude's own ended, as `Stop` does |
| transcript `ai-title` / `custom-title` | `session_info_update` with the title (a chosen name wins) |

**Message IDs** are Claude's own where it has them. An answer's is `MessageDisplay`'s
`message_id`, which Claude's transcript does not keep (the hook carries no transcript ID, and
mapping streamed pieces to transcript entries would hold the stream back). A typed or channel
message's is the hook's `prompt_id`, which the transcript keeps as the `promptId` of the
prompt that started the turn (checked live on 8 November 2026). A channel message that
arrives while Claude works on a turn carries that turn's `prompt_id` (recorded in
`hooks-channel.jsonl`), so an ID an earlier message of the session took gets a new UUID
instead; so does a message without one. The client's own prompts get the service's IDs.

The transcript is only read, from the path the hooks report, for the title. Approvals
and questions are kept apart: `session/request_permission` asks to allow an action,
`elicitation/create` asks for information. If the client cannot show forms, the question
is denied with a note so Claude asks in plain text.

#### MCP servers and the channel (opinions both ways)

The `mcpServers` of `session/new` and `session/resume` go into the per-launch
`--mcp-config` (stdio, http and sse), so Claude gets the client's servers and the
service's `gorynych` server. With a channel (the default once the person agreed,
[above](#consent-to-development-channels); `--no-channel` turns it off) the
adapter adds `--channel-url …/channel/<session>/<token>` to that `gorynych` server and starts
Claude with `--dangerously-load-development-channels server:gorynych`: the one server then
declares `capabilities.experimental["claude/channel"]`, long-polls the adapter for opinion
requests, pushes them as `notifications/claude/channel` with `meta: {kind: "opinion",
message_id}` (a `message_id` `c<run tag>-<n>`, never the service's `m…`), and posts `reply`
back; a `reply` the channel does not know goes on to the service. The content's first line names the asking session by
its `session_id` ([service-protocol.md](service-protocol.md#what-agents-are-told)); the meta
carries no sender label, which Claude could take for a session ID. `reply` reports an error
when the channel has no request with that `message_id`. Without a service-given server the adapter starts a channel-only
`gorynych` server itself.

`--append-system-prompt` holds two parts. With a channel, the adapter's own authorization
comes first: it authorizes opinions from the user's agents, forbids actions on their behalf,
says each channel message names the asking session by its `session_id` (or says it comes from
an agent outside Gorynych, which `ask_agent` cannot reach) and that such a message arrives as a
prompt when Claude was not running. Then comes the session's ACP
`systemPrompt`: the adapter advertises `sessionCapabilities.systemPrompt: {}` (append only,
since Claude Code can only add to its own prompt) and takes the text of the latest
`session/new` or `session/resume`. The service sends its peer instructions there, which tell
Claude about `list_agents`, `ask_agent` (by exact `session_id`; `reply` only answers),
`spawn_session` and reports ([service-protocol.md](service-protocol.md#what-agents-are-told)).
`override`, any other mode, an empty `content` and blocks other than text fail the request
with `-32602`, as ACP requires. Without a channel and without a `systemPrompt` there is no
`--append-system-prompt`. The gorynych tools ask nothing, as headless
([above](#the-gorynych-tools)): the launch's `--settings` allows them, and the PermissionRequest
hook allows any of them without asking (the service asks the person itself before a spawn they
did not ask for).

`_gorynych/note` (a child's report to its parent) becomes a channel message with
`meta: {kind: "report"}` and no `message_id`; the peer instructions say it needs no reply. The
adapter ignores the `sender` param of `_gorynych/opinion` and `_gorynych/note`: the text names
the session. It keeps the `_meta` each came with, and when Claude reports the channel message
as a prompt (`UserPromptSubmit`, `<channel … message_id="c1">` and the content), it finds it by
that `message_id`, or a report by its text, and puts those keys on the `user_message_chunk`
with `gorynych/origin: channel` and, for a request, `gorynych/opinion` = its `message_id`. So
the journal names the sender, a session or an agent outside Gorynych, as for a request in a
turn. It keeps at most 64 pushed messages that Claude has not reported yet. Without a channel (for the service, or for a session whose launch did not load it) the adapter answers method not found; while Claude's process is not
running (`session/resume` registers a session without starting one) it answers `-32600`, to
`_gorynych/opinion` as well. Either way the service sends the request or report as a turn
instead, which starts Claude, and closes the session again (ending the process) once its
service turns are over, unless a client opened it meanwhile
([adapter-contract.md](adapter-contract.md#61-waking-a-session-and-closing-it-again)). For
Claude, *loaded* therefore means registered in the adapter, not that its process runs.

In the TUI, a name given to `spawn_session` stays in the service's overlay: the transport cannot
write Claude's own `custom-title` (that needs `/rename` through the PTY), and Claude's
generated `ai-title` does not replace the name. The headless transport writes it natively
([Titles](#titles)).

Claude Code has no archive of its own, so the archive flag of a Claude session lives in the
service's overlay ([service-protocol.md](service-protocol.md#archive)); the adapter knows
nothing of it.

### Not done yet

- Headless: subagent messages, background tasks and `/compact` are not mapped; MCP servers'
  elicitations are declined; not tried on Linux and macOS.
- TUI: a Unix PTY host (the transport reports "not supported" off Windows), `/rename` from the
  GUI, resizing the pseudo console.
- Listing Claude sessions the adapter did not start (`~/.claude/projects`).

## Codex

Codex does not speak ACP (0.167 has no `codex acp`), so the adapter is a translator, like
Claude's. It is an in-process ACP agent that drives one `codex app-server`, the service's child,
over app-server's JSON-RPC. The design and the probes behind it are in
[research/codex-adapter-spec.md](../research/codex-adapter-spec.md); the probes are in
`research/probes/codex`. Built and checked against Codex 0.167.0 on 8 November 2026.

- **Types** are generated from the app-server schema that `codex app-server
  generate-json-schema` writes, kept in `schema/codex/0.167.0`, by `tools/Gorynych.CodexGen`.
  It works the way `Gorynych.AcpGen` does for ACP: open enums, tagged unions with an `Unknown`
  variant, and a source-generated `CodexJsonContext` for Native AOT. Only the types the adapter
  uses and those they reach are generated (the list is `Roots` in the generator). To move to a
  new Codex, generate the schema into a new folder and run the generator with its version.
- **Process.** `codex` is found on PATH. On Windows the native `codex.exe` that npm's
  `codex.cmd` shim would start through node is used directly. The adapter starts it as
  `codex app-server` (stdio), and app-server starts the threads' MCP servers as its own
  children, so they are inside Gorynych for the `gorynych-outside` walk. The shared daemon
  (`codex app-server daemon`) is not used: it starts MCP servers outside the service's process
  tree. When app-server exits, the adapter stops, the service starts it again on the next
  use, and every session is not loaded. Starting and initializing app-server takes about
  0.4 s.
- **Identity.** `initialize` sends `clientInfo {name: "gorynych", title: "Gorynych"}`. Codex
  records the name as the thread's `originator` and puts it in its user agent and OpenAI's
  compliance logs. The thread's `source` is `vscode`, as for Codex Desktop's threads, so
  Codex's own apps list Gorynych's threads too. The adapter turns off the notifications it
  does not use (`optOutNotificationMethods`): hooks, rate limits, MCP startup and the like.

### Sessions

| ACP | Codex |
| --- | --- |
| `session/new` | `thread/start {cwd, config, developerInstructions}`. Nothing is on disk before the first turn, and the thread is not listed until then (`gorynych/draftsNotDurable`: the service drops an unused draft instead of hiding it) |
| `session/list` | `thread/list {cwd, cursor, limit 50, archived: false, sortKey: updated_at, useStateDbOnly: true}`; `title` is the thread's `name` (none until a name is set or generated: the service shows its provisional title), `updatedAt` from unix seconds |
| `session/resume` | `thread/resume {threadId, excludeTurns: true, config, developerInstructions}`; with `gorynych/replayFrom`, then the thread's turns after the mark (below). `gorynych/draft` changes nothing: the app-server names a thread, so a thread never prompted is not there after the adapter restarts |
| `session/load` | the same, then the history from `thread/items/list`, page by page, as the updates below. The service no longer uses it: a thread the journal has not seen is imported through the replay from the start |
| `session/close` | `turn/interrupt` when a turn runs (ACP's close cancels it; `thread/unsubscribe` alone lets it run on), up to 5 s for the turn to end, then `thread/unsubscribe`. App-server is started with `-c thread_unload_delay_secs=0`, so it unloads the thread at once, and the close returns once `thread/closed` came (at most 10 s): the thread's writer lock is then free for another Codex app. A turn that has not ended by then ends `cancelled`: Codex sends nothing more of an unsubscribed thread |
| `session/delete` | stops a running turn as `session/close` does, then `thread/delete`; a thread Codex no longer has answers `-32002` |
| `_gorynych/set_title` | `thread/name/set` (works before the first turn too) |

- **The `gorynych` server** goes in `config.mcp_servers` of `thread/start` and of every
  `thread/resume`. Codex keeps that config only while the thread is loaded, and ignores it for
  a thread that is already loaded. A stdio server is `{command, args, env}`, an HTTP one
  `{url, http_headers}`; SSE servers are left out.
- **`gorynych-outside` is off** in Gorynych's threads when the user's Codex config registers
  it (`config/read`): `config.mcp_servers["gorynych-outside"] = {enabled: false}`. Turning off
  a server the config does not have fails the thread (`invalid transport`), so it is checked
  first. Together with the ancestry walk this hides the outside tools on every OS. The user's
  other MCP servers, hooks (`~/.codex/hooks.json`) and `AGENTS.md` apply to Gorynych's threads
  as to any other.
- **The appended system prompt** is `developerInstructions` (`sessionCapabilities.systemPrompt`,
  append only; any other mode or a non-text block fails with `-32602`). Codex keeps the
  instructions a thread was created with: sending others on `thread/resume` does not replace
  them (checked live), so a thread keeps the text of the service that created it.
- **One writer.** A thread has one writer process. While Gorynych's app-server has a thread
  loaded, Codex Desktop, the CLI or VS Code cannot continue it, and Gorynych cannot resume a
  thread one of them has open. Within its own process the adapter drives every turn, so there
  are no turns of Codex's own to mark (`gorynych/agentTurn`).
  - **The mechanism.** Codex takes an OS lock on `<CODEX_HOME>/thread-writer-locks/<thread
    id>.lock` (an empty file; Rust `File::try_lock`, `LockFileEx` on Windows; codex-rs
    `rollout/src/writer_lock.rs`). Nothing in it says who holds it, and app-server has no API
    for it: its `ThreadStatus` is about its own process.
  - **Who holds a thread, told beforehand (Windows).** The adapter asks the file system which
    processes have the lock file open (`NtQueryInformationFile`,
    `FileProcessIdsUsingFileInformation`). It opens the file for its attributes only, with
    read, write and delete sharing: it takes no lock, which could make Codex itself fail to
    open the thread, and creates or deletes nothing. Its own app-server, the service, and any
    process its app-server started (a wrapper's child) do not count. Only a Codex process that
    can be read is a holder, named by its image and its parent: `Codex Desktop` (any image
    under `%LOCALAPPDATA%\OpenAI\Codex\` or in the `OpenAI.Codex` package, or a `codex*.exe`
    started by `ChatGPT.exe`), `VS Code` (a `codex*.exe` in a VS Code extensions folder or
    started by `Code.exe`), otherwise a `codex*.exe` is `a terminal codex (pid N)`. Anything else
    that has the file open (an antivirus, an indexer, a backup, Explorer's preview), and a
    process that cannot be read (gone, its ID reused, elevated), is ignored: an early refusal is
    final, so a false one would lock a free thread out. (A codex that runs as `node` would be
    ignored too; none was seen: npm's shim starts the native `codex.exe`, which holds the lock.)
    `CODEX_HOME` comes from the service's environment, else `~/.codex`.
  - **What it does with it.** `session/load` and `session/resume` of a held thread fail at once
    with `This Codex thread is open in <holder>; close it there, then try again.`, before
    `thread/resume` (so the other app is left alone). The error has no `data`: Zed appends it
    to the message as raw JSON, and the message names the holder already. `session/list` marks each held thread with
    `_meta["gorynych/openElsewhere"]`, the folder read once per page, and the adapter answers
    `_gorynych/open_elsewhere` for the service's session lists
    ([service-protocol.md](service-protocol.md#sessions-open-in-another-app)).
  - **Changes as they happen.** While the adapter runs it watches the lock folder
    (`FileSystemWatcher`, file names only): a lock file appears when an app takes a thread and
    is usually deleted when it lets go. Once a burst of changes settles (0.75 s), it finds the
    holders of the threads they touched again; the threads it last saw held, in a list, an open
    or an event, it looks at again every 60 s, for a holder that went away without deleting its
    file (it crashed). Threads whose holders could not be found are looked at again on that
    tick. When the watcher may have missed changes (its 64 KB buffer overflowed) it looks at
    every thread it has seen, and keeps the watcher; each tick it also checks that the folder is
    still there and the watcher still runs (a deleted folder may go unreported), and makes a
    new one if not, after which it looks at every thread seen too. For each real change of
    holder it sends the service `_gorynych/sessions_changed {sessionId, change: "updated"}`
    (`gorynych/sessionsChanged`), which the service passes on to its clients, so Zed lists the
    thread again and shows the new mark. With no folder it watches nothing until the folder
    appears (looked for at the same 60 s); a watcher that fails leaves the timer alone. When the
    adapter stops, a callback of the watcher still queued finds it stopped and does nothing.
  - **A heuristic on Codex's insides.** Any failure (no folder, a file that does not open, a
    status other than success, a holder that is not Codex or cannot be read) means "not known":
    nothing is marked and the open goes on to app-server. Outside Windows nothing is known. App-server's own refusal, `thread <id> already
    has an active writer`, stays the fallback: `This Codex thread is open in another Codex app
    (Codex Desktop, the CLI or VS Code); close it there, then try again.`
  - **Checked live** on 9 November 2026 with Codex 0.167 (CLI) and Codex Desktop 26.1029: of 18
    threads listed, three held by Codex Desktop and two by codex in terminals were marked as
    such, and resuming one of Codex Desktop's failed at once with its name.
- **Idle time: 2 minutes** (`gorynych/unloadAfter: 120`; the service takes the shorter of it and
  `--unload-after`). A loaded thread locks Codex's own apps out, so it should go back soon after
  a conversation moves on. Loading it again is cheap (probe of 8 November 2026): `thread/resume`
  took 0.4 s, and the user's nine MCP servers were up 1.7 s after it, or up to about 11 s when
  one of them fails slowly. Two minutes still covers a quick follow-up question, which then
  finds the thread loaded.
- **Archive.** Codex refuses to resume an archived thread (`session <id> is archived`), while
  Gorynych's archive keeps a session usable, so the archive stays in the service's overlay.
  The list leaves Codex-archived threads out (as Kimi's does), so an archive made in Codex
  Desktop is not mirrored. A thread archived there after Gorynych knew it fails to open with
  `This Codex thread is archived in Codex. Unarchive it there (codex unarchive <id>), then try
  again.`

- **History made outside Gorynych** (Codex Desktop, `codex resume`, VS Code;
  [service-protocol.md](service-protocol.md#history-made-outside-gorynych)):
  - The adapter advertises `gorynych/replayFrom`. Its mark is the thread's newest turn ID:
    every prompt's result carries the ID of the turn it ran.
  - A resume with a mark lists the thread's turns newest first without their items
    (`thread/turns/list`, `sortDirection: desc`, `itemsView: notLoaded`, 50 a page) until the
    marked turn. Then it reads the items of the newer turns alone, turn by turn
    (`thread/items/list` with `turnId`), and sends them as they come, mapped as `session/load`
    maps items, with Codex's own IDs (a prompt of the service's under its
    `clientUserMessageId`). Nothing before the mark is read, and a replay is never held whole. A
    mark Codex no longer has (a rollback) or none replays every turn.
  - The new mark is the newest completed turn, never one still running. A turn still running
    (in another Codex app) is not replayed either: its messages are not whole yet, and the
    journal, which takes a message it has by its ID, would keep them cut. The next catch-up
    gets it whole. A thread without turns has none. Any failure to list them answers without
    `gorynych/replay`.
  - Verified live on 10 November 2026: a turn taken with `codex exec resume <id>` showed once,
    marked `native`, and a second open added nothing.
- **Letting go of a thread.** Codex 0.167 keeps a thread it has nobody subscribed to loaded,
  and its writer lock held, for `thread_unload_delay_secs`, 60 s by default (probed on
  10 November 2026; [probe](../research/probes/codex/writer_lock_probe.py)).
  - Measured, with nothing else running: after `thread/unsubscribe` the thread stayed in
    `thread/loaded/list` and its lock file stayed open in app-server's own process. After
    61 s app-server sent `thread/status/changed` `notLoaded` and `thread/closed`, and only then
    could a second app-server resume it. A name set before the turn changed nothing (it is no
    naming job that holds it).
  - So for a minute after Gorynych let go of a thread, Codex Desktop or `codex resume` were
    refused with "active writer".
  - App-server has no method to unload a thread at once. The adapter therefore starts it with
    `-c thread_unload_delay_secs=0` (per launch; `~/.codex` is not touched) and waits for
    `thread/closed` on a close.
  - Measured with that: the thread was unloaded within 3 s, also one with an MCP server given
    per thread, and `codex exec resume` took it at once with the service still running.

### Prompts and turns

- `session/prompt` is `turn/start {threadId, input}`, one `text` input per text block (a
  resource link as `@<uri>`). Its `clientUserMessageId` is the service's `messageId` for the
  prompt (`_meta["gorynych/messageId"]`), which Codex keeps as the message's `clientId`, so
  history replayed from Codex carries the journal's IDs.
- The turn ends with `turn/completed`: `completed` → `end_turn`, `interrupted` → `cancelled`,
  `failed` → the prompt fails with Codex's message (a context-window error → `max_tokens`).
  `session/cancel` is `turn/interrupt`; Codex then settles a pending approval itself
  (`serverRequest/resolved`), and the adapter withdraws it from the client.
- **A cancel is never lost.** One that comes before Codex has named the turn (while
  `turn/start` is on its way, or before the prompt's handler has run: the adapter compares the
  cancel's place in the ACP stream with the prompt's) interrupts the turn as soon as
  `turn/started` or `turn/start`'s answer names it. `turn/start` is always waited for, so no
  turn runs on that the adapter does not know.
- **Every turn ends.** When Codex unloads the thread mid-turn (`thread/closed`, status
  `notLoaded`), the turn ends `cancelled`; when app-server stops, the prompt fails. A close
  ends its turn also when `thread/unsubscribe` fails. Such an unload note can come late, for an
  unload before the thread was opened again, so it is checked against `thread/loaded/list`
  first; and a late `turn/completed` of a turn ended here is not taken for a later turn's.
- **`turn/interrupt` never holds anything up**: its answer is waited for at most 5 s, and a
  `session/cancel` does not wait for it, so the ACP connection reads on.
- Codex's own user message items are not echoed (ACP).

| Codex | ACP `session/update` |
| --- | --- |
| `item/agentMessage/delta` | `agent_message_chunk`, `messageId` = the item ID (`msg_…`); the completed item's text only when no delta came |
| `item/reasoning/summaryTextDelta`, `textDelta`, `summaryPartAdded` | `agent_thought_chunk`, `messageId` = the item ID (`rs_…`); a new summary part starts a paragraph |
| `commandExecution` | `tool_call` titled from `commandActions` (`Read <file>`, `Search …`, else the command without Codex's shell wrapper), kind `read`, `search` or `execute`; on completion its output as text and `rawOutput {exitCode, output}` |
| `fileChange` | `tool_call` kind `edit` (`Create`, `Delete` or `Edit <file>`), locations, each file's diff as text |
| `mcpToolCall` | `tool_call` titled `<tool> (<server>)`, `rawInput` = its arguments, the result's text |
| `webSearch`, `imageView`, `dynamicToolCall`, `collabAgentToolCall`, `imageGeneration` | `tool_call` kinds `fetch`, `read`, `other` |
| `turn/plan/updated` | `plan` |
| `thread/tokenUsage/updated` | `usage_update {used: the last request's input + output tokens, size: the model's context window}` |
| `thread/name/updated` | `session_info_update {title}` |
| `thread/settings/updated` | `config_option_update` |

### Approvals and forms

All approvals are `session/request_permission` on the item's tool call:

| Codex | Options |
| --- | --- |
| `item/commandExecution/requestApproval` | of its `availableDecisions`: `accept` (allow once), `acceptForSession` (allow for this session), `decline` (deny), `cancel` (deny and stop the turn) |
| `item/fileChange/requestApproval` | the same four |
| `item/permissions/requestApproval` | allow for this turn, allow for this session, deny |
| `mcpServer/elicitation/request` with `_meta.codex_approval_kind: mcp_tool_call` | allow once, allow for this session (`_meta.persist: session`), deny |

- **No option writes Codex's configuration.** `acceptWithExecpolicyAmendment` and
  `applyNetworkPolicyAmendment` add rules to `~/.codex/rules/default.rules`, and MCP's
  `persist: always` keeps an approval beyond the session. Codex offers them; the adapter does
  not (adapter-contract §8).
- **The gorynych tools ask nothing**: the `gorynych` server goes to Codex with
  `default_tools_approval_mode: "approve"` (verified per thread), so Codex runs its tools without
  an approval ([service-protocol.md](service-protocol.md#approvals)). The `full-access` mode is
  marked `gorynych/fullAccess`, as are a thread's own settings with approval `never` and sandbox
  `dangerFullAccess`, so the service asks the person before a session outside full access asks it.
- **Only what was offered.** An `optionId` the adapter did not offer counts as the first "deny"
  option (`decline`, or deny for a permissions request), never as an approval.
- **Withdrawn**, each request answers its own "nothing granted": `{decision: cancel}` for a
  command or file change, `{permissions: {}, scope: turn}` for permissions, `{action: cancel}`
  for an MCP elicitation, `{answers: {}}` for Codex's questions.
- **Titles.** A command's approval is titled from the request's own `commandActions`. A file
  change's request names only its item, and an MCP tool's approval names no item; app-server's
  requests are handled apart from its notifications, so the adapter waits up to a second for
  the item's `item/started` before falling back to a generic title.
- `item/tool/requestUserInput` (Codex's own questions) and an MCP server's form elicitation are
  `elicitation/create` forms; a URL elicitation is declined.

### Modes, models and effort

ACP config options, applied with the next `turn/start`, which Codex keeps for later turns:

- `mode`: Codex's permission presets.
  - `read-only`: approval `on-request`, sandbox `read-only`.
  - `auto`: `on-request`, `workspace-write`.
  - `auto-review` ("Approve for me"): as `auto`, with Codex's automatic reviewer
    (`approvalsReviewer: auto_review`), a standard per-thread setting of app-server, so nothing
    is written to `~/.codex`. Its decisions were not seen live: the probe's command needed no
    approval.
  - `full-access`: `never`, `danger-full-access`.
  - Settings that match none of these (the user's own config) show as `custom` until changed.
- `model`: `model/list`'s visible models.
- `reasoning_effort`: the selected model's efforts.
- A new thread takes the user's own defaults (here `gpt-6.1-sol`, effort `xhigh`); the adapter
  overrides nothing until the person changes an option.
- **Only what the person changed** goes with the next `turn/start`: a model or an effort alone,
  or a mode's approval policy, sandbox and reviewer. The adapter keeps Codex's own values: a
  granular approval policy or an external sandbox is never sent back as a name, and a mode
  whose sandbox type the thread already has sends the thread's own sandbox, with its writable
  roots and network access (or the one it had before, when switching back).
- A model or effort Codex does not list is refused at once. Should Codex still refuse a turn
  carrying a change, the change is dropped and the options are reset, so it cannot fail the
  turns after it.

### Not done yet

- Images and embedded resources in prompts.
- A live-note path through `turn/steer` (a child's report into the parent's running turn).
- Pre-allowing tools, for every harness at once.
- `thread/settings/update` (experimental) to apply an option before the next turn.

## DeepSeek Harness

DeepSeek Harness (DSH) speaks ACP itself (`dsh --profile acp`), so the adapter is not a
translator. It starts DSH with a per-launch patch and puts a thin ACP proxy between DSH and the
service. The proxy forwards every frame with its IDs and fields, and rewrites only what DSH's ACP
lacks. DSH's sandbox is per process, so there is one DSH process per permission mode, each
behind its own proxy, and a router in front of them ([Permission modes](#permission-modes)). The design, the probes and the decisions the user took are in
[research/dsh-adapter-spec.md](../research/dsh-adapter-spec.md); the probes are in
`research/probes/dsh`. Built and checked against DSH 0.2.2-rc.1 on 9 November 2026. DSH is a
developer preview that runs on the user's own DeepSeek API key, so every turn costs money.

- **Process.**
  - The adapter finds the `@deepseek-ai/dsh` package from the `dsh` on PATH: next to npm's
    shim on Windows, or through the `bin/dsh` link elsewhere.
  - It runs `node <package>/lib/bin.js --profile acp --patch <data>/dsh/gorynych.patch.yml`
    with `<data>/dsh` as the working folder. DSH loads its launch folder's `.env` into its
    environment, so that is never a repository.
  - It adds `DSH_TELEMETRY_DISABLED=1` to the environment and changes nothing else.
  - One process serves every read-only session. A second one, with
    `gorynych.full-access.patch.yml`, serves the full-access sessions: it starts when a session
    first needs it and stops when it holds none. DSH starts the sessions' stdio MCP servers as
    its own children, inside Gorynych for the `gorynych-outside` walk.
  - Starting and initializing takes about 3.7 s. Closing stdin ends DSH in under 0.1 s when it
    is idle; the adapter waits up to 6 s, then kills the tree.
  - The package's version is logged, with a warning when it is not one the adapter was checked
    against. `agentInfo.version` is a constant `0.0.1`.
- **Configuration.**
  - DSH runs on the user's own `~/.dsh`: its key, its sessions, the same store `dsh web` uses.
  - On first boot DSH creates its shipped `acp` profile in `~/.dsh/profiles/acp` and rewrites
    that profile's `cordis.yml` on every boot. The user accepted this. Gorynych writes nothing there.
  - Everything of Gorynych's is in the patches, rewritten before each start. A patch replaces a
    row's whole config, so its rows restate what they keep. Each holds:
    - **`system-prompt`.** The acp app's `personaPrefix`, restated, and a `personaSuffix`:
      the service's peer instructions; a line naming the gorynych tools in full (DSH has a
      `list_agents` of its own, for its subagents); and what a Gorynych DSH session can do
      (below). DSH assembles the system prompt per request, so it holds for every session of
      that launch and for no other. The adapter does not advertise
      `sessionCapabilities.systemPrompt`, and refuses to start if the text holds `{{`, which DSH
      reads as a template variable.
    - **`sandbox-policy`: `read-only` or `danger-full-access`; `approval`: `ask`.**
    - **`permission`: disabled.** DSH's permission presets would pin a sandbox into each new
      session, and a pinned session keeps it in any process ([Permission modes](#permission-modes)).
  - The user's own layers (`~/.dsh/cordis.patch.yml`, the acp profile's `cordis.patch.yml`)
    still apply, but these rows are replaced by Gorynych's.

### Permission modes

Like Codex's, a DSH session has an ACP config option `mode` (category `mode`, "Permissions"),
with Codex's IDs and names, so a client draws one selector for both:

- `read-only` ("Read only"), the default for every new session: a read-only shell and approved
  file writes ([Read only](#read-only-a-read-only-shell-approved-file-writes)).
- `full-access` ("Full access"): DSH asks nothing at all
  ([Full access](#full-access)). An explicit choice per session.

DSH has no auto mode that is safe on Windows: its `workspace-write` sandbox edits the
repository's ACL. DSH 0.2.2 cannot switch a session's sandbox over ACP either: its config options
are the model and the effort, and its `/permission` command has no ACP surface. So:

- **One process per mode.** The sandbox is a patch row, so each mode has its own process. The
  router sends a session's messages to the process that has it open, and the service never knows.
- **Switching** closes the session in its process and resumes it in the other; DSH's resume
  brings back its model and effort, and the answer carries both with the new mode.
  - Refused while a turn of the session is running.
  - Should the other process not start (it must answer `initialize` within 30 s), refuse the
    resume or fail in between, the session is resumed where it was and the client hears why. A
    second process left holding nothing is stopped.
  - Read-only is saved before the switch: if it cannot be saved, the switch is refused and the
    session stays in full access, as the client still shows it. Full access is saved after; if
    that fails, the session opens read-only next time.
- **No pins.** DSH's permission presets pin a sandbox and an approval policy into each session
  when it is made, and a pinned session keeps them in any process. The patches disable the
  presets, so Gorynych's sessions are not pinned and run in the sandbox of their process.
  - Sessions DSH did pin (made in `dsh web`, or by Gorynych before this mode existed, or opened
    once in `dsh web`, which pins its own default into them) keep their pin. The router reads it
    from DSH's projection cache (`storages/session_projcache/sessions/<id>.json` in DSH's home,
    read only, format 7 only) and shows it: a `read-only` pin that asks as Read only, a
    `danger-full-access` pin, whatever its approval, as Full access (that session goes to the
    full-access process, and the value is marked `gorynych/fullAccess`: with no sandbox nothing
    escalates, so nothing asks), any other pin as "Set in DeepSeek Harness" with its sandbox,
    unmarked (a `workspace-write` pin that never asks is still confined). Such a session cannot
    switch; the answer says to change it in `dsh web` with `/permission`.
- **Kept by Gorynych.** DSH keeps no mode for an unpinned session, so the adapter keeps the
  full-access ones in `<data>/dsh/modes.json`; a resume goes to the process of the session's
  mode, also after a restart.
- **The list** comes from the read-only process. DSH lists every session but those open in the
  same process, so it shows the full-access process's open sessions on whichever page. On a list
  that fits one page the router adds any it leaves out, once; DSH's entries carry only
  `sessionId` and `cwd`, so these do too.
- **A second resume** of a session open in one process goes to that process, which refuses it,
  as DSH does.
- **Forks.** DSH 0.2.2 offers no `session/fork`. Should it answer one, the fork stays in its
  parent's process, with its parent's mode.
- **IDs.** Each process numbers its own requests, so the router gives DSH's requests to the
  service IDs of its own and maps the answers back.
- **A process that ends.** When the full-access process ends, its sessions are gone, the
  service's requests to it fail and its questions are withdrawn. A request to such a session
  says that the full-access process stopped, until the session is opened again, which starts the
  process again. A process's output can end before the process lets go of its sessions, so the
  router stops it (killing it if it lingers) and starts the next process of its mode only once it
  has; otherwise that process's resume would find the session owned by an active write handle and
  be refused as open in another DSH app. The read-only process ending ends the adapter, and
  stopping the adapter stops both, also one still starting.

### Read only: a read-only shell, approved file writes

DSH's Windows sandbox would write a standing allow ACE, a `FILE_DELETE_CHILD` deny and a Low
integrity label onto a workspace tree at its first shell write. These edits cannot be removed
with icacls. Gorynych's read-only DSH sessions never let that happen:

- The process runs DSH's `read-only` sandbox. Reads, searches and commands that only read
  run without asking.
- It protects files from changes, not secrets: DSH's Windows sandbox (`WRITE_RESTRICTED`)
  confines writes and deletes only, so a command can still read any file the user can read and
  open network connections.
- DSH asks only to escalate a denied call, never otherwise. Only an allowlist reaches the
  person: a running call of DSH's `write` or `edit` (by the tool name DSH announced, ignoring
  case) whose arguments are a file tool's, a `file_path` and no `command`. The proxy refuses every
  other permission request itself, with `reject-once`, without asking anyone:
  - a shell command's;
  - an unknown or renamed tool's;
  - an MCP tool's (DSH runs those unasked, so a request for one is unexpected);
  - a call it did not see announced, or one that has already ended.

  So the ACL never changes.
  - The model's tool result says `the user rejected escalating this command …; stop and explain`.
    ACP has no way to give another reason, so the appended prompt says in advance that this
    text comes from Gorynych's policy, not from a person.
  - Checked live: the turn ends `end_turn`, and the model explains that the command was refused.
- A `write` or `edit` escalation goes to the person as an approval card. It names the tool,
  the path, the diff and "Asks to run this once with workspace-write permissions: <the model's
  justification>". The appended prompt asks the model to pass `sandbox_permissions` on every
  write, so a write asks once instead of failing first.
- The appended prompt says that builds, tests and installs are not available, that files are
  changed with the file tools, and that `git status`, `git diff` and `git log` work.

### Full access

The full-access process runs DSH's `danger-full-access` sandbox with approval `ask`:

- Nothing confines a command, so nothing is denied, nothing escalates and **nothing asks the
  person**: the shell and the file tools write anywhere on the disk, as Codex's `full-access`
  does. Builds, tests and installs work.
- No ACL changes: without a sandbox DSH writes no ACE and no label.
- The guard stays as it is: should DSH still ask, the same allowlist applies.
- The `full-access` value is marked `gorynych/fullAccess`, so the service asks the person before
  a session that is not in full access asks this one ([service-protocol.md](service-protocol.md#approvals)).
- The appended prompt says that the session has full access, that nobody reviews a command, and
  to keep to the task's folder and ask before reaching beyond it.

### Sessions

| ACP | DSH, through the proxy |
| --- | --- |
| `session/new` | DSH's own: a bare UUID, durable at once (it writes the session log before any prompt). DSH lists no title for it ever, and cannot delete it, so an unused draft gets a tombstone; it stays in `~/.dsh`, where `dsh web` can delete it |
| `session/list` | The read-only process's `{sessionId, cwd}`, newest-created first, with its cursor and `cwd` filter. DSH leaves out the sessions open in its own process, so the proxy adds back the sessions it has open, and the router those the full-access process has open, on the first page and in the requested folder. No `title` and no `updatedAt`: the service shows its provisional title |
| `session/resume` | DSH's own, in the process of the session's mode, with the request's `mcpServers` (about 0.2 s). A session open in another DSH process fails with `-32603`, which the proxy words as "This DeepSeek Harness session is open in another DSH app (dsh web or another dsh); close it there, then try again." |
| `session/load`, `session/delete` | not supported (`-32601`); history comes from the service's journal (not caught up yet, below), delete is a tombstone |
| `session/close` | DSH's own: it cancels a running prompt (`cancelled`), drains DSH's subagents and flushes |

- **Idle time.** `gorynych/unloadAfter` is 120 s: a session has one writer, so while Gorynych
  holds it, `dsh web` cannot open it.

### Prompts and turns

- Prompts, `session/cancel` and stop reasons are DSH's own. A cancel aborts the running tool
  call and ends the prompt `cancelled` within a quarter of a second.
- DSH's subagents run inside the prompt. The prompt stays in flight until they finish and DSH
  has taken their results. Their sessions are DSH's and are not listed.
- Messages and thoughts arrive whole, each as one chunk after the model step. DSH gives a
  thought and its answer one `messageId`; the proxy sends the thought as `<id>:thought`.
- **Tool calls.** DSH sends every call as `kind: other`, titled with the tool's name, with the
  arguments as `rawInput` and nothing else. The proxy adds:
  - **kinds:**
    - `pwsh` / `bash`, `job_kill` → `execute`;
    - `read`, `read_image`, `read_mcp_resource`, `job_output` → `read`;
    - `write`, `edit` → `edit`;
    - `glob`, `grep`, the MCP resource lists, `web_search` → `search`;
    - `web_fetch` → `fetch`;
    - `todo_write` and the goal tools → `think`;
    - everything else → `other`;
  - **titles:**
    - a shell call by its `description`, with the command in `rawInput`;
    - a file tool with its path, a search with its pattern;
    - MCP tools keep `mcp__<server>__<tool>`, which pre-allowing and `read_session` read;
  - **locations:** `file_path` resolved against the session's folder, or the shell's working folder;
  - **diffs:** `write` (new text) and `edit` (`old_string` → `new_string`).
- **The gorynych tools.** DSH runs MCP tools without asking, which is what the service wants
  of every agent for them; it asks the person itself where it must, before a spawn and before a
  question into full access ([service-protocol.md](service-protocol.md#approvals)).
- **Withdrawn questions.** On a cancel DSH drops its own pending approval without
  `$/cancel_request`. The proxy withdraws it from the service when the call ends or the prompt
  does, and drops a late answer.
- **Malformed output.** A message from DSH the proxy cannot read is logged and fails closed:
  - a permission request is refused;
  - another request gets `-32603`;
  - an answer passes on unchanged, so the service's request does not hang;
  - a notification is dropped.

  The proxy keeps reading either way. Everything for DSH's stdin goes through one queue and one
  writer, so reading never waits on DSH reading its input. A session DSH says it does not have
  (`unknown session`), or one whose close was answered at all, is no longer added to the list.
- No forms: the acp profile has no ask tool and no elicitation. No plans, no titles over ACP,
  no live-note path (`-32601`).

### Not done yet

- Switching a session DSH pinned (DSH 0.2.2 has no ACP surface for `/permission`).
- Titles from DSH: its acp app switches its model titles off, and the title it keeps is in its
  projection cache, not on the wire.
- History made outside Gorynych: sessions made in `dsh web`, and turns taken there or in the
  `dsh` TUI. DSH's ACP replays nothing. Its session log
  (`~/.dsh/sessions/<project>/<id>/session.v4.jsonl.zstd`) is append-only zstd JSONL with a
  contiguous `seq` and could give a catch-up after a mark. Reading it needs a zstd decoder,
  which .NET 10 does not have.
- Images in prompts (DSH offers them only for an image-capable route).
