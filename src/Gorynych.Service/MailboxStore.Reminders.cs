using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

// Internal scheduling data comes from service-owned group policy, never client metadata.
internal sealed record ReplyReminderSchedule(string ProjectKey, string GroupId,
    DateTimeOffset AdmittedAt, DateTimeOffset OverdueAt, TimeSpan Delay);

internal sealed partial class MailboxStore
{
    internal IReadOnlyList<JsonObject> ReminderCandidates()
    {
        lock (gate) return replyWaits.Where(p => p.Value is JsonObject w && (string?)w["state"] == "waiting" && w["reminders"] is JsonObject)
            .Select(p =>
            {
                var w = p.Value!.DeepClone().AsObject(); w["messageId"] = p.Key;
                w["reciprocalMessageId"] = ReciprocalWait(p.Key, w);
                return w;
            }).ToArray();
    }

    string? ReciprocalWait(string id, JsonNode wait) => replyWaits.FirstOrDefault(p => p.Key != id && p.Value is JsonObject other &&
        (string?)other["state"] == "waiting" && (string?)other["sender"] == (string?)wait["recipient"] &&
        (string?)other["recipient"] == (string?)wait["sender"] &&
        (string?)other["reminders"]?["projectKey"] == (string?)wait["reminders"]?["projectKey"] &&
        (string?)other["reminders"]?["groupId"] == (string?)wait["reminders"]?["groupId"]).Key;

    internal JsonObject ReminderCounts(DateTimeOffset now)
    {
        lock (gate)
        {
            var scheduled = 0; var overdue = 0; var started = 0; var uncertain = 0;
            foreach (var wait in replyWaits.Select(p => p.Value).OfType<JsonObject>())
            {
                if ((string?)wait["state"] != "waiting" || wait["reminders"] is not JsonObject reminder) continue;
                scheduled++;
                if (DateTimeOffset.TryParse((string?)reminder["overdueAt"], out var at) && now >= at) overdue++;
                if ((string?)reminder["reminder"] == "started" || (string?)reminder["escalation"] == "started") started++;
                if ((string?)reminder["reminder"] == "uncertain" || (string?)reminder["escalation"] == "uncertain") uncertain++;
            }
            return new JsonObject { ["scheduled"] = scheduled, ["overdue"] = overdue, ["started"] = started, ["uncertain"] = uncertain };
        }
    }

    internal void RecordOverdue(string id, DateTimeOffset now)
    {
        lock (gate)
        {
            if (replyWaits[id] is not JsonObject wait || (string?)wait["state"] != "waiting" ||
                wait["reminders"] is not JsonObject status || status["overdueNotice"] is not null ||
                !DateTimeOffset.TryParse((string?)status["overdueAt"], out var deadline) || now < deadline) return;
            var sender = (string?)wait["sender"]; var recipient = (string?)wait["recipient"];
            var reciprocal = replyWaits.FirstOrDefault(p => p.Key != id && p.Value is JsonObject other &&
                (string?)other["state"] == "waiting" && (string?)other["sender"] == recipient && (string?)other["recipient"] == sender &&
                (string?)other["reminders"]?["projectKey"] == (string?)status["projectKey"] &&
                (string?)other["reminders"]?["groupId"] == (string?)status["groupId"]).Key;
            var copy = replyWaits.DeepClone().AsObject();
            copy[id]!["reminders"]!["overdueNotice"] = new JsonObject
            { ["messageId"] = id, ["requestDelivery"] = (string?)wait["requestDelivery"],
              ["reason"] = (string?)status["reason"], ["reciprocalMessageId"] = reciprocal, ["observedAt"] = now.ToString("O") };
            Save(entries.DeepClone().AsArray(), next, waits: copy);
        }
    }

    internal void ReminderReason(string id, string reason)
    {
        if (reason.Length > 512) reason = reason[..512];
        lock (gate)
        {
            if (replyWaits[id]?["reminders"] is not JsonObject status || (string?)status["reason"] == reason) return;
            var copy = replyWaits.DeepClone().AsObject();
            copy[id]!["reminders"]!["reason"] = reason;
            Save(entries.DeepClone().AsArray(), next, waits: copy);
        }
    }

    // One atomic publication creates both the intent and the durable handoff claim. The
    // same gate linearizes this with reply and cancel; a lost response is never replayed.
    internal JsonObject? ClaimReminder(string id, bool escalation, DateTimeOffset now, string project, string group, string? stream = null)
    {
        lock (gate)
        {
            if (replyWaits[id] is not JsonObject wait || (string?)wait["state"] != "waiting" ||
                wait["reminders"] is not JsonObject status || (string?)status["projectKey"] != project || (string?)status["groupId"] != group) return null;
            var field = escalation ? "escalation" : "reminder";
            var sender = (string)wait["sender"]!; var recipient = (string)wait["recipient"]!;
            var reciprocal = ReciprocalWait(id, wait);
            var overdue = DateTimeOffset.TryParse((string?)status["overdueAt"], out var overdueAt) && now >= overdueAt;
            var review = DateTimeOffset.TryParse((string?)status["reviewAt"], out var reviewAt) && now >= reviewAt;
            if ((string?)status[field] != "pending" ||
                (escalation ? !overdue && !(review && reciprocal is not null) : !review || reciprocal is not null || (string?)wait["requestDelivery"] != "delivered")) return null;
            var replacement = entries.DeepClone().AsArray(); var trimmed = trimmedThrough;
            while (replacement.Count >= capacity)
            {
                var evict = replacement.OfType<JsonObject>().FirstOrDefault(PeerTurnEvictable);
                if (evict is null) throw new InvalidOperationException("Mailbox storage is full of pending messages");
                trimmed = Math.Max(trimmed, (long)evict["sequence"]!); replacement.Remove(evict);
            }
            var messageId = "service-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(field + ":" + id)));
            if (replacement.OfType<JsonObject>().Any(e => (string?)e["id"] == messageId))
                throw new InvalidOperationException("Reminder message ID collides with retained mailbox history");
            var target = escalation ? sender : recipient;
            var reason = (string?)status["reason"] ?? (string?)wait["requestDelivery"] ?? "unknown";
            var body = escalation
                ? $"Reply wait {id} {(overdue ? "is overdue" : "has a reciprocal dependency")}. Request delivery: {wait["requestDelivery"]}; latest obstacle: {reason}." +
                    (reciprocal is null ? "" : $" Reciprocal wait: {reciprocal}.")
                : $"Session {sender} is waiting for a reply to message_id {id}. Use reply_message for that ID, or state a specific obstacle.";
            var item = new JsonObject { ["id"] = messageId, ["sequence"] = next + 1, ["sender"] = "service", ["recipient"] = target,
                ["text"] = body, ["delivered"] = false, ["started"] = true, ["reminderFor"] = id, ["reminderKind"] = field,
                ["reminderSource"] = escalation ? recipient : sender, ["reminderStream"] = stream };
            replacement.Add((JsonNode)item);
            var waits = replyWaits.DeepClone().AsObject(); var changed = waits[id]!["reminders"]!;
            changed[field] = "started"; changed[field + "MessageId"] = messageId; changed["reason"] = null;
            Save(replacement, next + 1, trimmed, waits);
            return item.DeepClone().AsObject();
        }
    }
}
