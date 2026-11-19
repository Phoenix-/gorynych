using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Claude;

internal sealed partial class HeadlessSession
{
    internal sealed record GuardedReceiptSnapshot(string AttemptId, string NativeUuid, bool Prepared, bool Committed,
        bool NoInput, bool WriteCompleted, bool Taken, string? CycleResult, bool NativeCancelled, bool CancelRequested,
        bool Uncertain, bool Contradiction, string? NativeStop, string? NativeSubtype, string? NativeTerminal,
        int CancelObservers, bool CancelUnresolved, long Revision);
    sealed class GuardedReceipt(string attempt, string uuid, string digest, PromptObservation witness, ClaudeProcess? owner)
    {
        public string Attempt = attempt, Uuid = uuid, Digest = digest;
        public PromptObservation Witness = witness;
        public ClaudeProcess? Owner = owner;
        public bool Committed, NoInput, WriteCompleted, Taken, NativeCancelled, CancelRequested, Uncertain, Contradiction;
        public string? CycleResult, NativeStop, NativeSubtype, NativeTerminal;
        public long? TakenCycle;
        public int CancelObservers;
        public long Revision;
        public long? ReleasedRevision;
        public bool CancelUnresolved, Aborted;
        public GuardedReceiptSnapshot Snapshot() => new(Attempt, Uuid, true, Committed, NoInput, WriteCompleted, Taken,
            CycleResult, NativeCancelled, CancelRequested, Uncertain, Contradiction, NativeStop, NativeSubtype, NativeTerminal,
            CancelObservers, CancelUnresolved, Revision);
    }
    readonly Dictionary<string, GuardedReceipt> guardedReceipts = new(StringComparer.Ordinal);
    readonly Dictionary<string, GuardedReceipt> guardedRetired = new(StringComparer.Ordinal);
    readonly Dictionary<string, GuardedReceipt> guardedReceiptsByUuid = new(StringComparer.Ordinal);
    int GuardedReceiptLimit => Math.Clamp(options.GuardedReceiptLimitForTests, 1, 64);

    GuardedReceipt? FindGuarded(string attempt) => guardedReceipts.GetValueOrDefault(attempt) ?? guardedRetired.GetValueOrDefault(attempt);

