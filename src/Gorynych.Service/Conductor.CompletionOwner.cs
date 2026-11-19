using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    readonly Lock completionPilotGate = new();
    readonly HashSet<string> completionAutoClosing = new(StringComparer.Ordinal);
    bool completionMembershipMutation;
    string? completionRevokeIntent;
    CompletionReminderRecovery? completionIntentRecord;
    internal Func<Task>? BeforeCompletionPolicyAbortForTests { get; set; }
    internal Func<string, string>? CompletionPolicyAbortResultForTests { get; set; }
    internal Func<Task>? AfterCompletionOwnerClaimForTests { get; set; }
    static JsonRpcException CompletionPilotBusy() => new(JsonRpcException.InvalidRequest, "Completion pilot mutation busy or unresolved; retry later");
    // Caller holds completionPilotGate. A durable terminal transport fact ends the
    // fence even if a later UI validation/publication failed; missing state does not.
    void RefreshCompletionIntent()
    {
        if (completionIntentRecord is not { } saved) return;
        var current = mailbox.ReadCompletionRecovery(saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation, saved.AttemptId);
        if (current is not null && current with { Disposition = saved.Disposition } == saved &&
            current.Disposition is "not_sent" or "native_ended" or "native_failed" or "native_cancelled")
        { completionRevokeIntent = null; completionIntentRecord = null; }
    }
    // Called under uiMetadataGate. No network or asynchronous work holds either gate.
    CompletionReminderRecovery? BeginCompletionMembershipMutation(string method, JsonNode? parameters, out bool fenced)
    {
        fenced = false;
        lock (completionPilotGate)
        {
            if (method is not (GorynychProtocol.MethodGroupsDelete or GorynychProtocol.MethodAttributesPatch or "test-only/participant-policy" or "internal/service-policy")) return null;
            if (method == GorynychProtocol.MethodAttributesPatch &&
                (parameters?["patch"] is not JsonObject patch || !patch.ContainsKey(GorynychProtocol.MetaGroup))) return null;
            try { RefreshCompletionIntent(); } catch (InvalidOperationException) { throw CompletionPilotBusy(); }
            string? project = null, group = null, participant = null;
            if (method == GorynychProtocol.MethodGroupsDelete)
            {
                project = (string?)parameters?["projectKey"]; group = (string?)parameters?["groupId"];
                if (project is null || group is null) return null;
            }
            else if (method != "internal/service-policy")
            {
                participant = (string?)parameters?["sessionId"];
                if (participant is null) return null;
            }
            var pending = mailbox.CompletionReminderPending(project, group, participant);
            if (pending.Unknown || pending.Items.Count > 1) throw CompletionPilotBusy();
            var selected = pending.Items.SingleOrDefault();
            // Scope also fences a prospective claim while a matching metadata mutation runs.
            var inScope = false;
            if (completionScope is { } scope)
            {
                if (method == GorynychProtocol.MethodGroupsDelete) inScope = project == scope.Project && group == scope.Group;
                else if (method == "internal/service-policy") inScope = true;
                else
                {
                    bool Matches(JsonNode? node) => (string?)node?["projectKey"] == scope.Project && (string?)node?["groupId"] == scope.Group;
                    inScope = Matches(uiMetadata.Get(participant!)[GorynychProtocol.MetaGroup]) ||
                        (method == GorynychProtocol.MethodAttributesPatch && Matches(parameters?["patch"]?[GorynychProtocol.MetaGroup]));
                }
            }
            if (selected is null && !inScope) return null;
            if (completionMembershipMutation) throw CompletionPilotBusy();
            if (completionRevokeIntent is not null && selected?.AttemptId != completionRevokeIntent) throw CompletionPilotBusy();
            completionMembershipMutation = true; fenced = true;
            if (selected is not null) { completionRevokeIntent = selected.AttemptId; completionIntentRecord = selected; }
            return selected;
        }
    }
    void EndCompletionMembershipMutation(bool applied)
    {
        lock (completionPilotGate) { completionMembershipMutation = false; if (applied) { completionRevokeIntent = null; completionIntentRecord = null; } }
    }
    void ConfirmCompletionMembershipMutation(CompletionReminderRecovery? saved, string? verdict)
    {
        if (saved is null) return;
        lock (completionPilotGate)
        {
            var current = mailbox.ReadCompletionRecovery(saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation, saved.AttemptId);
            if (completionRevokeIntent != saved.AttemptId || current is null ||
                current with { Disposition = saved.Disposition } != saved) throw CompletionPilotBusy();
            if (current.Disposition is "not_sent" or "native_ended" or "native_failed" or "native_cancelled")
            { completionRevokeIntent = null; completionIntentRecord = null; return; }
            if (verdict == "confirmed_precommit")
            {
                var ledger = new CompletionHostLedger(options.TestCompletionLedgerPath ?? Path.Combine(options.DataDirectory, "completion-host-ledger.json"));
                var identity = CompletionHostLedger.Identity(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee,
                    saved.Generation, saved.OriginalRevision, saved.AttemptId, saved.Binding, saved.Stream, saved.Number);
                if (!ledger.CompactWithDurableConsumer(saved.Stream, saved.Number, saved.AttemptId, identity, outcome =>
                    outcome == "not_sent" && mailbox.SettleCompletionReminder(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee,
                        saved.Generation, saved.OriginalRevision, saved.AttemptId, CompletionReminderSettlement.ProvenNotSent))) throw CompletionPilotBusy();
            }
            else if (verdict != "already_committed") throw CompletionPilotBusy();
            // Transport order is established independently of whether the caller's
            // subsequent policy mutation succeeds. The active gate still prevents claims.
            completionRevokeIntent = null; completionIntentRecord = null;
        }
    }
    async Task<(bool Fenced, CompletionReminderRecovery? Saved, string? Verdict)> BeginCompletionParticipantPolicyAsync(string session, CancellationToken ct)
    {
        bool fenced = false; CompletionReminderRecovery? saved;
        await uiMetadataGate.WaitAsync(ct);
        try { saved = BeginCompletionMembershipMutation("test-only/participant-policy", new JsonObject { ["sessionId"] = session }, out fenced); }
        finally { uiMetadataGate.Release(); }
        try
        {
            string? verdict = null;
            if (fenced && saved is not null)
            {
                if (BeforeCompletionPolicyAbortForTests is { } barrier) await barrier().WaitAsync(ct);
                verdict = await AbortCompletionPolicyAsync(saved.Owner, saved.AssignmentId, saved.Assignee, saved.Generation, saved.AttemptId, ct);
            }
            return (fenced, saved, verdict);
        }
        catch { if (fenced) EndCompletionMembershipMutation(false); throw; }
    }
    internal async Task<string> AbortCompletionPolicyAsync(string owner, string id, string assignee,
        string generation, string attempt, CancellationToken ct = default)
    {
        var saved = mailbox.ReadCompletionRecovery(id, owner, assignee, generation, attempt);
        if (saved is null) return "unresolved";
        var b = saved.Binding;
        if (b.ProducerPid is not { } pid || b.ProducerStart is not { } start ||
            !CompletionReminderBinding.CanonicalProducerStart(start) ||
            store.Get(assignee) is not { Deleted: false, Agent: "claude" } session ||
            !agents.TryGetValue(session.Agent, out var host) || host.RouteOf(assignee) is not { Current: true } route || route.Rpc.IsClosed ||
            route.HostId != b.HostId || route.Generation?.Key != b.HostGeneration || route.ProcessId != pid || route.CompletionProcessStart != start)
            return "unresolved";
        var identity = CompletionHostLedger.Identity("delegation", id, owner, assignee, generation, saved.OriginalRevision,
            attempt, b, saved.Stream, saved.Number);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAbort,
                new JsonObject { ["_meta"] = identity.DeepClone() }, timeout.Token);
            if (host.RouteOf(assignee) != route || route.Rpc.IsClosed || mailbox.ReadCompletionRecovery(id, owner, assignee, generation, attempt) != saved ||
                response?["_meta"] is not JsonObject row || row["abortState"] is not null ||
                (string?)row["attemptId"] != attempt || row["identity"] is not JsonObject actual) return "unresolved";
            foreach (var field in identity)
                if (!JsonNode.DeepEquals(actual[field.Key], field.Value)) return "unresolved";
            var verdict = (string?)row["abortVerdict"] is "confirmed_precommit" or "already_committed"
                ? (string)row["abortVerdict"]! : "unresolved";
            if (verdict == "confirmed_precommit" && (string?)row["abortOrigin"] == "adapter")
            {
                if ((long?)row["abortRevision"] is not { } revision) return "unresolved";
                var retire = identity.DeepClone().AsObject(); retire["receiptRevision"] = revision;
                var released = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
                    new JsonObject { ["_meta"] = retire }, timeout.Token);
                if ((long?)released?["_meta"]?["retiredRevision"] != revision ||
                    !JsonNode.DeepEquals(released?["_meta"]?["identity"], identity)) return "unresolved";
            }
            return CompletionPolicyAbortResultForTests?.Invoke(verdict) ?? verdict;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonRpcException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        { ct.ThrowIfCancellationRequested(); return "unresolved"; }
    }

    internal async Task CompletionUserCancelAsync(string session, JsonNode? parameters)
    {
        bool fenced = false; CompletionReminderRecovery? saved = null;
        try { saved = BeginCompletionMembershipMutation("test-only/participant-policy", new JsonObject { ["sessionId"] = session }, out fenced); }
        catch (Exception e) when (e is JsonRpcException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { /* Cancellation of user work must remain available. */ }
        try
        {
            // Deliver user cancellation before waiting on the independent completion abort.
            await ForwardNotificationAsync(AcpMethods.Agent.SessionCancel, parameters, session);
            if (fenced && saved is not null)
            {
                if (BeforeCompletionPolicyAbortForTests is { } barrier) await barrier();
                var verdict = await AbortCompletionPolicyAsync(saved.Owner, saved.AssignmentId, saved.Assignee, saved.Generation, saved.AttemptId);
                try { ConfirmCompletionMembershipMutation(saved, verdict); }
                catch (JsonRpcException) { /* Retain the revoke intent until exact evidence arrives. */ }
            }
        }
        finally { if (fenced) EndCompletionMembershipMutation(false); }
    }

    internal async Task<JsonNode?> CompletionServiceMutationAsync(bool affectsClaude, Func<Task<JsonNode?>> action, CancellationToken ct)
    {
        if (!affectsClaude) return await action();
        bool fenced = false; CompletionReminderRecovery? saved;
        await uiMetadataGate.WaitAsync(ct);
        try { saved = BeginCompletionMembershipMutation("internal/service-policy", null, out fenced); }
        finally { uiMetadataGate.Release(); }
        var applied = false;
        try
        {
            string? verdict = null;
            if (fenced && saved is not null) verdict = await AbortCompletionPolicyAsync(saved.Owner, saved.AssignmentId, saved.Assignee, saved.Generation, saved.AttemptId, ct);
            if (fenced) ConfirmCompletionMembershipMutation(saved, verdict);
            var result = await action(); applied = true; return result;
        }
        finally { if (fenced) EndCompletionMembershipMutation(applied); }
    }

    internal async Task<JsonNode?> CompletionParticipantMutationAsync(string session, Func<Task<JsonNode?>> action, CancellationToken ct)
    {
        var pilot = await BeginCompletionParticipantPolicyAsync(session, ct);
        var applied = false;
        try
        {
            if (pilot.Fenced) ConfirmCompletionMembershipMutation(pilot.Saved, pilot.Verdict);
            var result = await action(); applied = true; return result;
        }
        finally { if (pilot.Fenced) EndCompletionMembershipMutation(applied); }
    }

    internal async Task<JsonObject> CompletionAssignmentMutationAsync(JsonRpcRequest request, bool continuation, CancellationToken ct)
    {
        var actor = MailboxPeer(request);
        var args = request.Params as JsonObject;
        var source = (string?)args?["source"]; var id = (string?)args?["assignmentId"]; var generation = (string?)args?["generation"];
        JsonObject Apply() => continuation ? UpdateContinuation(request) : UpdateAssignment(request);
        if (source != "delegation" || id is null || generation is null) return Apply();
        lock (completionPilotGate)
        {
            var pending = mailbox.CompletionReminderPending(null, null, actor);
            if (pending.Unknown) throw CompletionPilotBusy();
            if (!pending.Items.Any(x => x.AssignmentId == id && x.Generation == generation)) return Apply();
        }
        var pilot = await BeginCompletionParticipantPolicyAsync(actor, ct);
        var applied = false;
        try
        {
            if (pilot.Fenced) ConfirmCompletionMembershipMutation(pilot.Saved, pilot.Verdict);
            lock (completionPilotGate) { var result = Apply(); applied = true; return result; }
        }
        finally { if (pilot.Fenced) EndCompletionMembershipMutation(applied); }
    }

    // Status, ACK and retire never dispatch input. Each response is tied to the
    // durable claim and the same currently admitted original producer route.
    async Task<JsonObject> ReconcileCompletionLiveAsync(CompletionReminderRecovery saved, CancellationToken ct)
    {
        JsonObject Unknown(bool taken = false) => new() { ["state"] = "unresolved", ["nativeTaken"] = taken };
        var b = saved.Binding;
        if (!b.Valid() || b.ProducerPid is not { } pid || b.ProducerStart is not { } start ||
            !CompletionReminderBinding.CanonicalProducerStart(start) ||
            !agents.TryGetValue("claude", out var host) || host.RouteOf(saved.Assignee) is not { } route ||
            route.Rpc.IsClosed || route.HostId != b.HostId || route.Generation?.Key != b.HostGeneration ||
            route.ProcessId != pid || route.CompletionProcessStart != start ||
            route.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is not JsonValue capability ||
            !capability.TryGetValue<bool>(out var supported) || !supported) return Unknown();
        bool Current() => !route.Rpc.IsClosed && host.RouteOf(saved.Assignee) == route &&
            mailbox.ReadCompletionRecovery(saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation, saved.AttemptId) == saved;
        var identity = CompletionHostLedger.Identity(saved.Source, saved.AssignmentId, saved.Owner, saved.Assignee,
            saved.Generation, saved.OriginalRevision, saved.AttemptId, b, saved.Stream, saved.Number);
        var ledger = new CompletionHostLedger(options.TestCompletionLedgerPath ?? Path.Combine(options.DataDirectory, "completion-host-ledger.json"));
        var taken = false;
        try
        {
            if (!Current()) return Unknown();
            var row = ledger.ReadAttempt(saved.AttemptId);
            if (row is null || !JsonNode.DeepEquals(row["identity"], identity)) return Unknown();
            long? revision = (long?)row["ackRevision"];
            taken = (bool?)row["receipt"]?["committed"] == true && (bool?)row["receipt"]?["taken"] == true;
            if (revision is null)
            {
                var status = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedStatus,
                    new JsonObject { ["_meta"] = identity.DeepClone() }, ct);
                if (!Current() || !JsonNode.DeepEquals(status?["_meta"]?["identity"], identity) ||
                    status?["_meta"]?["receipt"] is not JsonObject receipt) return Unknown();
                taken = (bool?)receipt["committed"] == true && (bool?)receipt["taken"] == true;
                if ((string?)receipt["cycleResult"] is not ("ended" or "failed") || (long?)receipt["revision"] is not { } observed)
                    return Unknown(taken);
                var ackFields = identity.DeepClone().AsObject(); ackFields["receiptRevision"] = observed;
                var ack = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedAck,
                    new JsonObject { ["_meta"] = ackFields }, ct);
                if (!Current() || (long?)ack?["_meta"]?["ackRevision"] != observed ||
                    !JsonNode.DeepEquals(ack?["_meta"]?["identity"], identity)) return Unknown(taken);
                revision = observed;
            }
            var retireFields = identity.DeepClone().AsObject(); retireFields["receiptRevision"] = revision.Value;
            var retired = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedRetire,
                new JsonObject { ["_meta"] = retireFields }, ct);
            if (!Current() || (long?)retired?["_meta"]?["retiredRevision"] != revision ||
                !JsonNode.DeepEquals(retired?["_meta"]?["identity"], identity)) return Unknown(taken);
            var settled = ledger.CompactWithDurableConsumer(saved.Stream, saved.Number, saved.AttemptId, identity, outcome =>
            {
                CompletionReminderSettlement? disposition = outcome switch
                {
                    "native_ended" => CompletionReminderSettlement.NativeEnded,
                    "native_failed" => CompletionReminderSettlement.NativeFailed,
                    "native_cancelled" => CompletionReminderSettlement.NativeCancelled,
                    "not_sent" => CompletionReminderSettlement.ProvenNotSent,
                    _ => null,
                };
                return disposition is { } known && mailbox.SettleCompletionReminder(saved.Source, saved.AssignmentId, saved.Owner,
                    saved.Assignee, saved.Generation, saved.OriginalRevision, saved.AttemptId, known);
            });
            if (!settled) return Unknown(taken);
            return new JsonObject { ["state"] = "settled", ["attemptId"] = saved.AttemptId,
                ["disposition"] = mailbox.ReadCompletionReminder(saved.AssignmentId, saved.Owner, saved.Assignee, saved.Generation)?.Disposition };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonRpcException or OperationCanceledException or
            ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { return Unknown(taken); }
    }

    // Automatic migration and idle unload must not destroy a live producer's only
    // receipt. This is status-only; a stale route never gains submit authority.
    internal bool CompletionPendingOnRoute(string session, HostLink route, out CompletionReminderRecovery? saved)
    {
        var pending = mailbox.CompletionReminderPending(null, null, session);
        saved = null;
        if (pending.Unknown) return true;
        foreach (var item in pending.Items)
        {
            var b = item.Binding;
            if (item.Assignee == session && b.HostId == route.HostId && b.HostGeneration == route.Generation?.Key &&
                b.ProducerPid == route.ProcessId && b.ProducerStart == route.CompletionProcessStart)
            { saved = item; return true; }
            // Legacy Linux wall timestamps cannot identify a process incarnation.
            // Only veto automatic cleanup on the authenticated admitted route; never
            // return the claim for status, abort, settlement, or as an exit proof.
            if (OperatingSystem.IsLinux() && item.Assignee == session && route.HostId is not null &&
                route.Generation is not null && b.HostId == route.HostId && b.HostGeneration == route.Generation.Key &&
                b.ProducerStart is { } legacy && CompletionReminderBinding.CanonicalProducerStart(legacy) &&
                !ProcessIncarnation.ValidLinux(legacy)) return true;
        }
        return false;
    }

    async Task<bool> HoldCompletionForCleanupAsync(string session, CancellationToken ct)
    {
        if (store.Get(session) is not { Agent: "claude" } || !agents.TryGetValue("claude", out var host) ||
            host.RouteOf(session) is not { } route || route.Rpc.IsClosed) return false;
        CompletionReminderRecovery? saved;
        try { if (!CompletionPendingOnRoute(session, route, out saved)) return false; }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return true; }
        if (saved is null) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await ReconcileCompletionLiveAsync(saved, timeout.Token);
        try
        {
            if (host.RouteOf(session) != route) return false;
            return CompletionPendingOnRoute(session, route, out _);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return true; }
    }

    // Explicit authenticated recovery only: no dispatch or adapter startup.
    internal async Task<JsonObject> RecoverCompletionOwnerAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var owner = MailboxPeer(request);
        var args = AssignmentParams(request, "assignmentId", "generation");
        var id = RequiredMailboxText(args, "assignmentId"); var generation = RequiredMailboxText(args, "generation");
        if (id.Length > 256 || !Guid.TryParseExact(generation, "N", out _))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid assignment identity");
        var assignment = AssignmentFor("delegation", id, owner);
        if (assignment is null || (string?)assignment["parent"] != owner || (string?)assignment["generation"] != generation)
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Assignment unavailable or not accessible");
        try
        {
            if ((string?)assignment["child"] is not { } assignee) return new JsonObject { ["state"] = "unresolved" };
            var claim = mailbox.ReadCompletionReminder(id, owner, assignee, generation);
            if (claim is null) return new JsonObject { ["state"] = "no_pending_attempt" };
            var saved = mailbox.ReadCompletionRecovery(id, owner, assignee, generation, claim.AttemptId);
            if (saved is null) return new JsonObject { ["state"] = "unresolved" };
            var alreadySettled = saved.Disposition is "not_sent" or "native_ended" or "native_failed" or "native_cancelled" or "unknown_closed";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var live = await ReconcileCompletionLiveAsync(saved, timeout.Token);
            if ((string?)live["state"] == "settled") return live;
            var binding = saved.Binding;
            if (!binding.Valid() || binding.ProducerPid is not { } pid || binding.ProducerStart is not { } start ||
                !CompletionReminderBinding.CanonicalProducerStart(start) || !ProcessIncarnation.DefinitelyExited(pid, start))
                return alreadySettled ? new JsonObject { ["state"] = saved.Disposition } : live;
            var ledger = new CompletionHostLedger(options.TestCompletionLedgerPath ?? Path.Combine(options.DataDirectory, "completion-host-ledger.json"));
            return new JsonObject { ["state"] = ledger.RecoverAfterProducerExit(saved, mailbox) };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return new JsonObject { ["state"] = "unresolved" }; }
    }

    internal bool CompletionOwnerTriggerAvailable => completionScope is not null && CompletionScopeIsCurrent;
    const string CompletionPilotText = "Please record an explicit outcome or continuation.";
    internal async Task<JsonObject> SubmitCompletionOwnerAsync(JsonRpcRequest request, CancellationToken ct)
    {
        JsonObject Defer(string reason) => new() { ["state"] = "deferred", ["reason"] = reason };
        var owner = MailboxPeer(request);
        var args = AssignmentParams(request, "assignmentId", "generation");
        var id = RequiredMailboxText(args, "assignmentId");
        var requestedGeneration = RequiredMailboxText(args, "generation");
        if (id.Length > 256 || !Guid.TryParseExact(requestedGeneration, "N", out _))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Invalid assignment identity");
        if (completionScope is not { } scope || !CompletionScopeIsCurrent) return Defer("disabled");
        var ledger = new CompletionHostLedger(options.TestCompletionLedgerPath ?? Path.Combine(options.DataDirectory, "completion-host-ledger.json"));
        var quiet = TimeSpan.FromSeconds(1);
        var row = AssignmentFor("delegation", id, owner);
        if (row is null || (string?)row["parent"] != owner || (string?)row["child"] is not { } child ||
            (string?)row["generation"] is not { } generation || generation != requestedGeneration || (long?)row["revision"] is not { } revision ||
            (string?)row["status"] != "open" || (string?)row["delivery"] != "delivered" || row["continuation"] is not null) return Defer("assignment");
        if (store.Get(owner) is not { Deleted: false, Archived: false } || store.Get(child) is not { Deleted: false, Archived: false, Agent: "claude" } session ||
            Draining || IsStopping(session.Agent) || !agents.TryGetValue("claude", out var host) || host.RouteOf(child) is not { Current: true } route ||
            route.Rpc.IsClosed || route.HostId is null || route.Generation is null || route.ProcessId <= 0 || route.CompletionProcessStart is not { } started ||
            !CompletionReminderBinding.CanonicalProducerStart(started) || !feeds.TryGetValue(child, out var feed)) return Defer("route");
        if (route.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is not JsonValue guardedCapability ||
            !guardedCapability.TryGetValue<bool>(out var guardedSupported) || !guardedSupported) return Defer("guarded_unsupported");
        var first = host.OnCurrentRoute(child, route.Rpc, link => feed.ClaudeObservationFor(link, id, generation), null);
        if (first is null) return Defer("observation");
        JsonNode? response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            response = await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostClaudePromptObservation,
                new JsonObject { ["_meta"] = new JsonObject { ["sessionId"] = child, ["promptId"] = first.Prompt } }, timeout.Token);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException)
        { ct.ThrowIfCancellationRequested(); return Defer("observation_unavailable"); }
        var observed = response?["_meta"];
        if ((string?)observed?["state"] != "observed" || (string?)observed?["sessionId"] != child ||
            (string?)observed?["promptId"] != first.Prompt || (string?)observed?["stream"] != first.BackgroundStream ||
            (string?)observed?["nativeUuid"] is not { } native || (long?)observed?["revision"] is not { } observationRevision) return Defer("observation_mismatch");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(CompletionPilotText)));
        var binding = new CompletionReminderBinding(scope.Project, scope.Group, scope.Instance, first.Prompt, native, first.BackgroundStream,
            observationRevision, digest, route.HostId, route.Generation.Key, route.ProcessId, started);
        if (!binding.Valid()) return Defer("identity");
        var mode = false; var archive = false; var metadata = false;
        CompletionReminderRecovery? claimed = null;
        try
        {
            mode = await feed.ReminderModeGate.WaitAsync(0, ct); if (!mode) return Defer("mode_busy");
            archive = await archiveGate.WaitAsync(0, ct); if (!archive) return Defer("archive_busy");
            metadata = await uiMetadataGate.WaitAsync(0, ct); if (!metadata) return Defer("metadata_busy");
            bool Member(string sid) { var g = uiMetadata.Get(sid)[GorynychProtocol.MetaGroup]; return (string?)g?["projectKey"] == scope.Project && (string?)g?["groupId"] == scope.Group; }
            var latest = AssignmentFor("delegation", id, owner);
            var activity = feed.Activity.Snapshot();
            if (!CompletionScopeIsCurrent || !Member(owner) || !Member(child) ||
                store.Get(owner) is not { Deleted: false, Archived: false } || store.Get(child) is not { Deleted: false, Archived: false } ||
                Draining || IsStopping(session.Agent) || feed.Retired || feed.ReadOnly is not null || feed.OpenedReadOnly ||
                feed.ReminderModeUncertain || !ReminderModeKnown(feed.State()) || InFullAccess(child) ||
                feed.LastActivity == DateTimeOffset.MinValue || options.Time.GetUtcNow() - feed.LastActivity < quiet ||
                feed.Users != 0 || feed.HasSteeringInFlight || feed.HasBackgroundWork || feed.Asking != 0 || feed.PendingRequests != 0 ||
                (string?)activity["state"] != "idle" || mailbox.Read(child, 0, 1, true).Count != 0 ||
                route.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is not JsonValue guardedFinal ||
                !guardedFinal.TryGetValue<bool>(out var guardedStillSupported) || !guardedStillSupported ||
                host.OnCurrentRoute(child, route.Rpc, link => feed.ClaudeObservationFor(link, id, generation), null) != first ||
                latest is null || (string?)latest["generation"] != generation || (long?)latest["revision"] != revision ||
                (string?)latest["status"] != "open" || (string?)latest["delivery"] != "delivered" || latest["continuation"] is not null)
                return Defer("changed");
            lock (maintenanceGate)
            lock (mailboxGate)
            {
                if (Draining || IsStopping(session.Agent) || mailboxWorkers.ContainsKey(child)) return Defer("busy");
                var attempt = Guid.NewGuid().ToString("N");
                lock (completionPilotGate)
                {
                RefreshCompletionIntent();
                var pending = mailbox.CompletionReminderPending(null, null, null);
                if (completionMembershipMutation || completionRevokeIntent is not null || completionAutoClosing.Contains(child) || pending.Unknown || pending.Items.Count != 0) return Defer("busy");
                var acquired = host.OnCurrentRoute(child, route.Rpc, link => link.Current &&
                    link.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is JsonValue cap && cap.TryGetValue<bool>(out var supported) && supported &&
                    feed.ClaudeObservationFor(link, id, generation) == first &&
                    ledger.ClaimFromMailbox(mailbox, "delegation", id, owner, child, generation, revision, attempt, binding).Acquired, false);
                if (!acquired) return Defer("claim");
                claimed = mailbox.ReadCompletionRecovery(id, owner, child, generation, attempt);
                }
            }
        }
        finally { if (metadata) uiMetadataGate.Release(); if (archive) archiveGate.Release(); if (mode) feed.ReminderModeGate.Release(); }
        if (claimed is null) return Defer("claim_unknown");
        if (AfterCompletionOwnerClaimForTests is { } afterClaim) await afterClaim().WaitAsync(ct);
        lock (completionPilotGate)
            if (completionMembershipMutation || completionRevokeIntent == claimed.AttemptId)
                return new JsonObject { ["state"] = "uncertain", ["attemptId"] = claimed.AttemptId };
        var fields = CompletionHostLedger.Identity("delegation", id, owner, child, generation, revision, claimed.AttemptId,
            claimed.Binding, claimed.Stream, claimed.Number);
        fields["text"] = CompletionPilotText;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await route.Rpc.SendRequestAsync(GorynychProtocol.MethodHostCompletionGuardedSubmit, new JsonObject { ["_meta"] = fields }, timeout.Token);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException)
        { return new JsonObject { ["state"] = "uncertain", ["attemptId"] = claimed.AttemptId }; }
        var taken = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.TestCompletionObservationTimeout ?? TimeSpan.FromSeconds(8));
            while (true)
            {
                var result = await ReconcileCompletionLiveAsync(claimed, timeout.Token);
                if ((string?)result["state"] == "settled") return result;
                taken |= (bool?)result["nativeTaken"] == true;
                await Task.Delay(50, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        return new JsonObject { ["state"] = "uncertain", ["attemptId"] = claimed.AttemptId, ["nativeTaken"] = taken };
    }

}
