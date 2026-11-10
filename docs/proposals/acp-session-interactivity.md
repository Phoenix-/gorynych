# Session interactivity (draft RFD)

> A draft in the form of an ACP RFD, written for Gorynych. It has not been sent to the ACP project;
> it goes there only with the user's consent. Gorynych implements it today under `_meta`
> ([service-protocol.md](../service-protocol.md#read-only-sessions)).

- Author(s): Mikhail Kazakov
- Champion: (none yet)

## Elevator pitch

> What are you proposing to change?

Let an Agent tell the Client whether the user can prompt a session right now: an `interactivity`
of `"full"` (the default) or `"read_only"`, with a short reason for the user. The Client can then
show the session's history but turn its input off, say why, and turn it back on when the agent
says so. It goes on the session's summary (`SessionInfo`), on the answers to `session/load` and
`session/resume`, and in `session_info_update`.

## Status quo

> How do things work today and what problems does this cause? Why would we change things?

A Client learns about a session from `session/list`, `session/load`, `session/resume` and
`session_info_update`. None of them says whether the session can take a prompt. So a Client always
shows an input box, and the user finds out only when the prompt fails.

There are sessions an Agent can show but not continue now:

- **Held elsewhere.** Some harnesses let one process write a session at a time. Codex lets one
  writer process hold a thread. A thread open in Codex Desktop or in a terminal cannot be continued
  by another app-server until it is closed there, but its history can still be read. An Agent that
  wraps such a harness has two choices today. It can refuse the load: the Client shows "failed to
  launch" for a session whose history is right there. Or it can load it and then fail every prompt,
  which looks broken.
- **Unreachable.** A proxy or conductor that runs sessions on other machines can still show a
  session whose machine is down, from what it recorded, but cannot send it a prompt.
- **Not the user's to drive.** A subagent's session is watched, not prompted. The Subagents RFD
  already says "The Client cannot prompt, queue, or steer it" and disables client-initiated methods
  that modify a child session. That covers children, not a top-level session, and it gives no
  reason the Client could show.

Neither the error codes nor `notice` fill the gap. A refused prompt says so only after the user
typed. A notice is a live event, not state: a Client that loads the session later never sees it,
and nothing says when the restriction ends.

The Agent Host Protocol used by VS Code has this already: `ChatInteractivity` on `ChatState` and
`ChatSummary`, with `full` (the default when absent), `read-only` and `hidden`.

## What we propose to do about it

> What are you proposing to improve the situation?

A new optional field on the session, set by the Agent:

```json
"interactivity": "read_only",
"interactivityReason": "This thread is open in Codex Desktop. Close it there to write here."
```

- `interactivity`: `"full"` (the user can prompt the session) or `"read_only"` (the user can watch
  it but not prompt it). Omitted means `"full"`, so existing Agents and Clients are unaffected.
  The set of values is open, as `NoticeSeverity`'s is: a Client treats a value it does not know as
  `"read_only"`, and values starting with `_` are reserved for extensions.
- `interactivityReason`: optional plain text for the user, saying why and, where possible, what
  the user can do. Meaningful only when `interactivity` is not `"full"`.

Where it appears:

- `SessionInfo` in `session/list`, so a list can mark such sessions before any is opened;
- the responses of `session/load` and `session/resume`, which say the value even when it is
  `"full"`, since a Client may remember an older one;
- `session_info_update`, whenever it changes. An update that changes it MUST carry the value, also
  `"full"` when the restriction ends, since that is how a Client learns to turn its input back on.
  The field has patch semantics like the update's other fields: omitted leaves it as it was.

What it means for the protocol:

- A Client SHOULD NOT send `session/prompt` to a session whose `interactivity` is not `"full"`. It
  SHOULD show the session's history and the reason.
- An Agent MUST still accept such a prompt as a request and answer it. It MAY run it, if the
  session can be taken by now. Otherwise it fails it with `-32600` and a message that says why, and
  it sends an update if the value changed.
- `session/load` and `session/resume` of such a session SHOULD succeed and replay its history; the
  restriction is about prompting, not reading.
- Notices fit next to it, as an extra: an Agent that wants to tell the user when the state changes
  MAY send a `notice` (`warning` when it becomes read-only, `info` when it can be written again),
  under the Session Notices rules (`session.notices`). The state lives in `interactivity`, the
  event in the notice.

## Shiny future

> How will things play out once this feature exists?

A user opens a Codex thread they left open in Codex Desktop. The Client shows its full current
history, with the input greyed out and a line under it: "This thread is open in Codex Desktop.
Close it there to write here." They close it there; a moment later the input comes back, and their
next prompt continues the thread with nothing lost or doubled.

Conductors that span machines show a session on a machine that went to sleep the same way, with
its own reason. A Client that renders subagents can use the same field and drawing for a child
session, and the Subagents RFD can refer to it instead of describing the restriction on its own.

## Implementation details and plan

> Tell me more about your implementation. What is your detailed implementation plan?

Schema, v1 and v2:

```ts
type SessionInteractivity = "full" | "read_only" | string; // open set; "_"-prefixed for extensions

interface SessionInfo {
  // ...
  interactivity?: SessionInteractivity; // absent: "full"
  interactivityReason?: string | null;
}

interface LoadSessionResponse { /* ... */ interactivity?: SessionInteractivity; interactivityReason?: string | null }
interface ResumeSessionResponse { /* ... */ interactivity?: SessionInteractivity; interactivityReason?: string | null }

interface SessionInfoUpdate {
  sessionUpdate: "session_info_update";
  // ...
  interactivity?: SessionInteractivity;
  interactivityReason?: string | null;
}
```

No capability: a Client that does not know the field ignores it and gets a refused prompt, as
today. An Agent that never sets it changes nothing.

Prior implementation: Gorynych, an ACP conductor, sends the same thing today as
`_meta["gorynych/interactivity"]` (`"full"` | `"read-only"`, AHP's spelling) and
`_meta["gorynych/interactivityReason"]`. Its first source is a Codex thread held by another Codex
app, whose history it reads without taking the thread. A fork of Zed turns the input off for such
sessions. Moving to the standard field would rename the value to `"read_only"`, in line with the snake_case
of ACP's enum values (`end_turn`, `allow_once`); AHP spells it `read-only`.

Plan: land it behind `unstable_session_interactivity`, with Gorynych as the first Agent-side
implementation, and a Client one in Zed.

## Frequently asked questions

> What questions have arisen over the course of authoring this document or during subsequent discussions?

### Why not just fail `session/load`?

Because the history is readable and is what the user wants to see. A failed load shows nothing,
and it tells the Client nothing it could act on later.

### Why not only a notice?

A notice is an event: Clients that attach later, or that did not advertise `session.notices`,
never see it, and nothing says when the restriction ends. The state needs a field; a notice can
announce changes to it.

### Why not reuse the Subagents RFD's restriction?

It is about children and the methods a Client may call on them, without a reason to show. This is
a property any session can have for a while, top-level ones included, and it ends. The two fit
together: a subagent's session could say `"read_only"` with a reason like "Run by its parent
session".

### Why not AHP's `hidden`?

`hidden` is for worker chats a client should not list at all. ACP's lists do not need it for this
case; it can be added later as another value, since the set is open.

### Why `read_only` and not AHP's `read-only`?

ACP spells its enum values in snake_case (`end_turn`, `allow_once`, `agent_message_chunk`). The
meaning is AHP's, so a bridge between the two maps one to the other.

### Is `read_only` a permission or security boundary?

No. It describes what the Agent can do with a prompt now; the Agent still enforces it by refusing.

## Revision history

- 2026-11-10: First draft, written alongside Gorynych's implementation under `_meta`.
