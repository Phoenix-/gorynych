# Live session activity (ACP v1 extension)

ACP v1 describes work through a pending `session/prompt`, which cannot describe a
turn another client or the service starts. This extension uses ACP v1's `_meta`
and custom-notification extension points, with the aggregate foreground semantics
of ACP v2's `state_update`. It does not change v1 prompt responses or negotiate v2.

## Wire contract

The service advertises `agentCapabilities._meta["gorynych/sessionState"]: 1`.
Each `session/list` entry and successful `session/new`, `session/load` and
`session/resume` response has this current snapshot in `_meta["gorynych/sessionState"]`:

```json
{"state": "running", "epoch": "opaque-process-id", "revision": 12}
```

The service sends `_gorynych/session_state` notifications to initialized clients
whose agent scope covers the session, including clients that have not loaded its
conversation. Parameters are the same snapshot plus `sessionId`:

```json
{"sessionId": "session-id", "state": "idle", "epoch": "opaque-process-id", "revision": 13}
```

Adapter hosts retain authoritative native and agent-initiated activity separately
from their bounded replay buffer. On reattachment they publish a current
`sessionActivities` snapshot through the existing host-state notification, so
evicting an old start event cannot turn ongoing work into idle after a restart.

These notifications are live observations, not journal entries. Listing state
does not load an agent or refresh a session's last-interaction time.

* `running`: a prompt, service turn, or agent-initiated foreground workflow is active.
* `idle`: no such foreground work remains. It does not promise an absent process or
  an absence of detached background tasks.
* `requires_action`: the adapter explicitly reports that the native foreground
  workflow is blocked on user action. A pending permission or form alone is not
  sufficient evidence: other foreground work may continue.

The service aggregates its active work and native observations. Finishing one
prompt does not clear another prompt, a service turn, or native work. Codex's
native active flags provide blocked-state observations; adapters without an
authoritative whole-workflow signal continue reporting `running` while their
permission cards remain available through ACP's ordinary interaction APIs.

## Reconciliation

`revision` is a nonnegative integer increasing on each state transition for that
session within an `epoch`. Repeated identical state is idempotent. An epoch is
opaque and changes with the conductor process; it is not a timestamp or an ordered
version. Compare revisions only within one epoch. In-flight list/open responses
must not overwrite a more recent live notification from that epoch.

The client invalidates confirmed state on transport loss and obtains fresh
snapshots on reconnect. It must not turn a disconnect, unknown state, malformed
snapshot, or missing snapshot into confirmed `idle`. A bridge may reconnect its
service transport while preserving its client stdio connection; the next live
epoch replaces the old epoch, and late responses from retired epochs must not
restore it. History replay must never be interpreted as current activity.

Clients use this source only when the capability is advertised. Without it they
keep ordinary ACP v1 behavior. The UI should distinguish running, waiting for user
action, idle, and unconfirmed state, including on unopened imported session rows.

## References

* [ACP v1 extensibility](https://agentclientprotocol.com/protocol/v1/extensibility)
* [ACP v2 foreground lifecycle](https://agentclientprotocol.com/protocol/v2/prompt-lifecycle)
