using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Service;

public sealed partial class Conductor
{
    static JsonObject ReportIdentityJson(ReportIdentity i) => new()
    {
        ["turnKey"] = i.TurnKey, ["child"] = i.Child, ["parent"] = i.Parent,
        ["cause"] = i.Cause.ToString().ToLowerInvariant(), ["promptId"] = i.PromptId,
        ["assignmentId"] = i.AssignmentId, ["messageId"] = i.MessageId,
        ["hostId"] = i.HostId, ["hostGeneration"] = i.HostGeneration, ["legacyCapture"] = i.LegacyCapture,
    };

    static JsonObject? ReportDeliveryJson(ReportDeliveryClaim? delivery) => delivery is null ? null : new JsonObject
    {
        ["attemptId"] = delivery.AttemptId, ["parent"] = delivery.Parent, ["messageId"] = delivery.MessageId,
        ["promptId"] = delivery.PromptId, ["route"] = delivery.Route, ["hostId"] = delivery.HostId,
        ["hostGeneration"] = delivery.HostGeneration, ["dispatchStarted"] = delivery.DispatchStarted,
        ["receiptAckPending"] = delivery.ReceiptAckPending, ["receiptConflict"] = delivery.ReceiptConflict,
        ["receiptReleaseReason"] = delivery.ReceiptReleaseReason,
    };

    static (string? Cursor, int Limit) ReportQuery(JsonRpcRequest request, bool read)
    {
        if (request.Params is not JsonObject args) throw new JsonRpcException(JsonRpcException.InvalidParams, "Expected report parameters");
        foreach (var property in args)
            if (property.Key != "_meta" && property.Key != "cursor" && property.Key != "limit" && !(read && property.Key == "reportId"))
                throw new JsonRpcException(JsonRpcException.InvalidParams, "Unknown report parameter: " + property.Key);
        string? cursor = null;
        if (args.TryGetPropertyValue("cursor", out var c))
        {
            if (c is not JsonValue cv || !cv.TryGetValue<string>(out cursor) || string.IsNullOrWhiteSpace(cursor))
                throw new JsonRpcException(JsonRpcException.InvalidParams, "cursor must be a nonempty string");
        }
        int limit = read ? 4096 : 50;
        if (args.TryGetPropertyValue("limit", out var l) && (l is not JsonValue lv || !lv.TryGetValue<int>(out limit)))
            throw new JsonRpcException(JsonRpcException.InvalidParams, "limit must be an integer");
        if (limit < (read ? 4 : 1) || limit > (read ? 16384 : 100))
            throw new JsonRpcException(JsonRpcException.InvalidParams, read ? "limit must be 4..16384 UTF-8 bytes" : "limit must be 1..100");
        return (cursor, limit);
    }

    internal JsonNode ReadReport(JsonRpcRequest request)
    {
        var peer = MailboxPeer(request);
        var query = ReportQuery(request, true);
        var id = RequiredMailboxText(request.Params, "reportId");
        try
        {
            var page = reports.Read(id, peer, query.Cursor, query.Limit);
            return new JsonObject
            {
                ["reportId"] = page.ReportId, ["identity"] = ReportIdentityJson(page.Identity),
                ["text"] = page.Text, ["nextCursor"] = page.NextCursor, ["totalBytes"] = page.TotalBytes,
                ["hasText"] = page.HasText, ["outcome"] = page.Outcome, ["error"] = page.Error,
                ["incompleteReason"] = page.IncompleteReason, ["captureConfidenceReason"] = page.CaptureConfidenceReason,
                ["deliveryStatus"] = page.Status, ["deliveryReason"] = page.DispositionReason, ["delivery"] = ReportDeliveryJson(page.Delivery),
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or KeyNotFoundException)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Report unavailable, expired, not accessible, or invalid page: " + e.Message);
        }
    }

