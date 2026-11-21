using System.Text.Json.Nodes;

namespace Gorynych.Service;

internal sealed partial class MailboxStore
{
    static bool IsPending(JsonObject item) => (bool?)item["delivered"] != true;

    internal static bool IsCoordinatorNotice(JsonObject item) => (bool?)item["coordinatorNotice"] == true;

    internal static bool IsCoordinatorItem(JsonObject item) => IsCoordinatorNotice(item) ||
        (bool?)item["coordinatorReply"] == true || (string?)item["reminderKind"] == "escalation";

    IEnumerable<JsonObject> CoordinatorNotices(JsonObject? snapshot = null) => (snapshot ?? assignments)
        .SelectMany(pair => new[] { pair.Value?["coordinatorPreviousNotice"], pair.Value?["coordinatorNotice"] }.OfType<JsonObject>());

    static JsonObject? NoticeForSequence(JsonObject? assignment, long? sequence) =>
        new[] { assignment?["coordinatorPreviousNotice"], assignment?["coordinatorNotice"] }
            .OfType<JsonObject>().FirstOrDefault(notice => (long?)notice["sequence"] == sequence);


    internal IReadOnlyList<JsonObject> ReadManagedQueue(string recipient, int limit, bool includeNotices = true)
    {
        lock (gate)
            return entries.OfType<JsonObject>().Concat(includeNotices ? CoordinatorNotices() : [])
                .Where(item => (string?)item["recipient"] == recipient && IsPending(item))
                .OrderBy(item => (long)item["sequence"]!).Take(limit)
                .Select(item => item.DeepClone().AsObject()).ToArray();
    }

    static void SetCoordinatorNotice(string assignmentId, JsonObject assignment, long sequence, string kind)
    {
        var generation = (string)assignment["generation"]!;
        if (assignment["coordinatorNotice"] is JsonObject current && IsPending(current) && (bool?)current["started"] == true)
        {
            if (assignment["coordinatorPreviousNotice"] is JsonObject previous && IsPending(previous))
                throw new InvalidOperationException("Previous coordinator handoff is unresolved");
            assignment["coordinatorPreviousNotice"] = current.DeepClone();
        }
        assignment["coordinatorNotice"] = new JsonObject
        {
            ["id"] = $"assignment-{generation}-{sequence}", ["sequence"] = sequence,
            ["sender"] = "service", ["recipient"] = (string?)assignment["parent"],
            ["assignmentId"] = assignmentId, ["assignmentGeneration"] = generation,
            ["assignmentRevision"] = (long?)assignment["revision"], ["assignee"] = (string?)assignment["child"],
            ["coordinatorNotice"] = true, ["kind"] = kind, ["status"] = (string?)assignment["status"],
            ["text"] = kind == "outcome" ? (string?)assignment["summary"] : null,
            ["reportId"] = (string?)assignment["outcomeReportId"] ?? (string?)assignment["latestReportId"],
            ["delivered"] = false, ["delivery"] = "queued",
        };
    }

