# Report storage primitives (iteration 2, part A)

The production conductor and configured adapter hosts use these internal primitives for eligible service-controlled child prompts. Managed retrieval and durable report delivery are connected; a report does not complete an assignment. Callers construct `ReportIdentity` from
trusted service/host state and authenticate a session before `Read`, `List` or `Preview`.
`Recoverable` is a trusted service-side inventory, not a peer API.

`Reserve` durably records a turn identity and allocates a report ID. An identical retained
turn key and identity return the same ID; a conflict fails. Identity includes child, intended
parent, typed cause, prompt ID and optional assignment/message IDs. The producer must retain
the returned ID across retries. Retained turn-key deduplication ends on eviction; never infer
a new reservation for an old acknowledged turn. A persistent random store prefix and monotonic
counter prevent IDs from being reused within the store, including after eviction. A missing or
invalid sequence file beside an existing manifest fails closed. `AbandonReservation` is only
for a definitely unstarted turn, requires its matching identity, and takes the producer lease.

`OpenCapture` acquires a stable per-producer slot lock, separate from the spool being replaced.
At most `pinnedLimit` slot lock files are created lazily and they are never deleted; ACK releases
the slot for reuse. `ReportCapture` appends and flushes checksummed frames and keeps exact final
text, including whitespace and surrogate pairs split across chunks. When frame overhead approaches
the limit it atomically compacts the candidate to a UTF-8 checkpoint of at most B bytes plus
bounded framing. A checkpoint leaves at least B bytes of journal headroom, so small chunks cannot
cause a full-body rewrite after each chunk. `ToolCall`
atomically replaces the spool with an empty candidate, so old pre-tool text does not accumulate.
A crash before or after replacement leaves one authoritative spool; incomplete/torn frames
fail explicitly. Oversize or invalid Unicode marks the current candidate incomplete; later chunks
are ignored until a tool boundary resets it. An absent final text and an explicit empty text differ.

`Seal(outcome, error)` stores the terminal outcome, optional error and completeness reason in the
same checksummed terminal frame. A reopened sealed spool retains these values before any import.
Outcome is limited to 128 UTF-8 bytes; error and incomplete reason to 512 UTF-8 bytes each.
Oversized terminal fields are rejected before seal; host integration must explicitly bound them
and mark any truncation before calling `Seal`.
`Commit` derives these values from the sealed capture rather than a retry argument; mismatched
sealed results for one ID conflict. A corrupt/missing spool can be recorded through
`CommitCaptureFailure`, which never claims to have complete text. Integration must use that path
only with trusted evidence and must distinguish an unknown outcome from a known native result.

The immutable UTF-8 body is staged outside the shared metadata lock and moved into place under it.
One atomic manifest publishes its digest, result metadata and `pending` outbox intent together.
A failed manifest write leaves a reservation and possibly a body candidate; retry is idempotent.
The manifest is reloaded under a cross-process lock for metadata operations and written COW;
no global lock is taken for capture chunks. Disposition updates do not rewrite body files.
`Recoverable` and `List` use store-sequence continuations scoped to recovery or the authenticated
principal, so ACK or eviction of a prior page does not invalidate the cursor. Paging is not a
snapshot: newer records can appear after the continuation. `AcknowledgeProducer` takes the
producer lease, persists ACK before removing the spool, and does not imply target consumption.
`SetDisposition` records pending, claimed, accepted, uncertain, completed and failed without
an uncertain-to-pending replay edge. A disposition is not a Claude/native delivery receipt.

Defaults: body limit B = 8 MiB UTF-8, aggregate 512 MiB, 5000 records, 1000 pinned. The spool
journal is bounded to `2*B + 8192` bytes (UTF-16 frames and overhead). During compaction the old
spool and new UTF-8 compacted candidate coexist, at most `(2*B + 8192) + (B + 589)` bytes. During
publication a spool plus a prior body candidate and a staged body use at most
`(2*B + 8192) + B + B`. `DataBudget = 4*B + 16384` covers both peaks, including terminal-frame
headroom. This bound assumes one exclusive producer per reservation and the provided capture API.

The manifest records the configured body, aggregate, record and pinned limits; a store with
mismatched limits fails closed before mutation. Quota accounting charges logical file lengths once
plus *unused* data headroom for each unacknowledged
producer; it reserves two bounded manifest images for COW publication and 2048 bytes for the ID
sequence. Each manifest image reserves, per entry, its serialized identity size plus 16384 bytes
of fixed metadata allowance (4096 more than the earlier storage-only allowance). Outcome/error/incomplete/disposition reason bounds make that allowance
finite; `Save` verifies the complete manifest fits its bound before writing. File sizes used for
both physical and unused-headroom calculations come from one snapshot, so concurrent capture
writes are not subtracted from a different physical snapshot. Filesystem inode/block allocation is outside these logical-byte figures. At default B the data reservation
alone permits at most 15 simultaneous unacknowledged producers under 512 MiB, before metadata and
existing files. This is conservative but no longer counts a producer's already-written spool
again as wholly unused reservation. Pinned pending/uncertain and unacknowledged records are not
evicted. Acknowledged completed/failed records have a soft 30-day TTL, evaluated on a subsequent
reservation, and can be evicted earlier under pressure. Eviction publishes metadata first, removes
files second; retry cleans unreferenced generated files. No quiet default capacity increase occurs.

