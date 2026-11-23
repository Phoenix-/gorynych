using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    readonly ConcurrentDictionary<string, byte> reportDeliveryWorkers = new(StringComparer.Ordinal);
    readonly SemaphoreSlim reportDeliverySlots = new(8, 8);
    readonly Lock liveReportGate = new();
    readonly Dictionary<string, LiveRecipientGate> liveReportRecipients = new(StringComparer.Ordinal);
    sealed class LiveRecipientGate { internal readonly SemaphoreSlim Gate = new(1, 1); internal int Users; }
    LiveRecipientGate TakeLiveRecipient(string id)
    {
        lock (liveReportGate)
        {
            if (!liveReportRecipients.TryGetValue(id, out var gate)) liveReportRecipients.Add(id, gate = new());
            gate.Users++; return gate;
        }
    }
    void ReleaseLiveRecipient(string id, LiveRecipientGate gate)
    {
        lock (liveReportGate) if (--gate.Users == 0) liveReportRecipients.Remove(id);
    }
    internal Action? BeforeReportDeliveryStart { get; set; }
    internal Action? BeforeLiveReportForwardWait { get; set; }
    internal Action? AfterPeerMailboxSettlementForTests { get; set; }
    internal Action? AfterPeerRetainedForTests { get; set; }
    internal Action? BeforePeerRetentionAckForTests { get; set; }
    internal bool ReportDeliveryRunning(string id) => reportDeliveryWorkers.ContainsKey(id);

    readonly ConcurrentDictionary<string, byte> noteHostExitObservers = new(StringComparer.Ordinal);
    void ObserveNoteHostExit(HostLink link)
    {
        if (link.HostId is not { } hostId || link.ProcessId <= 0 || link.ProcessId == Environment.ProcessId || link.ProcessStart is null) return;
        try
        {
            string? cursor = null; var needed = false;
            do
            {
                var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
                needed |= page.Items.Any(item => item.Delivery is { Route: "note", DispatchStarted: true } delivery && delivery.HostId == hostId &&
                    (item.Status is "claimed" or "accepted" or "uncertain" || delivery.ReceiptAckPending == true));
            } while (!needed && cursor is not null);
            if (!needed || !noteHostExitObservers.TryAdd(hostId, 0)) return;
            Track(Task.Run(async () =>
            {
                try { await ReconcileAfterHostExitAsync(link); }
                finally { noteHostExitObservers.TryRemove(hostId, out _); }
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { Volatile.Write(ref reportRecoveryFailure, "Cannot observe report note host exit: " + e.GetType().Name); }
    }

    internal void QueueReportDeliveries(string? parent = null)
    {
        try
        {
            string? cursor = null;
            do
            {
                var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
                foreach (var item in page.Items)
                    if ((parent is null || item.Identity.Parent == parent) &&
                        (item.Status is "pending" or "claimed" or "accepted" ||
                         item.Identity.PeerRoute is not null && item.Status == "retained" && !item.PeerMailboxAck ||
                         item.Delivery?.Route == "note" && (item.Status == "uncertain" ||
                            item.Status is "completed" or "failed" && item.Delivery.ReceiptAckPending == true)))
                        QueueReportDelivery(item.ReportId);
            } while (cursor is not null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { Volatile.Write(ref reportRecoveryFailure, "Report delivery inventory failed: " + e.Message); }
    }

    void QueueReportDelivery(string id)
    {
        if (cleanupStop.IsCancellationRequested || Draining) return;
        while (!reportDeliveryWorkers.TryAdd(id, 0))
        {
            if (reportDeliveryWorkers.TryUpdate(id, 1, 0)) return;
            if (reportDeliveryWorkers.TryGetValue(id, out var queued) && queued == 1) return;
        }
        Track(Task.Run(async () =>
        {
            try
            {
                await DeliverReportAsync(id, cleanupStop.Token);
            }
            catch (OperationCanceledException) when (cleanupStop.IsCancellationRequested || Draining) { }
            catch (Exception e) when (e is not OutOfMemoryException)
            { Volatile.Write(ref reportRecoveryFailure, $"Report {id} delivery needs attention: {e.Message}"); }
            finally
            {
                if (reportDeliveryWorkers.TryRemove(id, out var rescan) && rescan == 1) QueueReportDelivery(id);
            }
        }));
    }

    static string DeliveryReason(string value)
    {
        var utf8 = new UTF8Encoding(false, true);
        while (utf8.GetByteCount(value) > ReportCapture.ReasonBytes)
        {
            var end = value.Length - 1;
            if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
            value = value[..end];
        }
        return value;
    }

    // A delivery terminal may precede later journaled host events. Request retained replay
    // independently of the ordinary stream cursor while a started claim is unresolved.
    bool NeedsDeliveryReplay(string parent, string hostId)
    {
        string? cursor = null;
        do
        {
            var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
            if (page.Items.Any(x => x.Identity.Parent == parent && x.Delivery is { DispatchStarted: true } d &&
                d.HostId == hostId && x.Status is "claimed" or "accepted" or "uncertain")) return true;
        } while (cursor is not null);
        return false;
    }

    internal bool HasUnresolvedDeliveryTerminal(JsonObject terminal, string parent, string? promptId, string? hostSeq)
    {
        if (promptId is null || hostSeq is null || !SessionJournal.TryParseHostSeq(hostSeq, out var host, out _)) return false;
        var meta = terminal["_meta"]?[GorynychProtocol.MetaReportDelivery];
        return reports.FindDeliveryByPrompt(parent, promptId).Any(x =>
            x.Delivery is { DispatchStarted: true } d && d.HostId == host &&
            (string?)meta?["reportId"] == x.ReportId && (string?)meta?["attemptId"] == d.AttemptId &&
            x.Status is "claimed" or "accepted" or "uncertain");
    }

    // A host terminal is independently replayable after a conductor restart. Only the
    // original host and service-generated attempt/prompt tuple can settle this claim.
    bool ObserveReportDeliveryTerminal(JsonObject terminal, string parent, string promptId, string? hostSeq)
    {
        var candidates = reports.FindDeliveryByPrompt(parent, promptId);
        if (candidates.Length == 0) return terminal["_meta"]?[GorynychProtocol.MetaReportDelivery] is null;
        if (candidates.Length != 1) throw new InvalidOperationException("Multiple delivery claims share a prompt ID");
        var item = candidates[0]; var claim = item.Delivery!;
        if (item.Status is "completed" or "failed") return true;
        var meta = terminal["_meta"]?[GorynychProtocol.MetaReportDelivery];
        if ((string?)meta?["reportId"] != item.ReportId || (string?)meta?["attemptId"] != claim.AttemptId ||
            claim.HostId is null || hostSeq is null || !SessionJournal.TryParseHostSeq(hostSeq, out var host, out _) || host != claim.HostId ||
            !claim.DispatchStarted) return false;
        if (item.Status == "claimed") { reports.SetDisposition(item.ReportId, "accepted"); item = reports.GetReport(item.ReportId); }
        if (item.Status is not ("accepted" or "uncertain")) return false;
        var error = terminal["error"] as JsonObject;
        var stop = (string?)terminal["result"]?["stopReason"];
        if (error is not null || stop is not (null or "end_turn"))
            reports.SetDisposition(item.ReportId, "failed", error is not null ? "Parent report turn returned a native error" :
                stop == "cancelled" ? "Parent report turn was cancelled" : "Parent report turn stopped before normal completion");
        else
            reports.SetDisposition(item.ReportId, "completed", item.Status == "uncertain" ? "Confirmed by correlated host terminal" : null);
        return true;
    }

    async Task ReconcileReportNoteAsync(ReportInfo info, ReportDeliveryClaim claim, CancellationToken ct)
    {
        var id = info.ReportId; var parent = info.Identity.Parent;
        if (parent is null) return;
        if (claim.HostId is not { } hostId || claim.HostGeneration is not { } generation) return;
        string? proof;
        try { proof = reports.ReadNoteNoInput(id, parent, hostId, generation, claim.AttemptId); }
        catch (InvalidOperationException)
        {
            if (info.Status is "claimed" or "accepted")
                reports.SetDisposition(id, "uncertain", "Native note handoff is not confirmed; no automatic replay");
            return;
        }
        if (proof is not null && info.Status is "claimed" or "uncertain")
        {
            reports.QueueProvenNoInput(id, parent, hostId, generation, claim.AttemptId);
            QueueReportDelivery(id); return;
        }
        var record = store.Get(parent);
        var route = record is null ? null : HostOf(record).RouteOf(parent);
        var canQuery = route is not null && route.HostId == hostId && route.Generation?.Key == generation &&
            (bool?)route.Info.Meta?[GorynychProtocol.CapHostReportNote]?["supported"] == true;
        if (canQuery && info.Status is not ("completed" or "failed"))
        {
            try
            {
                using var statusTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); statusTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await route!.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteStatus, new JsonObject { ["sessionId"] = parent,
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } } }, statusTimeout.Token);
            }
            catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException or TimeoutException)
            { Volatile.Write(ref reportRecoveryFailure, "Native report receipt query is unavailable: " + e.GetType().Name); }
        }
        var facts = reports.ReadNoteReceipt(id, parent, hostId, generation, claim.AttemptId);
        if (facts is not null && (long?)facts["revision"] is { } revision)
            reports.TrySettleNoteReceipt(id, parent, hostId, generation, claim.AttemptId, revision);
        info = reports.GetReport(id);
        if (info.Status is "completed" or "failed" && facts is not null &&
            ((bool?)facts["contradiction"] == true || (bool?)facts["cancelReceiptPending"] == true))
        {
            Volatile.Write(ref reportRecoveryFailure, "A settled report note has newer contradictory or pending native evidence; review is required");
            return;
        }
        if (info.Status is "claimed" or "accepted")
            reports.SetDisposition(id, "uncertain", "Native report note is awaiting matching receipt; no automatic replay");
        if (info.Status is not ("completed" or "failed") || !reports.NoteNeedsAck(id, parent, hostId, generation, claim.AttemptId)) return;
        facts = reports.ReadNoteReceipt(id, parent, hostId, generation, claim.AttemptId);
        if (facts is null || (long?)facts["revision"] is not { } current || (string?)facts["nativeMessageId"] is not { } uuid ||
            !reports.CanAcknowledgeNote(id, parent, hostId, generation, claim.AttemptId, uuid, current)) return;
        if (!canQuery)
        {
            if (claim.HostProcessId is { } pid && claim.HostProcessStart is { } startedAt &&
                ProducerDefinitelyExited(new ReportIdentity("host-exit", info.Identity.Child, parent,
                    info.Identity.Cause, HostProcessId: pid, HostProcessStart: startedAt)))
                reports.RecordNoteHostExitRelease(id, parent, hostId, generation, claim.AttemptId, pid, startedAt, current);
            return;
        }
        try
        {
            using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); ackTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await route!.Rpc.SendRequestAsync(GorynychProtocol.MethodHostReportNoteAck, new JsonObject { ["sessionId"] = parent,
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = id,
                    ["attemptId"] = claim.AttemptId, ["nativeMessageId"] = uuid, ["revision"] = current } } }, ackTimeout.Token);
        }
        catch (Exception e) when (e is IOException or JsonRpcException or OperationCanceledException or ObjectDisposedException or TimeoutException)
        { Volatile.Write(ref reportRecoveryFailure, "Native report receipt ACK is unresolved: " + e.GetType().Name); }
    }

    // Returns false only when no native note was handed off and the existing queued
    // route may proceed. The host's capability is checked for this actual link.
    async Task<bool> TryLiveReportNoteAsync(ReportInfo info, SessionUse use, CancellationToken ct)
    {
        var parentId = info.Identity.Parent; var feed = use.Feed;
        if (parentId is null) return false;
        var link = HostOf(use.Record).RouteOf(parentId);
        if (link?.HostId is not { } hostId || link.Generation?.Key is not { } generation ||
            (bool?)link.Info.Meta?[GorynychProtocol.CapHostReportNote]?["durableRouting"] != true) return false;
        ReportDeliveryClaim? claim = null; var started = false; bool markedBefore = true; long markVersion = 0;
        // One waiting recipient cannot occupy all global handoff slots. Entries exist
        // only while bounded delivery workers reference them and are removed at zero.
        var recipient = TakeLiveRecipient(parentId); var recipientHeld = false; var slotHeld = false;
        try
        {
            await recipient.Gate.WaitAsync(ct); recipientHeld = true;
            await reportDeliverySlots.WaitAsync(ct); slotHeld = true;
            Task<JsonNode?> response;
            await archiveGate.WaitAsync(ct);
            try
            {
                BeforeLiveReportForwardWait?.Invoke();
                await feed.ForwardGate.WaitAsync(ct);
                try
                {
                    var parent = store.Get(parentId);
                    if (Draining || feed.Retired) return true;
                    if (parent is null) { reports.PendingDeliveryReason(info.ReportId, "Intended parent is unavailable"); return true; }
                    if (parent.Deleted) { reports.SetDisposition(info.ReportId, "failed", "Intended parent was deleted"); return true; }
                    if (parent.Archived) { reports.PendingDeliveryReason(info.ReportId, "Intended parent is archived; restore it to continue"); return true; }
                    if (IsStopping(parent.Agent)) { reports.PendingDeliveryReason(info.ReportId, "Parent adapter is stopped or under maintenance"); return true; }
                    if (feed.ReadOnly is { } readOnly) { reports.PendingDeliveryReason(info.ReportId, DeliveryReason("Parent is read-only: " + readOnly.Reason)); return true; }
                    if (feed.OpenedReadOnly || HostOf(parent).RouteOf(parentId) != link) return false;
                    var text = ReportEnvelope(info.ReportId, info.Identity.Child);
                    var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                    claim = reports.ClaimDelivery(info.ReportId, parentId, hostId, generation, "note", digest,
                        link.ProcessId > 0 ? link.ProcessId : null, link.ProcessStart);
                    markedBefore = feed.MarkOtherSessionText(out markVersion);
                    reports.StartDelivery(info.ReportId, claim.AttemptId); started = true;
                    response = await link.Rpc.StartRequestAsync(GorynychProtocol.MethodHostReportNote,
                        new JsonObject { ["sessionId"] = parentId, ["text"] = text, ["_meta"] = new JsonObject
                        { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = info.ReportId, ["attemptId"] = claim.AttemptId } } }, CancellationToken.None);
                }
                finally { feed.ForwardGate.Release(); }
            }
            finally { archiveGate.Release(); }
            _ = await response.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var proof = reports.ReadNoteNoInput(info.ReportId, parentId, hostId, generation, claim!.AttemptId);
            if (proof is not null)
            {
                reports.QueueProvenNoInput(info.ReportId, parentId, hostId, generation, claim.AttemptId);
                feed.UnmarkOtherSessionText(markedBefore, markVersion);
                return false;
            }
            QueueReportDelivery(info.ReportId);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try
            {
                if (claim is null) reports.PendingDeliveryReason(info.ReportId, "Live report note admission failed (" + e.GetType().Name + ")");
                else if (!started) { reports.ReleaseUnstartedDelivery(info.ReportId, claim.AttemptId, "Live report note did not start"); feed.UnmarkOtherSessionText(markedBefore, markVersion); }
                else reports.SetDisposition(info.ReportId, "uncertain", "Live report note handoff is unresolved; no automatic replay");
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Volatile.Write(ref reportRecoveryFailure, "Live report delivery state could not be persisted: " + failure.GetType().Name); }
            return true;
        }
        finally
        {
            if (slotHeld) reportDeliverySlots.Release();
            if (recipientHeld) recipient.Gate.Release();
            ReleaseLiveRecipient(parentId, recipient);
        }
    }

    bool CanAutoSuperviseCoordinator(string parent, string child, string? originalHost = null, string? originalGeneration = null)
    {
        return options.CompletionAuto && CompletionScopeIsCurrent && completionScope is { } scope &&
            (string?)uiMetadata.Get(parent)[GorynychProtocol.MetaGroup]?["projectKey"] == scope.Project &&
            (string?)uiMetadata.Get(parent)[GorynychProtocol.MetaGroup]?["groupId"] == scope.Group &&
            (string?)uiMetadata.Get(child)[GorynychProtocol.MetaGroup]?["projectKey"] == scope.Project &&
            (string?)uiMetadata.Get(child)[GorynychProtocol.MetaGroup]?["groupId"] == scope.Group &&
            store.Get(child) is { Deleted: false, Archived: false, Agent: "claude" } &&
            agents.TryGetValue("claude", out var host) && host.RouteOf(child) is { Current: true, HostId: not null, Generation.Key: not null } route && !route.Rpc.IsClosed &&
            (originalHost is null || route.HostId == originalHost && route.Generation.Key == originalGeneration) &&
            route.Info.Meta?[GorynychProtocol.CapHostCompletionGuarded] is JsonValue value &&
            value.TryGetValue<bool>(out var supported) && supported;
    }

    // Only exact, acknowledged lifecycle delegation captures replace a report wake.
    // Recording the assignment reference first makes a crash before report disposition retryable.
    bool RetainCoordinatorReport(ReportInfo info)
    {
        var identity = info.Identity;
        if (identity.Parent is null) return false;
        var resume = identity.Cause == ReportCause.Reply && identity.ReplyWaitId is not null &&
            identity.ReplyWaitSequence is > 0 && identity.ReplyMessageSequence is > 0;
        if (identity.Cause != ReportCause.Delegation && !resume || identity.LegacyCapture ||
            identity.AssignmentId is not { } id || identity.AssignmentGeneration is not { } generation)
            return false;
        var notEnded = info.Outcome is "failed" or "cancelled" or "not_started" || info.Outcome?.StartsWith("stopped:", StringComparison.Ordinal) == true;
        if (!mailbox.RecordCoordinatorReport(id, generation, identity.Child, identity.Parent, info.ReportId, terminalNotEnded: notEnded, resume: resume)) return false;
        reports.SetDisposition(info.ReportId, "retained", "Available through assignment history; lifecycle notice handles owner wake");
        ReevaluateCoordinatorSupervision();
        QueueMailbox(identity.Parent);
        return true;
    }

    async Task DeliverReportAsync(string id, CancellationToken ct)
    {
        var info = reports.GetReport(id);
        if (info.Identity.PeerRoute is { } peerIdentity)
        {
            if (!info.ProducerAck) return;
            var recovery = info.ProducerEvidence == "failed" ? "producer_capture_failed" :
                info.ExactProducerExit ? "producer_exited" : null;
            if (recovery is null && (info.ProducerEvidence != "sealed" || info.CaptureConfidenceReason is not null)) return;
            if (info.Status == "retained")
            {
                if (!info.PeerMailboxAck && info.PeerMailboxIncarnation is { } incarnation)
                {
                    BeforePeerRetentionAckForTests?.Invoke();
                    if (mailbox.AcknowledgePeerRetained(peerIdentity, id, incarnation))
                        reports.AcknowledgePeerMailbox(id, peerIdentity, incarnation);
                }
                return;
            }
            if (info.Status != "pending") return;
            JsonObject settled;
            if (recovery is not null && info.Outcome is { } recoveredOutcome)
                settled = mailbox.SettlePeerTerminalUnknown(peerIdentity, id, recoveredOutcome, recovery);
            else if (info.Outcome is "failed" or "cancelled" || info.Outcome?.StartsWith("stopped: ", StringComparison.Ordinal) == true)
                settled = mailbox.SettlePeerTurn(peerIdentity, info.Outcome!, null, reportId: id);
            else if (info.Outcome != "ended") return;
            else if (info.Error is not null)
                settled = mailbox.SettlePeerTerminalUnknown(peerIdentity, id, "ended", "terminal_conflict");
            else if (info.IncompleteReason is not null)
                settled = mailbox.SettlePeerTerminalUnknown(peerIdentity, id, "ended", "capture_incomplete", info.PeerEvidence?.AcceptedMessageId);
            else if (info.PeerEvidence is not { } proof || proof.PromptId != peerIdentity.PromptId)
                settled = mailbox.SettlePeerTerminalUnknown(peerIdentity, id, "ended", "marker_unknown");
            else
            {
                var preview = reports.Preview(id, peerIdentity.Sender);
                settled = mailbox.SettlePeerTurn(peerIdentity, "ended", preview.Text, proof.AcceptedMessageId,
                    preview.Truncated, id, producerEvidenceComplete: true);
                if ((string?)settled["state"] == "unknown")
                    settled = mailbox.SettlePeerTerminalUnknown(peerIdentity, id, "ended", "accepted_unconfirmed", proof.AcceptedMessageId);
            }
            var settlementState = (string?)settled["state"];
            if (settlementState is not ("automatic_reply_accepted" or "explicit_reply_accepted" or "ended_without_text" or "outcome_unknown" or "failed" or "cancelled" or "stopped") ||
                (string?)settled["mailboxIncarnation"] is not { } mailboxIncarnation) return;
            if (settlementState == "automatic_reply_accepted") QueueMailbox(peerIdentity.Sender);
            AfterPeerMailboxSettlementForTests?.Invoke();
            reports.RetainPeerReport(id, peerIdentity, mailboxIncarnation);
            AfterPeerRetainedForTests?.Invoke();
            BeforePeerRetentionAckForTests?.Invoke();
            if (mailbox.AcknowledgePeerRetained(peerIdentity, id, mailboxIncarnation))
                reports.AcknowledgePeerMailbox(id, peerIdentity, mailboxIncarnation);
            return;
        }
        if (info.Delivery is { Route: "note", DispatchStarted: true } note && info.Status is "claimed" or "accepted" or "uncertain" or "completed" or "failed")
        {
            await ReconcileReportNoteAsync(info, note, ct);
            return;
        }
        if (info.Status is "claimed" or "accepted")
        {
            if (info.Status == "claimed" && info.Delivery is { DispatchStarted: false } unstarted)
                reports.ReleaseUnstartedDelivery(id, unstarted.AttemptId, "Previous claim ended before native handoff");
            else
            {
                reports.SetDisposition(id, "uncertain", "Native handoff may have started before service restart; no automatic replay");
                return;
            }
            info = reports.GetReport(id);
        }
        if (info.Status != "pending" || !info.ProducerAck) return;
        if (RetainCoordinatorReport(info)) return;
        var parentId = info.Identity.Parent;
        if (parentId is null) return;
        var parent = store.Get(parentId);
        if (parent is null) { reports.PendingDeliveryReason(id, "Intended parent is unavailable"); return; }
        if (parent.Deleted) { reports.SetDisposition(id, "failed", "Intended parent was deleted"); return; }
        if (parent.Archived) { reports.PendingDeliveryReason(id, "Intended parent is archived; restore it to continue"); return; }
        if (IsStopping(parent.Agent)) { reports.PendingDeliveryReason(id, "Parent adapter is stopped or under maintenance"); return; }
        if (feeds.TryGetValue(parentId, out var currentFeed) && currentFeed.ReadOnly is { } state)
        { reports.PendingDeliveryReason(id, DeliveryReason("Parent is read-only: " + state.Reason)); return; }

        ReportDeliveryClaim? claim = null; var started = false;
        try
        {
            SessionUse use;
            JsonRpcConnection rpc;
            // Do not begin an open while an archive operation is making the parent hidden.
            await archiveGate.WaitAsync(ct);
            try
            {
                parent = store.Get(parentId);
                if (parent is null) { reports.PendingDeliveryReason(id, "Intended parent is unavailable"); return; }
                if (parent.Deleted) { reports.SetDisposition(id, "failed", "Intended parent was deleted"); return; }
                if (parent.Archived) { reports.PendingDeliveryReason(id, "Intended parent is archived; restore it to continue"); return; }
                use = await UseAsync(parentId, ct);
                try { (rpc, _) = await EnsureLiveAsync(use.Record, ct); }
                catch { use.Dispose(); throw; }
            }
            finally { archiveGate.Release(); }
            using var held = use;
            var feed = use.Feed;
            if (info.Delivery?.Route != "note" && await TryLiveReportNoteAsync(info, use, ct)) return;
            if (feed.TurnGate.CurrentCount == 0) reports.PendingDeliveryReason(id, "Parent has an active turn; delivery is queued");
            await feed.TurnGate.WaitAsync(ct);
            feed.Activity.Begin();
            var enteredSlot = false;
            try
            {
                // Waiting on a busy recipient must not occupy global native-delivery slots.
                await reportDeliverySlots.WaitAsync(ct); enteredSlot = true;
                if (Draining || feed.Retired) return;
                if (feed.ReadOnly is { } readOnly) { reports.PendingDeliveryReason(id, DeliveryReason("Parent is read-only: " + readOnly.Reason)); return; }
                rpc = await RpcForTurnAsync(use.Record, rpc, ct, forAgent: true);
                await archiveGate.WaitAsync(ct);
                var archiveHeld = true;
                try
                {
                    if (Draining || feed.Retired) return;
                    if (feed.ReadOnly is { } changedReadOnly) { reports.PendingDeliveryReason(id, DeliveryReason("Parent is read-only: " + changedReadOnly.Reason)); return; }
                    parent = store.Get(parentId);
                    if (parent is null) { reports.PendingDeliveryReason(id, "Intended parent is unavailable"); return; }
                    if (parent.Deleted) { reports.SetDisposition(id, "failed", "Intended parent was deleted"); return; }
                    if (parent.Archived) { reports.PendingDeliveryReason(id, "Intended parent is archived; restore it to continue"); return; }
                    if (IsStopping(parent.Agent)) { reports.PendingDeliveryReason(id, "Parent adapter is stopped or under maintenance"); return; }
                    var route = HostOf(parent).RouteOf(parentId);
                    claim = reports.ClaimDelivery(id, parentId, route?.HostId, route?.Generation?.Key);
                    var text = ReportEnvelope(id, info.Identity.Child);
                    var sender = store.Get(info.Identity.Child) is { } source ? "child_" + source.Agent : "child_agent";
                    var meta = new JsonObject { [GorynychProtocol.MetaOrigin] = "report", [GorynychProtocol.MetaSender] = sender,
                        [GorynychProtocol.MetaFromSessionId] = info.Identity.Child, [GorynychProtocol.MetaPromptId] = claim.PromptId,
                        [GorynychProtocol.MetaReportDelivery] = new JsonObject { ["reportId"] = id, ["attemptId"] = claim.AttemptId } };
                    var block = new TextContent { Text = text };
                    // A definitely unstarted retry keeps the same prompt/message/attempt IDs;
                    // do not append another visible copy if the retained journal already has it.
                    if (!feed.Journal.ReadAll().OfType<JsonObject>().Any(row => (string?)row["messageId"] == claim.MessageId &&
                        (string?)row["sessionUpdate"] == "user_message_chunk"))
                        await feed.PublishAsync(AcpJson.ToNode<SessionUpdate>(new SessionUpdateUserMessageChunk
                            { Content = block, MessageId = claim.MessageId, Meta = meta.DeepClone().AsObject() })!.AsObject(), except: null, () => store.Touch(parentId));
                    Task<JsonNode?> response;
                    await feed.ForwardGate.WaitAsync(ct);
                    try
                    {
                        // ForwardGate may have been contended after the earlier checks.
                        // Release only the proven-unstarted claim when a boundary changed.
                        var blocked = Draining || feed.Retired ? "Service is draining or parent is unavailable" :
                            feed.ReadOnly is { } finalReadOnly ? DeliveryReason("Parent is read-only: " + finalReadOnly.Reason) :
                            IsStopping(parent.Agent) ? "Parent adapter is stopped or under maintenance" : null;
                        if (blocked is not null) { reports.ReleaseUnstartedDelivery(id, claim.AttemptId, blocked); return; }
                        BeforeReportDeliveryStart?.Invoke();
                        reports.StartDelivery(id, claim.AttemptId); started = true;
                        response = await rpc.StartRequestAsync(AcpMethods.Agent.SessionPrompt, WithMessageId(AcpJson.ToNode(new PromptRequest
                            { SessionId = parentId, Prompt = [block], Meta = meta }), claim.MessageId), CancellationToken.None);
                    }
                    finally { feed.ForwardGate.Release(); }
                    archiveGate.Release(); archiveHeld = false; // do not hold archive while the prompt runs
                    try
                    {
                        var result = await response;
                        if (reports.GetReport(id).Status is "completed" or "failed") return;
                        reports.SetDisposition(id, "accepted");
                        KeepMark(feed, result);
                        if (!await MarkPromptEndAsync(feed, claim.PromptId, result, null) && !feed.HasEndMark(claim.PromptId))
                            throw new IOException("Parent delivery terminal journal was not persisted");
                        var stop = (string?)result?["stopReason"];
                        if (stop is null or "end_turn") reports.SetDisposition(id, "completed");
                        else reports.SetDisposition(id, "failed", stop == "cancelled" ? "Parent report turn was cancelled" : "Parent report turn stopped before normal completion");
                    }
                    catch (JsonRpcException e)
                    {
                        if (reports.GetReport(id).Status is "completed" or "failed") return;
                        reports.SetDisposition(id, "failed", $"Parent report turn failed (native error code {e.Code})");
                        await MarkPromptEndAsync(feed, claim.PromptId, null, e);
                    }
                    return;
                }
                finally { if (archiveHeld) archiveGate.Release(); }
            }
            finally { if (enteredSlot) reportDeliverySlots.Release(); feed.Activity.End(); feed.TurnEnded(); feed.TurnGate.Release(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Do not expose exception messages, which may quote a prompt/body, in aggregate status.
            var reason = e is OperationCanceledException ? "Delivery interrupted; native handoff may be uncertain" :
                "Queued report delivery is blocked (" + e.GetType().Name + ")";
            try
            {
                if (claim is null) reports.PendingDeliveryReason(id, reason);
                else if (!started) reports.ReleaseUnstartedDelivery(id, claim.AttemptId, reason);
                else reports.SetDisposition(id, "uncertain", DeliveryReason("Native handoff may have started; no automatic replay: " + reason));
            }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Volatile.Write(ref reportRecoveryFailure, $"Report {id} delivery state could not be persisted: {write.Message}"); }
        }
    }
}
