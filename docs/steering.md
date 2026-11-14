# Steering

A message the person sends while an agent works goes into the running turn: the agent takes it
at its next safe input boundary (normally after running tools finish) and carries on with it,
instead of waiting for the turn's end or being cancelled. This file specifies steering end to end: the wire protocol,
the conductor, the adapters, the Zed fork and the AHP face. **Status:** specified on
13 November 2026, reviewed against upstream ACP on 14 November 2026. Building-it stages 1 and 2
are built (service, adapter hosts and resumable link; Claude headless and Codex); stages 3 and 4
(the Zed fork and the AHP face) are still planned and are not advertised by this work.

Why: today a message sent mid-turn in Zed either waits in its queue for the turn's end or, with
"Send Now", cancels the turn (`session/cancel`, then a new prompt), which throws away the
tool call in progress. Steering lets the person correct an agent without stopping it.

## The protocol

ACP v1 has no standard steering request. Its [prompt turn](https://agentclientprotocol.com/protocol/v1/prompt-turn)
lasts until the `session/prompt` response; [request cancellation](https://agentclientprotocol.com/protocol/v1/cancellation)
(`$/cancel_request`) and turn cancellation (`session/cancel`) are different operations, neither
of which adds a message. The published [v2 Draft prompt lifecycle](https://agentclientprotocol.com/protocol/v2/prompt-lifecycle)
instead acknowledges insertion with a `messageId` and reports foreground work through
`state_update`. It does not itself define busy-session delivery. The separate proposal is
[agentclientprotocol/agent-client-protocol#1261](https://github.com/agentclientprotocol/agent-client-protocol/pull/1261)
(`delivery: "steer" | "queue"` on `session/prompt`; open proposal, not part of the published
v2 Draft schema at this review).

Until then the closest thing to a standard is the extension that ACP adapters already share,
which the official Claude adapter calls "the agreed ACP steering wire protocol"
([agentclientprotocol/claude-agent-acp](https://github.com/agentclientprotocol/claude-agent-acp),
`src/v1/steering.ts`, `examples/steering.ts`). It is also used by
[block/buzz#3007](https://github.com/block/buzz/pull/3007) against claude-agent-acp and codex-acp,
and proposed for Kimi in [MoonshotAI/kimi-code#2374](https://github.com/MoonshotAI/kimi-code/pull/2374).
Gorynych conforms to it on both sides: toward clients as the agent, toward adapters as the client.

This is a shared **v1 extension**, not a standard ACP method. ACP's
[extensibility rules](https://agentclientprotocol.com/protocol/v1/extensibility) allow `_`-prefixed
methods and custom data in `_meta`, and recommend namespaced capability keys. Keep the existing
shared method name and top-level `_meta.steering` placement for interoperability; do not move
them into `agentCapabilities._meta` or introduce a Gorynych alias. The `gorynych/*` keys below
are this project's existing extension namespace, not ACP-defined fields. Custom error details
use JSON-RPC `error.data`; the standard Error object has no `_meta` field.

- **Capability.** `initialize` result, top-level `_meta` (next to `agentCapabilities`, not
  inside it): `"steering": {"supported": true}`.
- **Method.** `_session/steering`, a request:
  `{sessionId, prompt: ContentBlock[], _meta?: {steering?: {idleBehavior?: "promptRequired"}}}`.
  `prompt` is non-empty and takes the same blocks as `session/prompt`, restricted to the
  negotiated prompt capabilities. Steering does not enable additional content types.
- **Result.** One of:
  - `{"outcome": "injected"}`: accepted for the running turn. This acknowledges the adapter's
    input, not that the model has already read or answered it. Its output streams as that
    turn's `session/update`s. Gorynych's turn-completion rules are specified below.
  - `{"outcome": "promptRequired", "reason": "noRunningTurn"}`: no turn runs, nothing was
    delivered; the client sends the content as a `session/prompt`. Only when the request opted in
    with `idleBehavior: "promptRequired"`.
  - `{"outcome": "startedNewTurn"}`: no turn ran, so the agent started one with the message
    (without the opt-in: the legacy default). No `session/prompt` waits for that turn.
- **Reading an outcome.** `injected` and `startedNewTurn` confirm acceptance. `promptRequired`
  confirms non-delivery. Kimi's proposed `noActiveTurn` is a compatibility spelling of
  `promptRequired`, not another public Gorynych outcome. An unknown value, no `outcome`, or a
  lost response does not confirm either delivery or non-delivery. In particular, do not turn
  an unrecognized `failed` result into a safe retry without a verified adapter contract.
  Never infer support by probing the method: some adapters answer an unknown extension with
  an empty success, whereas ACP says an unknown method should return `-32601`.
- **Delivery priority is the agent's.** The wire carries no "now" or "later". Gorynych's adapters
  preserve running tools. Gorynych's own Claude adapter also avoids cutting off generation
  ([Claude](#claude-headless)); external adapters choose their own safe point. This is a
  delivery policy, not an extra wire option or a guarantee of the shared extension.

### ACP alignment and a future v2 face

The public v1 wire remains the shared extension above. The following boundaries are deliberate:

| Concern | Upstream basis | Gorynych v1 decision |
| --- | --- | --- |
| Turn completion | v1 `session/prompt` returns a stop reason at the end | Keep it open across accepted steers; do not adopt v2's insertion response on a v1 connection. |
| Message identity | v1 message chunks may carry `messageId` | Reuse that standard field, with existing `_meta` correlation for the resumable link. |
| Unknown delivery | v2 prompt lifecycle explicitly distinguishes a lost response from rejection | Keep the result uncertain; neither replay absence nor a matching text proves non-delivery. |
| Safe retries | [v2 Idempotency Keys](https://agentclientprotocol.com/rfds/v2/idempotency-keys), Draft introduced 13 November 2026 | Do not backport a root-level `idempotencyKey` into v1 or invent another public retry protocol. `promptId` is correlation, not an idempotency key. |
| Host recovery | ACP's [proxy-chains RFD](https://agentclientprotocol.com/rfds/proxy-chains) describes conductors, not durable delivery receipts | Extend Gorynych's existing internal host recovery, not the shared steering response. |

A future v2 implementation must follow the then-current schema. The current
[#1261 proposal text](https://github.com/kennethsinder/agent-client-protocol/blob/rfd/session-inject/docs/rfds/v2/prompt-delivery.mdx)
requires a separate mapping, not a method rename:

- Advertise `capabilities.session.prompt.delivery.steer`; return the inserted `messageId` and
  emit the corresponding `user_message`. Foreground completion uses `state_update`.
- An idle steer starts work normally. If the old turn ends before insertion, waiting steers
  start the next turn in receipt order. There is no `promptRequired` response.
- `session/cancel` stops current work but preserves pending prompts; `$/cancel_request`
  withdraws a prompt before insertion. Gorynych v1's cancel-and-drop policy below differs.
- Steering does not answer permissions or elicitation. Pending user input remains pending.

The pending-prompt rule in #1261 also differs from the **published v2 Draft**:
its [current `session/cancel` schema](https://agentclientprotocol.com/protocol/v2/schema#sessioncancel)
cancels foreground work and prompts not yet inserted. Do not treat #1261 as the current v2
contract or silently change the v1 cancellation policy to match that proposal.

These are migration constraints from an open proposal, not capabilities advertised by this
work. In particular, do not forward v2's `delivery` as an undeclared root field on v1 requests.

## Who can steer

Native versions below are the repository's probe baselines, not claims about the latest
releases. Runtime capability negotiation remains authoritative; the Kimi proposal was still
open at the ACP review above.

| Agent | Natively | In Gorynych |
| --- | --- | --- |
| Claude, headless | user message on stdin during a turn joins it after the current tool (default priority; [claude-headless-spec.md](../research/claude-headless-spec.md)) | built |
| Claude, TUI | the TUI queues typed input | no (`gorynych/steering: false`); not planned |
| Codex | `turn/steer {threadId, expectedTurnId, input, clientUserMessageId}` → `{turnId}` | built |
| Kimi 2.5.2 | `Session.steer()` in its engine; `kimi acp` refuses a second prompt with `-32600` and has no steering method | when `kimi acp` advertises `steering.supported` (kimi-code#2374, open since July) |
| DeepSeek Harness 0.2.2-rc.1 | `agent.steer()` in its engine, taken at the next step boundary; `dsh-acp` refuses a second prompt and has no extension methods | no; waiting for an upstream ACP steering surface |

## The service

### What a client sees

- **Capability.** The service's `initialize` result has `_meta.steering.supported: true`: it
  takes `_session/steering` for every session, and says per session whether that session's agent
  can be steered.
- **Per session.** The `_meta` of the `session/new`, `session/load` and `session/resume` results
  has `gorynych/steering`: `true` when the session's agent steers, else `false`. It is read when
  the session opens; an update of the adapter that changes it applies from the next open.
- **A session whose agent does not steer.** `_session/steering` fails with `-32600` and
  `data: {"gorynych/steerRefused": "notSupported"}`. The client keeps the message for the turn's
  end. The service never falls back to a cancel.

### What the conductor does with `_session/steering`

1. During a restart: refused with `gorynych/restarting`, like a prompt
   ([The resumable link](service-protocol.md#the-resumable-link)); a client on the link holds it
   and sends it again. This is an explicit refusal before forwarding, not a lost response.
2. The session is opened for use as for any request, but its turn gate is **not** waited for:
   that is the point.
3. The agent does not steer: `notSupported` (above).
4. Otherwise the request goes to the adapter, always with `idleBehavior: "promptRequired"`, so
   that no adapter ever starts a turn the conductor does not know about, and with
   `_meta["gorynych/messageId"]`, a fresh service message ID (a GUID) under which the message is
   journaled and kept natively (Codex's `clientUserMessageId`). It is sent under the session's
   forward gate, which `session/prompt` and `session/cancel` hold only while they are written
   (not for the whole turn). At the adapter's ingress, register the prompt and order steering
   and cancels before asynchronous handlers can overtake each other. The transport's write
   order alone is insufficient: the current RPC dispatcher runs request handlers separately.
   Use its existing arrival/sequence tracking; do not serialize the whole prompt response or
   block the reader while a steer waits for native startup. A client's prompt still waiting for
   the turn gate (behind an opinion turn, say) has not been sent: a steer meanwhile joins the
   turn that runs.
5. **`injected`.** The conductor journals the message as a `user_message_chunk` with that
   `messageId`, `_meta["gorynych/steered"]: true`, and the client's `gorynych/promptId` when it
   gave one (so a client on the link recognizes its own text in a replay), publishes it to the
   session's other clients (not to the sender, as with a prompt), and answers `injected`. The
   adapter does not echo the message itself. A GUI can draw a steered message inside the turn,
   apart from a prompt that starts one.
6. **`promptRequired`** (the turn ended meanwhile, or none ran): if the client opted in, the
   conductor answers `promptRequired` and journals nothing. If it did not (a client following the
   legacy default), the conductor starts the turn itself, as it does for a spawned session's
   first prompt: the message is journaled as that turn's prompt with the client's prompt ID
   if supplied, otherwise `service-<messageId>`; the turn waits for the turn gate like any prompt,
   its end is marked, and the answer is `startedNewTurn` once the turn is handed to the agent.
7. **A definite refusal** goes back as an error; nothing is journaled. A connection failure,
   malformed response, or failure after possible acceptance is an **uncertain result**: do not
   claim non-delivery or submit the content again. Recover confirmed acceptance as below;
   otherwise show the uncertainty to the client. Do not add an `uncertain` outcome to the
   shared wire protocol: this is local request/error handling.

The adapter decides whether a turn runs; the conductor does not guess from its own gate, since
the turn may end while the request is on its way. Every kind of turn can be steered: a client's
prompt, a turn of the agent's own, and a turn the service started (a spawned child's work, a
report, an opinion request; for an opinion the answer is still the agent's `reply`).

With [adapter hosts](service-protocol.md#adapter-hosts) the request goes through the session's
host to its adapter like a prompt; a turn that lives on in a host across a restart can be
steered once the next service has taken that host back. This requires more than forwarding:

- Before returning `injected`, the host retains an acceptance record in its existing
  numbered recovery buffer: session ID, service message ID, client prompt ID if present,
  content, and outcome. Retain it while unacknowledged, with the existing bounded-buffer
  overflow/failure policy; do not silently discard an acceptance and continue as if known.
  A conductor-owned legacy idle fallback uses the ordinary prompt's recovery path instead.
- A replacement conductor consumes it into the normal journal using the existing host sequence
  cursor. A record and a response for the same acceptance journal the message once. Accepted
  content must be journaled before the end marker of the ACP turn it joined; a fast native
  result must not put the message after that marker. This needs an explicit ordering test.
- This is internal host recovery, analogous to the existing turn-end records, not a new public
  ACP receipt or an exactly-once guarantee across a host/agent crash. If the host dies after
  native acceptance but before recording it, the result can still be unknown.
- The resumable link may resolve a pending steer from a correlated journal entry or explicit
  refusal. Absence from replay is inconclusive (journals are bounded). It never blindly resends
  a steer whose outcome is unknown, and never resends an accepted one just because its reply
  was lost. Existing `gorynych/promptId` correlates the message; it does not start or name a
  second ACP turn, enter `gorynych/prompts`, or require its own turn-end marker when injected.

The host report's concrete internal encoding is in
[service-protocol.md](service-protocol.md#adapter-hosts): a numbered
`_gorynych/steer_accepted {sessionId, messageId, promptId?, origin, prompt, outcome}` notification
in the existing replay buffer, written before the steering response. Reuse the existing host
framing, numbering, acknowledgements, and replay; there is no additional client-facing recovery
API in this work.

Agents do not steer each other: an agent reaches a running session through live notes
([adapter-contract.md](adapter-contract.md#64-the-live-note-path)), which stay as they are.

## The adapters

An adapter that steers advertises `_meta.steering.supported: true` at the top level of its
`initialize` result and takes `_session/steering` as specified [above](#the-protocol), with
these rules on top (they go into [adapter-contract.md](adapter-contract.md) when built):

- **A turn the adapter has accepted is running**, even before the agent has started it (a
  prompt still on its way in): the steer goes in after it, through the same ordered input, and
  answers `injected`.
- **Idle behavior follows the request.** With `idleBehavior: "promptRequired"`, no turn means
  `promptRequired` and no process launch or detached turn. Without that opt-in, an adapter
  exposing the shared extension to a direct ACP caller keeps its legacy `startedNewTurn`
  behavior and tracks the resulting work. The conductor always opts in, so its own fallback
  owns every idle start in Gorynych. Kimi's proposed unconditional idle refusal is a documented
  compatibility variant, not a rule for all adapters. An unexpected `startedNewTurn` despite
  the opt-in is still acceptance: report the contract violation and reconcile, never retry.
- **The prompt ends after native work settles.** Accepted steers stay attached to the owning
  ACP turn, whose prompt reports the native outcome. This does not promise a separate answer
  to each message or prove model consumption. If the agent closes its turn before it takes
  a steered message and takes it in a turn of its own right after, that turn is still the same
  ACP turn. Track the set of accepted steers attached to that ACP turn, including those not
  replayed yet: successful completion waits for all of them to be taken and for the native
  work that took them to end. Here "taken" means replayed/committed by the harness, not proof
  that a model request included it; see Codex's late-input case below. Cancellation or failure may end it sooner with the appropriate
  stop reason/error. Intermediate native results emit no ACP turn-end marker. Attach a steer
  and decide whether the ACP turn can settle atomically, so a concurrent completion cannot
  lose an accepted steer. The same rule covers an agent-initiated ACP turn.
- **No echo.** A steered message the agent replays or reports as a user message is not sent as a
  `user_message_chunk`: the conductor journaled it already (as with a client's prompt).
- **A cancel drops what the agent has not taken yet.** A steered message the cancel removes
  before the agent took it is removed from native pending input; the turn ends `cancelled`.
  Its already accepted, journaled user message stays in Gorynych history, including after
  reload. Acceptance does not prove model consumption. No per-message consumption status is
  promised in this version, and a generic turn cancellation alone does not prove which
  messages were read. This is Gorynych's v1 policy, not the proposed v2 pending-prompt policy.
- **Request cancellation is separate.** Use ACP's `$/cancel_request` for an outstanding
  request, not another steering-specific withdrawal method. Before native handoff, an own
  adapter can withdraw it and answer `-32800`. After committed acceptance it returns the normal
  steering result; cancelling the request or losing the client does not undo acceptance or
  cancel the whole turn. If handoff cannot be determined, preserve the uncertain result.
- **User action stays pending.** Steering neither grants/denies permission nor answers a
  question. Preserve pending permission/elicitation requests until answered or genuinely
  cancelled by the turn's lifecycle. A blocked tool may prevent the next safe point.
- **A turn that cannot take a message** answers `-32600` with
  `data: {"gorynych/steerRefused": "turnNotSteerable"}`, and the client keeps the message for the
  turn's end.

### Claude headless

- `_session/steering` writes one `user` message on stdin with `uuid` = the service's
  `messageId`, through the same input gate as prompts and live notes, at the default priority:
  Claude takes it after the current tool, in the same turn, and one `result` closes both
  (probed, [claude-headless-spec.md](../research/claude-headless-spec.md)). Not `now`, which ends
  the turn after the current tool, and not `later`, which waits for the turn's end. The official
  adapter normally picks `now` and switches to `later` while user input is pending
  ([implementation](https://github.com/agentclientprotocol/claude-agent-acp/blob/main/src/v1/acp-agent.ts));
  that SDK policy is not the wire contract. Keep Gorynych's probed default policy and verify
  that steering also preserves an open approval/question.
- "A turn runs" is what the adapter already tracks for prompts: the client's prompt written or
  waiting to be written, or a turn of Claude's own (`activeOwn`). A prompt still starting its
  process is registered too: steering follows its input write, or is refused/failed without
  delivery if startup fails. No accepted prompt or own turn: `promptRequired` on the conductor's
  opted-in path; it does not launch the process. The direct-call legacy fallback above uses the
  adapter's ordinary prompt/own-turn lifecycle.
- Track every steered `uuid` on the owning ACP turn. Replay marks it taken without emitting
  another user message. A `result` cannot successfully settle that ACP turn while an attached
  steer is still pending; a later native cycle remains part of it. Process exit, failed writes,
  a dropped message, and stalls resolve the pending state rather than leaving the prompt open.
- `session/cancel` sends `interrupt` with `cancel_queued` as today; steered messages in its
  `cancelled` list leave pending delivery (unlike live notes, which go back to the service),
  but their accepted journal entries remain. A cancel must work for an agent-initiated turn
  too; it cannot depend solely on the presence of a client's prompt waiter.

### Codex

- The conductor's opted-in path injects only while the adapter's turn is running. Before Codex
  has named it (`turn/start` is still on its way), the steer waits for its name **or** startup
  failure, turn completion/cancellation,
  session close/delete, or process exit. It cannot wait forever or attach to a later turn.
- `turn/steer {threadId, expectedTurnId: the running turn, input, clientUserMessageId: the
  service's messageId}`, `input` built as for `turn/start`. Success: `injected`.
- `expectedTurnId` no longer matches (the turn ended): `promptRequired`. The
  `activeTurnNotSteerable` error (`/review`, a manual `/compact`): `turnNotSteerable`.
  Classify these specific native refusals; do not translate every `-32600` into an idle race.
  The native path is documented in [Codex App Server](https://learn.chatgpt.com/docs/app-server#steer-an-active-turn).
- Codex's user message items are not echoed already ([adapters.md](adapters.md#prompts-and-turns-2)).
- `TurnSteerParams` and `TurnSteerResponse` come from `tools/Gorynych.CodexGen` (only the method
  name is generated today).
- The same call later gives Codex a live-note path: reports into a parent's running turn
  ([adapters.md](adapters.md#not-done-yet-1)). It is not part of this work.

**Source check (14 November 2026).** The repository's schema baseline, Codex `rust-v0.167.0`,
resolves to commit `086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a`. Its full TUI already uses the
same app-server path; no terminal-specific injection protocol is needed:

- [TUI submission](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/tui/src/app/user_turn_submission.rs#L7)
  calls `turn/steer` with the active turn ID and a client message ID. A definite no-active-turn
  refusal falls through to `turn/start`; non-steerable review/compact work is queued.
- [Core admission](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/core/src/session/turn_input.rs#L868)
  validates the turn under its lock, appends to turn-local pending input, and returns success.
  The RPC response therefore confirms queue admission, not a committed history item. The TUI
  keeps `pending_steers` until a [committed user-message event](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/tui/src/chatwidget.rs#L1327),
  correlated by `clientId` on current servers. Gorynych still suppresses the duplicate content
  echo, but may track that ID internally; a commit does not prove model consumption either.
- [Task interruption](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/core/src/tasks/mod.rs#L573)
  cancels/joins the active task and clears its pending input. The TUI's
  [interrupt handling](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/tui/src/chatwidget/input_restore.rs#L354)
  restores uncommitted text to the composer, or resubmits it in a new turn when the user's
  pending-steer interrupt action requested that. These are explicit TUI choices, not automatic
  behavior of `turn/interrupt`, and Gorynych's plain cancel does not resubmit accepted input.
- At normal completion, [leftover pending input is committed before the terminal event](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/core/src/tasks/mod.rs#L700),
  without another model call in that completion path. The upstream regression
  `task_finish_emits_turn_item_lifecycle_for_leftover_pending_user_input` covers this case.
  Thus even `end_turn` plus a committed steer does not guarantee the model answered that steer.
  Do not start an extra turn automatically to manufacture that stronger guarantee.

The TUI also has regressions `manual_interrupt_restores_pending_steers_to_composer` and
`esc_interrupt_sends_all_pending_steers_immediately_and_keeps_existing_draft` in
[review_mode.rs](https://github.com/openai/codex/blob/086ccc833bc9cf01024ad2dd6af7dbd421b6bf9a/codex-rs/tui/src/chatwidget/tests/review_mode.rs#L826).
This source review establishes native semantics; local integration tests still need to check
Gorynych's mapping, journal and recovery. The upstream Rust tests were read, not run here.

### Kimi and DeepSeek Harness

- **Kimi** is `kimi acp` as is, so it steers when its own `initialize` says so; the service then
  forwards `_session/steering` to it, reading `noActiveTurn` as `promptRequired`. Until then
  `gorynych/steering: false`.
- **DeepSeek Harness** runs behind the DSH proxy, which advertises nothing: `false`. Its engine
  could steer through its own session API, but that would be a second protocol next to ACP; we
  wait for `dsh-acp` to take the extension.

**DSH source check (14 November 2026).** Inspected `dsh-v0.2.2-rc.1`
(`66491b05e3f05ad35bcdd4f6cd7ed26041d9e3db`) and master
(`39163dd30f703a79079e2d2446545cff3c453738`). The ACP method-registration file is identical
at those revisions and has no steering handler/capability. The missing piece is the ACP bridge,
not native next-step input:

- [The driver](https://github.com/deepseek-ai/deepseek-harness/blob/39163dd30f703a79079e2d2446545cff3c453738/packages/core/agent-loop/src/agent.ts#L158)
  sends `steer()` to `next-step` with wakeup; default `cancel()` clears pending inbox input.
- [The cancel regression](https://github.com/deepseek-ai/deepseek-harness/blob/39163dd30f703a79079e2d2446545cff3c453738/packages/core/agent-loop/tests/cancel.spec.ts#L898)
  checks that an unclaimed steer is discarded without starting another turn. The release also
  tests steering from `agent/turn-stopping`, continuing the same turn.
- Native steering wakes an idle agent, and input after an abort is routed to a later turn.
  A future ACP extension must implement `idleBehavior` and guard the cancellation/admission
  race, rather than blindly invoking `agent.steer()` after asynchronous content conversion.

The current [ACP bridge](https://github.com/deepseek-ai/deepseek-harness/blob/39163dd30f703a79079e2d2446545cff3c453738/packages/acp/acp/src/index.ts#L377)
owns its session map inside the DSH process. Gorynych's out-of-process C# proxy cannot call
that Agent API by rewriting ACP frames. A focused upstream shared-extension implementation
is a reasonable next contribution, including content admission, message correlation, turn
settlement and cancel tests. A supported plugin extension point would also be acceptable if
established separately; do not assume the private bridge state is injectable. This review
does not enable DSH steering or introduce another transport, patched agent binary, or fork.
Upstream tests were inspected, not executed.

## The Zed fork

Stock Zed offers "Steer" on a queued message only for its own agent, where it ends the turn at
the next tool boundary and sends the message as a new turn; for an external agent it has only
the queue and "Send Now" (cancel, then send). The fork
([Phoenix-/zed](https://github.com/Phoenix-/zed), branch `gorynych`) adds steering for any ACP
agent that advertises the extension, so it works with Gorynych and with the official Claude
adapter alike.

- **Capability** (`crates/agent_servers/src/acp/v1.rs`): read the top-level
  `_meta.steering.supported` of the `initialize` result, next to the fork's other advertised
  extensions, and `gorynych/steering` from the `_meta` of `session/new`, `session/load` and
  `session/resume` results (absent: as the connection says; `false`: off for that session).
- **Connection** (`crates/acp_thread/src/connection.rs`): `AgentConnection` gets whether a
  session steers and a `steer(session_id, content)` that sends `_session/steering` with
  `idleBehavior: "promptRequired"` and the queued message's `gorynych/promptId`, returning
  `Injected`, `PromptRequired` or an error (`startedNewTurn` is read as delivered).
- **UI** (`crates/agent_ui/src/conversation_view/thread_view.rs`, `message_queue.rs`): the
  "Steer" toggle on a queued message is shown for such a session too. For an external agent,
  turning it on while the agent works (or queueing a message with it on) sends the message at
  once with `steer`:
  - `Injected`: the message leaves the queue and appears in the thread as a user message at the
    current point of the running turn (a new `AcpThread` method that adds a user message without
    starting a turn); the turn goes on.
  - `PromptRequired`: the turn has just ended; the message is sent as an ordinary prompt, first
    in line. Reserve that queue entry while steering is in flight: an ordinary turn-end handler
    must not send it too. Completion and the steering response can arrive in either order.
  - A definite refusal: the message goes back to the queue with "Steer" off, and the error is
    shown. Ordinary queue delivery is allowed after that refusal.
  - An uncertain result: keep the text visible with a delivery-unknown error, outside automatic
    queue delivery. Reconcile by the link's correlated replay if available. Do not resend it
    automatically or infer delivery by text matching.
- "Send Now" keeps Zed's meaning (cancel, then send). The fork's own agent keeps its existing
  "Steer" behaviour.

## The AHP face

AHP has steering in its state model: `chat/pendingMessageSet` with `kind: "steering"` (one
steering message per chat, replaced on each set) and `chat/pendingMessageRemoved`, which the host
dispatches when it consumes the message. In step 2 of the [AHP face](ahp-face.md#step-2-working-in-sessions):

- A steering message while a turn runs is sent with `_session/steering` (opted in).
  `injected`: the face removes it (`chat/pendingMessageRemoved`) and shows it in the running turn.
  `promptRequired`: it becomes the next turn, as a queued message. Not supported or refused: it
  stays pending and is sent when the turn ends.
- An uncertain result is shown as an error without automatic submission. An in-flight steering
  entry cannot also be flushed by the ordinary queue. If the AHP pending slot is replaced while
  delivery is in flight, its old request's result must not remove or submit the replacement;
  retain the identity of the entry that was sent. Replacing a slot does not retract an already
  accepted steer.
- Whether VS Code's Agents window sends steering messages to a remote host is checked in that
  step's live check before this is built.

## Building it

1. **The conductor, host and link — built.** The capability, `gorynych/steering`, input
   registration, forwarding, acceptance recovery and uncertain-result handling. Focused fake
   adapter tests cover the shared request/result shape, unsupported content, invalid parameters,
   no capability, `promptRequired`, the conductor's legacy idle fallback, `notSupported`, a native
   turn end racing the steer answer, a lost response after acceptance, a restart between adapter
   acceptance and conductor journaling, and duplicate/late acceptance records. The resumable link
   resolves an accepted steer from the correlated replay and answers uncertainty without resending.
2. **Claude and Codex — built.** Fake Claude covers one native result for both messages, several
   steers, an intermediate native result followed by the cycle that takes pending steers, an own
   turn, process startup failure, own-turn cancel, and steering while an approval/question remains
   open. Parent review added regressions for terminal-before-acceptance host replay across
   restart, agent-owned turn-end ordering, cancellation of a queued legacy fallback, cancelled
   later Claude cycles, lost steers after an intermediate result, and reconnect after an
   already answered steer. Fake Codex covers generated `turn/steer` types, late turn naming,
   turn identity, the two native refusals, and close/exit while waiting.
   `SteeringLiveTests.ClaudeNativeSteer_PreservesTheRunningTool` and
   `CodexNativeSteer_PreservesTheRunningTool` passed against Claude 2.1.302 (the user's configured
   API route) and Codex 0.167.0 on 14 November 2026: the actual command created its start marker
   before steering, completed with its output marker, and the final answer included the steered
   instruction. The earlier Claude startup blocker was Gorynych's subscription-only policy,
   now removed. Harness authentication and billing are outside Gorynych's scope.
3. **The Zed fork — planned.** The capability, connection method and toggle. Check turn-end queue
   races, definite refusals and uncertain results. Live checks through Gorynych with Claude and
   Codex: steer taken at a safe point, continued work, one user message, and still one after reload.
4. **The AHP face — planned**, with its step 2, including pending-slot replacement during delivery.

Steps 1 and 2 are documented as they land: the extension in
[service-protocol.md](service-protocol.md#extensions), the adapter rules in
[adapter-contract.md](adapter-contract.md), each adapter in [adapters.md](adapters.md).

## Open questions

- **Where a steered message stands in the history.** The conductor journals it when the agent
  accepts it, which can be a tool call before the agent actually takes it. Accepted as close
  enough; the alternative (the agent's echo at the moment it takes it) needs an echo every agent
  gives, and Kimi's is unknown.
- **Per-message consumption status.** Accepted messages remain in history even if native cancel
  drops them. A future UI could mark a reliably reported drop; this work does not invent a
  cross-agent read/consumed receipt or claim all accepted messages reached the model.
- **Claude's TUI transport** could steer by typing into the PTY mid-turn. Not planned while
  headless is the default.