    // Producer acknowledgement proves only the retained report, not the hosted
    // prompt boundary or the current route. The normal terminal path records those.
    internal bool RecordCoordinatorReport(string id, string generation, string child, string parent, string reportId,
        bool terminalNotEnded = false, bool resume = false)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject current || (bool?)current["coordinatorLifecycle"] != true ||
                (string?)current["generation"] != generation || (string?)current["child"] != child ||
                (string?)current["parent"] != parent || resume && (string?)current["status"] is not ("completed" or "failed")) return false;
            if ((string?)current["latestReportId"] == reportId && (bool?)current["coordinatorReportObserved"] == true &&
                (!terminalNotEnded || (bool?)current["coordinatorFallbackIssued"] == true || !StillNeedsOutcome(current))) return true;
            var copy = assignments.DeepClone().AsObject(); var item = copy[id]!.AsObject();
            var latest = (string?)item["latestReportId"];
            // Never let a delayed ACK replace a later allocation. A foreign or
            // unorderable incarnation is not authority to rewrite this pointer.
            var advance = latest is null || latest == reportId ||
                ReportStore.TryCompareReportOrder(reportId, latest, out var order) && order > 0;
            if (advance) item["latestReportId"] = reportId;
            if (!resume) item["coordinatorReportObserved"] = true;
            var sequence = next;
            if (!resume && terminalNotEnded && StillNeedsOutcome(item) && item["coordinatorNotice"] is null &&
                (bool?)item["coordinatorFallbackIssued"] != true && item["completionReminder"] is null)
            {
                SetCoordinatorNotice(id, item, ++sequence, "report_only"); item["coordinatorFallbackIssued"] = true;
            }
            else if (advance && item["coordinatorNotice"] is JsonObject notice && IsPending(notice) && (bool?)notice["started"] != true &&
                item["outcomeReportId"] is null) notice["reportId"] = reportId;
            Save(entries.DeepClone().AsArray(), sequence, assigned: copy);
            return true;
        }
    }

    // Used only on service restart, when a retained report outlived the exact
    // in-memory terminal boundary. It is not evidence of a successful turn.
    internal bool RecoverCoordinatorReportWithoutBoundary(string id, string generation, string child, string parent)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject item || (bool?)item["coordinatorLifecycle"] != true ||
                (string?)item["generation"] != generation || (string?)item["child"] != child || (string?)item["parent"] != parent ||
                (bool?)item["coordinatorReportObserved"] != true || (bool?)item["coordinatorInitialTurnSettled"] == true ||
                (bool?)item["coordinatorFallbackIssued"] == true || item["completionReminder"] is not null ||
                item["coordinatorNotice"] is not null || !StillNeedsOutcome(item)) return false;
            var copy = assignments.DeepClone().AsObject(); var updated = copy[id]!.AsObject();
            SetCoordinatorNotice(id, updated, next + 1, "report_only"); updated["coordinatorFallbackIssued"] = true;
            Save(entries.DeepClone().AsArray(), next + 1, assigned: copy); return true;
        }
    }

    internal bool RecordCoordinatorUnsupervisedTurn(string id, string generation, string child, string parent) =>
        RecordCoordinatorInitialTurn(id, generation, child, parent, supervision: false);

    internal bool RecordCoordinatorInitialTurn(string id, string generation, string child, string parent, bool supervision, string? hostId = null, string? hostGeneration = null, long? routeEpoch = null, bool fallbackAllowed = true)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject current || (bool?)current["coordinatorLifecycle"] != true ||
                (string?)current["generation"] != generation || (string?)current["child"] != child ||
                (string?)current["parent"] != parent) return false;
            var issue = fallbackAllowed && (bool?)current["coordinatorImplicitNoticeOptOut"] != true && !supervision && StillNeedsOutcome(current) && current["coordinatorNotice"] is null &&
                (bool?)current["coordinatorFallbackIssued"] != true && current["completionReminder"] is null;
            var upgradesLegacyMarker = supervision && routeEpoch is not null && (long?)current["coordinatorRouteEpoch"] is null;
            if (!issue && !upgradesLegacyMarker && (bool?)current["coordinatorInitialTurnSettled"] == true) return false;
            var copy = assignments.DeepClone().AsObject(); var item = copy[id]!.AsObject();
            item["coordinatorInitialTurnSettled"] = true;
            if (!fallbackAllowed) item["coordinatorImplicitNoticeOptOut"] = true;
            if (supervision) { item["coordinatorHostId"] = hostId; item["coordinatorHostGeneration"] = hostGeneration; item["coordinatorRouteEpoch"] = routeEpoch; }
            var sequence = next;
            if (issue)
            {
                SetCoordinatorNotice(id, item, ++sequence, "unsupervised");
                item["coordinatorFallbackIssued"] = true;
            }
            Save(entries.DeepClone().AsArray(), sequence, assigned: copy);
            return issue;
        }
    }

    // A stable, capacity-bounded inventory. Callers recheck exact identity under gate.
    internal JsonObject[] CoordinatorSupervisionInventory(bool includeReportOnly = false)
    {
        lock (gate)
            return assignments.Where(p => p.Value is JsonObject item && (bool?)item["coordinatorLifecycle"] == true &&
                    ((bool?)item["coordinatorInitialTurnSettled"] == true || includeReportOnly && (bool?)item["coordinatorReportObserved"] == true) &&
                    (bool?)item["coordinatorFallbackIssued"] != true && (bool?)item["coordinatorImplicitNoticeOptOut"] != true &&
                    item["completionReminder"] is null && StillNeedsOutcome(item))
                .Select(p => { var item = p.Value!.DeepClone().AsObject(); item["assignmentId"] = p.Key; return item; }).ToArray();
    }

    static bool StillNeedsOutcome(JsonObject item) => (string?)item["status"] == "open" && item["continuation"] is null;

    static void RemoveUnstartedCoordinatorEscalation(JsonObject item)
    {
        if (item["coordinatorNotice"] is JsonObject notice && (string?)notice["kind"] != "outcome" &&
            IsPending(notice) && (bool?)notice["started"] != true)
            item.Remove("coordinatorNotice");
    }

    internal sealed record CoordinatorReplyResume(string AssignmentId, string Generation, string WaitId, long WaitSequence, long ReplySequence);

    // Provenance is derived only from the durable exact wait and reply. An ambiguous
    // batch or more than one matching assignment stays on ordinary report delivery.
    CoordinatorReplyResume? ResolveCoordinatorReplyLocked(IReadOnlyList<JsonObject> batch, string parent)
    {
        if (batch.Count != 1 || (bool?)batch[0]["coordinatorReply"] != true ||
            (string?)batch[0]["replyTo"] is not { } waitId || (string?)batch[0]["id"] is not { } replyId ||
            (long?)batch[0]["sequence"] is not { } replySequence || (string?)batch[0]["recipient"] is not { } child ||
            replyWaits[waitId] is not JsonObject wait || (long?)wait["sequence"] is not { } waitSequence ||
            (string?)wait["sender"] != child || (string?)wait["recipient"] != (string?)batch[0]["sender"] ||
            (string?)wait["state"] != "replied" || (string?)wait["replyId"] != replyId ||
            (string?)wait["replyDelivery"] != "queued") return null;
        var matches = assignments.Where(p => p.Value is JsonObject row && (bool?)row["coordinatorLifecycle"] == true &&
            (string?)row["source"] == "delegation" && (string?)row["parent"] == parent && (string?)row["child"] == child &&
            (string?)row["status"] is ("open" or "blocked") && (string?)row["continuation"]?["kind"] == "reply_wait" &&
            (string?)row["continuation"]?["messageId"] == waitId && (long?)row["continuation"]?["sequence"] == waitSequence)
            .Take(2).ToArray();
        return matches.Length == 1 ? new(matches[0].Key, (string)matches[0].Value!["generation"]!, waitId, waitSequence, replySequence) : null;
    }

    internal CoordinatorReplyResume? ResolveCoordinatorReply(IReadOnlyList<JsonObject> batch, string parent)
    { lock (gate) return ResolveCoordinatorReplyLocked(batch, parent); }

    // Every member of the batch is claimed in the same mailbox publication. A lost reply
    // or restart after the claim leaves every member uncertain and ineligible for replay.
    internal bool TryStartCoordinatorBatch(IReadOnlyList<JsonObject> batch, CoordinatorReplyResume? resume = null, string? parent = null) => WriteCoordinatorBatch(batch, "handoff_started", resume, parent);

    internal void CompleteCoordinatorBatch(IReadOnlyList<JsonObject> batch) => WriteCoordinatorBatch(batch, "delivered");

    bool WriteCoordinatorBatch(IReadOnlyList<JsonObject> batch, string disposition, CoordinatorReplyResume? resume = null, string? parent = null)
    {
        lock (gate)
        {
            var starting = disposition == "handoff_started";
            if (starting && resume is not null && (parent is null || ResolveCoordinatorReplyLocked(batch, parent) != resume)) return false;
            var entryCopy = entries.DeepClone().AsArray();
            var assignmentCopy = assignments.DeepClone().AsObject();
            var waitCopy = replyWaits.DeepClone().AsObject();
            var changed = false;
            foreach (var expected in batch)
            {
                var id = (string)expected["id"]!;
                var item = IsCoordinatorNotice(expected)
                    ? NoticeForSequence(assignmentCopy[(string)expected["assignmentId"]!] as JsonObject, (long?)expected["sequence"])
                    : entryCopy.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == id);
                if (item is null || (long?)item["sequence"] != (long?)expected["sequence"] ||
                    (string?)item["recipient"] != (string?)expected["recipient"] || !IsPending(item) ||
                    starting && (!JsonNode.DeepEquals(item, expected) || (bool?)item["started"] == true))
                {
                    if (starting) return false;
                    continue; // an in-flight notice can be superseded by a newer assignment outcome
                }

                item[starting ? "started" : "delivered"] = true;
                if (IsCoordinatorNotice(item)) item["delivery"] = disposition;
                ApplyWaitDelivery(waitCopy, item, disposition);
                changed = true;
            }
            if (changed) Save(entryCopy, next, waits: waitCopy, assigned: assignmentCopy);
            return changed;
        }
    }

    internal void FailCoordinatorNotice(JsonObject expected, string reason, bool uncertain)
    {
        lock (gate)
        {
            var id = (string)expected["assignmentId"]!;
            if (NoticeForSequence(assignments[id] as JsonObject, (long?)expected["sequence"]) is not { } item || !IsPending(item)) return;
            var copy = assignments.DeepClone().AsObject();
            var notice = NoticeForSequence(copy[id] as JsonObject, (long?)expected["sequence"])!;
            notice["error"] = reason.Length <= 1024 ? reason : reason[..1024];
            notice["delivery"] = uncertain && (bool?)notice["started"] == true ? "uncertain" : "failed";
            notice["delivered"] = true;
            Save(entries.DeepClone().AsArray(), next, assigned: copy);
        }
    }
}