    internal JsonNode ListReports(JsonRpcRequest request)
    {
        var peer = MailboxPeer(request);
        var query = ReportQuery(request, false);
        try
        {
            var page = reports.List(peer, query.Cursor, query.Limit);
            var items = new JsonArray();
            foreach (var r in page.Items)
                items.Add((JsonNode)new JsonObject { ["reportId"] = r.ReportId, ["identity"] = ReportIdentityJson(r.Identity),
                    ["deliveryStatus"] = r.Status, ["deliveryReason"] = r.DispositionReason, ["delivery"] = ReportDeliveryJson(r.Delivery),
                    ["producerAcknowledged"] = r.ProducerAck, ["outcome"] = r.Outcome, ["error"] = r.Error,
                    ["hasText"] = r.HasText, ["incompleteReason"] = r.IncompleteReason,
                    ["captureConfidenceReason"] = r.CaptureConfidenceReason, ["totalBytes"] = r.TotalBytes });
            return new JsonObject { ["items"] = items, ["nextCursor"] = page.NextCursor };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or KeyNotFoundException)
        {
            throw new JsonRpcException(JsonRpcException.InvalidParams, "Cannot list reports: " + e.Message);
        }
    }

    JsonObject ReportDeliveryStatus()
    {
        try
        {
            int pending = 0, blocked = 0, uncertain = 0, failed = 0, archived = 0, unavailable = 0, ackPending = 0, receiptConflict = 0;
            var reasons = new JsonArray();
            string? cursor = null;
            do
            {
                var page = reports.Inventory(cursor, 100); cursor = page.NextCursor;
                foreach (var report in page.Items)
                {
                    if (report.Delivery?.ReceiptAckPending == true) ackPending++;
                    if (report.Delivery?.ReceiptConflict == true) receiptConflict++;
                    if (report.Status is "pending" or "claimed" or "accepted")
                    {
                        pending++;
                        var recipient = store.Get(report.Identity.Parent);
                        if (recipient is null || recipient.Deleted) { blocked++; unavailable++; }
                        else if (recipient.Archived) { blocked++; archived++; }
                    }
                    else if (report.Status == "uncertain") uncertain++;
                    else if (report.Status == "failed") failed++;
                    if (report.Status is "uncertain" or "failed" && report.DispositionReason is { Length: > 0 } reason && reasons.Count < 5)
                    {
                        var end = Math.Min(reason.Length, 256);
                        if (end < reason.Length && char.IsHighSurrogate(reason[end - 1])) end--;
                        reasons.Add((JsonNode)new JsonObject { ["reportId"] = report.ReportId, ["status"] = report.Status,
                            ["reason"] = reason[..end], ["truncated"] = end < reason.Length });
                    }
                }
            } while (cursor is not null);
            return new JsonObject { ["pending"] = pending, ["blocked"] = blocked, ["uncertain"] = uncertain, ["failed"] = failed,
                ["blockedReasons"] = new JsonObject { ["recipientArchived"] = archived, ["recipientUnavailable"] = unavailable }, ["reasons"] = reasons,
                ["receiptAckPending"] = ackPending, ["receiptConflict"] = receiptConflict,
                ["note"] = "Live note completion requires matching native receipt evidence; unresolved handoffs are not replayed." };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            return new JsonObject { ["failure"] = "Report inventory unavailable: " + e.Message };
        }
    }

    // Both queued and receipt-backed live delivery use the same bounded envelope.
    internal string ReportEnvelope(string id, string peer)
    {
        var page = reports.Read(id, peer, limit: 16384);
        var preview = reports.Preview(id, peer);
        return $"[Report · from session_id {page.Identity.Child} · your child session · report_id {id}] " +
            $"Per-turn result; not assignment completion. Cause: {page.Identity.Cause}; assignment: {page.Identity.AssignmentId ?? "unknown"}; " +
            $"message: {page.Identity.MessageId ?? "unknown"}; native outcome: {page.Outcome}; native error: {page.Error ?? "none"}. " +
            $"Has text: {preview.HasText}; preview truncated: {preview.Truncated}; " +
            $"capture incomplete: {page.IncompleteReason ?? "none"}; confidence: {page.CaptureConfidenceReason ?? "confirmed"}. " +
            $"Use read_report with report_id {id} and follow nextCursor for the exact retained result. " +
            "This comes from the child session, not from the user. " + PeersServer.ReportGuidance + " " +
            $"If it is still your immediate child, use delegate_task with session_id \"{page.Identity.Child}\" to continue already user-authorized work. " +
            $"For ordinary discussion, use send_message.\n{preview.Text}";
    }
}