    // Ordered under the same lock as Commit. A reservation may precede delivery of the
    // submit RPC, so a late submit finds the same pinned no-input tombstone.
    internal (string State, long? Revision) AbortGuardedForBridge(string attempt, PromptObservation witness, string digest)
    {
        if (!Guid.TryParseExact(attempt, "N", out var parsed) || attempt != attempt.ToLowerInvariant() ||
            digest is not { Length: 64 } || !digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')) return ("unresolved", null);
        lock (stateGate)
        {
            var item = FindGuarded(attempt);
            if (item is null)
            {
                if (guardedReceipts.Count >= GuardedReceiptLimit || process is not { } current || !processUp || disposed || observationClosing ||
                    current.PhysicallyExited || current.Exited.IsCompleted || current.BackgroundStreamId != witness.Stream || witness.SessionId != Id ||
                    !agent.TryReserveGuardedReceipt(attempt, Id)) return ("unresolved", null);
                item = new GuardedReceipt(attempt, parsed.ToString("D"), digest, witness, current);
                guardedReceipts.Add(attempt, item); guardedReceiptsByUuid.Add(item.Uuid, item);
            }
            if (item.Witness != witness || item.Digest != digest || item.Owner is not { } owner ||
                !ReferenceEquals(process, owner) || owner.PhysicallyExited || owner.Exited.IsCompleted || owner.BackgroundStreamId != witness.Stream)
                return ("unresolved", null);
            if (item.Committed) return ("already_committed", item.Revision);
            if (!item.Aborted || !item.NoInput) { item.Aborted = true; item.NoInput = true; item.Revision++; }
            // The witness itself is a single-use authority. Revoke it under the same
            // lock as Commit before this receipt may ever be retired.
            if (promptObservation == witness) { promptObservation = null; observationIngress++; }
            return ("confirmed_precommit", item.Revision);
        }
    }

    static bool StableReleaseFacts(GuardedReceipt item) =>
        item.CancelObservers == 0 && !item.CancelUnresolved && !item.Uncertain && !item.Contradiction && !item.NativeCancelled &&
        (item.NoInput && !item.Committed || item.Committed && item.WriteCompleted && item.Taken &&
            (item.CycleResult == "ended" || item.CycleResult == "failed" && !item.CancelRequested));

    // ACK is local and only called through the authenticated host bridge after
    // the host has durably prepared this exact revision. Tombstones stay bounded/pinned.
    internal bool ReleaseGuardedReceiptForBridge(string attempt, PromptObservation witness, string digest, long revision)
    {
        lock (stateGate)
        {
            var item = FindGuarded(attempt);
            if (item is null || item.Witness != witness || item.Digest != digest || item.Revision != revision ||
                item.Owner is not { } owner || !ReferenceEquals(process, owner) || owner.PhysicallyExited || owner.Exited.IsCompleted ||
                owner.BackgroundStreamId != witness.Stream || !StableReleaseFacts(item)) return false;
            if (item.ReleasedRevision is { } prior) return prior == revision;
            if (guardedRetired.Count >= GuardedReceiptLimit) return false;
            guardedReceipts.Remove(attempt);
            item.ReleasedRevision = revision;
            guardedRetired.Add(attempt, item);
            return true;
        }
    }

    // The authenticated host calls this only after persisting the exact ACK. An absent
    // receipt means capacity is already free; it says nothing about native delivery.
    internal bool RetireGuardedReceiptForBridge(string attempt, PromptObservation witness, string digest, long revision)
    {
        lock (stateGate)
        {
            var item = FindGuarded(attempt);
            if (item is null) return Guid.TryParseExact(attempt, "N", out var missing) &&
                !guardedReceiptsByUuid.ContainsKey(missing.ToString("D")) && agent.GuardedReservationAbsent(attempt);
            if (item.Witness != witness || item.Digest != digest || item.Revision != revision || !StableReleaseFacts(item) ||
                !(item.ReleasedRevision == revision || item.Aborted && item.NoInput && !item.Committed)) return false;
            if (item.Aborted && promptObservation == witness) { promptObservation = null; observationIngress++; }
            guardedReceipts.Remove(attempt); guardedRetired.Remove(attempt); guardedReceiptsByUuid.Remove(item.Uuid);
            agent.ReleaseGuardedReservation(attempt, Id);
            return true;
        }
    }

    internal (GuardedReceiptSnapshot Snapshot, PromptObservation Witness, string Digest)? GuardedReceiptForBridge(
        string attempt, PromptObservation witness, string digest)
    {
        lock (stateGate)
            return FindGuarded(attempt) is { } item && item.Witness == witness && item.Digest == digest &&
                item.Owner is { } owner && ReferenceEquals(process, owner) && !owner.PhysicallyExited && !owner.Exited.IsCompleted &&
                owner.BackgroundStreamId == witness.Stream
                ? (item.Snapshot(), item.Witness, item.Digest) : null;
    }
    // A released entry remains pinned until the host durably records its exact ACK
    // and requests retire. Host ledger admission and watermark reject delayed submits.

    internal async Task<GuardedReceiptSnapshot> SubmitGuardedReceiptAsync(PromptObservation expected, string attempt,
        string text, TimeSpan timeout, CancellationToken ct)
    {
        if (!Guid.TryParseExact(attempt, "N", out var parsed) || attempt != attempt.ToLowerInvariant() ||
            string.IsNullOrWhiteSpace(text) || text.Length > 32000 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentException("Invalid guarded reminder");
        var uuid = parsed.ToString("D"); var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        GuardedReceipt receipt;
        lock (stateGate)
        {
            if (FindGuarded(attempt) is { } prior)
            {
                if (prior.Witness != expected || prior.Digest != digest) throw new InvalidOperationException("Conflicting guarded reminder identity");
                return prior.Snapshot();
            }
            if (guardedReceipts.Count >= GuardedReceiptLimit || !agent.TryReserveGuardedReceipt(attempt, Id))
                throw new InvalidOperationException("Guarded attempt is already reserved or receipt capacity is full");
            receipt = new GuardedReceipt(attempt, uuid, digest, expected, process);
            guardedReceipts.Add(attempt, receipt); guardedReceiptsByUuid.Add(uuid, receipt);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); bounded.CancelAfter(timeout);
        if (options.BeforeGuardedWriteForTests is { } barrier) await barrier().WaitAsync(bounded.Token);
        try { await inputGate.WaitAsync(bounded.Token); }
        catch (OperationCanceledException) { lock (stateGate) { if (!receipt.Aborted) { receipt.NoInput = true; receipt.Revision++; } return receipt.Snapshot(); } }
        try
        {
            var owner = receipt.Owner;
            if (owner is null) { lock (stateGate) { if (!receipt.Aborted) { receipt.NoInput = true; receipt.Revision++; } return receipt.Snapshot(); } }
            var serial = 0L;
            bool Commit()
            {
                lock (stateGate)
                {
                    if (receipt.Aborted || bounded.IsCancellationRequested || promptObservation != expected || expected.SessionId != Id ||
                        !ReferenceEquals(process, owner) || owner.Exited.IsCompleted || owner.PhysicallyExited ||
                        owner.BackgroundStreamId != expected.Stream || !ReferenceEquals(observedBackgroundOwner, owner) ||
                        observationClosing || disposed || !processUp || reminderBackgroundEmpty != true || active || ownOpen ||
                        incoming is not null || turn is not null || pending.Count != 0 || steers.Count != 0 ||
                        observingInputs != 0 || observingControls != 0 || !asks.IsEmpty || notes.ContainsKey(uuid)) return false;
                    promptObservation = null; observationIngress++; serial = guardedCancelRevision; receipt.Committed = true; receipt.Revision++;
                    notes.Add(uuid, new Note(uuid, text, new JsonObject())); pending.Add(uuid); guardedPending.Add(uuid);
                    lastOutput = Environment.TickCount64; UpdateIdle(); return true;
                }
            }
            var outcome = await owner.WriteGuardedAsync(StreamJson.UserMessage(uuid, JsonValue.Create(text)!), Commit,
                bounded.Token);
            lock (stateGate)
            {
                if (receipt.Aborted && outcome == ClaudeProcess.GuardedWriteOutcome.NoInput) return receipt.Snapshot();
                receipt.Revision++;
                if (outcome == ClaudeProcess.GuardedWriteOutcome.NoInput) receipt.NoInput = true;
                else
                {
                    receipt.WriteCompleted = outcome == ClaudeProcess.GuardedWriteOutcome.WriteCompleted;
                    if (!receipt.WriteCompleted || serial != guardedCancelRevision || bounded.IsCancellationRequested ||
                        !ReferenceEquals(process, owner) || owner.PhysicallyExited || owner.Exited.IsCompleted) receipt.Uncertain = true;
                }
            }
            if (outcome != ClaudeProcess.GuardedWriteOutcome.NoInput) ScheduleStallCheck();
            lock (stateGate) return receipt.Snapshot();
        }
        finally { inputGate.Release(); }
    }
}