`Read` authorizes only the stored parent or child, verifies the bounded body digest, and returns
4096 UTF-8 bytes by default (limit 4..16384) on scalar boundaries with a report-bound cursor.
`List` and `Recoverable` return metadata pages of at most 100. `Preview` returns at most 2000
UTF-16 code units without splitting a surrogate pair, plus truncation and capture completeness.
Each read currently verifies the entire bounded body, costing repeated I/O. Expired and unknown
IDs return not found. Lost or corrupt sequence/manifest/body data must not be treated as complete.

The target is process restart with closed/flushed files and same-directory atomic rename. No
cross-platform sudden power-loss guarantee is claimed. Cross-process crash/reconnect, quotas on
all supported filesystems, mismatched store configurations and host/adapter interleavings require
integration tests. Restart reconciliation handles retained captures and marks old hosts explicitly
incomplete. Delivery uses durable claims and confirmed note or prompt evidence; an unresolved handoff
remains uncertain. No storage status grants authority, guarantees consumption or completes an assignment.





If capture has already recorded a loss reason, `SealIncomplete` preserves that first
reason even when its caller supplies a later one. The bounded terminal outcome and error
are stored separately, so the later failure can still be reported. The supplied reason
is validated against the same limit. Replay rejects a terminal that changes a recorded loss.


B1 host admission opens only a matching reserved child/prompt and rejects any existing spool:
that spool requires recovery and must not trigger a second native prompt. Host releases its
producer lease after seal or a visible capture failure. Successful capture remains reserved and
sealed until a separate conductor import and producer ACK. A host reporting capture I/O failure
may leave an unsealed or corrupt spool, which must not be considered a complete result.

Host shutdown, agent EOF, and agent error release in-flight capture leases without sealing or
removing the spool. Admission, live updates, terminal seal and shutdown cleanup coordinate through
the host state lock; a stopped host does not continue a previously registered native dispatch.
History-producing load/resume/catch-up requests and captured prompts are mutually excluded per
session before dispatch, so history text or tool calls cannot be labeled complete live output.


B2 configures one `reports` directory under the service data directory in the conductor and
new adapter-host processes. The conductor checks the actual routed host link's initialized
capture capability before reserving. Eligible user, spawn and mailbox turns get a reservation
with service-derived cause and correlation before native prompt handoff; client report and
assignment provenance metadata are stripped. Report-only and opinion turns do not reserve.
An old host without the capability continues on the legacy reporting path. Its observed results
are stored with explicit capture incompleteness; restart reconciliation preserves uncertainty where
the old host cannot supply durable evidence.

On a supported normal path the host's terminal event drives single-flight import of the sealed
spool and its pending intent, then producer ACK, before the host cursor and prompt end marker
advance. The response path requires that import, including on native failure or cancellation.
Storage failure yields a separate persistence error and session notice, leaves the spool or
pending unacknowledged record for reconciliation, and does not cause another native prompt.
The durable outbox is scheduled after import and inventoried after restart or reattachment. Queued
turns and supported live notes use their respective correlated evidence before terminal disposition;
ambiguous handoff stays uncertain. Managed retrieval and status use the stored immutable identity.

B2 terminal replay checks retained durable identity by child and prompt even after the originating
caller completes. A matching ID is required before import/ACK or cursor advancement; missing,
unknown or mismatched correlation and storage faults fail closed. Once storage recovers, a
subsequent replay retries the same immutable result without a native prompt. This check is bounded
by retained report history; if an old retained prompt ID is reused by a legacy turn, ambiguous
missing correlation is refused rather than treated as proof of a legacy turn.


B3 records the actual producing host ID, generation fingerprint and, where known, process ID
and process start time in each new reservation. Old records load without these optional fields;
they are not retrospectively migrated. After terminal seal and producer lease release, the host
writes bounded producer outcome, error and seal/failure evidence to the manifest before sending
the terminal. Until that evidence is durable the reservation is `unconfirmed`; a valid-looking
sealed spool alone cannot erase a possibly lost host failure. If the producer has definitely exited in that narrow window and the sealed spool is valid,
recovery preserves its exact retained text, HasText and recorded native outcome/error, but marks
capture completeness uncertain with a separate `captureConfidenceReason`. An unsealed or corrupt
spool has no verified final outcome and is recorded as unknown/incomplete. On successful publication
the duplicate producer outcome/error/failure strings are removed, retaining bounded failure hash
and the immutable result's own terminal fields within the existing metadata budget.

