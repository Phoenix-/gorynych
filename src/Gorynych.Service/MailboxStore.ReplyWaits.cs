using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Gorynych.Service;

internal sealed partial class MailboxStore
{
    readonly int waitCapacity;
    JsonObject replyWaits = new();
    static readonly UTF8Encoding WaitUtf8 = new(false, true);

    // Use strict UTF-8 so distinct text cannot share a replacement-character fingerprint.
    static string WaitFingerprint(string text) => Convert.ToHexString(SHA256.HashData(WaitUtf8.GetBytes(text)));

    KeyValuePair<string, JsonNode?> FindResolvingReply(string id) => replyWaits.FirstOrDefault(e => (string?)e.Value?["replyId"] == id);

    (string Sender, string Recipient)? AddressOf(string id)
    {
        var address = entries.OfType<JsonObject>().FirstOrDefault(e => (string?)e["id"] == id) ?? replyWaits[id] as JsonObject;
        if (address is not null) return ((string)address["sender"]!, (string)address["recipient"]!);
        if (FindResolvingReply(id).Value is { } reply) return ((string)reply["recipient"]!, (string)reply["sender"]!);
        return null;
    }

    internal (string Sender, string Recipient)? FindAddress(string id)
    {
        lock (gate) return AddressOf(id);
    }

