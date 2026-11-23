# Inter-session messages

New managed sessions use `send_message {session_id,text}` to start an exchange and
`reply_message {message_id,text}` to reply. Reviews, clarifications and notifications share
this interface. There is no separate opinion tool, mandatory acknowledgement or internal
waiting tool. `list_agents`, spawn, archive and restore remain available.

`delegate_task {session_id,text}` continues an existing user-authorized assignment with an
immediate child. The service authenticates the caller and checks the stored parent link before
opening or delivering to the recipient. Other relationships use `send_message`. The child gets
a service-generated delegation envelope and remains bound by user restrictions and existing
approval and mode checks. Each accepted call returns `messageId`, `assignmentId`, and `status`;
the first two currently have the same opaque value. Spawn returns `assignment_id` as well;
its first prompt and journal identify the same assignment. An assignment ID is not itself
completion, a wait, or permission to exceed the user's assignment. Each new explicit
delegation has its own ID; calls cannot attach to an existing assignment.

New delegation and spawn admissions retain bounded assignment records independently of
ordinary message history. Pending and uncertain deliveries and open assignments are pinned;
terminal resolved records can be evicted. Retry identity lasts while the message or assignment
record is retained, and cross-kind reuse is rejected. No historical assignments are inferred.

## Explicit assignment outcomes

`list_assignments {cursor?,limit?}` lists records where the calling session is parent or child;
`read_assignment {source,assignment_id}` reads one. Source is required and is `spawn` or
`delegation`: the same ID may occur in both stores, and a call never falls back to the other.
The listing uses an opaque participant-scoped keyset cursor, a default limit of 50 and maximum
100. Inserts before the cursor are not included in later pages; eviction does not invalidate it.

`update_assignment {source,assignment_id,generation,expected_revision,operation_id,outcome,summary?,report_id?}`
records an explicit outcome. The child may use `completed`, `blocked`, `failed`, or `open`
(to clear a blocker); the parent may use `cancelled`. A bounded summary is required except
for `open`. Reuse the same operation ID and arguments for a retry. Each record retains the
latest 64 receipts; an evicted retry with an old revision is rejected. A completion can reference
a published retained report for exactly this source, assignment and participants. The summary
is sufficient without a report, and later report eviction does not undo the outcome.
Pass the exact service-generated `generation` returned by list or read: if an expired ID is
later reused, an old generation cannot mutate the new assignment or attach its old report.
Reports without matching generation, including historical reports, cannot be attached. A turn
ending or a report arriving does not complete an assignment. Cancellation before the durable
handoff claim suppresses that start; cancellation after claim never sends `session/cancel` and
does not erase delivery uncertainty.

Only authenticated session participants can use these methods. Parent cancellation is available;
there is currently no separate trusted user-control cancellation path. Absence of a peer token,
client-supplied role fields, or client identity strings do not grant that authority.

Send and reply return `{"messageId":"...","status":"accepted"}` after durable service
acceptance, without waiting for recipient work. Opening an unloaded recipient and required
full-access consent precede acceptance, so these steps can still take time or fail. After
acceptance, cancelling the sending call does not withdraw its message. A lost response is
uncertain; the shim retries with the same transport-generated request ID.

## Explicit reply expectations

Managed peers can use `request_reply {session_id,text}` when they explicitly need a
correlated response. Acceptance atomically records the ordinary message and a wait keyed
by its `messageId`; it does not wait for delivery or a response. The recipient replies with
`reply_message` and the first accepted reply atomically resolves an open wait. If already
cancelled, the wait stays cancelled while retaining the first reply identity for retries. `list_reply_waits
{cursor?,limit?}` pages metadata for either participant; `cancel_reply_wait {message_id}`
lets only the waiter cancel an open expectation. Cancellation does not retract the message
or prevent later ordinary replies. The states `waiting`, `replied` and `cancelled` describe
the expectation, separately from request and reply delivery disposition. An uncertain
handoff is not replayed merely because a wait remains open.

The mailbox retains at most 1000 wait records independently of its message history.
Open waits are never evicted; admission fails when all slots are open. Oldest terminal
waits are evicted first. While retained, the wait preserves retry fingerprints and reply
correlation even if message bodies expire. A retry after its wait has expired is rejected
if the original message is still retained; it does not recreate the expectation. Existing mailbox stores load without implicit
waits. These tools neither grant authority nor change mode or approval checks. Outside
agents cannot create or inspect waits; no scheduling or task-completion inference is implied.

