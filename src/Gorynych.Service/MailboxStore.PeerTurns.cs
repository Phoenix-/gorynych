using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

// Only a trusted transport that can prove the specific running prompt may create this.
// A session token or an agent-provided prompt ID is not a turn identity.
internal sealed record PeerTurnIdentity(string MessageId, long Sequence, string Sender, string Recipient, string PromptId);
internal sealed record PeerSettlementPin(PeerTurnIdentity Turn, string ReportId);

internal sealed partial class MailboxStore
{
    static bool PeerTurnEvictable(JsonObject item) => (bool?)item["delivered"] == true &&
        (string?)item["peerTurn"]?["state"] != "started" && item["peerTurn"]?["settlementPin"] is null;

    static bool PeerTurnMatches(JsonObject item, PeerTurnIdentity turn) =>
        (string?)item["id"] == turn.MessageId && (long?)item["sequence"] == turn.Sequence &&
        (string?)item["sender"] == turn.Sender && (string?)item["recipient"] == turn.Recipient &&
        (string?)item["peerTurn"]?["promptId"] == turn.PromptId;

    static bool InitializePeerTurn(JsonObject item, PeerTurnIdentity turn)
    {
        if ((string?)item["id"] != turn.MessageId || (long?)item["sequence"] != turn.Sequence ||
            (string?)item["sender"] != turn.Sender || (string?)item["recipient"] != turn.Recipient ||
            string.IsNullOrWhiteSpace(turn.PromptId) || turn.PromptId.Length > 512 ||
            turn.Sender.StartsWith("outside:", StringComparison.Ordinal) || turn.Sender == "service" ||
            turn.Recipient.StartsWith("outside:", StringComparison.Ordinal) ||
            (bool?)item["delegation"] == true || (bool?)item["expectReply"] == true ||
            (bool?)item["peerAutoReply"] == true || item["replyTo"] is not null || item["peerTurn"] is not null) return false;
        item["peerTurn"] = new JsonObject { ["promptId"] = turn.PromptId, ["state"] = "started" };
        return true;
    }

