using System.Text.Json.Nodes;

namespace Gorynych.Service;

// This is the internal delegation slice. Public tools and spawn records are separate work.
internal sealed partial class MailboxStore
{
    int assignmentCapacity;
    internal int AssignmentCapacityForTests { set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); lock (gate) assignmentCapacity = value; } }
    JsonObject assignments = new();
    const int AssignmentOperationLimit = 64;

    JsonObject AdmitAssignment(string id, string parent, string child, string text, string generation)
    {
        var copy = assignments.DeepClone().AsObject();
        while (copy.Count >= assignmentCapacity)
        {
            var victim = copy.Where(p => (string?)p.Value?["status"] is "completed" or "cancelled" or "failed" &&
                    (string?)p.Value?["delivery"] is "delivered" or "failed" or "suppressed")
                .OrderBy(p => (long)p.Value!["sequence"]!).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key).FirstOrDefault();
            if (victim is null) throw new InvalidOperationException("Assignment storage is full of active or unresolved assignments");
            copy.Remove(victim);
        }
        copy[id] = new JsonObject { ["parent"] = parent, ["child"] = child, ["source"] = "delegation",
            ["generation"] = generation, ["sequence"] = next + 1, ["fingerprint"] = WaitFingerprint(text), ["status"] = "open", ["revision"] = 1L,
            ["delivery"] = "queued", ["operations"] = new JsonObject() };
        return copy;
    }

    JsonObject? WithAssignmentDelivery(string id, string delivery)
    {
        if (assignments[id] is null) return null;
        var copy = assignments.DeepClone().AsObject();
        copy[id]!["delivery"] = delivery;
        return copy;
    }

    internal JsonObject? FindAssignment(string id, string participant)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject item ||
                ((string?)item["parent"] != participant && (string?)item["child"] != participant)) return null;
            return AssignmentView(id, item);
        }
    }

    static JsonObject AssignmentView(string id, JsonObject item)
    {
        var view = item.DeepClone().AsObject();
        view.Remove("fingerprint"); view.Remove("operations"); view["assignmentId"] = id;
        return view;
    }

    // The caller supplies an authenticated actor; request IDs are stable transport identities.
    internal JsonObject TransitionAssignment(string id, string actor, long expectedRevision,
        string operationId, string outcome, string summary, string? outcomeReportId = null, string? generation = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256 || summary.Length > 4096)
            throw new ArgumentException("Invalid operation ID or summary");
        if (outcome is not ("completed" or "blocked" or "failed" or "open" or "cancelled"))
            throw new ArgumentException("Unknown assignment outcome");
        if (outcomeReportId is not null && (outcome != "completed" || string.IsNullOrWhiteSpace(outcomeReportId) || outcomeReportId.Length > 256))
            throw new ArgumentException("A report reference is allowed only for completion");
        if (outcome != "open" && string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("An outcome requires a summary or reason");
        lock (gate)
        {
            if (assignments[id] is not JsonObject current ||
                (outcome == "cancelled" ? (string?)current["parent"] != actor : (string?)current["child"] != actor))
                throw new InvalidOperationException("Assignment not found or actor is not authorized for this outcome");
            if (generation is null || (string?)current["generation"] != generation)
                throw new InvalidOperationException("Assignment generation conflict");
            var ops = current["operations"]!.AsObject();
            if (ops[operationId] is JsonObject receipt)
            {
                if ((string?)receipt["actor"] != actor || (string?)receipt["outcome"] != outcome ||
                    (string?)receipt["summaryFingerprint"] != WaitFingerprint(summary) || (string?)receipt["outcomeReportId"] != outcomeReportId ||
                    (long?)receipt["expectedRevision"] != expectedRevision)
                    throw new InvalidOperationException("Operation ID is already used for another transition");
                return receipt["result"]!.DeepClone().AsObject();
            }
            if ((long?)current["revision"] != expectedRevision) throw new InvalidOperationException("Assignment revision conflict");
            var status = (string?)current["status"];
            if (status is "completed" or "cancelled" or "failed") throw new InvalidOperationException("Assignment is already terminal");
            if (outcome == "open" && status != "blocked") throw new InvalidOperationException("Only a blocked assignment can be reopened");
            var copy = assignments.DeepClone().AsObject(); var item = copy[id]!.AsObject();
            item["status"] = outcome; item["summary"] = summary; item["revision"] = expectedRevision + 1;
            if (outcome is "completed" or "failed" or "cancelled") item.Remove("continuation");
            if (outcomeReportId is not null) item["outcomeReportId"] = outcomeReportId;
            var entriesCopy = entries.DeepClone().AsArray();
            if (outcome == "cancelled" && (string?)item["delivery"] == "queued")
            {
                var entry = entriesCopy.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == id);
                if (entry is null || (bool?)entry["started"] == true) throw new InvalidOperationException("Assignment delivery state is inconsistent");
                entry["delivered"] = true; entry["suppressed"] = true; item["delivery"] = "suppressed";
            }
            var result = AssignmentView(id, item);
            var updatedOps = item["operations"]!.AsObject();
            while (updatedOps.Count >= AssignmentOperationLimit)
            {
                var oldest = updatedOps.OrderBy(p => (long)p.Value!["expectedRevision"]!).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key;
                updatedOps.Remove(oldest);
            }
            updatedOps[operationId] = new JsonObject { ["actor"] = actor, ["outcome"] = outcome,
                ["summaryFingerprint"] = WaitFingerprint(summary), ["outcomeReportId"] = outcomeReportId, ["expectedRevision"] = expectedRevision, ["result"] = result.DeepClone() };
            Save(entriesCopy, next, assigned: copy);
            return result;
        }
    }
    internal JsonObject UpdateContinuation(string id, string actor, string generation, long revision, string operation, string action, JsonObject? target)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject) throw new InvalidOperationException("Assignment unavailable");
            var copy = assignments.DeepClone().AsObject();
            // Recheck same-store identities inside the same lock as publication. A retained
            // operation receipt is checked first by Update and survives later target eviction.
            if (action == "register" && target is not null)
            {
                if ((string?)target["kind"] == "reply_wait")
                    target["confirmed"] = ContinuationWait((string)target["messageId"]!, actor, (long?)target["sequence"]) is not null && target["sequence"] is not null;
                else if ((string?)target["source"] == "delegation")
                    target["confirmed"] = assignments[(string)target["assignmentId"]!] is JsonObject dependency &&
                        (string?)dependency["parent"] == actor && (string?)dependency["generation"] == (string?)target["generation"];
            }
            var (changed, result) = AssignmentContinuations.Update(copy[id]!.AsObject(), actor, generation, revision, operation, action, target);
            result["assignmentId"] = id;
            if (changed) Save(entries.DeepClone().AsArray(), next, assigned: copy);
            return result;
        }
    }

    internal JsonObject[] AssignmentViews(string participant)
    {
        lock (gate) return assignments.Where(p => p.Value is JsonObject a &&
                ((string?)a["parent"] == participant || (string?)a["child"] == participant))
            .Select(p => AssignmentView(p.Key, p.Value!.AsObject())).ToArray();
    }

    internal JsonObject? RetryAssignmentTransition(string id, string actor, long revision, string operationId,
        string outcome, string summary, string? reportId, string generation)
    {
        lock (gate)
        {
            if (assignments[id] is not JsonObject current ||
                (outcome == "cancelled" ? (string?)current["parent"] != actor : (string?)current["child"] != actor))
                throw new InvalidOperationException("Assignment not found or actor is not authorized for this outcome");
            if ((string?)current["generation"] != generation) throw new InvalidOperationException("Assignment generation conflict");
            if (current["operations"]?[operationId] is not JsonObject receipt) return null;
            if ((string?)receipt["actor"] != actor || (string?)receipt["outcome"] != outcome ||
                (string?)receipt["summaryFingerprint"] != WaitFingerprint(summary) ||
                (string?)receipt["outcomeReportId"] != reportId || (long?)receipt["expectedRevision"] != revision)
                throw new InvalidOperationException("Operation ID is already used for another transition");
            return receipt["result"]!.DeepClone().AsObject();
        }
    }

}
