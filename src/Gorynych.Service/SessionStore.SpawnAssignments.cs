using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

public sealed partial class SessionStore
{
    internal int SpawnAssignmentCapacity { get; set; } = 1000;
    const int SpawnOperationCapacity = 64;

    JsonObject NewSpawnAssignment(SessionRecord child, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || string.IsNullOrWhiteSpace(child.ParentSessionId))
            throw new InvalidOperationException("Spawn assignment requires a valid ID and parent");
        return new JsonObject { ["assignmentId"] = id, ["parent"] = child.ParentSessionId, ["child"] = child.SessionId,
            ["source"] = "spawn", ["generation"] = Guid.NewGuid().ToString("N"), ["status"] = "open", ["revision"] = 1L, ["delivery"] = "queued", ["operations"] = new JsonObject() };
    }

    static JsonObject SpawnView(JsonObject state)
    {
        var result = state.DeepClone().AsObject(); result.Remove("operations"); return result;
    }

    internal JsonObject? FindSpawnAssignment(string id, string participant)
    {
        lock (gate)
        {
            var state = records.Values.Select(r => r.SpawnAssignment).FirstOrDefault(a => (string?)a?["assignmentId"] == id);
            if (state is null || ((string?)state["parent"] != participant && (string?)state["child"] != participant)) return null;
            return SpawnView(state);
        }
    }

    // writeGate -> gate matches CommitSpawnAssignment; publish copies only after durable write.
    T ChangeSpawn<T>(string child, Func<SessionRecord, JsonObject, (bool Changed, T Result)> change)
    {
        lock (writeGate)
        lock (gate)
        {
            if (!records.TryGetValue(child, out var current) || current.SpawnAssignment is null)
                throw new InvalidOperationException("Spawn assignment not found");
            var candidate = Clone(current); var (changed, result) = change(candidate, candidate.SpawnAssignment!);
            if (!changed) return result;
            CommitSpawnChange(child, current, candidate);
            return result;
        }
    }

    // Both caller locks are held: no eviction or identity replacement can interleave.
    void CommitSpawnChange(string child, SessionRecord current, SessionRecord candidate)
    {
        var snapshot = records.Values.Select(r => r.SessionId == child ? candidate : r).OrderBy(r => r.CreatedAt).ToList();
        WriteFile(path, JsonSerializer.Serialize(snapshot, ServiceJsonContext.Default.ListSessionRecord), true);
        records[child] = candidate; written = ++changes;
        Changed?.Invoke(Clone(current), Clone(candidate));
    }

    internal bool ClaimSpawnAssignment(string child, string id, string promptId, string? reportId = null) => ChangeSpawn(child, (candidate, state) =>
    {
        if ((string?)state["assignmentId"] != id) throw new InvalidOperationException("Spawn assignment identity mismatch");
        if (candidate.Deleted || (string?)state["status"] == "cancelled" || (string?)state["delivery"] != "queued") return (false, false);
        state["delivery"] = "started"; state["promptId"] = promptId;
        if (reportId is not null) state["reportId"] = reportId;
        return (true, true);
    });

    internal bool AbandonUnstartedSpawn(string child, string id) => ChangeSpawn(child, (_, state) =>
    {
        if ((string?)state["assignmentId"] != id) throw new InvalidOperationException("Spawn assignment identity mismatch");
        if ((string?)state["delivery"] != "queued") return (false, false);
        state["delivery"] = "not_started"; return (true, true);
    });

    internal bool RecordSpawnDelivery(string child, string? promptId, string delivery) =>
        RecordSpawnDeliveryCore(child, promptId, delivery, null);

    // Call only after the conductor has verified the host's report/session/prompt receipt.
    internal bool RecordProvenUnstartedSpawn(string child, string? promptId, string reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId) || reportId.Length > 256) throw new ArgumentException("A matching host report ID is required");
        return RecordSpawnDeliveryCore(child, promptId, "not_started", reportId);
    }

    bool RecordSpawnDeliveryCore(string child, string? promptId, string delivery, string? provenReportId)
    {
        if (promptId is null || delivery is not ("delivered" or "failed" or "not_started" or "uncertain")) return false;
        lock (writeGate)
        lock (gate)
        {
            if (!records.TryGetValue(child, out var current) || current.SpawnAssignment is not { } existing ||
                (string?)existing["promptId"] != promptId ||
                (provenReportId is not null && (string?)existing["reportId"] != provenReportId)) return false;
            var prior = (string?)existing["delivery"];
            if (prior is not ("started" or "uncertain") ||
                (prior == "uncertain" && (delivery == "uncertain" || delivery == "not_started" && provenReportId is null))) return false;
            var candidate = Clone(current); var state = candidate.SpawnAssignment!;
            state["delivery"] = delivery;
            if (provenReportId is not null) state["hostRefusalReportId"] = provenReportId;
            CommitSpawnChange(child, current, candidate);
            return true;
        }
    }

    internal JsonObject TransitionSpawnAssignment(string child, string id, string actor, long expectedRevision,
        string operationId, string outcome, string summary, string? outcomeReportId = null, string? generation = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256 || summary.Length > 4096)
            throw new ArgumentException("Invalid operation ID or summary");
        if (outcome is not ("completed" or "blocked" or "failed" or "open" or "cancelled")) throw new ArgumentException("Unknown assignment outcome");
        if (outcomeReportId is not null && (outcome != "completed" || string.IsNullOrWhiteSpace(outcomeReportId) || outcomeReportId.Length > 256))
            throw new ArgumentException("A report reference is allowed only for completion");
        if (outcome != "open" && string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("An outcome requires a summary or reason");
        var fingerprint = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(summary)));
        return ChangeSpawn(child, (_, state) =>
        {
            if ((string?)state["assignmentId"] != id || (outcome == "cancelled" ? (string?)state["parent"] != actor : (string?)state["child"] != actor))
                throw new InvalidOperationException("Assignment not found or actor is not authorized for this outcome");
            if (generation is null || (string?)state["generation"] != generation)
                throw new InvalidOperationException("Assignment generation conflict");
            var ops = state["operations"]!.AsObject();
            if (ops[operationId] is JsonObject prior)
            {
                if ((string?)prior["actor"] != actor || (string?)prior["outcome"] != outcome ||
                    (string?)prior["summaryFingerprint"] != fingerprint || (string?)prior["outcomeReportId"] != outcomeReportId ||
                    (long?)prior["expectedRevision"] != expectedRevision)
                    throw new InvalidOperationException("Operation ID is already used for another transition");
                return (false, prior["result"]!.DeepClone().AsObject());
            }
            if ((long?)state["revision"] != expectedRevision) throw new InvalidOperationException("Assignment revision conflict");
            var status = (string?)state["status"];
            if (status is "completed" or "cancelled" or "failed") throw new InvalidOperationException("Assignment is already terminal");
            if (outcome == "open" && status != "blocked") throw new InvalidOperationException("Only a blocked assignment can be reopened");
            state["status"] = outcome; state["summary"] = summary; state["revision"] = expectedRevision + 1;
            if (outcome is "completed" or "failed" or "cancelled") state.Remove("continuation");
            if (outcomeReportId is not null) state["outcomeReportId"] = outcomeReportId;
            if (outcome == "cancelled" && (string?)state["delivery"] == "queued") state["delivery"] = "suppressed";
            var result = SpawnView(state);
            while (ops.Count >= SpawnOperationCapacity)
                ops.Remove(ops.OrderBy(p => (long)p.Value!["expectedRevision"]!).ThenBy(p => p.Key, StringComparer.Ordinal).First().Key);
            ops[operationId] = new JsonObject { ["actor"] = actor, ["outcome"] = outcome, ["summaryFingerprint"] = fingerprint,
                ["outcomeReportId"] = outcomeReportId, ["expectedRevision"] = expectedRevision, ["result"] = result.DeepClone() };
            return (true, result);
        });
    }

    internal JsonObject UpdateSpawnContinuation(string child, string id, string actor, string generation, long revision, string operation, string action, JsonObject? target) =>
        ChangeSpawn(child, (_, state) =>
        {
            if ((string?)state["assignmentId"] != id) throw new InvalidOperationException("Assignment unavailable");
            if (action == "register" && (string?)target?["kind"] == "child_assignment" && (string?)target?["source"] == "spawn")
                target!["confirmed"] = records.Values.Any(r => r.SpawnAssignment is JsonObject dependency &&
                    (string?)dependency["assignmentId"] == (string?)target["assignmentId"] &&
                    (string?)dependency["parent"] == actor && (string?)dependency["generation"] == (string?)target["generation"]);
            return AssignmentContinuations.Update(state, actor, generation, revision, operation, action, target);
        });

    // No spawn prompt is blindly replayed after conductor loss. A claim is not a native receipt.
    internal void ReconcileSpawnAssignments()
    {
        lock (writeGate)
        lock (gate)
        {
            var changeset = records.Values.Where(r => (string?)r.SpawnAssignment?["delivery"] is "queued" or "started").ToArray();
            if (changeset.Length == 0) return;
            var copies = changeset.ToDictionary(r => r.SessionId, Clone, StringComparer.Ordinal);
            foreach (var c in copies.Values)
                c.SpawnAssignment!["delivery"] = (string?)c.SpawnAssignment["delivery"] == "queued" ? "not_started" : "uncertain";
            var snapshot = records.Values.Select(r => copies.TryGetValue(r.SessionId, out var copy) ? copy : r).OrderBy(r => r.CreatedAt).ToList();
            WriteFile(path, JsonSerializer.Serialize(snapshot, ServiceJsonContext.Default.ListSessionRecord), true);
            foreach (var (id, candidate) in copies) records[id] = candidate;
            written = ++changes;
        }
    }
    internal JsonObject[] SpawnAssignmentViews(string participant)
    {
        lock (gate) return records.Values.Select(r => r.SpawnAssignment).Where(a => a is not null &&
                ((string?)a["parent"] == participant || (string?)a["child"] == participant))
            .Select(a => SpawnView(a!)).ToArray();
    }

    internal JsonObject? RetrySpawnTransition(string child, string id, string actor, long revision,
        string operationId, string outcome, string summary, string? reportId, string generation)
    {
        lock (gate)
        {
            if (!records.TryGetValue(child, out var record) || record.SpawnAssignment is not JsonObject state ||
                (string?)state["assignmentId"] != id ||
                (outcome == "cancelled" ? (string?)state["parent"] != actor : (string?)state["child"] != actor))
                throw new InvalidOperationException("Assignment not found or actor is not authorized for this outcome");
            if ((string?)state["generation"] != generation) throw new InvalidOperationException("Assignment generation conflict");
            if (state["operations"]?[operationId] is not JsonObject prior) return null;
            var fingerprint = Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(summary)));
            if ((string?)prior["actor"] != actor || (string?)prior["outcome"] != outcome ||
                (string?)prior["summaryFingerprint"] != fingerprint || (string?)prior["outcomeReportId"] != reportId ||
                (long?)prior["expectedRevision"] != revision)
                throw new InvalidOperationException("Operation ID is already used for another transition");
            return prior["result"]!.DeepClone().AsObject();
        }
    }

}