    const int PeerAcceptanceReceiptLimit = 16;
    // The receipt proves only that this exact mailbox reply was accepted after the source
    // admission. It does not establish which native turn invoked the tool.
    static bool RecordPeerAcceptance(JsonArray rows, string requestId, string sender, string recipient, string? replyTo, long sequence)
    {
        if (replyTo is null) return false;
        var source = rows.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == replyTo &&
            (string?)e["sender"] == recipient && (string?)e["recipient"] == sender &&
            (long?)e["sequence"] < sequence && (string?)e["peerTurn"]?["state"] == "started");
        if (source?["peerTurn"] is not JsonObject peer) return false;
        var receipts = peer["acceptanceReceipts"] as JsonArray;
        if (receipts is null) peer["acceptanceReceipts"] = receipts = new JsonArray();
        if (receipts.Count >= PeerAcceptanceReceiptLimit) peer["acceptanceOverflow"] = true;
        else receipts.Add((JsonNode)new JsonObject { ["id"] = requestId, ["sequence"] = sequence });
        return true;
    }

    // Bounded restart inventory includes retained reports, which the normal pending-report scan skips.
    internal IReadOnlyList<PeerSettlementPin> PeerSettlementPins(long after = 0, int limit = 64)
    {
        if (after < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
            return entries.OfType<JsonObject>().Where(e => (long?)e["sequence"] > after && e["peerTurn"]?["settlementPin"] is not null)
                .OrderBy(e => (long)e["sequence"]!).Take(limit).Select(e => new PeerSettlementPin(
                    new PeerTurnIdentity((string)e["id"]!, (long)e["sequence"]!, (string)e["sender"]!, (string)e["recipient"]!,
                        (string)e["peerTurn"]!["promptId"]!), (string)e["peerTurn"]!["settlementPin"]!)).ToArray();
    }

    // Trusted caller has checked the original report's durable retained disposition.
    // Missing is accepted only for a validated canonical incarnation whose eviction cursor
    // passed this exact source; started/pinned rows cannot reach that state by ordinary eviction.
    internal bool AcknowledgePeerRetained(PeerTurnIdentity turn, string reportId, string incarnation)
    {
        lock (gate)
        {
            if (publicationUncertain && !ReconcileCompletionPublicationLocked()) return false;
            if (peerMailboxIncarnation != incarnation || !Guid.TryParseExact(incarnation, "N", out _) ||
                committedDigest is null || !File.Exists(path)) return false;
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), committedDigest)) return false;
                var disk = ParseSnapshot(bytes); if (disk is null) return false;
                ValidateSnapshot(disk);
                if ((string?)disk["peerMailboxIncarnation"] != incarnation || (long?)disk["next"] != next ||
                    (long?)disk["trimmedThrough"] != trimmedThrough || disk["entries"] is not JsonArray) return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
            { return false; }
            var row = entries.OfType<JsonObject>().FirstOrDefault(e => (long?)e["sequence"] == turn.Sequence);
            if (row is null) return trimmedThrough >= turn.Sequence && next >= turn.Sequence;
            if (!PeerTurnMatches(row, turn) || (string?)row["peerTurn"]?["reportId"] != reportId ||
                (string?)row["peerTurn"]?["mailboxIncarnation"] != incarnation || (string?)row["peerTurn"]?["state"] == "started") return false;
            if (row["peerTurn"]?["settlementPin"] is null) return (string?)row["peerTurn"]?["retentionAck"] == reportId;
            if ((string?)row["peerTurn"]?["settlementPin"] != reportId) return false;
            var copy = entries.DeepClone().AsArray(); var target = copy.OfType<JsonObject>().First(e => PeerTurnMatches(e, turn));
            target["peerTurn"]!.AsObject().Remove("settlementPin"); target["peerTurn"]!["retentionAck"] = reportId;
            Save(copy, next, completionClaimId: "peer-retention:" + reportId); return true;
        }
    }

    // The caller has authenticated a durable terminal but cannot establish the reply
    // disposition. This consumes no outbound capacity and does not claim no input/reply.
    internal JsonObject SettlePeerTerminalUnknown(PeerTurnIdentity turn, string reportId, string outcome,
        string reason, string? witnessedAcceptedId = null)
    {
        if (!Guid.TryParseExact(reportId, "N", out _) || reportId != reportId.ToLowerInvariant() ||
            string.IsNullOrWhiteSpace(outcome) || Encoding.UTF8.GetByteCount(outcome) > ReportCapture.OutcomeBytes ||
            reason is not ("marker_unknown" or "accepted_unconfirmed" or "capture_incomplete" or "terminal_conflict" or "producer_capture_failed" or "producer_exited"))
            throw new ArgumentException("Invalid unknown terminal identity");
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain");
            var current = entries.OfType<JsonObject>().FirstOrDefault(e => PeerTurnMatches(e, turn));
            if (current?["peerTurn"] is not JsonObject prior) return new JsonObject { ["state"] = "unknown" };
            if ((string?)prior["state"] != "started")
            {
                if ((string?)prior["state"] != "outcome_unknown" || (string?)prior["reportId"] != reportId ||
                    (string?)prior["outcome"] != outcome || (string?)prior["unknownReason"] != reason ||
                    (string?)prior["witnessedAcceptedId"] != witnessedAcceptedId)
                    throw new InvalidOperationException("Unknown peer terminal conflicts with retained disposition");
                return prior.DeepClone().AsObject();
            }
            var copy = entries.DeepClone().AsArray(); var target = copy.OfType<JsonObject>().First(e => PeerTurnMatches(e, turn));
            var state = target["peerTurn"]!.AsObject(); var incarnation = peerMailboxIncarnation ?? Guid.NewGuid().ToString("N");
            state["state"] = "outcome_unknown"; state["outcome"] = outcome; state["unknownReason"] = reason;
            state["reportId"] = reportId; state["settlementPin"] = reportId; state["mailboxIncarnation"] = incarnation;
            if (witnessedAcceptedId is not null) state["witnessedAcceptedId"] = witnessedAcceptedId;
            Save(copy, next, completionClaimId: "peer-unknown:" + reportId, newPeerMailboxIncarnation: incarnation);
            return state.DeepClone().AsObject();
        }
    }

    // This is an internal primitive only: it neither authenticates a caller nor
    // infers prompt ownership. Input and accepted reply share this COW lock.
    internal JsonObject SettlePeerTurn(PeerTurnIdentity turn, string outcome, string? finalText, string? witnessedAcceptedId = null,
        bool finalTextTruncated = false, string? reportId = null, bool producerEvidenceComplete = false)
    {
        if (outcome is not ("ended" or "failed" or "cancelled") &&
            (!outcome.StartsWith("stopped: ", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(outcome) > ReportCapture.OutcomeBytes))
            throw new ArgumentException("Invalid peer turn outcome");
        if (outcome == "ended" && !producerEvidenceComplete) return new JsonObject { ["state"] = "unknown" };
        if (outcome != "ended" && witnessedAcceptedId is not null) throw new ArgumentException("A non-success terminal has no reply suppression proof");
        if (reportId is not null && (!Guid.TryParseExact(reportId, "N", out _) || reportId != reportId.ToLowerInvariant()))
            throw new ArgumentException("Invalid peer report ID");
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain");
            var current = entries.OfType<JsonObject>().FirstOrDefault(e => PeerTurnMatches(e, turn));
            if (current?["peerTurn"] is not JsonObject prior) return new JsonObject { ["state"] = "unknown" };
            var finalDigest = outcome == "ended" ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finalText ?? ""))) : null;
            var (preview, previewWasLimited) = ReportStore.LimitPreview(finalText ?? "");
            var truncated = outcome == "ended" && (finalTextTruncated || previewWasLimited);
            if ((string?)prior["state"] != "started")
            {
                // A durable unknown terminal is final for that exact witnessed reply;
                // a retry must not silently reclassify it or create a second outbound.
                if ((string?)prior["state"] == "outcome_unknown" && (string?)prior["unknownReason"] == "accepted_unconfirmed" &&
                    (string?)prior["outcome"] == outcome && (string?)prior["reportId"] == reportId &&
                    (string?)prior["witnessedAcceptedId"] == witnessedAcceptedId) return prior.DeepClone().AsObject();
                if ((string?)prior["outcome"] != outcome || (string?)prior["finalDigest"] != finalDigest ||
                    ((bool?)prior["previewTruncated"] == true) != truncated ||
                    (string?)prior["reportId"] != reportId || (string?)prior["witnessedAcceptedId"] != witnessedAcceptedId)
                    throw new InvalidOperationException("Peer turn settlement conflicts with the retained terminal");
                return prior.DeepClone().AsObject();
            }
            var copy = entries.DeepClone().AsArray();
            var initial = copy.OfType<JsonObject>().First(e => PeerTurnMatches(e, turn));
            var state = initial["peerTurn"]!.AsObject(); state["outcome"] = outcome;
            var incarnation = reportId is null ? null : peerMailboxIncarnation ?? Guid.NewGuid().ToString("N");
            if (reportId is not null) { state["reportId"] = reportId; state["settlementPin"] = reportId; state["mailboxIncarnation"] = incarnation; }
            if (witnessedAcceptedId is not null) state["witnessedAcceptedId"] = witnessedAcceptedId;
            // Witness belongs to the terminal capture, never to Accept. Verify the exact
            // already-durable outgoing reply while holding the same lock as settlement.
            var accepted = witnessedAcceptedId is null ? null : copy.OfType<JsonObject>()
                .FirstOrDefault(e => (string?)e["id"] == witnessedAcceptedId);
            if (witnessedAcceptedId is not null)
            {
                var receipts = (state["acceptanceReceipts"] as JsonArray)?.OfType<JsonObject>()
                    .Where(r => (string?)r["id"] == witnessedAcceptedId && (long?)r["sequence"] > turn.Sequence).ToArray() ?? [];
                if ((bool?)state["acceptanceOverflow"] == true || receipts.Length > 1)
                    return new JsonObject { ["state"] = "unknown" };
                var retainedExact = accepted is not null && (long?)accepted["sequence"] > turn.Sequence &&
                    (string?)accepted["sender"] == turn.Recipient && (string?)accepted["recipient"] == turn.Sender &&
                    (string?)accepted["replyTo"] == turn.MessageId && (bool?)accepted["peerAutoReply"] != true;
                if (receipts.Length != 1 && !retainedExact) return new JsonObject { ["state"] = "unknown" };
                if (receipts.Length == 1 && accepted is not null && (!retainedExact ||
                    (long?)accepted["sequence"] != (long?)receipts[0]["sequence"]))
                    return new JsonObject { ["state"] = "unknown" };
                state["explicitReplyId"] = witnessedAcceptedId;
            }
            if (finalDigest is not null) state["finalDigest"] = finalDigest;
            if (truncated) state["previewTruncated"] = true;
            var sequence = next; var trimmed = trimmedThrough;
            if (outcome != "ended") state["state"] = outcome.StartsWith("stopped: ", StringComparison.Ordinal) ? "stopped" : outcome;
            else if ((string?)state["explicitReplyId"] is not null) state["state"] = "explicit_reply_accepted";
            else if (string.IsNullOrWhiteSpace(finalText)) state["state"] = "ended_without_text";
            else
            {
                while (copy.Count >= capacity)
                {
                    var evict = copy.OfType<JsonObject>().FirstOrDefault(e => PeerTurnEvictable(e) && !ReferenceEquals(e, initial));
                    if (evict is null) return new JsonObject { ["state"] = "deferred" };
                    trimmed = Math.Max(trimmed, (long)evict["sequence"]!); copy.Remove(evict);
                }
                var identity = new JsonObject { ["sequence"] = turn.Sequence, ["messageId"] = turn.MessageId,
                    ["promptId"] = turn.PromptId, ["sender"] = turn.Sender, ["recipient"] = turn.Recipient };
                var bytes = Encoding.UTF8.GetBytes(identity.ToJsonString());
                var replyId = "peer-answer-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (copy.OfType<JsonObject>().Any(e => (string?)e["id"] == replyId)) throw new InvalidOperationException("Automatic reply identity collision");
                copy.Add((JsonNode)new JsonObject { ["id"] = replyId, ["sequence"] = ++sequence, ["sender"] = turn.Recipient,
                    ["recipient"] = turn.Sender, ["text"] = truncated ? preview + "\n\n[Response truncated.]" : preview, ["replyTo"] = turn.MessageId,
                    ["peerAutoReply"] = true, ["approvedFullAccess"] = false, ["delivered"] = false });
                state["state"] = "automatic_reply_accepted"; state["automaticReplyId"] = replyId;
            }
            Save(copy, sequence, trimmed, completionClaimId: "peer-settlement:" + turn.MessageId, newPeerMailboxIncarnation: incarnation);
            return state.DeepClone().AsObject();
        }
    }
}
