using System.Text.Json.Nodes;

namespace Gorynych.Service;

// Internal-only final handoff boundary. This is not evidence that a prompt was sent,
// and is never called by a scheduler or exposed through a client protocol.
internal enum CompletionReminderSettlement { NativeEnded, NativeFailed, NativeCancelled, ProvenNotSent }

internal sealed record CompletionReminderClaim(string AttemptId, long AssignmentRevision, string Disposition, string? Stream = null, long Number = 0);
internal sealed record CompletionReminderAdmission(bool Acquired, CompletionReminderClaim? Claim);

// Service-owned immutable facts captured before the final handoff claim. Missing producer
// PID/start is allowed, but cannot be used as evidence that an old owner exited.
internal sealed record CompletionReminderBinding(string ProjectKey, string GroupId, string GroupInstance, string PromptId,
    string NativeUuid, string Stream, long ObservationRevision, string Digest, string HostId,
    string HostGeneration, int? ProducerPid, string? ProducerStart)
{
    internal JsonObject ToJson() => new() { ["projectKey"] = ProjectKey, ["groupId"] = GroupId, ["groupInstance"] = GroupInstance, ["promptId"] = PromptId,
        ["nativeUuid"] = NativeUuid, ["stream"] = Stream, ["observationRevision"] = ObservationRevision, ["digest"] = Digest,
        ["hostId"] = HostId, ["hostGeneration"] = HostGeneration, ["producerPid"] = ProducerPid, ["producerStart"] = ProducerStart };
    internal static bool CanonicalProducerStart(string value) =>
        DateTime.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc &&
        parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture) == value;
    internal bool Valid() => ProjectKey is { Length: > 0 and <= 512 } && GroupId is { Length: > 0 and <= 256 } &&
        Guid.TryParseExact(GroupInstance, "N", out _) && GroupInstance == GroupInstance.ToLowerInvariant() &&
        PromptId is { Length: > 0 and <= 256 } && Guid.TryParseExact(NativeUuid, "D", out _) && Guid.TryParseExact(Stream, "N", out _) &&
        ObservationRevision >= 0 && Digest is { Length: 64 } && Digest.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') &&
        HostId is { Length: > 0 and <= 256 } && HostGeneration is { Length: > 0 and <= 256 } &&
        (ProducerPid is null && ProducerStart is null || ProducerPid > 0 && ProducerStart is { Length: > 0 and <= 64 } start && CanonicalProducerStart(start));
    internal static CompletionReminderBinding? Parse(JsonObject? obj)
    {
        try
        {
            if (obj is null || obj.Count != 12) return null;
            var binding = new CompletionReminderBinding((string)obj["projectKey"]!, (string)obj["groupId"]!, (string)obj["groupInstance"]!, (string)obj["promptId"]!,
                (string)obj["nativeUuid"]!, (string)obj["stream"]!, (long)obj["observationRevision"]!, (string)obj["digest"]!,
                (string)obj["hostId"]!, (string)obj["hostGeneration"]!, (int?)obj["producerPid"], (string?)obj["producerStart"]);
            return binding.Valid() ? binding : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or NullReferenceException) { return null; }
    }
}
internal sealed record CompletionReminderRecovery(string Source, string AssignmentId, string Owner, string Assignee,
    string Generation, long OriginalRevision, string AttemptId, string Disposition, CompletionReminderBinding Binding, string Stream, long Number);

