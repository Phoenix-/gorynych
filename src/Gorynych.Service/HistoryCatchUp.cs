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

        /// <summary>Everything, because the journal's mark is no longer in the history: it was rewritten.</summary>
        MarkLost,

        /// <summary>Everything, with no mark to go by (a journal from before marks, or an agent without them, Kimi).</summary>
        All,
    }

    public enum Kind { Nothing, Append, Replace }

    /// <summary>The updates to append, or the journal's new entries.</summary>
    public sealed record Plan(Kind Kind, List<JsonObject> Updates);

    public const string NativeOrigin = "native";
    public const string NoticeOrigin = "notice";
    public const string HistoryRewritten = "historyRewritten";

    /// <param name="journal">The journal as it is.</param>
    /// <param name="replay">The agent's updates in the order it sent them.</param>
    /// <param name="agent">The agent's display name, for the notice of a rewrite.</param>
    public static Plan Make(IReadOnlyList<JsonNode> journal, IReadOnlyList<JsonObject> replay, Replayed replayed, string agent)
    {
        // Only a turn's parts are history; a replayed mode or command list is state.
        var history = replay.Where(u => SessionJournal.IsTurnKind((string?)u["sessionUpdate"])).ToList();
        var known = KnownIds(journal);
        switch (replayed)
        {
            case Replayed.AfterMark:
                return Appending(history.Where(u => !known.Contains(IdOf(u) ?? "")));
            case Replayed.MarkLost:
                return Rewritten(journal, history, agent);
        }
        var parts = journal.OfType<JsonObject>().Where(e => SessionJournal.IsTurnKind((string?)e["sessionUpdate"]) && !IsNotice(e)).ToList();
        if (parts.Count == 0) return Appending(history);
        // The journal's last part of a turn from the agent that the history has, by ID ...
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            if (!IsAgentPart(parts[i]) || IdOf(parts[i]) is not { } id) continue;
            var at = history.FindLastIndex(u => IdOf(u) == id);
            if (at >= 0) return Appending(history.Skip(at + 1).Where(u => !known.Contains(IdOf(u) ?? "")));
        }
        // ... else its last user messages, by text: an agent that gives no IDs (Kimi), or prompts
        // the agent keeps under IDs of its own (Claude).
        if (AfterUserTexts(parts, history) is { } next)
            return Appending(history.Skip(next).Where(u => !known.Contains(IdOf(u) ?? "")));
        return Rewritten(journal, history, agent);
    }

    static Plan Appending(IEnumerable<JsonObject> updates)
    {
        var tagged = updates.Select(Native).ToList();
        return new Plan(tagged.Count == 0 ? Kind.Nothing : Kind.Append, tagged);
    }

    /// <summary>
    /// The journal anew: a notice that says what happened, then the agent's history, whose messages
    /// keep the journal's IDs where they are the same messages.
    /// </summary>
    static Plan Rewritten(IReadOnlyList<JsonNode> journal, List<JsonObject> history, string agent)
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
            if (m.Kind == "user_message_chunk" && m.Id is { } id)
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
        return new Plan(Kind.Replace, entries);
    }

    /// <summary>
    /// Where the history goes on past the journal, found by the journal's last user messages in a
    /// row (three, else two, else the last alone, as a compaction may have kept only that; the
    /// latest occurrence): the start of the next user message, or the end.
    /// </summary>
    static int? AfterUserTexts(List<JsonObject> journal, List<JsonObject> history)
    {
        var mine = Messages(journal).Where(m => m.Kind == "user_message_chunk").Select(m => m.Text).ToList();
        var theirs = Messages(history).Where(m => m.Kind == "user_message_chunk").ToList();
        for (var k = Math.Min(3, mine.Count); k >= 1; k--)
            for (var i = theirs.Count - k; i >= 0; i--)
            {
                var same = true;
                for (var j = 0; j < k && same; j++) same = theirs[i + j].Text == mine[mine.Count - k + j];
                if (!same) continue;
                var last = i + k - 1;
                return last + 1 < theirs.Count ? theirs[last + 1].Start : history.Count;
            }
        return null;
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
                text.Append((string?)updates[i]["content"]?["text"]);
                i++;
            }
            all.Add(new Message(start, i, kind!, id, Normalized(text.ToString())));
        }
        return all;
    }

    /// <summary>Text compared without its white space: a prompt of two blocks comes back joined differently.</summary>
    static string Normalized(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>A message's or tool call's ID, kept apart by kind (IDs are unique per kind of thing).</summary>
    static string? IdOf(JsonNode? update)
    {
        var kind = (string?)update?["sessionUpdate"];
        if (SessionJournal.IsChunkKind(kind)) return (string?)update!["messageId"] is { } m ? "m:" + m : null;
        if (kind is "tool_call" or "tool_call_update") return (string?)update!["toolCallId"] is { } t ? "t:" + t : null;
        return null;
    }

    static HashSet<string> KnownIds(IReadOnlyList<JsonNode> journal)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal)
            if (IdOf(entry) is { } id) known.Add(id);
        return known;
    }

    static bool IsAgentPart(JsonObject update) => (string?)update["sessionUpdate"] is "agent_message_chunk" or "agent_thought_chunk" or "tool_call" or "tool_call_update";

    static bool IsNotice(JsonObject entry) => (string?)entry["_meta"]?[GorynychProtocol.MetaOrigin] == NoticeOrigin;

    /// <summary>A copy marked as coming from the agent's own history, unless the agent named its origin.</summary>
    static JsonObject Native(JsonObject update)
    {
        var copy = (JsonObject)update.DeepClone();
        if (copy["_meta"] is not JsonObject meta) copy["_meta"] = meta = [];
        if (meta[GorynychProtocol.MetaOrigin] is null) meta[GorynychProtocol.MetaOrigin] = NativeOrigin;
        return copy;
    }
}