Startup and host reattach inventory page retained report records independently of bounded host
replay. Confirmed sealed or failed producers are imported and ACKed. A definitely exited producer
without durable evidence is reconciled only after checking the producer lease; valid sealed data
retains its recorded outcome and body with incomplete confidence, while an unsealed/corrupt spool
is unknown/incomplete.
A live process, an unknown process identity, access-denied process inspection, or a busy producer
is not proof of death. A captured host disconnect starts an event-driven wait for the same verified
process to exit; it does not cancel the native turn merely because its RPC disconnected. Inventory
and ordinary terminal processing use the same single-flight import. A durable `endJournaled` bit
is recorded only after the journal end mark exists; it prevents republishing that mark after journal
eviction. If journal write or end evidence fails, the host cursor is not advanced by the terminal,
the caller sees a persistence error, and inventory can retry. The session journal resets speculative
in-memory rows after a failed file write before checking whether an end mark reached disk.

A new service-controlled turn on a legacy host reserves identity without sending that host a report
ID. Its observed terminal is stored as incomplete with the bounded text actually observed by the
service, never as full native output. A pre-existing adopted legacy turn whose provenance cannot be
established produces an explicit observation-gap notice instead of inferring an assignment or
report-only classification. Host state carries a trusted turn kind for new hosts, so adopted
report-only turns do not recursively report upward. `_gorynych/status` exposes the number of
unconfirmed reservations and the last recovery failure in `_meta["gorynych/reportRecovery"]`;
this is a recovery health signal; durable delivery disposition is exposed separately.

Supporting hosts and observed legacy terminals use the same bounded native-outcome mapping:
`end_turn` becomes `ended`, `cancelled` stays `cancelled`, native errors become `failed` with
error detail kept separately, and other known stop reasons become `stopped: <reason>`. A reason
that exceeds the 128-byte outcome bound becomes `stopped: [reason truncated]`, never `ended`.
These outcome values remain independent of capture incompleteness and pending delivery.


C3a adds one bounded queued delivery claim to the same manifest: attempt ID, stable message and
prompt IDs, fixed route, host ID (at most 128 UTF-8 bytes), host generation (at most 256 UTF-8
bytes), and a dispatch-started bit. The intended parent is read from immutable identity rather
than duplicated. The additional 4096 bytes per manifest image covers these fields even with
JSON escaping; the aggregate default remains 512 MiB. Claim and start marker are COW writes.
Only a claim whose start bit is false can return to pending, retaining the same IDs. A claim
with start bit set across a restart becomes uncertain and is pinned; it is never blindly replayed.
A bounded preview does not create another full-body copy in the manifest.

For a proven-unstarted spawn, the initial reservation atomically includes the bounded start error,
`not_started` outcome and producer-failure evidence. A result or ACK write failure leaves those
facts recoverable without a native producer or prompt ID; reconciliation publishes the same result
and outbox intent. If the reservation write itself fails, no durable evidence is claimed.

Host-tracked report-note metadata shares each report entry and its existing 16384-byte per-image
metadata headroom: one SHA-256 digest, one host handoff marker, one bounded UUID/revision and a
single set of native facts. Six optional native text fields are limited to 64 UTF-8 bytes each,
including an explicit truncation suffix; a worst-case escaped-field test exercises the existing
manifest bound alongside maximum result error, incompleteness, disposition and host identity.
The aggregate 512 MiB default is unchanged. A shared-store publication is the evidence boundary.
Only an identity- and digest-bound `native_not_running` or `receipt_capacity` proof from the original
host-forwarded attempt allows a distinct queued attempt; the bounded prior claim and proof remain
in metadata. Other errors and process loss leave the started note uncertain. The existing per-entry
reserve covers the proof, both bounded host/generation strings and the queued claim even with worst-case
JSON escaping; the regression exercises these maximum fields. A receipt is settled under the same
manifest lock only if its revision is still current. The report stays pinned until that revision has
a confirmed adapter ACK; newer contradictory or cancellation-pending facts stay pinned and visible.
If the exact host process is independently confirmed exited after a terminal settlement, no adapter
ledger exists to acknowledge: the manifest records a separate `confirmed_host_exit` release, not an
ACK and not new delivery evidence. Host PID and start time are bound to the original claim. A mere
RPC disconnect, uninspectable process, missing legacy identity or unsettled receipt never permits this
release; those records remain pinned pending evidence.
A separate private ACK response may prove that the same live adapter ledger no longer retains the
attempt or UUID. The host validates the original claim and stores `ledger_not_retained` only against
the exact already-settled, current, nonconflicting revision. This releases only the ledger-retention
hold; it is neither a positive ACK nor evidence of non-delivery, and cannot authorize retry or fallback.
Plain `unknown` status has no such effect. Retired entries with active/dirty observers or an in-flight
observer reference cannot be evicted.