    JsonObject? AdmitOrResolveWait(string id, string sender, string recipient, string text, string? replyTo, bool expectReply, ReplyReminderSchedule? schedule)
    {
        if (expectReply)
        {
            var copy = replyWaits.DeepClone().AsObject();
            while (copy.Count >= waitCapacity)
            {
                var oldestTerminal = copy.Where(e => (string?)e.Value?["state"] != "waiting")
                    .OrderBy(e => (long)e.Value!["sequence"]!).ThenBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key).FirstOrDefault();
                if (oldestTerminal is null) throw new InvalidOperationException("Reply wait storage is full of open waits. Cancel a wait before requesting another reply.");
                copy.Remove(oldestTerminal);
            }
            copy[id] = new JsonObject
            {
                ["sequence"] = next + 1, ["sender"] = sender, ["recipient"] = recipient,
                ["fingerprint"] = WaitFingerprint(text), ["state"] = "waiting", ["requestDelivery"] = "queued",
            };
            if (schedule is not null)
                copy[id]!["reminders"] = new JsonObject
                {
                    ["projectKey"] = schedule.ProjectKey, ["groupId"] = schedule.GroupId,
                    ["admittedAt"] = schedule.AdmittedAt.ToString("O"),
                    ["overdueAt"] = schedule.OverdueAt.ToString("O"),
                    ["delayTicks"] = schedule.Delay.Ticks,
                    ["reminder"] = "pending", ["escalation"] = "pending",
                };
            return copy;
        }
        if (replyTo is null || replyWaits[replyTo] is not JsonObject current || current["replyId"] is not null ||
            (string?)current["state"] is not ("waiting" or "cancelled")) return null;
        var resolved = replyWaits.DeepClone().AsObject();
        var wait = resolved[replyTo]!.AsObject();
        if ((string?)wait["state"] == "waiting") wait["state"] = "replied";
        wait["replyId"] = id;
        wait["replyFingerprint"] = WaitFingerprint(text);
        wait["replyDelivery"] = "queued";
        return resolved;
    }

    JsonObject? WithWaitDelivery(JsonObject item, string delivery)
    {
        var id = (string)item["id"]!;
        if ((string?)item["reminderFor"] is { } original && (string?)item["reminderKind"] is { } kind &&
            replyWaits[original]?["reminders"] is JsonObject reminders && (string?)reminders[kind + "MessageId"] == id)
        {
            var copy = replyWaits.DeepClone().AsObject();
            copy[original]!["reminders"]![kind] = delivery;
            return copy;
        }
        if ((bool?)item["expectReply"] == true && replyWaits[id] is not null)
        {
            var copy = replyWaits.DeepClone().AsObject();
            copy[id]!["requestDelivery"] = delivery;
            if (delivery == "delivered" && copy[id]!["reminders"] is JsonObject reminder && reminder["reviewAt"] is null)
                reminder["reviewAt"] = clock.GetUtcNow().AddTicks((long)reminder["delayTicks"]!).ToString("O");
            return copy;
        }
        if ((string?)item["replyTo"] is not { } replyTo || (string?)replyWaits[replyTo]?["replyId"] != id) return null;
        var updated = replyWaits.DeepClone().AsObject();
        updated[replyTo]!["replyDelivery"] = delivery;
        return updated;
    }

    internal JsonObject CancelReplyWait(string id, string peer)
    {
        lock (gate)
        {
            var wait = replyWaits[id];
            if (wait is null || (string?)wait["sender"] != peer)
                throw new InvalidOperationException("No retained reply wait with this message ID belongs to you");
            if ((string?)wait["state"] == "waiting")
            {
                var copy = replyWaits.DeepClone().AsObject();
                copy[id]!["state"] = "cancelled";
                Save(entries.DeepClone().AsArray(), next, waits: copy);
                wait = copy[id]!;
            }
            return WaitMetadata(id, wait);
        }
    }

    internal JsonObject ListReplyWaits(string peer, long after, int limit)
    {
        if (after < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (gate)
        {
            var page = replyWaits.Where(pair => pair.Value is { } w && (long)w["sequence"]! > after &&
                ((string?)w["sender"] == peer || (string?)w["recipient"] == peer))
                .OrderBy(pair => (long)pair.Value!["sequence"]!).Take(limit + 1).ToArray();
            return new JsonObject
            {
                ["items"] = new JsonArray(page.Take(limit).Select(pair => (JsonNode)WaitMetadata(pair.Key, pair.Value!)).ToArray()),
                ["nextCursor"] = page.Length > limit ? ((long)page[limit - 1].Value!["sequence"]!).ToString(CultureInfo.InvariantCulture) : null,
            };
        }
    }

    JsonObject WaitMetadata(string id, JsonNode wait)
    {
        var result = new JsonObject
        {
        ["messageId"] = id, ["waiter"] = (string?)wait["sender"], ["recipient"] = (string?)wait["recipient"],
        ["state"] = (string?)wait["state"], ["requestDelivery"] = (string?)wait["requestDelivery"],
        ["replyId"] = (string?)wait["replyId"], ["replyDelivery"] = (string?)wait["replyDelivery"],
            ["reminders"] = wait["reminders"]?.DeepClone(),
        };
        if (result["reminders"] is JsonObject reminder && DateTimeOffset.TryParse((string?)reminder["overdueAt"], out var at))
        {
            reminder["overdue"] = (string?)wait["state"] == "waiting" && clock.GetUtcNow() >= at;
            if ((string?)wait["state"] == "waiting")
                reminder["reciprocalMessageId"] = replyWaits.FirstOrDefault(p => p.Key != id && p.Value is JsonObject other &&
                    (string?)other["state"] == "waiting" && (string?)other["sender"] == (string?)wait["recipient"] &&
                    (string?)other["recipient"] == (string?)wait["sender"] &&
                    (string?)other["reminders"]?["projectKey"] == (string?)reminder["projectKey"] &&
                    (string?)other["reminders"]?["groupId"] == (string?)reminder["groupId"]).Key;
        }
        return result;
    }

    // Sequence binds a retained ID to its particular admission, even after ID reuse.
    internal JsonObject? ContinuationWait(string id, string waiter, long? sequence = null)
    {
        lock (gate)
        {
            if (replyWaits[id] is not JsonObject wait || (string?)wait["sender"] != waiter ||
                (sequence is not null && (long?)wait["sequence"] != sequence)) return null;
            return new JsonObject { ["messageId"] = id, ["sequence"] = (long)wait["sequence"]!,
                ["state"] = (string?)wait["state"], ["requestDelivery"] = (string?)wait["requestDelivery"],
                ["replyDelivery"] = (string?)wait["replyDelivery"] };
        }
    }

    internal int OpenReplyWaitCount
    {
        get { lock (gate) return replyWaits.Count(pair => (string?)pair.Value?["state"] == "waiting"); }
    }
}
