# Catching the journal up with the agent's own history: specification

Draft for review, 9 November 2026. Nothing is built yet.

## Problem

`session/load` replays the service's journal. The journal is filled by what the service sees:
its own turns, and once, on a first load, the agent's replay. A session continued outside
Gorynych (Codex Desktop, `codex resume`, `claude --resume`, the Kimi CLI, `dsh web`) gains turns
the journal never sees. Seen live: Codex thread `01a1b074…` was journaled at 20:23 and continued
in Codex Desktop from 20:50 to 22:42. Zed shows none of that, although Codex in Gorynych resumes
it. That breaks "native state is the source of truth".

**Goal.** Before `session/load` replays the journal, the service appends whatever the agent's own
history gained after the last point the journal knows. When that point is gone (the history was
rewritten), it re-imports the history and says so. Nothing is lost silently, and nothing is
duplicated, including the service's own turns.

## Standards (checked 9 November 2026)

- **[message-id](https://agentclientprotocol.com/rfds/message-id)** is completed. `messageId` on
  chunks is stable in v1 and optional. It defines no replay cursor and no de-duplication.
- **`session/resume`** is stable in v1 and replays nothing. `session/load` replays everything.
- **[v2 session-resume-replay](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/docs/rfds/v2/session-resume-replay.mdx)**
  is a draft. It moves replay to `session/resume` with an optional
  `replayFrom: {"type": "start"}`, allows other cursor types later, and answers `-32602` to one
  the agent does not know. It says nothing on rewrites.
- **session-notices** is completed: `sessionUpdate: "notice"` (`severity`, `title`,
  `description`). Notices are live only and SHOULD NOT be replayed, so they cannot mark a gap in a
  journal.
- **session-compaction** is in preview, so not used.

We follow the v2 shape inside v1's extension points. A `replayFrom` cursor goes in
`session/resume`'s `_meta`, with a type of our own for "after this mark". The replayed history
arrives as ordinary `session/update`s before the response, as with `session/load`.

## Mechanism

**The mark.** The mark is an opaque string chosen by the adapter. It names the last point of the
agent's history that the journal holds. The service stores it per session, next to the journal
(`<journal>.native`, written atomically, deleted with the journal), and never parses it. An
adapter reports a new mark:

- in the `session/prompt` response, as `_meta["gorynych/historyMark"]`, at the end of every turn
  the service ran: a client's, an opinion, a report, a spawned session's first turn, and a
  cancelled turn too;
- on the `ended` update of a turn of the agent's own, as `_meta["gorynych/historyMark"]`
  (Claude's live notes and terminal turns, [adapter-contract.md §7](../docs/adapter-contract.md));
- in the response to a catch-up (below).

**When.** Catch-up runs on `session/load`, when all of these hold:

- the journal is not empty;
- the session is not loaded in its adapter;
- no turn runs.

A loaded session needs no catch-up: Codex and DSH let one writer at a time hold it, and the
others get the next open. A session the journal has never seen is imported as today.

**Protocol.** A capability, `agentCapabilities._meta["gorynych/replayFrom"]: true`, says the
adapter takes the cursor. The service then opens the session with `session/resume` and:

- `_meta["gorynych/replayFrom"]`: `{"type": "gorynych/after", "mark": "<mark>"}`, or
  `{"type": "start"}` when it has no mark. A journal from before this change has none.
- The adapter sends the history after the mark (exclusive), or all of it, as `session/update`s,
  before it answers. It does so the way it streams live, with the harness's IDs.
- It answers with `_meta["gorynych/replay"]`: `{"from": "mark" | "start", "mark": "<new mark>"}`.
  `from: "start"` means the mark was asked for but is no longer in the history's active line, so
  everything was sent.
- A failure of the catch-up alone (the history cannot be read) fails open. The adapter answers
  the resume without `gorynych/replay`, and the service replays its journal as it is and logs
  why.

**Applying it.** The service collects the replayed updates instead of publishing them, then:

- **After the mark:** it appends them to the journal and sends them to the clients already
  attached. Each one gets `_meta["gorynych/origin"]: "native"` (from the agent's own history,
  made outside Gorynych). It keeps the harness's `messageId`s, as the contract asks of live ones,
  and gives one to a chunk that has none. As a safety net it skips a message or tool call whose
  `messageId` or `toolCallId` the journal already has. That covers a crash between journaling a
  turn and storing its mark.
- **From the start:** the service first aligns the replay with the journal. If the journal's last
  message is in the replay (same `messageId`, or same user text, below), only what follows it is
  appended. That is also how a journal with no mark is brought up to date, the live case. Otherwise
  the history was rewritten. The old journal is kept as `<journal>.<UTC time>.before` (the last
  three), and the journal is replaced by:
  - a notice entry: a `user_message_chunk` with `_meta["gorynych/origin"]: "notice"` and
    `"gorynych/notice": "historyRewritten"`, text along the lines of "[History rewritten · Codex's
    history no longer holds what this session showed after 21:04; below is that history as it is
    now. The service keeps the earlier one.]";
  - then the replay, with journal `messageId`s kept where a message aligns.

  Attached clients get the notice entry and a live ACP `notice`.

**Alignment** is one routine with two keys. It compares by `messageId` where the replay has IDs
(Codex, Claude, DSH). For a user message without a matching ID, or an agent without IDs (Kimi),
it compares the user messages' text. The service's own notice entries are left out. It searches
from the end for the journal's last three user messages, in order. The replay's items after the
matched user message's turn are new.

Then the journal is replayed to the client, as today.

## Per adapter

| | Mark | Catch-up | Rewrites |
| --- | --- | --- | --- |
| **Codex** | the last turn's ID (UUIDv7, from `turn/completed`) | `thread/resume` with `excludeTurns`, as now, gives `turnsBackwardsCursor`. `thread/turns/list` descending pages back to the marked turn; the newer turns' items come from `thread/items/list` (`turnId`), mapped as `ReplayAsync` maps them. Nothing before the mark is read by us. | `thread/rollback` drops turns: the mark is not found, so `start`. Compaction keeps the turns visible and adds a `contextCompaction` item, which is appended as the live stream shows it. A deleted thread fails the resume: the journal is shown, as today. |
| **Claude** | the `uuid` of the turn's last transcript entry, from the stream, plus that entry's byte offset once seen | The transcript (`~/.claude/projects/<folder>/<id>.jsonl`) is read backwards from its end to the mark's line, then forwards. The active line is the main chain (not `isSidechain`), walked from its last entry by `parentUuid`, and across a compact boundary by `logicalParentUuid`. Reaching the mark: the entries after it are sent (user text, assistant text and thinking with `message.id`, `tool_use` and `tool_result` as tool calls). | A rewind or checkpoint restore makes another branch active, so the walk misses the mark: `start`, the whole file's active chain. `/clear` continues in a new session ID: this session's file is unchanged, so nothing is new. A missing file: the journal is shown, logged. Claude keeps no service ID with a prompt (the adapter stamps a new UUID), so its user messages align by text. |
| **DSH** | `<seq>@<offset>`: the last event's `seq` and the byte offset of the zstd frame after it, read by the proxy from the log after the turn | DSH's ACP has no replay. The proxy reads `~/.dsh/sessions/<project>/<id>/session.v4.jsonl.zstd`, which takes no write handle, from the offset (whole zstd frames, one per append batch, a torn last frame ignored). It maps the events with seq > mark as DSH's own live mapping does (`assistantUpdates`, `toolCallUpdate`, `toolResultUpdate`, same message IDs), plus user messages. It reads only the folder of sessions, never DSH's config. | The log is never rewritten. Compaction appends a `user/message` with `surfaceOp: replace`, appended as a short notice. Fork makes a new session. A shorter file, a frame that does not decode, or a seq gap means `start`. This also gives history to sessions made in `dsh web`, open today ([adapters.md](../docs/adapters.md#not-done-yet-2)). Needs a managed zstd decoder that is AOT-safe (ZstdSharp.Port, MIT) unless .NET 10 has one. |
| **Kimi** | none: the service aligns | No adapter of ours: `kimi acp` is native. Its `session/resume` replays nothing and its `session/load` replays the folded context, without IDs or a cursor. For a catch-up the service opens it with `session/load` and keeps the replay instead of dropping it, then aligns by user text. This is the fallback for any adapter that loads but does not advertise `gorynych/replayFrom`. Its `wire.jsonl` is append-only and could give a cheap tail later, but its format is Kimi's internal one. | The replay is Kimi's context: after a compaction it is a summary plus a kept tail. If the journal's last user messages are in that tail, only newer turns are appended; else `start`. An undo (`context.undone`) shortens it, so `start`. `/clear` in the CLI is a new session. |

An adapter with neither the cursor nor `session/load` gets no catch-up. That is DSH today, and
the proxy above changes it.

## Edge cases

| Case | What happens |
| --- | --- |
| The service's own turns | Each sets the mark at its end, so a catch-up after them sends nothing. The alignment and the ID safety net catch a lost mark. |
| A turn that failed (JSON-RPC error, no mark) | Its native part comes again at the next catch-up. Messages with IDs are skipped; Kimi's align by text. |
| The agent writes while we read (Claude, which has no lock) | The catch-up takes what is written by then; the rest comes on the next open. |
| A journal trimmed by its bound | The mark is about the agent's history, not the journal, so nothing changes. |
| History gone (deleted thread or file) | The journal is replayed, and the log says why. The journal is never dropped for it. |
| A client attached while another loads | It gets the appended updates live; after a rewrite it gets the notice and the history. |

## Contract and protocol changes

- **adapter-contract.md**:
  - §4: the cursor, its capability and ordering, `from: "start"` on a rewrite, failing open.
  - §5: `gorynych/historyMark` on prompt responses and on `ended`.
  - A conformance row "History catch-up": the marks above.
- **service-protocol.md**: `gorynych/origin` values `native` and `notice`, `gorynych/notice`, and
  the kept `.before` files.

## Tests

- **Service, with the fake agents (`LifecycleAgent`), extended by a native history with marks:**
  - an append after the mark appears once, with origin `native`, and reaches an attached client;
  - the service's own turns are never repeated;
  - no catch-up for a loaded session or during a turn;
  - a stale mark: no duplicates by ID;
  - a mark-less journal brought up to date by ID;
  - a rewrite: the `.before` file kept, the notice entry written, the IDs kept;
  - an adapter without the capability: the journal only;
  - a failed catch-up: the journal only;
  - Kimi-style alignment by text, also after a compaction and an undo.
- **Codex, with `FakeAppServer`:**
  - `thread/turns/list` paged descending to the mark;
  - `thread/items/list` by turn;
  - a rollback gives `start`;
  - only pages after the mark are read.
- **Claude, on transcript fixtures:**
  - a plain append;
  - a rewind to another branch;
  - a compact boundary;
  - sidechains left out;
  - a large file whose prefix is never read (count the bytes read).
- **DSH, on log fixtures** (zstd frames written in the test):
  - from an offset;
  - a torn last frame;
  - a seq gap gives `start`;
  - compaction;
  - the mapping matches the live stream's IDs.

## Live check (own test sessions only)

Each in a new temporary folder, under the user's own login. The user's own threads and Codex
Desktop are not touched.

1. Open a new session in Gorynych and run one turn. Close it and let it unload.
2. Continue it natively in a terminal, non-interactively:
   - `codex exec resume <id> "…"`;
   - `claude -p --resume <id> "…"` (check that it keeps the ID);
   - the Kimi CLI and DSH with their resume flags, found from `--help`.
3. Load it again in Gorynych. Expect:
   - the native turn once, with origin `native`;
   - a second load appends nothing.

For Codex, a rollback is made through `thread/rollback` by the test, and the load must then show
the notice. Rewinds in Claude cannot be scripted, so the fixtures cover them. Each test session is
deleted afterwards where the harness can delete.

## For review

1. Appended messages keep the harness's IDs (as the contract asks of live ones) rather than new
   ones. New IDs only where the harness gives none (Kimi).
2. The gap marker is a `user_message_chunk` with origin `notice`, since notices are not replayed.
   An agent message would put words in the agent's mouth.
3. A rewrite keeps the old journal as a file but shows only the history as the agent has it now.
4. DSH: reading its session log directly, and a zstd package. Kimi: the service's own alignment
   over `session/load`, not reading `wire.jsonl`.
5. `_gorynych/read` (a peer reading another session) still reads the journal as it is. Catching
   up there would load the session (Codex's one writer), so it is left out.
