using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    readonly ReplyReminderPolicy? reminderPolicy;
    readonly string reminderPolicyStatus;

    // Caller holds uiMetadataGate so a membership edit cannot race admission.
    ReplyReminderSchedule? ReminderScheduleFor(string sender, string recipient)
    {
        if (reminderPolicy is not { } policy || !ReminderGroupExists(policy) ||
            !ReminderGroupMember(sender, policy) || !ReminderGroupMember(recipient, policy)) return null;
        var now = options.Time.GetUtcNow();
        return new(policy.ProjectKey, policy.GroupId, now, now + policy.Overdue, policy.Delay);
    }

    string ReminderPolicyStatus() => reminderPolicy is { } policy && !ReminderGroupExists(policy)
        ? "disabled: configured group does not exist" : reminderPolicyStatus;

    bool ReminderGroupExists(ReplyReminderPolicy policy) => uiMetadata.List(policy.ProjectKey)
        .Any(g => (string?)g?["groupId"] == policy.GroupId);

    bool ReminderGroupMember(string session, ReplyReminderPolicy policy) =>
        uiMetadata.Get(session)[GorynychProtocol.MetaGroup] is JsonObject group &&
        (string?)group["projectKey"] == policy.ProjectKey && (string?)group["groupId"] == policy.GroupId;
    ITimer? reminderTimer;
    int reminderSweepRunning;
    internal Func<string, Task>? BeforeReminderClaim { get; set; }

    void StartReminderSweep()
    {
        if (reminderPolicy is not { } policy) return;
        reminderTimer = options.Time.CreateTimer(_ =>
        {
            if (Interlocked.Exchange(ref reminderSweepRunning, 1) != 0 || cleanupStop.IsCancellationRequested) return;
            Track(Task.Run(async () =>
            {
                try { await SweepReplyRemindersAsync(cleanupStop.Token); }
                catch (OperationCanceledException) when (cleanupStop.IsCancellationRequested) { }
                catch (Exception e) { ServiceLog.Warn("reply reminder sweep: " + e.Message); }
                finally { Interlocked.Exchange(ref reminderSweepRunning, 0); }
            }));
        }, null, policy.Sweep, policy.Sweep);
    }

    internal async Task SweepReplyRemindersAsync(CancellationToken ct = default)
    {
        if (reminderPolicy is not { } policy || Draining) return;
        foreach (var wait in mailbox.ReminderCandidates())
        {
            ct.ThrowIfCancellationRequested();
            var id = (string)wait["messageId"]!;
            var status = wait["reminders"]!;
            var now = options.Time.GetUtcNow();
            var overdue = DateTimeOffset.TryParse((string?)status["overdueAt"], out var deadline) && now >= deadline;
            var review = DateTimeOffset.TryParse((string?)status["reviewAt"], out var reviewAt) && now >= reviewAt;
            if (!overdue && !review) continue;
            if (overdue)
            {
                mailbox.ReminderReason(id, OriginalRecipientObstacle(wait));
                mailbox.RecordOverdue(id, now);
            }
            var reciprocal = review && (string?)wait["reciprocalMessageId"] is not null;
            var escalation = (overdue || reciprocal) && (string?)status["escalation"] == "pending";
            if (!escalation && (!review || (string?)status["reminder"] != "pending")) continue;
            var target = (string)wait[escalation ? "sender" : "recipient"]!;
            var source = (string)wait[escalation ? "recipient" : "sender"]!;
            await TryReminderAsync(id, target, source, escalation, policy, ct);
        }
    }

    // Absence of a reported mode is not evidence that an automatic prompt is safe.
    static bool ReminderModeKnown(JsonObject state)
    {
        if (state["modes"] is JsonObject modes && (string?)modes["currentModeId"] is { } current &&
            modes["availableModes"] is JsonArray available && available.OfType<JsonObject>().Any(m => (string?)m["id"] == current)) return true;
        foreach (var option in (state["configOptions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)option["category"] != "mode" || (string?)option["type"] != "select" || (string?)option["currentValue"] is not { } value) continue;
            var choices = (option["options"] as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(o => o["options"] is JsonArray nested ? nested.OfType<JsonObject>() : [o]);
            if (choices.Any(o => (string?)o["value"] == value)) return true;
        }
        return false;
    }

    bool ReminderAuthorityAllows(string target, string source)
    {
        if (!feeds.TryGetValue(target, out var targetFeed) || (targetFeed.ReminderModeUncertain || !ReminderModeKnown(targetFeed.State()))) return false;
        if (!InFullAccess(target)) return true;
        if (!feeds.TryGetValue(source, out var senderFeed) || store.Get(source) is not { Deleted: false } sender ||
            IsStopping(sender.Agent) || HostOf(sender).RouteOf(source) is not { Current: true } route || route.Rpc.IsClosed) return false;
        return senderFeed.HasConfirmedIdle(route.Rpc) && !senderFeed.ReminderModeUncertain && ReminderModeKnown(senderFeed.State()) && InFullAccess(source);
    }

    string OriginalRecipientObstacle(JsonObject wait)
    {
        var id = (string)wait["recipient"]!;
        if ((string?)wait["requestDelivery"] != "delivered") return "Original request delivery: " + (string?)wait["requestDelivery"];
        if (store.Get(id) is not { Deleted: false } record) return "Original recipient is unavailable";
        if (record.Archived) return "Original recipient is archived";
        if (IsStopping(record.Agent)) return "Original recipient adapter is stopped or under maintenance";
        if (!feeds.TryGetValue(id, out var feed) || HostOf(record).RouteOf(id) is null) return "Original recipient has no live route";
        if (feed.ReadOnly is not null || feed.OpenedReadOnly) return "Original recipient is read-only";
        if (feed.TurnRunning || feed.HasBackgroundWork || feed.Asking != 0 || feed.PendingRequests != 0) return "Original recipient is busy";
        return "No reply was received";
    }

    async Task TryReminderAsync(string id, string target, string source, bool escalation, ReplyReminderPolicy policy, CancellationToken ct)
    {
        if (!ReminderGroupExists(policy) || !ReminderGroupMember(target, policy) || !ReminderGroupMember(source, policy))
        { mailbox.ReminderReason(id, "Participants are no longer in the configured group"); return; }
        if (!feeds.TryGetValue(target, out var feed) || store.Get(target) is not { Deleted: false, Archived: false } record ||
            IsStopping(record.Agent) || feed.Retired || feed.ReadOnly is not null || feed.OpenedReadOnly)
        { mailbox.ReminderReason(id, "Recipient is archived, unavailable, stopped or read-only"); return; }
        var route = HostOf(record).RouteOf(target);
        if (route is null || !route.Current || route.Rpc.IsClosed || !feed.HasConfirmedIdle(route.Rpc))
        { mailbox.ReminderReason(id, "No current live recipient route"); return; }
        var activity = feed.Activity.Snapshot();
        if ((string?)activity["state"] != "idle" || activity["background"] is not JsonArray background || background.Count != 0 ||
            (activity["backgroundKnown"] is JsonArray known && known.Count != 0) || !feed.HasFreshReminderBackground(route.Rpc) || feed.HasBackgroundWork || feed.Asking != 0 ||
            feed.PendingRequests != 0 || feed.HasSteeringInFlight || feed.AgentTurnRunning || feed.Users != 0 || feed.LastActivity == DateTimeOffset.MinValue)
        { mailbox.ReminderReason(id, "Recipient activity or background state is busy or unknown"); return; }
        if (!ReminderModeKnown(feed.State()))
        { mailbox.ReminderReason(id, "Recipient permission mode is unknown"); return; }
        if (!ReminderAuthorityAllows(target, source))
        { mailbox.ReminderReason(id, "Recipient full-access approval is required"); return; }
        if (!await feed.LifeGate.WaitAsync(0, ct)) return;
        SessionUse? use = null;
        try
        {
            if (feed.Retired || store.Get(target) is not { Deleted: false, Archived: false } current || IsStopping(current.Agent)) return;
            lock (maintenanceGate)
            {
                if (IsStopping(current.Agent)) return;
                feed.EnterUse(); use = new SessionUse(feed, current, () => IdleFromNow(target));
            }
        }
        finally { feed.LifeGate.Release(); }
        if (use is null) return;
        var sourceFeed = Feed(source); var sourceGate = sourceFeed.ForwardGate;
        var targetMode = false; var sourceMode = false; var sourceForward = false;
        var turn = false; var forward = false; var opened = false; var archive = false; var metadata = false; var handed = false;
        TaskCompletionSource? worker = null; JsonObject? item = null;
        try
        {
            targetMode = await feed.ReminderModeGate.WaitAsync(0, ct); if (!targetMode) return;
            // The source mode matters only when the target itself has full access.
            if (InFullAccess(target)) { sourceMode = await sourceFeed.ReminderModeGate.WaitAsync(0, ct); if (!sourceMode) return; }
            turn = await feed.TurnGate.WaitAsync(0, ct);
            if (!turn) { mailbox.ReminderReason(id, "Recipient has an active turn"); return; }
            forward = await feed.ForwardGate.WaitAsync(0, ct);
            if (!forward) { mailbox.ReminderReason(id, "Recipient has pending forwarded work"); return; }
            sourceForward = await sourceGate.WaitAsync(0, ct);
            if (!sourceForward) { mailbox.ReminderReason(id, "Sender has a pending mode or forwarded operation"); return; }
            if (BeforeReminderClaim is { } beforeClaim) await beforeClaim(id).WaitAsync(ct);
            opened = await feed.OpenGate.WaitAsync(0, ct); if (!opened) return;
            archive = await archiveGate.WaitAsync(0, ct); if (!archive) return;
            metadata = await uiMetadataGate.WaitAsync(0, ct); if (!metadata) return;
            activity = feed.Activity.Snapshot();
            if (Draining || feed.Retired || feed.ReadOnly is not null || feed.OpenedReadOnly || feed.HasBackgroundWork ||
                feed.Asking != 0 || feed.PendingRequests != 0 || feed.AgentTurnRunning || feed.HasSteeringInFlight || feed.Users != 1 ||
                (string?)activity["state"] != "idle" || activity["background"] is not JsonArray b || b.Count != 0 ||
                (activity["backgroundKnown"] is JsonArray k && k.Count != 0) || !feed.HasFreshReminderBackground(route.Rpc) ||
                store.Get(target) is not { Deleted: false, Archived: false } final || IsStopping(final.Agent) ||
                store.Get(source) is not { Deleted: false } || HostOf(final).RouteOf(target) != route || !feed.HasConfirmedIdle(route.Rpc) || !ReminderGroupExists(policy) ||
                !ReminderGroupMember(target, policy) || !ReminderGroupMember(source, policy) || !ReminderModeKnown(feed.State()) ||
                (InFullAccess(target) && !sourceMode) || !ReminderAuthorityAllows(target, source)) return;
            lock (maintenanceGate)
            lock (mailboxGate)
            {
                if (Draining || IsStopping(final.Agent)) return;
                if (mailboxWorkers.ContainsKey(target) || mailbox.Read(target, 0, 1, true).Count != 0) return;
                var stream = feed.ReminderBackgroundStream(route.Rpc);
                if (stream is null) return;
                item = mailbox.ClaimReminder(id, escalation, options.Time.GetUtcNow(), policy.ProjectKey, policy.GroupId, stream);
                if (item is null) return;
                worker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mailboxWorkers[target] = worker.Task;
            }
            uiMetadataGate.Release(); metadata = false;
            archiveGate.Release(); archive = false;
            feed.OpenGate.Release(); opened = false;
            // Durable claim precedes scheduling. A crash in this gap is uncertain and is
            // never replayed; other recipients' overdue checks do not wait for this turn.
            var ownedWorker = worker; var ownedItem = item; var ownedUse = use;
            Track(Task.Run(async () =>
            {
                try { await DeliverMailboxTurnAsync(ownedItem, cleanupStop.Token, ownedUse, route.Rpc, suppliedGates: true, authorityGate: sourceGate, authorityModeHeld: sourceMode); }
                catch (Exception e) when (!cleanupStop.IsCancellationRequested)
                { FailMailbox(ownedItem, e.Message, uncertain: e is not Gorynych.Acp.JsonRpc.JsonRpcException); }
                finally
                {
                    if (sourceMode) sourceFeed.ReminderModeGate.Release(); feed.ReminderModeGate.Release();
                    lock (mailboxGate)
                    {
                        mailboxWorkers.Remove(target); ownedWorker.TrySetResult();
                        if (!cleanupStop.IsCancellationRequested && !Draining && mailbox.Read(target, 0, 1, true).Count > 0) QueueMailbox(target);
                    }
                }
            }));
            handed = true; worker = null;
        }
        finally
        {
            if (metadata) uiMetadataGate.Release(); if (archive) archiveGate.Release(); if (opened) feed.OpenGate.Release();
            if (!handed) { if (sourceForward) sourceGate.Release(); if (forward) feed.ForwardGate.Release(); if (turn) feed.TurnGate.Release(); if (sourceMode) sourceFeed.ReminderModeGate.Release(); if (targetMode) feed.ReminderModeGate.Release(); use.Dispose(); }
            if (worker is not null)
            {
                lock (mailboxGate)
                {
                    mailboxWorkers.Remove(target); worker.TrySetResult();
                    if (!cleanupStop.IsCancellationRequested && !Draining && mailbox.Read(target, 0, 1, true).Count > 0) QueueMailbox(target);
                }
            }
        }
    }

}
