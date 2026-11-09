using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

/// <summary>
/// Brings a session's journal up to date with its agent's own history, which may have grown outside
/// Gorynych (docs/service-protocol.md, "History made outside Gorynych"). Given what the agent
/// replayed when the session was opened, it decides what to append, or that the history was
/// rewritten and is imported again. Pure: the conductor applies the plan.
/// </summary>
internal static class HistoryCatchUp
{
    /// <summary>What the agent replayed.</summary>
    public enum Replayed
    {
        /// <summary>What follows the journal's mark.</summary>
        AfterMark,

        /// <summary>
        /// All of it: there was no mark (a journal from before marks, an agent without them, Kimi),
        /// or the mark is no longer in the history.
        /// </summary>
        All,
    }

    public enum Kind { Nothing, Append, Replace }

    /// <summary>
    /// The updates to append, or the journal's new entries; whether the journal then misses a part
    /// of the history (the replay was longer than the journal keeps, and what it dropped was not in
    /// the journal yet); and why, for the log.
    /// </summary>
    public sealed record Plan(Kind Kind, List<JsonObject> Updates, bool Trimmed, string Why);

    public const string NativeOrigin = "native";
    public const string NoticeOrigin = "notice";
    public const string HistoryRewritten = "historyRewritten";

    const string Reminder = "<system-reminder>";
    const string ReminderEnd = "</system-reminder>";

    /// <param name="journal">The journal as it is.</param>
    /// <param name="replay">The agent's updates in the order it sent them (its last ones, when <paramref name="dropped"/>).</param>
    /// <param name="dropped">Older updates of the replay were dropped, as the journal could not keep them.</param>
    /// <param name="agent">The agent's display name, for the notice of a rewrite.</param>
    public static Plan Make(IReadOnlyList<JsonNode> journal, IReadOnlyList<JsonObject> replay, Replayed replayed, string agent, bool dropped = false)
    {
        // Only a turn's parts are history; text the agent put into a user message itself (a
        // leading system reminder, as Kimi adds one to each) was not typed and is cut off.
        var history = replay.Where(u => SessionJournal.IsTurnKind((string?)u["sessionUpdate"])).Select(WithoutReminder).OfType<JsonObject>().ToList();
        var known = KnownKeys(journal);
        var parts = journal.OfType<JsonObject>().Where(e => SessionJournal.IsTurnKind((string?)e["sessionUpdate"]) && !IsNotice(e)).ToList();
        // What the replay dropped came right after the mark, so the journal misses it.
        if (replayed == Replayed.AfterMark)
            return Appending(WithoutPromptAgain(parts, history.Where(u => !known.Contains(KnownKey(u))).ToList()), native: true, trimmed: dropped, "after the mark");
        if (parts.Count == 0) return Appending(history, native: false, trimmed: dropped, "imported"); // the first import is just that
        // A replay without a single turn says nothing about the journal's turns (an empty or
        // unreadable history, turns without items): the journal stays.
        if (history.Count == 0) return new Plan(Kind.Nothing, [], false, "the agent replayed no turn; the journal is kept");
        // Aligned with the journal (below), the history goes on from a part the journal has: what
        // the replay dropped came before it, so the journal has that already.
        // First by the ID of the journal's last part of a turn from the agent, from that part's
        // first update. A tool call's later ones (the one that ends it) are then left to what the
        // journal knows: it may have only started the call.
        if (parts.LastOrDefault(p => IsAgentPart(p) && AnchorId(p) is not null) is { } last)
        {
            var at = history.FindIndex(u => AnchorId(u) == AnchorId(last));
            if (at >= 0) return Appending(history.Skip(at + 1).Where(u => !known.Contains(KnownKey(u))), native: true, trimmed: false, "aligned by ID");
        }
        // Else by the user messages' text: an agent that gives no IDs (Kimi), or a last part of the
        // journal's that has an ID of the service's (a text Claude gave none).
        var (next, why) = AfterUserTexts(parts, history);
        if (next is { } from) return Appending(history.Skip(from).Where(u => !known.Contains(KnownKey(u))), native: true, trimmed: false, why);
        return Rewritten(journal, history, agent, dropped, why);
    }

    static Plan Appending(IEnumerable<JsonObject> updates, bool native, bool trimmed, string why)
    {
        var copies = updates.Select(u => native ? Native(u) : (JsonObject)u.DeepClone()).ToList();
        return copies.Count == 0 ? new Plan(Kind.Nothing, [], false, why) : new Plan(Kind.Append, copies, trimmed, why);
    }

