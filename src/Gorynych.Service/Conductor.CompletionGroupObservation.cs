using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    internal bool CompletionGroupObservationAvailable
    {
        get
        {
            if (completionObservationScope is not { } scope) return false;
            try { return uiMetadata.IsCurrentGroupInstance(scope.Project, scope.Group, scope.Instance); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or JsonException) { return false; }
        }
    }

    internal Func<Task>? AfterCompletionGroupPullForTests { get; set; }

    internal static bool ValidCompletionObservationIdentity(string? native, long revision) =>
        native is not null && Guid.TryParseExact(native, "D", out var id) &&
        native == id.ToString("D") && revision >= 0;

    // Advisory only: no admission, assignment mutation, session opening or native input.
    internal async Task<JsonObject> ObserveCompletionGroupAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var actor = MailboxPeer(request); var args = AssignmentParams(request, "cursor", "limit");
        var limit = 16;
        if (args.TryGetPropertyValue("limit", out var n) && (n is not JsonValue v || !v.TryGetValue<int>(out limit) || limit is < 1 or > 16))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "limit must be 1..16");
        if (completionObservationScope is not { } scope || !CompletionGroupObservationAvailable)
            return new JsonObject { ["state"] = "disabled", ["items"] = new JsonArray(), ["nextCursor"] = null };
        bool Member(string id)
        {
            var group = uiMetadata.Get(id)[GorynychProtocol.MetaGroup];
            return (string?)group?["projectKey"] == scope.Project && (string?)group?["groupId"] == scope.Group;
        }
        if (store.Get(actor) is not { Deleted: false, Archived: false } || !Member(actor))
            throw new JsonRpcException(JsonRpcException.InvalidRequest, "Observation unavailable");
        string? afterSource = null, afterId = null;
        if (args.TryGetPropertyValue("cursor", out var raw))
        {
            try
            {
                if (raw is not JsonValue cv || !cv.TryGetValue<string>(out var cursor) || cursor.Length > 4096) throw new FormatException();
                var node = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))) as JsonObject ?? throw new FormatException();
                if ((string?)node["actor"] != actor || (string?)node["instance"] != scope.Instance ||
                    (string?)node["source"] is not ("spawn" or "delegation") || (string?)node["id"] is not { Length: > 0 and <= 256 }) throw new FormatException();
                afterSource = (string)node["source"]!; afterId = (string)node["id"]!;
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
            { throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid observation cursor"); }
        }
        var rows = store.SpawnAssignmentViews(actor).Concat(mailbox.AssignmentViews(actor))
            .Where(a => (string?)a["parent"] == actor && (string?)a["child"] is { } child && Member(child) &&
                (afterSource is null || string.CompareOrdinal((string?)a["source"], afterSource) > 0 ||
                ((string?)a["source"] == afterSource && string.CompareOrdinal((string?)a["assignmentId"], afterId) > 0)))
            .OrderBy(a => (string?)a["source"], StringComparer.Ordinal).ThenBy(a => (string?)a["assignmentId"], StringComparer.Ordinal)
            .Take(limit + 1).ToArray();
        string? next = null;
        if (rows.Length > limit)
        {
            var last = rows[limit - 1];
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes(new JsonObject { ["actor"] = actor, ["instance"] = scope.Instance,
                ["source"] = (string?)last["source"], ["id"] = (string?)last["assignmentId"] }.ToJsonString()));
        }
        var output = new JsonArray();
        foreach (var row in rows.Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            output.Add((JsonNode)await ObserveCompletionGroupRowAsync(row, actor, scope, Member, ct));
        }
        // Membership may change while an individual host pull is in flight.
        if (!CompletionGroupObservationAvailable || store.Get(actor) is not { Deleted: false, Archived: false } || !Member(actor) ||
            rows.Take(limit).Any(r => !Member((string)r["child"]!)))
            return new JsonObject { ["state"] = "disabled", ["items"] = new JsonArray(), ["nextCursor"] = null };
        return new JsonObject { ["state"] = "observed", ["items"] = output, ["nextCursor"] = next };
    }

    async Task<JsonObject> ObserveCompletionGroupRowAsync(JsonObject row, string actor,
        (string Project, string Group, string Instance) scope, Func<string, bool> member, CancellationToken ct)
    {
        var source = (string)row["source"]!; var id = (string)row["assignmentId"]!;
        var child = (string)row["child"]!; var generation = (string)row["generation"]!; var revision = (long)row["revision"]!;
        var continuation = ContinuationView(row); var diagnostic = AssignmentDiagnostic(row, continuation);
        var reason = (string?)diagnostic["reasonCode"] ?? "assignment_status_unknown";
        var output = new JsonObject { ["source"] = source, ["assignmentId"] = id, ["generation"] = generation,
            ["revision"] = revision, ["status"] = row["status"]?.DeepClone(), ["delivery"] = row["delivery"]?.DeepClone(),
            ["continuationKind"] = continuation?["kind"]?.DeepClone(), ["continuationState"] = continuation?["state"]?.DeepClone(),
            ["requestDelivery"] = continuation?["requestDelivery"]?.DeepClone(), ["replyDelivery"] = continuation?["replyDelivery"]?.DeepClone(),
            ["initialTurn"] = diagnostic["initialTurn"]?.DeepClone(), ["foregroundIdle"] = diagnostic["foregroundIdle"]?.DeepClone(),
            ["sessionActivity"] = diagnostic["sessionActivity"]?.DeepClone(), ["decision"] = "defer", ["reason"] = reason };
        JsonObject Defer(string code) { output["reason"] = code; return output; }
        if (reason is not ("no_correlated_execution" or "open_without_recorded_continuation")) return output;
        if (source != "delegation" || (string?)row["status"] != "open" || (string?)row["delivery"] != "delivered" || row["continuation"] is not null)
            return Defer("assignment_not_eligible");
        var previous = mailbox.ReadCompletionReminder(id, actor, child, generation);
        if (previous is not null) return Defer("attempt_already_recorded");
        var pending = mailbox.CompletionReminderPending(null, null, null);
        if (pending.Unknown || pending.Items.Count > 0) return Defer("previous_handoff_pending_or_unknown");
        if (Draining || store.Get(actor) is not { Deleted: false, Archived: false } ||
            store.Get(child) is not { Deleted: false, Archived: false, Agent: "claude" } session || IsStopping(session.Agent)) return Defer("recipient_unavailable");
        if (!agents.TryGetValue("claude", out var host) || host.RouteOf(child) is not { Current: true } route || route.Rpc.IsClosed ||
            route.HostId is null || route.Generation is null || route.ProcessId <= 0 || route.CompletionProcessStart is not { } start ||
            !CompletionReminderBinding.CanonicalProducerStart(start) || !feeds.TryGetValue(child, out var feed)) return Defer("route_unavailable");
        if (route.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is not JsonValue cap || !cap.TryGetValue<bool>(out var supported) || !supported)
            return Defer("guarded_unsupported");
        bool Busy() => feed.Retired || feed.ReadOnly is not null || feed.OpenedReadOnly || feed.Users != 0 || feed.TurnRunning ||
            feed.AgentTurnRunning || feed.Asking != 0 || feed.PendingRequests != 0 || feed.HasSteeringInFlight ||
            mailbox.Read(child, 0, 1, true).Count != 0 || (string?)feed.Activity.Snapshot()["state"] != "idle";
        bool SafeMode() => !feed.ReminderModeUncertain && ReminderModeKnown(feed.State()) && !InFullAccess(child);
        if (!SafeMode()) return Defer("mode_unknown_or_full_access");
        if (Busy()) return Defer("recipient_busy_or_pending");
        bool Quiet() => feed.LastActivity != DateTimeOffset.MinValue &&
            options.Time.GetUtcNow() - feed.LastActivity >= TimeSpan.FromSeconds(1);
        if (!Quiet()) return Defer("quiet_period");
        if (feed.HasBackgroundWork) return Defer("background_active_or_unconfirmed");
        var first = host.OnCurrentRoute(child, route.Rpc, link => feed.ClaudeObservationFor(link, id, generation), null);
        if (first is null) return Defer("claude_evidence_unknown");
        JsonNode? response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            response = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
                new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = first.Prompt } }, timeout.Token);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException)
        { ct.ThrowIfCancellationRequested(); return Defer("claude_pull_unknown"); }
        if (AfterCompletionGroupPullForTests is { } afterPull) await afterPull().WaitAsync(ct);
        var meta = response?["_meta"];
        var last = host.OnCurrentRoute(child, route.Rpc, link => feed.ClaudeObservationFor(link, id, generation), null);
        var current = AssignmentFor(source, id, actor);
        var remaining = mailbox.CompletionReminderPending(null, null, null);
        if (!CompletionGroupObservationAvailable || !member(actor) || !member(child) || first != last ||
            (string?)meta?["state"] != "observed" || (string?)meta?["sessionId"] != child ||
            (string?)meta?["promptId"] != first.Prompt || (string?)meta?["stream"] != first.BackgroundStream ||
            (string?)meta?["nativeUuid"] is not { } native ||
            (long?)meta?["revision"] is not { } observedRevision || !ValidCompletionObservationIdentity(native, observedRevision) ||
            !Guid.TryParseExact(first.BackgroundStream, "N", out _) ||
            current is null || (string?)current["generation"] != generation || (long?)current["revision"] != revision ||
            (string?)current["status"] != "open" || (string?)current["delivery"] != "delivered" || current["continuation"] is not null ||
            mailbox.ReadCompletionReminder(id, actor, child, generation) is not null || remaining.Unknown || remaining.Items.Count != 0 ||
            Draining || store.Get(actor) is not { Deleted: false, Archived: false } || store.Get(child) is not { Deleted: false, Archived: false } ||
            IsStopping(session.Agent) || Busy() || !SafeMode() || !Quiet() || feed.HasBackgroundWork) return Defer("evidence_or_assignment_changed");
        output["decision"] = "candidate"; output["reason"] = "shadow_candidate";
        output["claudeObservation"] = "same_process_prompt_settled";
        return output;
    }
}
