using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    JsonObject PublicAssignment(JsonObject state) => new()
    {
        ["source"] = state["source"]?.DeepClone(), ["assignmentId"] = state["assignmentId"]?.DeepClone(),
        ["parent"] = state["parent"]?.DeepClone(), ["child"] = state["child"]?.DeepClone(),
        ["generation"] = state["generation"]?.DeepClone(), ["status"] = state["status"]?.DeepClone(), ["revision"] = state["revision"]?.DeepClone(),
        ["delivery"] = state["delivery"]?.DeepClone(), ["summary"] = state["summary"]?.DeepClone(),
        ["outcomeReportId"] = state["outcomeReportId"]?.DeepClone(),
        ["continuation"] = ContinuationView(state),
    };

    JsonObject? ContinuationView(JsonObject owner)
    {
        if (owner["continuation"] is not JsonObject reference) return null;
        var actor = (string)owner["child"]!;
        if ((string?)reference["kind"] == "reply_wait")
        {
            var message = (string)reference["messageId"]!;
            var wait = mailbox.ContinuationWait(message, actor, (long)reference["sequence"]!);
            var state = (string?)wait?["state"]; var requestDelivery = (string?)wait?["requestDelivery"];
            var disposition = wait is null ? "unavailable" : state == "replied" ? "ready" : state == "cancelled" ? "cancelled" :
                requestDelivery == "failed" ? "undeliverable" : requestDelivery == "uncertain" ? "unknown" : "waiting";
            return new JsonObject { ["kind"] = "reply_wait", ["messageId"] = message, ["state"] = disposition,
                ["requestDelivery"] = requestDelivery, ["replyDelivery"] = wait?["replyDelivery"]?.DeepClone(),
                ["reason"] = wait is null ? "Retained reply wait unavailable" : disposition is "undeliverable" or "unknown" ? requestDelivery : null };
        }
        if ((string?)reference["kind"] != "child_assignment") return null;
        var source = (string)reference["source"]!; var id = (string)reference["assignmentId"]!;
        var generation = (string)reference["generation"]!;
        var child = AssignmentFor(source, id, actor);
        if ((string?)child?["parent"] != actor || (string?)child?["generation"] != generation) child = null;
        var status = (string?)child?["status"]; var delivery = (string?)child?["delivery"];
        var childState = child is null ? "unavailable" : status is "completed" or "failed" or "cancelled" ? "ready" :
            status == "blocked" ? "blocked" : delivery is "failed" or "not_started" or "suppressed" ? "undeliverable" :
            delivery == "uncertain" ? "unknown" : "waiting";
        var reason = childState switch
        {
            "unavailable" => "Retained child assignment unavailable",
            "blocked" => "Child assignment is blocked",
            "ready" => status,
            "undeliverable" => "Child assignment delivery: " + delivery,
            "unknown" => "Child assignment delivery is uncertain",
            _ => null,
        };
        return new JsonObject { ["kind"] = "child_assignment", ["source"] = source, ["assignmentId"] = id,
            ["generation"] = generation, ["state"] = childState, ["delivery"] = delivery, ["reason"] = reason };
    }

    internal Action? ContinuationAfterLookupForTests { get; set; }

    internal JsonObject UpdateContinuation(JsonRpcRequest request)
    {
        if (Draining) throw Restarting();
        var actor = MailboxPeer(request);
        var args = AssignmentParams(request, "source", "assignmentId", "generation", "expectedRevision", "operationId", "action", "messageId", "targetSource", "targetAssignmentId", "targetGeneration");
        var (source, id) = AssignmentIdentity(args);
        var generation = RequiredMailboxText(args, "generation"); var operation = RequiredMailboxText(args, "operationId");
        var action = RequiredMailboxText(args, "action");
        if (!Guid.TryParseExact(generation, "N", out _) || operation.Length > 256 || action is not ("register" or "withdraw") ||
            args["expectedRevision"] is not JsonValue value || !(value.TryGetValue<long>(out var revision) ||
                value.TryGetValue<int>(out var small) && (revision = small) >= 0) || revision < 1)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid continuation operation");
        var hasMessage = args.ContainsKey("messageId");
        var hasChild = args.ContainsKey("targetSource") || args.ContainsKey("targetAssignmentId") || args.ContainsKey("targetGeneration");
        if (action == "withdraw" && (hasMessage || hasChild) || action == "register" && hasMessage == hasChild)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Register requires one target; withdraw accepts none");
        string? message = null, childSource = null, childId = null, childGeneration = null;
        if (action == "register" && hasMessage)
        {
            message = RequiredMailboxText(args, "messageId");
            if (message.Length > 256) throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid message ID");
        }
        else if (action == "register")
        {
            childSource = RequiredMailboxText(args, "targetSource"); childId = RequiredMailboxText(args, "targetAssignmentId");
            childGeneration = RequiredMailboxText(args, "targetGeneration");
            if (childSource is not ("spawn" or "delegation") || childId.Length > 256 || !Guid.TryParseExact(childGeneration, "N", out _))
                throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid child assignment identity");
        }
        try
        {
            var owner = AssignmentFor(source, id, actor);
            if (owner is null || (string?)owner["child"] != actor) throw new InvalidOperationException("Assignment unavailable");
            JsonObject? target = null;
            if (message is not null)
            {
                target = mailbox.ContinuationWait(message, actor) ?? new JsonObject { ["messageId"] = message };
                target["kind"] = "reply_wait"; target["confirmed"] = target["sequence"] is not null;
                target.Remove("state"); target.Remove("requestDelivery"); target.Remove("replyDelivery");
            }
            else if (childId is not null)
            {
                var dependency = AssignmentFor(childSource!, childId, actor);
                target = new JsonObject { ["kind"] = "child_assignment", ["source"] = childSource, ["assignmentId"] = childId,
                    ["generation"] = childGeneration, ["confirmed"] = (string?)dependency?["parent"] == actor && (string?)dependency?["generation"] == childGeneration };
            }
            ContinuationAfterLookupForTests?.Invoke();
            var result = source == "spawn" ? store.UpdateSpawnContinuation((string)owner["child"]!, id, actor, generation, revision, operation, action, target)
                : mailbox.UpdateContinuation(id, actor, generation, revision, operation, action, target);
            return PublicAssignment(result);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new JsonRpcException(JsonRpcException.InvalidParams, "Continuation unavailable: " + e.Message); }
    }

    static JsonObject AssignmentParams(JsonRpcRequest request, params string[] allowed)
    {
        if (request.Params is not JsonObject args || args.Any(p => p.Key != "_meta" && !allowed.Contains(p.Key, StringComparer.Ordinal)))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid assignment parameters");
        return args;
    }

    static (string Source, string Id) AssignmentIdentity(JsonObject args)
    {
        var source = RequiredMailboxText(args, "source"); var id = RequiredMailboxText(args, "assignmentId");
        if (source is not ("spawn" or "delegation") || id.Length > 256)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "source must be spawn or delegation and assignmentId must be bounded");
        return (source, id);
    }

    JsonObject? AssignmentFor(string source, string id, string actor) => source == "spawn"
        ? store.FindSpawnAssignment(id, actor) : mailbox.FindAssignment(id, actor);

    internal JsonObject ReadAssignment(JsonRpcRequest request)
    {
        var actor = MailboxPeer(request); var args = AssignmentParams(request, "source", "assignmentId");
        var (source, id) = AssignmentIdentity(args);
        var item = AssignmentFor(source, id, actor) ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Assignment unavailable or not accessible");
        return PublicAssignment(item);
    }

    internal JsonObject ListAssignments(JsonRpcRequest request)
    {
        var actor = MailboxPeer(request); var args = AssignmentParams(request, "cursor", "limit");
        var limit = 50;
        if (args.TryGetPropertyValue("limit", out var n) && (n is not JsonValue v || !v.TryGetValue<int>(out limit) || limit is < 1 or > 100))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "limit must be 1..100");
        string? afterSource = null, afterId = null;
        if (args.TryGetPropertyValue("cursor", out var raw))
        {
            try
            {
                if (raw is not JsonValue cv || !cv.TryGetValue<string>(out var cursor) || cursor.Length > 4096) throw new FormatException();
                var node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))) as JsonObject ?? throw new FormatException();
                if ((string?)node["actor"] != actor || (string?)node["source"] is not ("spawn" or "delegation") ||
                    (string?)node["id"] is not { Length: > 0 and <= 256 }) throw new FormatException();
                afterSource = (string)node["source"]!; afterId = (string)node["id"]!;
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
            { throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid assignment cursor"); }
        }
        var items = store.SpawnAssignmentViews(actor).Concat(mailbox.AssignmentViews(actor))
            .Where(a => afterSource is null || string.CompareOrdinal((string?)a["source"], afterSource) > 0 ||
                ((string?)a["source"] == afterSource && string.CompareOrdinal((string?)a["assignmentId"], afterId) > 0))
            .OrderBy(a => (string?)a["source"], StringComparer.Ordinal).ThenBy(a => (string?)a["assignmentId"], StringComparer.Ordinal)
            .Take(limit + 1).ToArray();
        string? next = null;
        if (items.Length > limit)
        {
            var last = items[limit - 1];
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["actor"] = actor,
                ["source"] = (string?)last["source"], ["id"] = (string?)last["assignmentId"] }.ToJsonString()));
        }
        return new JsonObject { ["items"] = new JsonArray(items.Take(limit).Select(a => (JsonNode)PublicAssignment(a)).ToArray()), ["nextCursor"] = next };
    }

    internal JsonObject UpdateAssignment(JsonRpcRequest request)
    {
        if (Draining) throw Restarting();
        var actor = MailboxPeer(request);
        var args = AssignmentParams(request, "source", "assignmentId", "expectedRevision", "operationId", "outcome", "summary", "reportId", "generation");
        var (source, id) = AssignmentIdentity(args);
        var operation = RequiredMailboxText(args, "operationId"); var outcome = RequiredMailboxText(args, "outcome");
        var generation = RequiredMailboxText(args, "generation");
        if (!Guid.TryParseExact(generation, "N", out _)) throw new JsonRpcException(JsonRpcException.InvalidParams, "generation must be the exact assignment generation");
        if (operation.Length > 256 || outcome is not ("completed" or "blocked" or "failed" or "open" or "cancelled"))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid assignment operation");
        if (args["expectedRevision"] is not JsonValue rev || !(rev.TryGetValue<long>(out var revision) ||
            rev.TryGetValue<int>(out var small) && (revision = small) >= 0) || revision < 1)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "expectedRevision must be a positive integer");
        var summary = "";
        if (args.TryGetPropertyValue("summary", out var sm) && (sm is not JsonValue sv || !sv.TryGetValue<string>(out summary)))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "summary must be text");
        if (summary.Length > 4096 || (outcome != "open" && string.IsNullOrWhiteSpace(summary)))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "A bounded summary is required for this outcome");
        string? reportId = null;
        if (args.TryGetPropertyValue("reportId", out var r) && (r is not JsonValue rv || !rv.TryGetValue<string>(out reportId) ||
            string.IsNullOrWhiteSpace(reportId) || reportId.Length > 256 || outcome != "completed"))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "reportId is permitted only for completion");
        try
        {
            var item = AssignmentFor(source, id, actor) ?? throw new InvalidOperationException("Assignment unavailable or not accessible");
            var parent = (string)item["parent"]!; var child = (string)item["child"]!;
            JsonObject? retry = source == "spawn" ? store.RetrySpawnTransition(child, id, actor, revision, operation, outcome, summary, reportId, generation)
                : mailbox.RetryAssignmentTransition(id, actor, revision, operation, outcome, summary, reportId, generation);
            if (retry is not null) return PublicAssignment(retry);
            JsonObject Commit() => source == "spawn" ? store.TransitionSpawnAssignment(child, id, actor, revision, operation, outcome, summary, reportId, generation)
                : mailbox.TransitionAssignment(id, actor, revision, operation, outcome, summary, reportId, generation);
            var result = reportId is null ? Commit() : reports.WithAssignmentReport(reportId, actor, parent, child, id,
                generation, source == "spawn" ? ReportCause.Spawn : ReportCause.Delegation, Commit);
            return PublicAssignment(result);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            // Another identical caller can commit while this caller is acquiring the report
            // lock; if retention then evicts the report, its durable receipt still wins.
            if (reportId is not null)
            {
                try
                {
                    var current = AssignmentFor(source, id, actor);
                    if (current is not null)
                    {
                        var retry = source == "spawn" ? store.RetrySpawnTransition((string)current["child"]!, id, actor, revision, operation, outcome, summary, reportId, generation)
                            : mailbox.RetryAssignmentTransition(id, actor, revision, operation, outcome, summary, reportId, generation);
                        if (retry is not null) return PublicAssignment(retry);
                    }
                }
                catch (InvalidOperationException) { }
            }
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Assignment transition unavailable: " + e.Message);
        }
    }
}
