using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    JsonObject PublicAssignment(JsonObject state, bool includeDiagnostic = false)
    {
        var continuation = ContinuationView(state);
        var result = new JsonObject
        {
            ["source"] = state["source"]?.DeepClone(), ["assignmentId"] = state["assignmentId"]?.DeepClone(),
            ["parent"] = state["parent"]?.DeepClone(), ["child"] = state["child"]?.DeepClone(),
            ["generation"] = state["generation"]?.DeepClone(), ["status"] = state["status"]?.DeepClone(), ["revision"] = state["revision"]?.DeepClone(),
            ["delivery"] = state["delivery"]?.DeepClone(), ["summary"] = state["summary"]?.DeepClone(),
            ["outcomeReportId"] = state["outcomeReportId"]?.DeepClone(), ["continuation"] = continuation,
        };
        if (includeDiagnostic)
        {
            result["diagnostic"] = AssignmentDiagnostic(state, continuation);
            result["latestReportId"] = VisibleCoordinatorReport((string?)state["latestReportId"], (string?)state["assignmentId"],
                (string?)state["generation"], (string?)state["parent"], (string?)state["child"]);
            if (state["coordinatorNotice"] is JsonObject notice)
                result["outcomeNotification"] = new JsonObject {
                    ["kind"] = notice["kind"]?.DeepClone(),
                    ["delivery"] = notice["delivery"]?.DeepClone() ?? JsonValue.Create(
                        (bool?)notice["delivered"] == true ? "delivered" : (bool?)notice["started"] == true ? "uncertain" : "queued") };
        }
        return result;
    }

    string? VisibleCoordinatorReport(string? reportId, string? assignment, string? generation, string? parent, string? child)
    {
        if (reportId is null || assignment is null || generation is null || parent is null || child is null) return null;
        try
        {
            var report = reports.GetReport(reportId); var identity = report.Identity;
            return report.ProducerAck && (identity.Cause == ReportCause.Delegation || identity.Cause == ReportCause.Reply &&
                identity.ReplyWaitId is not null && identity.ReplyWaitSequence is > 0 && identity.ReplyMessageSequence is > 0) && !identity.LegacyCapture &&
                identity.AssignmentId == assignment && identity.AssignmentGeneration == generation &&
                identity.Parent == parent && identity.Child == child ? reportId : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or KeyNotFoundException or System.Text.Json.JsonException) { return null; }
    }

    internal Action? AfterDiagnosticProofForTests { get; set; }

    // This is a read-only projection, not a scheduler and not a claim about task success.
    JsonObject AssignmentDiagnostic(JsonObject assignment, JsonObject? continuation)
    {
        var status = (string?)assignment["status"]; var delivery = (string?)assignment["delivery"];
        var kind = (string?)continuation?["kind"]; var dependency = (string?)continuation?["state"];
        var requestDelivery = (string?)continuation?["requestDelivery"]; var replyDelivery = (string?)continuation?["replyDelivery"];
        var childDelivery = (string?)continuation?["delivery"];
        var reason = status switch
        {
            "completed" => "terminal_completed", "failed" => "terminal_failed", "cancelled" => "terminal_cancelled",
            "blocked" => "explicit_blocked", _ => null,
        };
        if (reason is null && status != "open") reason = "assignment_status_unknown";
        reason ??= delivery switch
        {
            "queued" => "assignment_queued", "started" or "uncertain" => "assignment_handoff_unconfirmed",
            "failed" or "not_started" or "suppressed" => "assignment_delivery_failed", "delivered" => null,
            _ => "assignment_delivery_unknown",
        };
        if (reason is null && continuation is null && assignment["continuation"] is not null) reason = "dependency_unknown";
        if (reason is null && continuation is not null)
        {
            reason = dependency switch
            {
                "cancelled" or "undeliverable" or "blocked" => "dependency_needs_attention",
                "unknown" or "unavailable" => "dependency_unknown",
                "ready" when kind == "reply_wait" && replyDelivery == "delivered" => "ready_to_inspect",
                "ready" when kind == "reply_wait" && replyDelivery is "queued" => "reply_delivery_queued",
                "ready" when kind == "reply_wait" && replyDelivery is "handoff_started" or "uncertain" => "reply_delivery_unconfirmed",
                "ready" when kind == "reply_wait" && replyDelivery == "failed" => "reply_delivery_failed",
                "ready" when kind == "reply_wait" => "reply_delivery_unknown",
                "ready" => "ready_to_inspect",
                "waiting" when kind == "reply_wait" && requestDelivery == "delivered" => "waiting_reply",
                "waiting" when kind == "reply_wait" && requestDelivery == "queued" => "dependency_queued",
                "waiting" when kind == "reply_wait" && requestDelivery == "handoff_started" => "dependency_handoff_unconfirmed",
                "waiting" when kind == "child_assignment" && childDelivery == "delivered" => "waiting_child",
                "waiting" when kind == "child_assignment" && childDelivery == "queued" => "dependency_queued",
                "waiting" when kind == "child_assignment" && childDelivery == "started" => "dependency_handoff_unconfirmed",
                "waiting" when kind == "background_task" => "waiting_background",
                _ => "dependency_unknown",
            };
        }
        // Session activity is deliberately separate: it may be an unrelated turn or job.
        // An idle default is not authoritative evidence that this assignment was abandoned.
        var observed = new JsonObject { ["route"] = "unavailable", ["state"] = "unknown", ["background"] = "unknown",
            ["scope"] = "session_only", ["freshness"] = "unverified" };
        if ((string?)assignment["child"] is { } child && store.Get(child) is { Deleted: false } record &&
            agents.TryGetValue(record.Agent, out var host) && host.RouteOf(child) is not null && feeds.TryGetValue(child, out var feed))
        {
            var activity = feed.Activity.Snapshot(); var state = (string?)activity["state"];
            observed["route"] = "connected";
            if (state is "running" or "requires_action") observed["state"] = state;
            if (activity["background"] is JsonArray full && full.Count > 0 || activity["backgroundKnown"] is JsonArray partial && partial.Count > 0)
                observed["background"] = "present";
        }
        // Read both ephemeral facts together while route publication/removal is excluded.
        // The assignment itself remains the participant-scoped durable snapshot read above.
        (bool Terminal, bool LoadedIdle) proof = (false, false);
        if ((string?)assignment["source"] == "delegation" && delivery == "delivered" &&
            (string?)assignment["assignmentId"] is { } assignmentId && (string?)assignment["generation"] is { } generation &&
            (string?)assignment["child"] is { } session && store.Get(session) is { Deleted: false } owner &&
            agents.TryGetValue(owner.Agent, out var agent) && feeds.TryGetValue(session, out var assignmentFeed))
            proof = agent.OnCurrentRoute(session, null, link => link.HostId is null ? (false, false) :
                assignmentFeed.InitialAssignmentEvidence(link.Rpc, assignmentId, generation), (false, false));
        if (reason is null && status == "open" && assignment["continuation"] is null && proof.Terminal && proof.LoadedIdle &&
            (string?)assignment["assignmentId"] is { } checkedId && (string?)assignment["child"] is { } checkedChild)
        {
            // Do not nest mailbox and route locks. A concurrent durable mutation or eviction
            // invalidates only the actionable reason; the public row remains its original snapshot.
            AfterDiagnosticProofForTests?.Invoke();
            var current = mailbox.FindAssignment(checkedId, checkedChild);
            if (current is not null && (string?)current["source"] == "delegation" &&
                (string?)current["parent"] == (string?)assignment["parent"] && (string?)current["child"] == checkedChild &&
                (string?)current["generation"] == (string?)assignment["generation"] &&
                (long?)current["revision"] == (long?)assignment["revision"] && (string?)current["status"] == "open" &&
                (string?)current["delivery"] == "delivered" && current["continuation"] is null)
                reason = "open_without_recorded_continuation";
        }
        var initialTurn = proof.Terminal ? "terminal_observed" : "unknown";
        var foregroundIdle = proof.LoadedIdle ? "loaded_idle_observed" : "unknown";
        return new JsonObject { ["reasonCode"] = reason ?? "no_correlated_execution", ["execution"] = "unknown",
            ["initialTurn"] = initialTurn, ["foregroundIdle"] = foregroundIdle, ["sessionActivity"] = observed };
    }

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
        if ((string?)reference["kind"] == "background_task")
        {
            var taskId = (string)reference["taskId"]!; var current = CurrentBackgroundEvidence(actor);
            var comparable = (string?)reference["nativeType"] == "_claude_task" && current is not null && current.HostId == (string?)reference["hostId"] &&
                current.Stream == (string?)reference["stream"] && current.Revision >= (long)reference["revision"]!;
            var present = comparable && current!.Items.Contains(taskId, StringComparer.Ordinal);
            var finished = comparable && !present && current!.Revision > (long)reference["revision"]!;
            return new JsonObject { ["kind"] = "background_task", ["nativeType"] = "_claude_task", ["taskId"] = taskId,
                ["state"] = !comparable || !present && !finished ? "unknown" : finished ? "ready" : "waiting",
                ["reason"] = finished ? "Task is no longer listed; inspect its result" : !comparable ? "Comparable background evidence unavailable" : null };
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

    static readonly UTF8Encoding BackgroundReferenceUtf8 = new(false, true);
    const int BackgroundReferenceLimit = 8192;
    sealed record BackgroundSelection(string Host, string Stream, long Revision, string TaskId);

    static string BackgroundReference(string actor, string host, string stream, long revision, string taskId)
    {
        var actorHash = Convert.ToHexString(SHA256.HashData(BackgroundReferenceUtf8.GetBytes(actor)));
        var encoded = new JsonObject { ["actorHash"] = actorHash, ["host"] = host, ["stream"] = stream,
            ["revision"] = revision, ["taskId"] = taskId }.ToJsonString();
        return Convert.ToBase64String(BackgroundReferenceUtf8.GetBytes(encoded));
    }

    static BackgroundSelection ParseBackgroundReference(string actor, string token)
    {
        try
        {
            if (token.Length is < 1 or > BackgroundReferenceLimit) throw new FormatException();
            var value = JsonNode.Parse(BackgroundReferenceUtf8.GetString(Convert.FromBase64String(token))) as JsonObject ?? throw new FormatException();
            if (value.Count != 5 || value["host"] is not JsonValue h || !h.TryGetValue<string>(out var host) ||
                string.IsNullOrWhiteSpace(host) || host.Length > 256 || value["stream"] is not JsonValue s || !s.TryGetValue<string>(out var stream) ||
                !Guid.TryParseExact(stream, "N", out _) || value["revision"] is not JsonValue v || !v.TryGetValue<long>(out var revision) || revision < 1 ||
                value["taskId"] is not JsonValue t || !t.TryGetValue<string>(out var task) || string.IsNullOrWhiteSpace(task) || task.Length > 256 ||
                token != BackgroundReference(actor, host, stream, revision, task)) throw new FormatException();
            return new BackgroundSelection(host, stream, revision, task);
        }
        catch (Exception e) when (e is FormatException or JsonException or DecoderFallbackException or EncoderFallbackException or InvalidOperationException)
        { throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid background reference"); }
    }

    internal JsonObject ListBackgroundTasks(JsonRpcRequest request)
    {
        var actor = MailboxPeer(request); AssignmentParams(request);
        var evidence = CurrentBackgroundEvidence(actor);
        return new JsonObject { ["state"] = evidence is null ? "unknown" : "available",
            ["items"] = new JsonArray((evidence?.Items ?? []).Select(id => (JsonNode)new JsonObject { ["nativeType"] = "_claude_task", ["taskId"] = id,
                ["reference"] = BackgroundReference(actor, evidence!.HostId, evidence.Stream, evidence.Revision, id) }).ToArray()) };
    }

    internal Action? ContinuationAfterLookupForTests { get; set; }

    internal JsonObject UpdateContinuation(JsonRpcRequest request)
    {
        if (Draining) throw Restarting();
        var actor = MailboxPeer(request);
        var args = AssignmentParams(request, "source", "assignmentId", "generation", "expectedRevision", "operationId", "action", "messageId", "targetSource", "targetAssignmentId", "targetGeneration", "backgroundRef");
        var (source, id) = AssignmentIdentity(args);
        var generation = RequiredMailboxText(args, "generation"); var operation = RequiredMailboxText(args, "operationId");
        var action = RequiredMailboxText(args, "action");
        if (!Guid.TryParseExact(generation, "N", out _) || operation.Length > 256 || action is not ("register" or "withdraw") ||
            args["expectedRevision"] is not JsonValue value || !(value.TryGetValue<long>(out var revision) ||
                value.TryGetValue<int>(out var small) && (revision = small) >= 0) || revision < 1)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid continuation operation");
        var hasMessage = args.ContainsKey("messageId");
        var hasChild = args.ContainsKey("targetSource") || args.ContainsKey("targetAssignmentId") || args.ContainsKey("targetGeneration");
        var hasBackground = args.ContainsKey("backgroundRef");
        if (action == "withdraw" && (hasMessage || hasChild || hasBackground) ||
            action == "register" && (Convert.ToInt32(hasMessage) + Convert.ToInt32(hasChild) + Convert.ToInt32(hasBackground) != 1))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Register requires one target; withdraw accepts none");
        string? message = null, childSource = null, childId = null, childGeneration = null, backgroundRef = null;
        BackgroundSelection? selected = null;
        if (action == "register" && hasMessage)
        {
            message = RequiredMailboxText(args, "messageId");
            if (message.Length > 256) throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid message ID");
        }
        else if (action == "register" && hasBackground)
        {
            backgroundRef = RequiredMailboxText(args, "backgroundRef");
            selected = ParseBackgroundReference(actor, backgroundRef);
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
            else if (selected is not null)
            {
                var evidence = CurrentBackgroundEvidence(actor);
                var matches = evidence is not null && evidence.HostId == selected.Host && evidence.Stream == selected.Stream &&
                    evidence.Revision == selected.Revision && evidence.Items.Contains(selected.TaskId, StringComparer.Ordinal);
                target = new JsonObject { ["kind"] = "background_task", ["nativeType"] = "_claude_task", ["taskId"] = selected.TaskId,
                    ["hostId"] = matches ? evidence!.HostId : null, ["stream"] = matches ? evidence!.Stream : null,
                    ["revision"] = matches ? evidence!.Revision : null, ["selector"] = backgroundRef, ["confirmed"] = matches };
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

    internal JsonObject ListCompletionNotices(JsonRpcRequest request)
    {
        var owner = MailboxPeer(request);
        var args = AssignmentParams(request, "cursor", "limit");
        var limit = 32;
        if (args.TryGetPropertyValue("limit", out var n) && (n is not JsonValue v || !v.TryGetValue<int>(out limit) || limit is < 1 or > 100))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "limit must be 1..100");
        string? cursor = null;
        if (args.TryGetPropertyValue("cursor", out var raw) && (raw is not JsonValue cv || !cv.TryGetValue<string>(out cursor) || cursor.Length > 65))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid notice cursor");
        try { return mailbox.ListCompletionUnknownReceipts(owner, cursor, limit); }
        catch (InvalidOperationException) { throw new JsonRpcException(JsonRpcException.InvalidRequest, "Completion notices temporarily unavailable"); }
    }

    internal JsonObject AckCompletionNotice(JsonRpcRequest request)
    {
        var owner = MailboxPeer(request);
        var args = AssignmentParams(request, "assignmentId", "assignee", "generation", "revision", "attemptId");
        var id = RequiredMailboxText(args, "assignmentId"); var assignee = RequiredMailboxText(args, "assignee");
        var generation = RequiredMailboxText(args, "generation"); var attempt = RequiredMailboxText(args, "attemptId");
        if (id.Length > 256 || assignee.Length > 512 || !Guid.TryParseExact(generation, "N", out _) ||
            !Guid.TryParseExact(attempt, "N", out _) || args["revision"] is not JsonValue v ||
            !v.TryGetValue<long>(out var revision) || revision < 1)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid notice identity");
        try
        {
            if (!mailbox.AckCompletionUnknownReceipt(owner, id, assignee, generation, revision, attempt))
                throw new JsonRpcException(JsonRpcException.InvalidParams, "Notice unavailable or not accessible");
            return new JsonObject { ["acknowledged"] = true };
        }
        catch (InvalidOperationException) { throw new JsonRpcException(JsonRpcException.InvalidRequest, "Completion notices temporarily unavailable"); }
    }

    internal JsonObject ReadAssignment(JsonRpcRequest request)
    {
        var actor = MailboxPeer(request); var args = AssignmentParams(request, "source", "assignmentId");
        var (source, id) = AssignmentIdentity(args);
        var item = AssignmentFor(source, id, actor) ?? throw new JsonRpcException(JsonRpcException.InvalidParams, "Assignment unavailable or not accessible");
        return PublicAssignment(item, includeDiagnostic: true);
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
        return new JsonObject { ["items"] = new JsonArray(items.Take(limit).Select(a => (JsonNode)PublicAssignment(a, includeDiagnostic: true)).ToArray()), ["nextCursor"] = next };
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
            if (retry is not null)
            {
                if (source == "delegation") Track(Task.Run(() => QueueMailbox(parent)));
                return PublicAssignment(retry);
            }
            JsonObject Commit() => source == "spawn" ? store.TransitionSpawnAssignment(child, id, actor, revision, operation, outcome, summary, reportId, generation)
                : mailbox.TransitionAssignment(id, actor, revision, operation, outcome, summary, reportId, generation);
            var result = reportId is null ? Commit() : reports.WithAssignmentReport(reportId, actor, parent, child, id,
                generation, source == "spawn" ? ReportCause.Spawn : ReportCause.Delegation, Commit);
            if (source == "delegation") Track(Task.Run(() => QueueMailbox(parent)));
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
                        if (retry is not null)
                        {
                            if (source == "delegation") Track(Task.Run(() => QueueMailbox((string)current["parent"]!)));
                            return PublicAssignment(retry);
                        }
                    }
                }
                catch (InvalidOperationException) { }
            }
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Assignment transition unavailable: " + e.Message);
        }
    }
}