## Targeted reply reminders

Automatic reminders are disabled unless the service data directory contains a valid
`reply-reminders.json` identifying one existing UI group with `projectKey` and `groupId`.
Optional integer `delaySeconds`, `overdueSeconds` and `sweepSeconds` default to 120,
600 and 30. The accepted ranges are 1–86400, 1–604800 and 1–3600 respectively.
The file is read at service startup; changing or removing it takes effect after a service
restart. Invalid or missing configuration and a missing group are shown in service status.
Only newly admitted waits whose two participants belong to the group receive a schedule;
older waits stay passive. Group membership is checked again before a delivery claim.

The review deadline starts at confirmed original delivery; overdue is measured from
admission. At most one reminder and one escalation notice are claimed per wait. Their
stable service message IDs and native delivery dispositions are retained with the wait.
A reply or cancellation before claim suppresses delivery; an already claimed turn can
finish. A started claim without a confirmed result is uncertain and is never replayed.
Overdue metadata remains visible to the two wait participants even when no notice can be
delivered, and a direct reciprocal open wait is escalated at review without alternating reminders.

Automatic delivery only uses a current already-open route, positive idle evidence from a
completed native response and live activity observed on that route, a known-empty complete
native background change stream confirmed by the current adapter via its opaque
`gorynych/backgroundCompleteStream` ID, no known nonempty partial background snapshot,
and a known recipient permission mode. Unknown
or stale activity, busy turns, pending approvals, read-only, archive,
maintenance and the existing full-access boundary defer delivery. While a claimed reminder
turn is running, target mode/config changes wait for its native response. If the target is
in full access, the source must have confirmed current-route evidence of full access and its
mode/config changes also wait to preserve the authority check;
ordinary mailbox acceptance and session cancellation remain available. Waits on older hosts
without the stream marker, or adapters exposing only partial background observations, remain
visible but are not auto-woken by this policy. A reminder is a service notice, not a user instruction, and the recipient
replies to the original message ID. Ordinary mailbox messages and reports are unchanged.

## Delivery and authority

This first implementation serializes delivery through ordinary ACP `session/prompt`
turns, including for adapters that support steering. Messages wait for an occupied turn;
they never cancel a tool or turn. Idle recipients are opened and started automatically.
Replies are new correlated messages and wake their original sender in the same way.
The message names its sender and ID, and a reply also names the original ID.

Fresh child reports direct the parent to `delegate_task` for further already authorized work
with that immediate child and to `send_message` for ordinary discussion. A child contacting its
parent continues to use `send_message`.

A peer message is not from the user and grants no new authority. Existing user constraints
and narrower message restrictions apply. A review request permits read-only investigation,
not edits. Both send and reply preserve the existing restricted-to-full-access approval
boundary. Delivery rechecks this boundary if modes changed while the message waited.
Outside sender addresses are private bearer identities and never appear in model-facing
messages or journal sender labels.

An agent with no independent work ends its turn; later messages resume it. Turn completion
does not declare task completion. Child reports still describe completed turns, which may
say that the child is awaiting replies. An acknowledgement need not be acknowledged again.