    /// <summary>
    /// The updates without the first one's user message when it is the journal's last prompt
    /// again, still unanswered there (compared as <see cref="AfterUserTexts"/> does): the service
    /// stopped in that turn, so the mark is from before it, and the agent's history has the prompt
    /// under an ID of its own. An answered prompt is never taken: the same text may have been sent
    /// again outside.
    /// </summary>
    static List<JsonObject> WithoutPromptAgain(List<JsonObject> journal, List<JsonObject> updates)
    {
        var prompt = Messages(journal).LastOrDefault(m => m.Kind == "user_message_chunk");
        if (prompt is null || prompt.Text.Length == 0 || journal.Skip(prompt.End).Any(IsAgentPart)) return updates;
        var first = Messages(updates).FirstOrDefault();
        if (first is null || first.Start != 0 || first.Kind != "user_message_chunk" || first.Text != prompt.Text) return updates;
        return updates.Skip(first.End).ToList();
    }

    /// <summary>
    /// The journal anew: a notice that says what happened, then the agent's history, whose messages
    /// keep the journal's IDs where they are the same messages.
    /// </summary>
    static Plan Rewritten(IReadOnlyList<JsonNode> journal, List<JsonObject> history, string agent, bool dropped, string why)
    {
        var entries = new List<JsonObject>
        {
            new()
            {
                ["sessionUpdate"] = "user_message_chunk",
                ["content"] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"[Gorynych: {agent}'s own history of this session no longer holds what this session showed before " +
                        "(it was rolled back, rewound or undone outside Gorynych). Below is that history as " +
                        $"{agent} has it now; the service keeps the earlier one next to its journal.]",
                },
                ["messageId"] = SessionFeed.NewMessageId(),
                ["_meta"] = new JsonObject { [GorynychProtocol.MetaOrigin] = NoticeOrigin, [GorynychProtocol.MetaNotice] = HistoryRewritten },
            },
        };
        // The journal's user messages by text, for an agent that replays them without the journal's IDs.
        var userIds = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach (var m in Messages(journal.OfType<JsonObject>().Where(e => !IsNotice(e)).ToList()))
            if (m.Kind == "user_message_chunk" && m.Id is { } id && m.Text.Length > 0)
            {
                if (!userIds.TryGetValue(m.Text, out var ids)) userIds[m.Text] = ids = new();
                ids.Enqueue(id);
            }
        var copy = history.Select(u => (JsonObject)u.DeepClone()).ToList();
        foreach (var m in Messages(copy))
        {
            if (copy[m.Start]["messageId"] is not null) continue;
            var id = m.Kind == "user_message_chunk" && userIds.TryGetValue(m.Text, out var ids) && ids.Count > 0
                ? ids.Dequeue()
                : SessionFeed.NewMessageId();
            for (var i = m.Start; i < m.End; i++) copy[i]["messageId"] = id;
        }
        entries.AddRange(copy.Select(Native));
        return new Plan(Kind.Replace, entries, dropped, why);
    }

    /// <summary>
    /// Where the history goes on past the journal, found by the user messages' text (compared
    /// without white space; an empty text, a prompt of only an image, is left out of both):
    /// the journal's user messages as the start of the history's; else its last three in a row,
    /// else two, else the last alone (a compaction may keep only the end), where they occur once.
    /// Occurring more than once is not taken: a turn could be skipped. Returns where the new part
    /// starts: the next user message, or right after the matched one when the journal has no
    /// answer to it yet.
    /// </summary>
    static (int? From, string Why) AfterUserTexts(List<JsonObject> journal, List<JsonObject> history)
    {
        var mine = Messages(journal).Where(m => m.Kind == "user_message_chunk" && m.Text.Length > 0).ToList();
        var theirs = Messages(history).Where(m => m.Kind == "user_message_chunk" && m.Text.Length > 0).ToList();
        if (mine.Count == 0) return (null, "no user message to align by");
        int? matched = null;
        var why = "";
        if (theirs.Count >= mine.Count && mine.Select((m, i) => theirs[i].Text == m.Text).All(same => same))
        {
            matched = mine.Count - 1;
            why = "the journal is the start of the history";
        }
        for (var k = Math.Min(3, mine.Count); matched is null && k >= 1; k--)
        {
            var at = new List<int>();
            for (var i = 0; i + k <= theirs.Count; i++)
            {
                var same = true;
                for (var j = 0; j < k && same; j++) same = theirs[i + j].Text == mine[mine.Count - k + j].Text;
                if (same) at.Add(i + k - 1);
            }
            if (at.Count > 1) return (null, $"the journal's last {k} user messages occur {at.Count} times in the history");
            if (at.Count == 1)
            {
                matched = at[0];
                why = $"aligned by the last {k} user messages";
            }
        }
        if (matched is not { } u) return (null, "the journal's last user messages are not in the history");
        var answer = journal.Skip(mine[^1].End).Where(IsAgentPart).ToList();
        // The journal's last user message got no answer in it yet (the service stopped in the turn): its answer is new.
        if (answer.Count == 0) return (theirs[u].End, why);
        var turnEnd = u + 1 < theirs.Count ? theirs[u + 1].Start : history.Count;
        // An agent that gives its messages IDs answered that turn with other messages and tool
        // calls: it was taken again, not the same turn. One that gives none (Kimi) replays its tool
        // calls under other IDs than it gave them live (probed with Kimi Code 2.5.2: "0:Read_0_…"
        // live, "1:Read_0_…" replayed), so they say nothing.
        if (!history.Any(h => SessionJournal.IsChunkKind((string?)h["sessionUpdate"]) && h["messageId"] is not null)) return (turnEnd, why);
        var mineIds = answer.Select(AnchorId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var theirIds = history.Skip(theirs[u].End).Take(turnEnd - theirs[u].End).Where(IsAgentPart).Select(AnchorId).OfType<string>().ToList();
        if (theirIds.Count > 0 && !theirIds.Any(mineIds.Contains)) return (null, "the journal's last turn was answered differently in the history");
        return (turnEnd, why);
    }

    /// <summary>A message: consecutive chunks of one kind and one <c>messageId</c> (or none), as a client would read them.</summary>
    sealed record Message(int Start, int End, string Kind, string? Id, string Text);

    static List<Message> Messages(IReadOnlyList<JsonObject> updates)
    {
        var all = new List<Message>();
        for (var i = 0; i < updates.Count;)
        {
            var kind = (string?)updates[i]["sessionUpdate"];
            if (!SessionJournal.IsChunkKind(kind)) { i++; continue; }
            var id = (string?)updates[i]["messageId"];
            var text = new StringBuilder();
            var start = i;
            while (i < updates.Count && (string?)updates[i]["sessionUpdate"] == kind && (string?)updates[i]["messageId"] == id)
            {
                text.Append(updates[i]["content"]?["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : "");
                i++;
            }
            all.Add(new Message(start, i, kind!, id, Normalized(text.ToString())));
        }
        return all;
    }

    /// <summary>Text compared without its white space: a prompt of two blocks comes back joined differently.</summary>
    static string Normalized(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>A message's or tool call's ID, apart by kind, to find a part of a turn in the history.</summary>
    static string? AnchorId(JsonNode? update)
    {
        var kind = (string?)update?["sessionUpdate"];
        if (SessionJournal.IsChunkKind(kind)) return update!["messageId"] is JsonValue m && m.TryGetValue<string>(out var id) ? $"{kind}:{id}" : null;
        if (kind is "tool_call" or "tool_call_update") return update!["toolCallId"] is JsonValue t && t.TryGetValue<string>(out var id) ? "tool:" + id : null;
        return null;
    }

    /// <summary>
    /// What the journal already has of an update: a message by its ID, a tool call by its ID and
    /// status, so a tool call the journal has only started still gets the update that ends it.
    /// </summary>
    static string KnownKey(JsonNode? update)
    {
        var id = AnchorId(update);
        if (id is null) return "";
        return id.StartsWith("tool:", StringComparison.Ordinal) ? $"{id}:{(update!["status"] is JsonValue s && s.TryGetValue<string>(out var status) ? status : "")}" : id;
    }

    static HashSet<string> KnownKeys(IReadOnlyList<JsonNode> journal)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal)
            if (KnownKey(entry) is { Length: > 0 } key) known.Add(key);
        return known;
    }

    static bool IsAgentPart(JsonObject update) => (string?)update["sessionUpdate"] is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";

    static bool IsNotice(JsonObject entry) => (string?)entry["_meta"]?[GorynychProtocol.MetaOrigin] == NoticeOrigin;

    /// <summary>
    /// A user message chunk without the system reminders it starts with (Kimi puts one of its own
    /// after every replayed user message, as a part of its own); null when nothing else is left. A
    /// reminder anywhere else is part of what the person wrote and stays.
    /// </summary>
    static JsonObject? WithoutReminder(JsonObject update)
    {
        if ((string?)update["sessionUpdate"] != "user_message_chunk" || update["content"]?["text"] is not JsonValue v || !v.TryGetValue<string>(out var text)) return update;
        var rest = text;
        while (rest.TrimStart().StartsWith(Reminder, StringComparison.Ordinal) && rest.IndexOf(ReminderEnd, StringComparison.Ordinal) is var end && end >= 0)
            rest = rest[(end + ReminderEnd.Length)..];
        if (ReferenceEquals(rest, text)) return update;
        if (rest.Trim().Length == 0) return null;
        var copy = (JsonObject)update.DeepClone();
        copy["content"]!["text"] = rest.TrimStart();
        return copy;
    }

    /// <summary>A copy marked as coming from the agent's own history, unless the agent named its origin.</summary>
    static JsonObject Native(JsonObject update)
    {
        var copy = (JsonObject)update.DeepClone();
        if (copy["_meta"] is not JsonObject meta) copy["_meta"] = meta = [];
        if (meta[GorynychProtocol.MetaOrigin] is null) meta[GorynychProtocol.MetaOrigin] = NativeOrigin;
        return copy;
    }
}
