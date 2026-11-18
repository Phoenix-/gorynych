using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Claude;

// Mutable facts are protected by this receipt's lock. Text stays only in the ordinary pending Note;
// this bounded receipt holds a hash and native facts, never another copy of the envelope.
internal sealed class ReportNoteAttempt(string reportId, string attemptId, string uuid, string digest, bool hostTracking = false)
{
    internal string ReportId { get; } = reportId;
    internal string AttemptId { get; } = attemptId;
    internal string Uuid { get; } = uuid;
    internal string Digest { get; } = digest;
    internal bool HostTracking { get; } = hostTracking;
    long revision;
    JsonObject? lastFacts;
    internal bool NotificationActive { get; set; }
    internal bool NotificationDirty { get; set; }
    internal int ObserverReferences { get; set; }
    internal bool WriteStarted { get; set; }
    internal bool WriteCompleted { get; set; }
    internal bool Taken { get; set; }
    internal bool CancelRequested { get; set; }
    internal bool CancelReceiptPending { get; set; }
    internal string? CycleResult { get; set; }
    internal string? NativeError { get; set; }
    internal string? NativeSubtype { get; set; }
    internal string? NativeStopReason { get; set; }
    internal string? NativeTerminalReason { get; set; }
    internal string? DropReason { get; set; }
    internal string? Uncertainty { get; set; }
    internal bool Dropped { get; set; }
    internal bool Resolved { get { lock (this) return !CancelReceiptPending && !(Taken && Dropped) && (CycleResult is not null || Dropped); } }
    // Revision describes factual changes, not status reads. An ACK can therefore
    // compare the same snapshot while holding the receipt lock; a mutation that has
    // not yet reached the host is detected before capacity is released.
    internal JsonObject Receipt()
    {
        lock (this)
        {
            var facts = new JsonObject
            {
                ["reportId"] = ReportId, ["attemptId"] = AttemptId, ["nativeMessageId"] = Uuid,
                ["state"] = Taken && Dropped || CancelReceiptPending ? "uncertain" : CycleResult is not null ? "cycle_result" : Dropped ? "dropped" :
                    Uncertainty is not null ? "uncertain" : Taken ? "taken" : WriteCompleted ? "written" : WriteStarted ? "writing" : "prepared",
                ["writeStarted"] = WriteStarted, ["writeCompleted"] = WriteCompleted, ["taken"] = Taken, ["cancelRequested"] = CancelRequested, ["cancelReceiptPending"] = CancelReceiptPending,
                ["cycleResult"] = CycleResult, ["nativeError"] = NativeError, ["nativeSubtype"] = NativeSubtype,
                ["nativeStopReason"] = NativeStopReason, ["nativeTerminalReason"] = NativeTerminalReason,
                ["nativeCancelled"] = Dropped, ["contradiction"] = Taken && Dropped, ["dropReason"] = DropReason,
                ["uncertainty"] = Taken && Dropped ? "Native cancelled[] and replay both reported this UUID; reconcile without replay" :
                    CancelReceiptPending ? Uncertainty ?? "Native interrupt receipt is pending; reconcile without replay" : Uncertainty,
            };
            if (!JsonNode.DeepEquals(lastFacts, facts)) { revision++; lastFacts = facts.DeepClone().AsObject(); }
            facts["revision"] = revision;
            return new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = facts } };
        }
    }

}