The [planned routing for peer turn results](delegation-and-task-lifecycle.md#planned-routing-for-peer-turn-results)
describes how a future version will route the final answer to the sender and use an exact
`reply_message` to suppress the automatic answer.

## Outside agents

Outside discovery retains `list_sessions` and `read_session` and exposes `send_message`,
`reply_message`, and `receive_messages {after?,limit?,wait_ms?}`. Outside agents have no
Gorynych-owned input channel and explicitly receive their replies:

```text
receive_messages {after: 0, limit: 50, wait_ms: 0}
receive_messages {after: <previous nextCursor>, wait_ms: 30000}
```

Defaults are `after:0`, `limit:50` (1–100), `wait_ms:0` (0–60000). Zero reads immediately;
a positive wait long-polls until a page arrives or its timeout expires. The result has
`messages`, `nextCursor` and `historyTrimmed`. Items contain IDs, sequence, sender, text and
optional `replyTo`; private mailbox addresses are omitted. Cancellation stops receiving,
not message delivery. Repeating the same cursor replays the same retained page. Advancing
the cursor acknowledges older messages, allowing their retention eviction. Generating a
response does not acknowledge the returned page, so a lost response is safe to retry.
`historyTrimmed` conservatively indicates that eviction may have passed the cursor.

A random private identity belongs to each outside shim process and survives service
reconnects. Different shims do not share an inbox by cwd or client name. Optional
`--mailbox-file <path>` saves the identity with private file permissions for shim restarts;
the parent directory must exist. Use a separate file per outside agent; deliberately
sharing the file shares the inbox. Without it, a shim restart starts a fresh mailbox.
Gorynych does not change harness configuration to enable this option. The shim supplies
identity in trusted outer metadata, never from model arguments. The same-user service pipe
remains the local trust boundary.

## Persistence and failure

Atomic `mailbox.json` in the service data folder retains at most 5000 entries of up to
32000 text characters. The oldest terminal entries are evicted to admit new messages;
pending entries are never evicted. A store full of pending messages refuses acceptance.
Deduplication and reply ownership are available while their entries remain in this bounded
history. A retained reply receipt can be retried even after its original was evicted.

A restart resumes messages queued before handoff. A durable started claim precedes writing
a native prompt and stays pending until the adapter answers. If a crash leaves only that
claim, delivery is reported as uncertain and is not blindly replayed. A failed delivery and
its notice to the sender commit together; notices do not generate recursive failure notices.
Acceptance is durable, but this is not an exactly-once model-processing guarantee. A turn
already handed to a surviving host continues under the existing host recovery mechanism.

## Wire and compatibility

ACP remains v1. Peer methods `_gorynych/send_message {sessionId,text,_meta}` and
`_gorynych/reply_message {messageId,text,_meta}` carry the peer token and a stable
`gorynych/messageRequestId` in `_meta`. Prompt updates use `gorynych/mailboxMessage`.
Outside tools retain `_gorynych/mcp_message`, with private `gorynych/outsideMailbox` and
write request IDs in its outer `_meta`. New custom data stays in ACP extension points.
`_gorynych/delegate_task` uses the same input and transport request ID as send; the service
adds `gorynych/assignmentId` to its prompt metadata. Existing mailbox delivery claims and
uncertain handoff behavior also apply to delegation.

This follows [ACP v1 extensibility](https://agentclientprotocol.com/protocol/v1/extensibility).
[MCP Tasks](https://modelcontextprotocol.io/extensions/tasks/overview) supplies asynchronous
operation handles, not mailbox routing. ACP v2 remains a draft, and its
[session-directed messages](https://agentclientprotocol.com/protocol/v2/prompt-lifecycle#session-directed-messages)
describe transcript presentation. This change upgrades neither protocol nor harnesses.

Legacy `ask_agent`/`reply` handlers and `_gorynych/opinion`/`_gorynych/reply` remain for
already running sessions and in-flight requests, but new discovery uses the new tools.
Channel-only legacy Claude servers still expose `reply`. New `reply_message` bridges legacy
service requests and Claude channel IDs, returning `status:"legacy_delivered",durable:false`
for direct legacy delivery; those old replies do not promise mailbox retry durability.
Legacy asks retain their synchronous waiting/reclaim semantics and nested-ask restriction.
New mailbox exchanges have no such restriction. Native sessions can retain cached old tools
until their next launch/resume. Codex preserves the developer instructions from thread
creation even after resume; new tool discovery and per-message envelopes carry the current
messaging contract, while new threads receive current creation instructions. Host rotation
does not rewrite historical native instructions. Updating an install is a separate explicit
deployment through the existing update command.

The 16 November 2026 Windows upgrade probe confirmed that an existing Codex thread moved to
the new host generation and discovered send/reply tools, while retaining its old creation
instructions. The installed outside shim sent it a message, long-polled, and received a
correlated reply produced by the thread's real `reply_message` call. The conductor restart
kept the running parent turn in its old host. A Codex draft can keep an old host alive until
its first turn or closure, as described in the existing host-generation lifecycle.


Managed child and intended-parent sessions can use `list_reports` to discover retained per-turn
report metadata and `read_report` with a report ID and continuation cursor to read exact bounded
UTF-8 pages. A report does not complete an assignment. Current automatic delivery still uses the
legacy route; reliable delivery receipts and the new bounded preview envelope are not yet wired.
