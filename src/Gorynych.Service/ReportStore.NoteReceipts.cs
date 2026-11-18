using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

internal sealed partial class ReportStore
{
    internal bool BeginHostNote(string reportId, string parent, string hostId, string generation, string attempt, string text)
    {
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Utf8.GetBytes(text)));
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["dispatchStarted"] != true ||
                (string?)delivery?["envelopeDigest"] != digest)
                throw new InvalidOperationException("Report note does not match the durable started claim");
            if ((bool?)delivery?["hostHandoffStarted"] == true) return false;
            if ((string?)item["status"] is "completed" or "failed") throw new InvalidOperationException("Delivery is already terminal");
            delivery!["hostHandoffStarted"] = true; Save(entries); return true;
        }
    }

    // Bounded, identity-checked native facts in the same COW manifest as the claim.
    // No note text is retained. Booleans are monotone; the first cycle result is immutable.
    internal void RecordNoteReceipt(string reportId, string parent, string hostId, string generation, JsonObject facts)
    {
        ValidateId(reportId);
        var attempt = (string?)facts["attemptId"];
        var uuid = (string?)facts["nativeMessageId"];
        if (facts["revision"] is not JsonValue revisionValue || !revisionValue.TryGetValue<long>(out var revision) || revision <= 0)
            throw new ArgumentException("Invalid native receipt revision");
        if ((string?)facts["reportId"] != reportId || !Guid.TryParseExact(attempt, "N", out _) ||
            !Guid.TryParse(uuid, out _)) throw new ArgumentException("Invalid report note identity");
        string[] flags = ["writeStarted", "writeCompleted", "taken", "cancelRequested", "nativeCancelled", "contradiction"];
        string[] fields = ["cycleResult", "nativeError", "nativeSubtype", "nativeStopReason", "nativeTerminalReason", "dropReason", "uncertainty"];
        foreach (var field in flags)
            if (facts[field] is not null && (facts[field] is not JsonValue v || !v.TryGetValue<bool>(out _)))
                throw new ArgumentException("Invalid native receipt flag");
        foreach (var field in fields)
            if (facts[field] is not null && (facts[field] is not JsonValue v || !v.TryGetValue<string>(out var text) || Utf8.GetByteCount(text) > 64))
                throw new ArgumentException("Native receipt field exceeds its bound");
        if ((string?)facts["cycleResult"] is { } cycle &&
            (cycle is not ("ended" or "failed" or "cancelled") || (bool?)facts["taken"] != true))
            throw new ArgumentException("A native cycle result requires a matching taken UUID");
        if ((bool?)facts["writeCompleted"] == true && (bool?)facts["writeStarted"] != true)
            throw new ArgumentException("Native write completion requires a started write");
        if ((bool?)facts["contradiction"] == true && ((bool?)facts["taken"] != true || (bool?)facts["nativeCancelled"] != true))
            throw new ArgumentException("Contradictory receipt must preserve both native facts");
        if (facts["cancelReceiptPending"] is not null &&
            (facts["cancelReceiptPending"] is not JsonValue pending || !pending.TryGetValue<bool>(out _)))
            throw new ArgumentException("Invalid native interrupt receipt");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["dispatchStarted"] != true ||
                (bool?)delivery?["hostHandoffStarted"] != true || delivery?["noInputProof"] is not null)
                throw new InvalidOperationException("Native receipt does not match the durable started claim");
            var prior = delivery!["nativeReceipt"] as JsonObject;
            if (prior is not null && (string?)prior["nativeMessageId"] != uuid)
                throw new InvalidOperationException("Native message identity is immutable");
            if ((long?)prior?["revision"] >= revision) return;
            var next = prior is null ? new JsonObject { ["nativeMessageId"] = uuid } : (JsonObject)prior.DeepClone();
            next["revision"] = revision;
            foreach (var field in flags) next[field] = (bool?)next[field] == true || (bool?)facts[field] == true;
            var hadResult = (string?)next["cycleResult"] is not null;
            foreach (var field in fields)
            {
                if (field == "uncertainty") { next[field] = facts[field]?.DeepClone(); continue; }
                if (!hadResult || field == "dropReason")
                    if (facts[field] is not null && next[field] is null) next[field] = facts[field]!.DeepClone();
            }
            // Cancellation pending is current state; the factual cancellation/taken flags remain monotone.
            next["cancelReceiptPending"] = (bool?)facts["cancelReceiptPending"] == true;
            delivery["nativeReceipt"] = next; Save(entries);
        }
    }

    internal JsonObject? ReadNoteReceipt(string reportId, string parent, string hostId, string generation, string attempt)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("Native receipt identity does not match claim");
            return delivery?["nativeReceipt"]?.DeepClone() as JsonObject;
        }
    }

    // Settle from the exact durable receipt revision under the same cross-process lock
    // used by RecordNoteReceipt. A later cancellation or contradictory snapshot must
    // never be hidden by a decision made from an earlier read.
    internal bool TrySettleNoteReceipt(string reportId, string parent, string hostId, string generation, string attempt, long revision)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("Native receipt identity does not match claim");
            var previousStatus = (string?)item["status"];
            var receipt = delivery?["nativeReceipt"];
            if ((long?)receipt?["revision"] != revision || (bool?)receipt?["cancelReceiptPending"] == true ||
                (bool?)receipt?["contradiction"] == true) return false;
            var cycle = (string?)receipt?["cycleResult"];
            var taken = (bool?)receipt?["taken"] == true;
            var dropped = (bool?)receipt?["nativeCancelled"] == true;
            string status; string? reason;
            if (taken && !dropped && cycle == "ended") { status = "completed"; reason = "Confirmed by matching native report note cycle"; }
            else if (taken && !dropped && cycle is "failed" or "cancelled")
            { status = "failed"; reason = cycle == "cancelled" ? "Native report note cycle was cancelled" : "Native report note cycle failed"; }
            else if (!taken && dropped) { status = "failed"; reason = "Native report note was dropped before being taken"; }
            else return false;
            if (previousStatus is "completed" or "failed")
            {
                // New non-contradictory facts may extend an immutable terminal receipt.
                // Refresh the ACK revision without changing its settled disposition.
                if (previousStatus != status || (long?)delivery?["settledReceiptRevision"] == revision) return false;
                delivery!["settledReceiptRevision"] = revision; Save(entries); return true;
            }
            if (previousStatus is not ("claimed" or "accepted" or "uncertain"))
                throw new InvalidOperationException("Report note is not in a settleable state");
            item["status"] = status; item["reason"] = reason;
            item["terminalAt"] = Now().ToUnixTimeMilliseconds();
            delivery!["settledReceiptRevision"] = revision;
            Save(entries); return true;
        }
    }


    // A proof is accepted only for the original host-forwarded attempt and digest.
    // It is retained in the same manifest; a generic error or missing receipt is not proof.
    internal void RecordNoteNoInput(string reportId, string parent, string hostId, string generation,
        string attempt, string digest, string reason)
    {
        if (reason is not ("native_not_running" or "receipt_capacity")) throw new ArgumentException("Invalid no-input reason");
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (string?)delivery?["envelopeDigest"] != digest ||
                (bool?)delivery?["dispatchStarted"] != true || (bool?)delivery?["hostHandoffStarted"] != true ||
                delivery?["nativeReceipt"] is not null || (string?)item["status"] is "completed" or "failed")
                throw new InvalidOperationException("No-input proof does not match an unresolved started note claim");
            if ((string?)delivery?["noInputProof"] is { } previous)
            {
                if (previous != reason) throw new InvalidOperationException("No-input proof conflicts with retained evidence");
                return;
            }
            delivery!["noInputProof"] = reason; Save(entries);
        }
    }


    internal string? ReadNoteNoInput(string reportId, string parent, string hostId, string generation, string attempt)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("No-input evidence identity does not match claim");
            return (string?)delivery?["noInputProof"];
        }
    }


    internal void QueueProvenNoInput(string reportId, string parent, string hostId, string generation, string attempt)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["hostHandoffStarted"] != true ||
                (string?)delivery?["noInputProof"] is not ("native_not_running" or "receipt_capacity") ||
                delivery?["nativeReceipt"] is not null || (string?)item["status"] is not ("claimed" or "uncertain"))
                throw new InvalidOperationException("Only a correlated durable no-input proof permits queued fallback");
            item["status"] = "pending"; item["reason"] = "Native report note was refused before input; queued fallback";
            Save(entries);
        }
    }


    // This records a confirmed adapter ACK, never a request to retry native input.
    // A newer stored snapshot or an unresolved/changed disposition rejects stale ACKs.
    internal bool RecordNoteAcknowledged(string reportId, string parent, string hostId, string generation, string attempt, long revision)
    {
        if (revision <= 0) throw new ArgumentOutOfRangeException(nameof(revision));
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (bool?)delivery?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("Native receipt identity does not match claim");
            if ((string?)item["status"] is not ("completed" or "failed") ||
                (long?)delivery?["settledReceiptRevision"] != revision ||
                (long?)delivery?["nativeReceipt"]?["revision"] != revision ||
                (bool?)delivery?["nativeReceipt"]?["cancelReceiptPending"] == true ||
                (bool?)delivery?["nativeReceipt"]?["contradiction"] == true) return false;
            if ((long?)delivery?["acknowledgedReceiptRevision"] == revision) return true;
            delivery!["acknowledgedReceiptRevision"] = revision; Save(entries); return true;
        }
    }


    internal bool CanAcknowledgeNote(string reportId, string parent, string hostId, string generation, string attempt, string uuid, long revision)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (string?)delivery?["nativeReceipt"]?["nativeMessageId"] != uuid ||
                (bool?)delivery?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("Native receipt ACK identity does not match claim");
            return (string?)item["status"] is "completed" or "failed" && revision > 0 &&
                (long?)delivery?["settledReceiptRevision"] == revision && (long?)delivery?["nativeReceipt"]?["revision"] == revision &&
                (bool?)delivery?["nativeReceipt"]?["cancelReceiptPending"] != true && (bool?)delivery?["nativeReceipt"]?["contradiction"] != true;
        }
    }


    internal bool NoteNeedsAck(string reportId, string parent, string hostId, string generation, string attempt)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var item = Find(Load(), reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt) throw new InvalidOperationException("Native receipt identity does not match claim");
            return (long?)delivery?["settledReceiptRevision"] is { } revision &&
                (((long?)delivery?["acknowledgedReceiptRevision"] != revision && (long?)delivery?["hostExitReleasedRevision"] != revision &&
                  (long?)delivery?["ledgerAbsentReleasedRevision"] != revision) ||
                 (long?)delivery?["nativeReceipt"]?["revision"] != revision);
        }
    }


    // The caller must independently prove that the exact host process exited. This
    // records only ledger release, never native delivery or an adapter ACK.
    internal bool RecordNoteHostExitRelease(string reportId, string parent, string hostId, string generation,
        string attempt, int processId, string? processStart, long revision)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var delivery = item["delivery"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)delivery?["route"] != "note" ||
                (string?)delivery?["hostId"] != hostId || (string?)delivery?["hostGeneration"] != generation ||
                (string?)delivery?["attemptId"] != attempt || (int?)delivery?["hostProcessId"] != processId ||
                (string?)delivery?["hostProcessStart"] != processStart || processId <= 0)
                throw new InvalidOperationException("Host-exit evidence identity does not match the delivery claim");
            if ((string?)item["status"] is not ("completed" or "failed") ||
                (long?)delivery?["settledReceiptRevision"] != revision || (long?)delivery?["nativeReceipt"]?["revision"] != revision ||
                (bool?)delivery?["nativeReceipt"]?["contradiction"] == true || (bool?)delivery?["nativeReceipt"]?["cancelReceiptPending"] == true)
                return false;
            if ((long?)delivery?["hostExitReleasedRevision"] == revision) return true;
            delivery!["hostExitReleasedRevision"] = revision; Save(entries); return true;
        }
    }


    internal bool RecordNoteLedgerNotRetained(string reportId, string parent, string hostId, string generation, string attempt, string uuid, long revision)
    {
        lock (gate) using (SharedFileLock.Acquire(manifest))
        {
            var entries = Load(); var item = Find(entries, reportId); var d = item["delivery"]; var receipt = d?["nativeReceipt"];
            if ((string?)item["identity"]?["parent"] != parent || (string?)d?["route"] != "note" ||
                (string?)d?["hostId"] != hostId || (string?)d?["hostGeneration"] != generation ||
                (string?)d?["attemptId"] != attempt || (string?)receipt?["nativeMessageId"] != uuid || (bool?)d?["hostHandoffStarted"] != true)
                throw new InvalidOperationException("Ledger release identity does not match the delivery claim");
            if ((string?)item["status"] is not ("completed" or "failed") || revision <= 0 ||
                (long?)d?["settledReceiptRevision"] != revision || (long?)receipt?["revision"] != revision ||
                (bool?)receipt?["contradiction"] == true || (bool?)receipt?["cancelReceiptPending"] == true) return false;
            if ((long?)d?["ledgerAbsentReleasedRevision"] == revision) return true;
            d!["ledgerAbsentReleasedRevision"] = revision; Save(entries); return true;
        }
    }

}
