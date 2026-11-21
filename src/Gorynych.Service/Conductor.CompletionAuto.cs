using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    ITimer? completionAutoTimer;
    int completionAutoRunning;
    string? completionAutoCursor;

    void StartCompletionAutoSweep()
    {
        if (!options.CompletionAuto || completionScope is null) return;
        completionAutoTimer = options.Time.CreateTimer(_ =>
        {
            if (cleanupStop.IsCancellationRequested) return;
            Track(Task.Run(async () =>
            {
                try { await SweepCompletionAutoAsync(cleanupStop.Token); }
                catch (OperationCanceledException) when (cleanupStop.IsCancellationRequested) { }
                catch (Exception e) { ServiceLog.Warn("completion auto sweep: " + e.Message); }
            }));
        }, null, options.CompletionAutoSweepInterval, options.CompletionAutoSweepInterval);
    }

    // One bounded page or one status-only recovery, with no parallel sweeps.
    internal async Task SweepCompletionAutoAsync(CancellationToken ct = default)
    {
        if (!options.CompletionAuto || Interlocked.Exchange(ref completionAutoRunning, 1) != 0) return;
        try
        {
            if (Draining) return;
            ReevaluateCoordinatorSupervision();
            if (completionScope is not { } scope || !CompletionScopeIsCurrent) return;
            var pending = mailbox.CompletionReminderPending(null, null, null);
            if (pending.Unknown) return;
            if (pending.Items.Count != 0)
            {
                if (pending.Items is [var saved] && saved.Binding.ProjectKey == scope.Project &&
                    saved.Binding.GroupId == scope.Group && saved.Binding.GroupInstance == scope.Instance)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await ReconcileCompletionLiveAsync(saved, timeout.Token);
                }
                // A recovery pass never dispatches, even when settlement succeeds.
                return;
            }
            var candidates = mailbox.CompletionAutoCandidates(completionAutoCursor, 16);
            if (candidates.Length == 0 && completionAutoCursor is not null)
            {
                completionAutoCursor = null;
                candidates = mailbox.CompletionAutoCandidates(null, 16);
            }
            if (candidates.Length == 0) return;
            bool Member(string sid)
            {
                var group = uiMetadata.Get(sid)[GorynychProtocol.MetaGroup];
                return (string?)group?["projectKey"] == scope.Project && (string?)group?["groupId"] == scope.Group;
            }
            foreach (var row in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var id = (string)row["assignmentId"]!;
                completionAutoCursor = id;
                var owner = (string)row["parent"]!; var child = (string)row["child"]!;
                var generation = (string)row["generation"]!; var revision = (long)row["revision"]!;
                if (!CompletionScopeIsCurrent || !Member(owner) || !Member(child) ||
                    store.Get(owner) is not { Deleted: false, Archived: false, Draft: false } ||
                    store.Get(child) is not { Deleted: false, Archived: false, Draft: false, Agent: "claude" } ||
                    !feeds.TryGetValue(child, out var feed) || feed.LastActivity == DateTimeOffset.MinValue ||
                    options.Time.GetUtcNow() - feed.LastActivity < TimeSpan.FromSeconds(60)) continue;
                // The snapshot's generation and revision are checked again by the
                // shared guarded path, including at the durable claim boundary.
                var result = await SubmitCompletionAsync(owner, id, generation, revision, TimeSpan.FromSeconds(60), ct);
                if ((string?)result["state"] is "settled" or "uncertain" ||
                    (string?)result["reason"] is "claim" or "claim_unknown") return;
            }
        }
        finally { Volatile.Write(ref completionAutoRunning, 0); }
    }
}
