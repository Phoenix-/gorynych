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

Snapshots carry `background`, a complete live snapshot modeled on ACP v2's
background references. Missing or null means unreported or unavailable; an empty
array means confirmed empty. A malformed array is unknown in its entirety. Known
references use `{"type":"terminal","terminalId":"..."}`,
`{"type":"tool_call","toolCallId":"..."}`, or
`{"type":"subagent","sessionId":"..."}`. IDs are nonempty, stable within the
session and must name the corresponding native or ACP entity; type-specific IDs
must not repeat. Future or implementation-specific types remain valid and count as
work; implementation-specific details belong in `_meta`. The extension does not
itself promise that native references have been announced as ACP entities.

An adapter that observes only a subset cannot report that subset as a complete
background snapshot. It can instead send `_meta["gorynych/backgroundKnown"]`
with the same reference format. The service carries it separately as
`backgroundKnown`: null means unavailable, `[]` means that observed subset is
empty, and a nonempty array names at least that much work. It never makes
`background` known. An observed nonempty subset pins the session; an unknown
observation retains that pin and a confirmed empty subset releases it. Foreground `idle` and background work may coexist. Clients
must not infer that the agent will resume responding when a task ends.

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
The host also retains whether the session was pinned by a prior nonempty snapshot.
If the latest observation is unknown, reconnect restores that pin without presenting
the old tasks as confirmed live. A lost adapter connection is not proof that its
native process exited. Adapters must not put live activity metadata in history replay.

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

`revision` is a nonnegative integer increasing on each foreground or background
snapshot change for that session within an `epoch`. Repeated identical state is idempotent. An epoch is
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
* [ACP v2 background work schema](https://github.com/agentclientprotocol/agent-client-protocol/blob/main/schema/v2/schema.json)
* [Claude SDK background snapshots](https://code.claude.com/docs/en/agent-sdk/typescript#sdkbackgroundtaskschangedmessage)

## Opt-in native smoke

On Windows, from the repository root, `python tests/NativeSmoke/claude-background.py`
and `python tests/NativeSmoke/codex-background.py` each start one isolated native
session, request a short background sleep with a bounded model turn, and wait for a
nonempty snapshot followed by an empty one. They use the user's native authentication,
disable MCP for the test, and do not change its configuration or install anything.
Claude uses no session persistence; Codex uses an ephemeral thread. Both terminate
their owned app-server process on completion or timeout. These tests consume model
usage and require native CLI access and a working account. A runtime may choose not
to start background work, in which case the smoke fails rather than inventing proof.
