using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    ReportStore reports = null!;
    string? reportRecoveryFailure;
    internal ReportStore ReportStorage => reports;
    readonly ConcurrentDictionary<string, ReportAdmission> admittedReports = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Lazy<bool>> reportImports = new(StringComparer.Ordinal);
    sealed class ReportPersistenceException(string message, Exception? inner = null) : Exception(message, inner);
    sealed class ReportAdmission(string id, ReportIdentity identity, bool created)
    {
        internal string Id { get; } = id;
        internal ReportIdentity Identity { get; } = identity;
        internal bool Created { get; } = created;
        internal string Session => Identity.Child;
        internal string? PromptId => Identity.PromptId;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource EndReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object Gate { get; } = new();
    }

    ReportAdmission? AdmitReport(SessionRecord record, JsonRpcConnection rpc, string promptId, string turnKey,
        ReportCause cause, string? assignment, string? message, string? assignmentGeneration = null,
        MailboxStore.CoordinatorReplyResume? resume = null)
    {
        if (!record.ReportToParent || record.ParentSessionId is not { } parent) return null;
        var link = HostOf(record).RouteOf(record.SessionId);
        if (link is null || link.Rpc != rpc || (options.Hosts is not null && link.HostId is null)) return null;
        var legacy = link.HostId is null || (bool?)link.Info.Meta?[GorynychProtocol.MetaReportCapture]?["supported"] != true;
        try
        {
            var identity = new ReportIdentity(turnKey, record.SessionId, parent, cause, promptId, assignment, message,
                link.HostId, link.Generation?.Key, link.HostId is null ? Environment.ProcessId : link.ProcessId > 0 ? link.ProcessId : null,
                link.HostId is null ? AgentHost.ProcessStartedAt(Environment.ProcessId) : link.ProcessStart, legacy, assignmentGeneration,
                resume?.WaitId, resume?.WaitSequence, resume?.ReplySequence);
            var receipt = reports.Reserve(identity);
            var id = receipt.ReportId;
            var admission = new ReportAdmission(id, identity, receipt.Created);
            if (!admittedReports.TryAdd(id, admission)) throw new InvalidOperationException("Report is already active");
            return admission;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new ReportPersistenceException("Report storage could not admit this turn; no native prompt was sent: " + e.Message, e);
        }
    }
    static JsonNode? WithReport(JsonNode? request, ReportAdmission? admission)
    {
        if (request is not JsonObject obj || admission is null || admission.Identity.LegacyCapture) return request;
        if (obj["_meta"] is not JsonObject meta) obj["_meta"] = meta = new JsonObject();
        meta[GorynychProtocol.MetaReportId] = admission.Id;
        return obj;
    }
    bool ImportTerminal(JsonObject terminal, string session)
    {
        var promptId = (string?)terminal["promptId"];
        var admission = admittedReports.Values.FirstOrDefault(a => a.Session == session && a.PromptId == promptId);
        var id = (string?)terminal["_meta"]?[GorynychProtocol.MetaReportId];
        try
        {
            // Durable identity survives caller completion and also prevents a missing-ID replay
            // from being mistaken for a legacy terminal. No host cursor advances on lookup failure.
            var expected = promptId is null ? [] : reports.FindByPrompt(session, promptId);
            if (id is null)
            {
                if (expected.Length == 0 && admission is null) return true;
                var legacy = expected.Where(r => r.Identity.LegacyCapture).ToArray();
                if (expected.Length != 1 || legacy.Length != 1 || (admission is not null && legacy[0].ReportId != admission.Id))
                    throw new InvalidOperationException("terminal lacks matching host correlation");
                id = legacy[0].ReportId;
                if (legacy[0].ProducerAck) { admission?.Completion.TrySetResult(); return true; }
                var nativeError = terminal["error"] as JsonObject;
                var outcome = ReportTerminalOutcome.From(terminal["result"], nativeError is not null);
                var message = nativeError is null ? null : (string?)nativeError["message"];
                if (message is { Length: > 100 })
                {
                    var end = char.IsHighSurrogate(message[99]) ? 99 : 100;
                    message = message[..end] + " [truncated]";
                }
                using (var capture = reports.OpenCapture(id))
                {
                    if (!capture.Snapshot().Sealed)
                    {
                        var observed = Feed(session).SnapshotTurnText();
                        if (observed is not null) capture.Append(observed);
                        capture.SealIncomplete("Legacy host cannot supply the full final text", outcome, message);
                    }
                    reports.Commit(id, capture);
                }
                reports.AcknowledgeProducer(id);
                QueueReportDelivery(id);
                admission?.Completion.TrySetResult();
                return true;
            }
            var stored = reports.GetReport(id);
            if (stored.Identity.Child != session || stored.Identity.PromptId != promptId ||
                (admission is not null && id != admission.Id))
                throw new InvalidOperationException("terminal lacks matching host correlation");
            ImportReport(id, (string?)terminal["_meta"]?[GorynychProtocol.MetaReportCapture]?["failure"]);
            var published = reports.GetReport(id);
            if (published.ProducerEvidence == "failed")
                admission?.Completion.TrySetException(new ReportPersistenceException($"Report {id} capture was incomplete: {reports.Read(id, session).IncompleteReason}"));
            else admission?.Completion.TrySetResult();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Volatile.Write(ref reportRecoveryFailure, $"Report terminal for {session} remains unresolved: {e.Message}");
            admission?.Completion.TrySetException(new ReportPersistenceException($"Report {admission.Id} could not be durably imported: {e.Message}; native turn was not repeated.", e));
            return false;
        }
    }
    void AbandonUnstarted(ReportAdmission? admission)
    {
        if (admission is null || !admission.Created) return;
        try { reports.AbandonReservation(admission.Id, admission.Identity); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new ReportPersistenceException($"Report {admission.Id} was not dispatched but its reservation could not be released: {e.Message}", e);
        }
    }
    bool HostRefusedBeforeDispatch(ReportAdmission? admission, JsonRpcException error)
    {
        var proof = error.ErrorData?[GorynychProtocol.MetaReportRefused];
        return admission is { Created: true } && (string?)proof?["reportId"] == admission.Id &&
            (string?)proof?["sessionId"] == admission.Session && (string?)proof?["promptId"] == admission.PromptId;
    }
    async Task HandleReportResponseErrorAsync(ReportAdmission? admission, JsonRpcException error)
    {
        if (HostRefusedBeforeDispatch(admission, error)) { AbandonUnstarted(admission); return; }
        await RequireImportedAsync(admission);
    }
    // Explicit reconciliation may retry this after a failed publication or ACK.
    internal void ImportReport(string id, string? captureFailure = null)
    {
        var import = reportImports.GetOrAdd(id, key => new Lazy<bool>(() =>
        {
            var current = reports.GetReport(key);
            if (current.ProducerAck) return true;
            if (current.Status != "reserved") { reports.AcknowledgeProducer(key); return true; }
            if (current.Identity.HostId is not null && current.ProducerEvidence is not ("sealed" or "failed"))
                throw new InvalidOperationException("Producer seal has no durable host evidence; reconciliation required");
            if (current.ProducerEvidence == "failed")
            {
                // A host failure can make even a visually sealed spool untrustworthy. Preserve
                // a known incomplete snapshot when possible; otherwise publish explicit loss.
                try
                {
                    using var capture = reports.OpenCapture(key);
                    var snapshot = capture.Snapshot();
                    if (snapshot.Sealed && snapshot.IncompleteReason is not null) reports.Commit(key, capture);
                    else reports.CommitCaptureFailure(key, current.ProducerOutcome ?? "unknown", current.ProducerFailure ?? "Producer capture failed", current.ProducerError);
                }
                catch (InvalidDataException)
                {
                    reports.CommitCaptureFailure(key, current.ProducerOutcome ?? "unknown", current.ProducerFailure ?? "Producer capture failed", current.ProducerError);
                }
            }
            else using (var capture = reports.OpenCapture(key))
            {
                var snapshot = capture.Snapshot();
                if (captureFailure is not null && snapshot.IncompleteReason is null)
                    throw new InvalidOperationException("Host reported capture failure without a durable incomplete reason; reconciliation required");
                reports.Commit(key, capture);
            }
            reports.AcknowledgeProducer(key);
            return true;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try { _ = import.Value; QueueReportDelivery(id); }
        finally { reportImports.TryRemove(new KeyValuePair<string, Lazy<bool>>(id, import)); }
    }
    void PublishUnstartedSpawnFailure(SessionRecord child, SessionRecord parent, string? promptId, Exception failure)
    {
        if (!child.ReportToParent) return;
        try
        {
            var generation = (string?)child.SpawnAssignment?["generation"];
            var identity = new ReportIdentity("spawn-unstarted:" + (child.SpawnAssignmentId ?? child.SessionId) + (generation is null ? "" : ":" + generation), child.SessionId,
                parent.SessionId, ReportCause.Spawn, promptId, child.SpawnAssignmentId, AssignmentGeneration: generation);
            var id = reports.Reserve(identity, BoundLocalNativeError(failure.Message)).ReportId;
            ImportReport(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            Volatile.Write(ref reportRecoveryFailure, "Could not publish unstarted spawn failure: " + e.Message);
            ServiceLog.Warn($"{child.SessionId}: unstarted spawn failure remains unresolved: {e.Message}");
        }
    }

    static string BoundLocalNativeError(string text)
    {
        const string suffix = " [truncated]";
        if (Encoding.UTF8.GetByteCount(text) <= ReportCapture.ErrorBytes) return text;
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > ReportCapture.ErrorBytes - suffix.Length) break;
            result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return result.Append(suffix).ToString();
    }

    void ImportLocalReport(ReportAdmission? admission, SessionFeed feed, JsonNode? result, JsonRpcException? error = null)
    {
        if (admission is null || admission.Identity.HostId is not null) return;
        try
        {
            using (var capture = reports.OpenCapture(admission.Id))
            {
                var observed = feed.SnapshotTurnText();
                if (observed is not null) capture.Append(observed);
                capture.SealIncomplete("In-process adapter cannot supply a complete durable native capture",
                    ReportTerminalOutcome.From(result, error is not null), error is null ? null : BoundLocalNativeError(error.Message));
                reports.Commit(admission.Id, capture);
            }
            reports.AcknowledgeProducer(admission.Id);
            admission.Completion.TrySetResult(); admission.EndReady.TrySetResult();
            QueueReportDelivery(admission.Id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { throw new ReportPersistenceException($"Report {admission.Id} could not be durably imported; native work was not repeated: {e.Message}", e); }
    }

    static async Task RequireImportedAsync(ReportAdmission? admission)
    {
        if (admission is null) return;
        if (!admission.Completion.Task.IsCompleted)
            throw new ReportPersistenceException($"Report {admission.Id} has no confirmed terminal import; native handoff is uncertain.");
        await admission.Completion.Task;
        await admission.EndReady.Task;
    }
    void ForgetAdmission(ReportAdmission? admission)
    {
        if (admission is not null) admittedReports.TryRemove(new KeyValuePair<string, ReportAdmission>(admission.Id, admission));
    }
    async Task ReportPersistenceNoticeAsync(SessionFeed feed, ReportPersistenceException error)
    {
        Volatile.Write(ref reportRecoveryFailure, error.Message);
        try
        {
            var meta = new JsonObject { [GorynychProtocol.MetaReportCapture] = new JsonObject { ["failure"] = error.Message } };
            await feed.PublishAsync(AcpJson.ToNode<SessionUpdate>(new SessionInfoUpdate { Meta = meta })!.AsObject(), except: null, () => { });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"{feed.SessionId}: report persistence notice could not be journaled: {e.Message}");
        }
    }

    static bool ProducerDefinitelyExited(ReportIdentity identity)
    {
        if (identity.HostProcessId is not { } pid || pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited) return true;
            if (identity.HostProcessStart is null) return false;
            return process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture) != identity.HostProcessStart;
        }
        catch (ArgumentException) { return true; } // PID does not exist
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; } // lack of permission or process evidence is not proof of exit
    }

    // Independent of bounded host replay. The store's producer lease is also checked before
    // treating an unconfirmed spool as dead: a busy writer remains unknown, never cancelled.
    internal async Task ReconcileReportInventoryAsync(string? hostId = null)
    {
        try
        {
            string? cursor = null;
            do
            {
                var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
                foreach (var item in page.Items)
                {
                    var identity = item.Identity;
                    // A live caller owns normal response and temporary delivery. Inventory takes
                    // over only after that admission is gone, avoiding a duplicate report race.
                    if (admittedReports.ContainsKey(item.ReportId)) continue;
                    if ((hostId is not null && identity.HostId != hostId) ||
                        (identity.PromptId is null && !identity.TurnKey.StartsWith("spawn-unstarted:", StringComparison.Ordinal)) ||
                        removed.ContainsKey(identity.Child) || store.Get(identity.Child) is not { Deleted: false }) continue;
                    try
                    {
                        if (!item.ProducerAck)
                        {
                            if (item.Status == "reserved" && item.ProducerEvidence is not ("sealed" or "failed"))
                            {
                                if (!ProducerDefinitelyExited(identity)) continue;
                                var sealedCandidate = false;
                                ReportCapture? candidate = null;
                                try { candidate = reports.OpenCapture(item.ReportId); }
                                catch (InvalidDataException) { /* corrupt spool: no verified final candidate */ }
                                catch (IOException e)
                                {
                                    Volatile.Write(ref reportRecoveryFailure, $"Report {item.ReportId} producer remains unavailable or busy: {e.Message}");
                                    continue; // not proven safe to reconcile
                                }
                                if (candidate is not null)
                                {
                                    using (candidate)
                                        if (candidate.Snapshot().Sealed)
                                        {
                                            // Publication failures must reach health reporting; only
                                            // an unsuccessful lease/open is treated as producer busy.
                                            reports.CommitUnconfirmed(item.ReportId, candidate);
                                            sealedCandidate = true;
                                        }
                                }
                                if (!sealedCandidate)
                                    reports.CommitCaptureFailure(item.ReportId, "unknown", "Producer exited without a valid sealed capture");
                                reports.AcknowledgeProducer(item.ReportId);
                            }
                            else ImportReport(item.ReportId);
                        }
                        QueueReportDelivery(item.ReportId);
                        // No prompt ID exists if session open failed before prompt construction.
                        if (identity.PromptId is null) continue;
                        var result = reports.Read(item.ReportId, identity.Child);
                        var feed = Feed(identity.Child);
                        if (reports.GetReport(item.ReportId).EndJournaled)
                        {
                            if (feed.Adopted is { } already && already.ReportId == item.ReportId && already.HostId == identity.HostId &&
                                feed.EndAdoptedIf(already) is { } completed) EndAdopted(feed, completed);
                            continue;
                        }
                        var stopReason = result.Outcome == "cancelled" ? "cancelled" :
                            result.Outcome.StartsWith("stopped: ", StringComparison.Ordinal) ? result.Outcome[9..] : "end_turn";
                        JsonObject? nativeResult = result.Outcome == "not_started" ? null : new JsonObject { ["stopReason"] = stopReason };
                        Exception? failure = result.Outcome is "failed" or "not_started" or "unknown" or "stopped: [reason truncated]"
                            ? new JsonRpcException(JsonRpcException.InternalError, result.Error ?? result.IncompleteReason ?? "Native outcome is unknown") : null;
                        var marked = await MarkPromptEndAsync(feed, identity.PromptId, nativeResult, failure);
                        if (!marked && !feed.HasEndMark(identity.PromptId))
                        {
                            await ReportPersistenceNoticeAsync(feed, new ReportPersistenceException($"Report {item.ReportId} end journal remains unwritten; recovery required."));
                            continue;
                        }
                        reports.RecordEndJournaled(item.ReportId);
                        if (feed.Adopted is { } adopted && adopted.ReportId == item.ReportId && adopted.HostId == identity.HostId &&
                            feed.EndAdoptedIf(adopted) is { } ended) EndAdopted(feed, ended);
                        if (!marked) continue;

                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
                    {
                        Volatile.Write(ref reportRecoveryFailure, $"Report {item.ReportId} recovery remains uncertain: {e.Message}");
                        await ReportPersistenceNoticeAsync(Feed(identity.Child), new ReportPersistenceException($"Report {item.ReportId} recovery remains uncertain: {e.Message}", e));
                    }
                }
            } while (cursor is not null);
            // A reattached host may make an intended parent available, even if it
            // produced none of the reports filtered by this inventory pass.
            QueueReportDeliveries();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            Volatile.Write(ref reportRecoveryFailure, "Report inventory failed; recovery remains uncertain: " + e.Message);
            ServiceLog.Warn($"Report inventory failed; recovery remains uncertain: {e.Message}");
        }
    }

    JsonObject ReportRecoveryStatus()
    {
        try
        {
            var unknown = 0; string? cursor = null;
            do
            {
                var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
                unknown += page.Items.Count(r => r.Status == "reserved" &&
                    r.ProducerEvidence is not ("sealed" or "failed"));
            } while (cursor is not null);
            return new JsonObject { ["unconfirmed"] = unknown, ["lastFailure"] = Volatile.Read(ref reportRecoveryFailure) };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            return new JsonObject { ["failure"] = "Report inventory cannot be read: " + e.Message };
        }
    }

    bool HasRetainedProducer(string session, string promptId, string hostId)
    {
        try { return reports.FindByPrompt(session, promptId).Any(item => item.Identity.HostId == hostId); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Volatile.Write(ref reportRecoveryFailure, "Cannot determine producer state: " + e.Message);
            return true; // do not synthesize cancellation when durable identity cannot be read
        }
    }

    async Task ReconcileAfterHostExitAsync(HostLink link)
    {
        if (link.HostId is null) return;
        await ReconcileReportInventoryAsync(link.HostId);
        if (link.ProcessId <= 0 || link.ProcessId == Environment.ProcessId || link.ProcessStart is null) return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(link.ProcessId);
            var started = process.StartTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (started == link.ProcessStart && !process.HasExited)
                await process.WaitForExitAsync(cleanupStop.Token);
        }
        catch (ArgumentException) { } // process no longer exists
        catch (OperationCanceledException) when (cleanupStop.IsCancellationRequested) { return; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Volatile.Write(ref reportRecoveryFailure, "Cannot establish producer process exit: " + e.Message);
            return;
        }
        await ReconcileReportInventoryAsync(link.HostId);
        foreach (var feed in feeds.Values)
        {
            if (feed.Adopted is not { } adopted || adopted.HostId != link.HostId || adopted.PromptId is null ||
                HasRetainedProducer(feed.SessionId, adopted.PromptId, link.HostId)) continue;
            await ReportPersistenceNoticeAsync(feed, new ReportPersistenceException("The old host exited; this adopted turn's provenance and native outcome are unknown."));
            await MarkPromptEndAsync(feed, adopted.PromptId, null,
                new JsonRpcException(JsonRpcException.InternalError, "The host exited and the native outcome is unknown"));
            if (feed.EndAdoptedIf(adopted) is { } ended) EndAdopted(feed, ended);
        }
    }

}