internal sealed partial class MailboxStore
{
    readonly int completionClaimCapacity;
    readonly int completionReceiptCapacity;
    JsonObject completionReceipts = new();
    static string CompletionReceiptKey(string generation, string attemptId) => generation + ":" + attemptId;
    // Prepare before the first claim. An uncertain publication never grants a stream.
    internal string PrepareCompletionStream()
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain");
            if (completionStream is not null) return completionStream;
            if (assignments.Any(p => p.Value?["completionReminder"] is not null))
                throw new InvalidOperationException("Legacy completion claim prevents stream initialization");
            var stream = Guid.NewGuid().ToString("N");
            Save(entries.DeepClone().AsArray(), next, completionClaimId: "__completion_stream__",
                newCompletionStream: stream, newCompletionNext: 0);
            return stream;
        }
    }
    internal bool CompletionStreamIsPristine(string stream)
    {
        lock (gate) return !publicationUncertain && completionStream == stream && completionNext == 0 &&
            !assignments.Any(p => p.Value?["completionReminder"] is not null);
    }

    bool HasOwnerReceipt(string id, JsonObject item, JsonObject claim)
    {
        if ((bool?)claim["ownerReceiptAcked"] == true) return true;
        if ((string?)item["generation"] is not { } generation || (string?)claim["attemptId"] is not { } attempt) return false;
        var receipt = completionReceipts[CompletionReceiptKey(generation, attempt)] as JsonObject;
        return (string?)receipt?["state"] is "unacked" or "acked" &&
            ReceiptMatches(receipt, "delegation", id, (string?)item["parent"] ?? "", (string?)item["child"] ?? "", generation,
                (long?)claim["assignmentRevision"] ?? -1, attempt);
    }
    bool CompletionAllowsAssignmentEviction(string id, JsonObject item)
    {
        if (item["completionReminder"] is not JsonObject claim) return item["completionReminder"] is null;
        return (string?)claim["disposition"] switch
        {
            "native_ended" or "native_failed" or "native_cancelled" or "not_sent" => true,
            "unknown_closed" => HasOwnerReceipt(id, item, claim),
            _ => false,
        };
    }
    static bool HoldsCompletionSlot(string? disposition) => disposition is not null and not
        ("native_ended" or "native_failed" or "native_cancelled" or "not_sent" or "unknown_closed");
    internal CompletionReminderAdmission ClaimCompletionReminder(string source, string id, string owner, string assignee,
        string generation, long revision, string attemptId, CompletionReminderBinding binding)
    {
        if (binding is null || !binding.Valid()) throw new ArgumentException("Invalid completion reminder binding");
        if (source != "delegation" || string.IsNullOrWhiteSpace(id) || id.Length > 256 ||
            string.IsNullOrWhiteSpace(owner) || owner.Length > 512 || string.IsNullOrWhiteSpace(assignee) || assignee.Length > 512 ||
            !Guid.TryParseExact(generation, "N", out _) || revision < 1 ||
            !Guid.TryParseExact(attemptId, "N", out _) || attemptId != attemptId.ToLowerInvariant())
            throw new ArgumentException("Invalid completion reminder identity");
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reopen the store before claiming");
            if (assignments[id] is not System.Text.Json.Nodes.JsonObject current || (string?)current["source"] != source ||
                (string?)current["parent"] != owner || (string?)current["child"] != assignee || (string?)current["generation"] != generation)
                return new(false, null);
            if (current["completionReminder"] is System.Text.Json.Nodes.JsonObject prior)
            {
                if ((string?)prior["attemptId"] != attemptId || (long?)prior["assignmentRevision"] != revision ||
                    !JsonNode.DeepEquals(prior["binding"], binding?.ToJson())) return new(false, null);
                if ((string?)prior["stream"] != completionStream || (long?)prior["number"] is not { } priorNumber || priorNumber < 1 || priorNumber > completionNext) return new(false, null);
                return new(false, new(attemptId, revision, (string)prior["disposition"]!, completionStream, priorNumber));
            }
            if ((long?)current["revision"] != revision || (string?)current["status"] != "open" ||
                (string?)current["delivery"] != "delivered" || current["continuation"] is not null)
                return new(false, null);
            if (assignments.Count(p => HoldsCompletionSlot((string?)p.Value?["completionReminder"]?["disposition"])) >= completionClaimCapacity)
                return new(false, null);
            var key = CompletionReceiptKey(generation, attemptId);
            if (completionReceipts.ContainsKey(key)) return new(false, null);
            var reserved = completionReceipts.DeepClone().AsObject();
            while (reserved.Count >= completionReceiptCapacity)
            {
                var victim = reserved.Where(p => (string?)p.Value?["state"] == "acked")
                    .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
                if (victim is null) return new(false, null);
                reserved.Remove(victim);
            }
            reserved[key] = new JsonObject { ["source"] = source, ["assignmentId"] = id, ["owner"] = owner,
                ["assignee"] = assignee, ["generation"] = generation, ["revision"] = revision, ["attemptId"] = attemptId, ["state"] = "reserved" };
            if (completionNext == long.MaxValue) return new(false, null);
            var stream = completionStream ?? Guid.NewGuid().ToString("N"); var number = completionNext + 1;
            var copy = assignments.DeepClone().AsObject();
            copy[id]!["completionReminder"] = new System.Text.Json.Nodes.JsonObject
                { ["attemptId"] = attemptId, ["assignmentRevision"] = revision, ["disposition"] = "handoff_started",
                    ["binding"] = binding.ToJson(), ["stream"] = stream, ["number"] = number };
            Save(entries.DeepClone().AsArray(), next, assigned: copy, completionClaimId: id, completionClaimStarted: true, receipts: reserved,
                newCompletionStream: stream, newCompletionNext: number);
            return new(true, new(attemptId, revision, "handoff_started", stream, number));
        }
    }

    // Exact, bounded lookup; legacy claims without a complete binding cannot be recovered
    // by inventing a current host, process or observation.
    internal CompletionReminderRecovery? ReadCompletionRecovery(string id, string owner, string assignee, string generation, string attemptId)
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile before recovery");
            if (assignments[id] is not JsonObject item || (string?)item["source"] != "delegation" ||
                (string?)item["parent"] != owner || (string?)item["child"] != assignee || (string?)item["generation"] != generation ||
                item["completionReminder"] is not JsonObject claim || (string?)claim["attemptId"] != attemptId ||
                (long?)claim["assignmentRevision"] is not { } revision || (string?)claim["disposition"] is not { } disposition ||
                CompletionReminderBinding.Parse(claim["binding"] as JsonObject) is not { } binding ||
                (string?)claim["stream"] is not { } stream || stream != completionStream ||
                (long?)claim["number"] is not { } number || number < 1 || number > completionNext) return null;
            return new("delegation", id, owner, assignee, generation, revision, attemptId, disposition, binding, stream, number);
        }
    }

    // Internal bounded inventory; a missing or legacy binding never becomes recovery evidence.
    internal (IReadOnlyList<CompletionReminderRecovery> Items, string? NextCursor) ListCompletionRecovery(string owner, string? cursor, int limit)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 512 || cursor is { Length: > 256 } || limit is < 1 or > 100)
            throw new ArgumentException("Invalid completion recovery page");
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile before recovery");
            var rows = new List<CompletionReminderRecovery>();
            var ids = assignments.Select(p => p.Key).Where(id => cursor is null || StringComparer.Ordinal.Compare(id, cursor) > 0)
                .OrderBy(id => id, StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (assignments[id] is not JsonObject item || (string?)item["parent"] != owner ||
                    item["completionReminder"] is not JsonObject claim ||
                    (string?)claim["disposition"] is not ("handoff_started" or "uncertain") ||
                    (string?)item["child"] is not { } child || (string?)item["generation"] is not { } generation ||
                    (string?)claim["attemptId"] is not { } attempt) continue;
                var found = ReadCompletionRecovery(id, owner, child, generation, attempt);
                if (found is null) continue;
                if (rows.Count == limit) return (rows, rows[^1].AssignmentId);
                rows.Add(found);
            }
            return (rows, null);
        }
    }

    internal (IReadOnlyList<CompletionReminderRecovery> Items, bool Unknown) CompletionReminderPending(string? project, string? group, string? participant)
    {
        lock (gate)
        {
            if (publicationUncertain) return ([], true);
            var result = new List<CompletionReminderRecovery>(); var unknown = false;
            foreach (var (id, value) in assignments)
            {
                if (value is not JsonObject item || item["completionReminder"] is not JsonObject claim ||
                    (string?)claim["disposition"] is not ("handoff_started" or "uncertain")) continue;
                var owner = (string?)item["parent"]; var child = (string?)item["child"];
                if (participant is not null && participant != owner && participant != child) continue;
                var binding = CompletionReminderBinding.Parse(claim["binding"] as JsonObject);
                if (binding is null) { unknown = true; continue; }
                if (project is not null && binding.ProjectKey != project || group is not null && binding.GroupId != group) continue;
                if (owner is null || child is null || (string?)item["generation"] is not { } generation ||
                    (string?)claim["attemptId"] is not { } attempt || ReadCompletionRecovery(id, owner, child, generation, attempt) is not { } recovery)
                { unknown = true; continue; }
                result.Add(recovery);
            }
            return (result, unknown);
        }
    }

    // The trusted caller must already possess the exact native terminal or no-input proof.
    // Assignment outcome/revision can have advanced independently since the handoff claim.
    internal bool SettleCompletionReminder(string source, string id, string owner, string assignee, string generation,
        long originalRevision, string attemptId, CompletionReminderSettlement settlement)
    {
        var disposition = settlement switch
        {
            CompletionReminderSettlement.NativeEnded => "native_ended", CompletionReminderSettlement.NativeFailed => "native_failed",
            CompletionReminderSettlement.NativeCancelled => "native_cancelled", CompletionReminderSettlement.ProvenNotSent => "not_sent",
            _ => throw new ArgumentOutOfRangeException(nameof(settlement)),
        };
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile before settlement");
            if (source != "delegation" || assignments[id] is not System.Text.Json.Nodes.JsonObject item ||
                (string?)item["source"] != source || (string?)item["parent"] != owner || (string?)item["child"] != assignee ||
                (string?)item["generation"] != generation || item["completionReminder"] is not System.Text.Json.Nodes.JsonObject claim ||
                (string?)claim["attemptId"] != attemptId || (long?)claim["assignmentRevision"] != originalRevision) return false;
            var prior = (string?)claim["disposition"];
            if (prior == disposition) return true;
            if (prior is not ("handoff_started" or "uncertain" or "unknown_closed")) return false;
            if (prior == "unknown_closed" && !HasOwnerReceipt(id, item, claim)) return false;
            var copy = assignments.DeepClone().AsObject(); copy[id]!["completionReminder"]!["disposition"] = disposition;
            var receipts = completionReceipts.DeepClone().AsObject(); var key = CompletionReceiptKey(generation, attemptId);
            if ((string?)receipts[key]?["state"] == "reserved") receipts.Remove(key);
            Save(entries.DeepClone().AsArray(), next, assigned: copy, completionClaimId: id, receipts: receipts);
            return true;
        }
    }

    // Caller must know the original handoff can no longer be reconciled; disconnect or
    // reopening the store alone is insufficient. This closes only the retention hold.
    internal bool CloseCompletionReminderUnknown(string source, string id, string owner, string assignee,
        string generation, long originalRevision, string attemptId)
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile before closing");
            if (source != "delegation" || assignments[id] is not System.Text.Json.Nodes.JsonObject item ||
                (string?)item["source"] != source || (string?)item["parent"] != owner || (string?)item["child"] != assignee ||
                (string?)item["generation"] != generation || item["completionReminder"] is not System.Text.Json.Nodes.JsonObject claim ||
                (string?)claim["attemptId"] != attemptId || (long?)claim["assignmentRevision"] != originalRevision) return false;
            var prior = (string?)claim["disposition"];
            if (prior == "unknown_closed") return HasOwnerReceipt(id, item, claim);
            if (prior is not ("handoff_started" or "uncertain")) return false;
            var key = CompletionReceiptKey(generation, attemptId);
            if (!ReceiptMatches(completionReceipts[key] as JsonObject, source, id, owner, assignee, generation, originalRevision, attemptId) ||
                (string?)completionReceipts[key]?["state"] != "reserved") return false;
            var copy = assignments.DeepClone().AsObject(); copy[id]!["completionReminder"]!["disposition"] = "unknown_closed";
            var receipts = completionReceipts.DeepClone().AsObject(); receipts[key]!["state"] = "unacked";
            receipts[key]!["event"] = "handoff_unknown_at_closure";
            Save(entries.DeepClone().AsArray(), next, assigned: copy, completionClaimId: id, receipts: receipts);
            return true;
        }
    }

    static bool ReceiptMatches(JsonObject? receipt, string source, string id, string owner, string assignee,
        string generation, long revision, string attemptId) => receipt is not null && (string?)receipt["source"] == source &&
        (string?)receipt["assignmentId"] == id && (string?)receipt["owner"] == owner && (string?)receipt["assignee"] == assignee &&
        (string?)receipt["generation"] == generation && (long?)receipt["revision"] == revision && (string?)receipt["attemptId"] == attemptId;

    // The owner is authenticated by the future service boundary; no actor is accepted from client metadata.
    internal JsonObject? ReadCompletionUnknownReceipt(string owner, string id, string assignee, string generation, long revision, string attemptId)
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile first");
            var receipt = completionReceipts[CompletionReceiptKey(generation, attemptId)] as JsonObject;
            if (!ReceiptMatches(receipt, "delegation", id, owner, assignee, generation, revision, attemptId) ||
                (string?)receipt!["state"] is not ("unacked" or "acked")) return null;
            return receipt.DeepClone().AsObject();
        }
    }

    internal JsonObject ListCompletionUnknownReceipts(string owner, string? after = null, int limit = 32)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 512 || limit is < 1 or > 100 || after is { Length: > 65 })
            throw new ArgumentException("Invalid receipt page request");
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile first");
            var page = completionReceipts.Where(p => p.Value is JsonObject item && (string?)item["owner"] == owner &&
                (string?)item["state"] == "unacked" && (after is null || string.CompareOrdinal(p.Key, after) > 0))
                .OrderBy(p => p.Key, StringComparer.Ordinal).Take(limit + 1).ToArray();
            var nextCursor = page.Length > limit ? page[limit - 1].Key : null;
            return new JsonObject { ["items"] = new JsonArray(page.Take(limit).Select(p => p.Value!.DeepClone()).ToArray()), ["nextCursor"] = nextCursor };
        }
    }

    internal bool AckCompletionUnknownReceipt(string owner, string id, string assignee, string generation, long revision, string attemptId)
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reconcile first");
            var key = CompletionReceiptKey(generation, attemptId); var item = completionReceipts[key] as JsonObject;
            if (!ReceiptMatches(item, "delegation", id, owner, assignee, generation, revision, attemptId)) return false;
            if ((string?)item!["state"] == "acked") return true;
            if ((string?)item["state"] != "unacked") return false;
            var receipts = completionReceipts.DeepClone().AsObject(); receipts[key]!["state"] = "acked";
            JsonObject? assigned = null;
            if (assignments[id] is JsonObject existing && (string?)existing["parent"] == owner && (string?)existing["child"] == assignee &&
                (string?)existing["generation"] == generation && (string?)existing["completionReminder"]?["attemptId"] == attemptId &&
                (long?)existing["completionReminder"]?["assignmentRevision"] == revision)
            {
                assigned = assignments.DeepClone().AsObject(); assigned[id]!["completionReminder"]!["ownerReceiptAcked"] = true;
            }
            Save(entries.DeepClone().AsArray(), next, assigned: assigned, completionClaimId: id, receipts: receipts);
            return true;
        }
    }

    internal CompletionReminderClaim? ReadCompletionReminder(string id, string owner, string assignee, string generation)
    {
        lock (gate)
        {
            if (publicationUncertain) throw new InvalidOperationException("Mailbox publication is uncertain; reopen the store before reading a claim");
            if (assignments[id] is not System.Text.Json.Nodes.JsonObject current || (string?)current["source"] != "delegation" ||
                (string?)current["parent"] != owner || (string?)current["child"] != assignee || (string?)current["generation"] != generation ||
                current["completionReminder"] is not System.Text.Json.Nodes.JsonObject claim) return null;
            return new((string)claim["attemptId"]!, (long)claim["assignmentRevision"]!, (string)claim["disposition"]!);
        }
    }
}
