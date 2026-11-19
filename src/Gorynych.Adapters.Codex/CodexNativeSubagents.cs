using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Codex.Protocol;
using static Gorynych.Adapters.Codex.CodexMap;

namespace Gorynych.Adapters.Codex;

sealed partial class CodexAgent
{
    sealed record NativeChild(string Parent, CodexThread Thread);
    readonly ConcurrentDictionary<string, NativeChild> nativeChildren = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> unexposedChildren = new(StringComparer.Ordinal);
    // Live observations only, accessed in the server notification queue.
    readonly Dictionary<string, JsonObject> nativeChildStates = new(StringComparer.Ordinal);
    readonly Lock nativeEventsGate = new();
    Task nativeEvents = Task.CompletedTask;

    // The RPC read loop must remain free to receive thread/read responses. A single chain keeps
    // notifications in transport order while the association and workspace are verified.
    Task QueueServerNotification(string method, JsonNode? value)
    {
        var thread = (string?)value?["threadId"] is { } nativeId ? Thread(nativeId) : null;
        var idle = method == CodexMethods.Notification.ThreadStatusChanged && (string?)value?["status"]?["type"] == "idle";
        if (thread is not null && !idle && method != CodexMethods.Notification.TurnCompleted)
            InvalidateIdle(thread);
        var idleIngress = idle ? thread?.CaptureProvisionalIdleIngress() : null;
        options.NativeIngressForTests?.Invoke(method, (string?)value?["threadId"]);
        EnqueueNativeEvent(async () =>
        {
            if (options.BeforeNativeEventForTests is { } barrier) await barrier(method, (string?)value?["threadId"]);
            if ((string?)value?["threadId"] is { } id && unexposedChildren.ContainsKey(id)) return;
            await OnServerNotificationAsync(method, value, thread, idleIngress);
        });
        return Task.CompletedTask;
    }

    Task EnqueueNativeEvent(Func<Task> action)
    {
        lock (nativeEventsGate)
            return nativeEvents = ProcessAfterAsync(nativeEvents, action);
    }

    async Task ProcessAfterAsync(Task previous, Func<Task> action)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception e) { Log($"codex: preceding notification failed: {e.Message}"); }
        await Task.Yield();
        await action();
    }

    Task ServerNotificationsBeforeAsync() { lock (nativeEventsGate) return nativeEvents; }

    string ClientSessionId(string id) => clientSubagents ? id : NativeRootOf(id);

    string NativeRootOf(string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(id) && nativeChildren.TryGetValue(id, out var child)) id = child.Parent;
        return id;
    }

    async Task<bool> AnnounceNativeChildAsync(string parent, string child, string? title, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(child) || child == parent || Thread(parent) is not { } parentThread) return false;
        if (!nativeChildren.TryGetValue(child, out var known))
        {
            try
            {
                var response = await ReadNativeChildAsync(child, ct);
                var thread = response?["thread"];
                var cwd = (string?)thread?["cwd"];
                if ((string?)thread?["id"] != child || (string?)thread?["parentThreadId"] != parent ||
                    cwd is null || !Path.IsPathFullyQualified(cwd))
                {
                    unexposedChildren[child] = parent;
                    return false;
                }
                known = new NativeChild(parent, new CodexThread(child, cwd, parentThread.Settings));
                if (!nativeChildren.TryAdd(child, known)) known = nativeChildren[child];
            }
            catch (Exception e) when (e is IOException or JsonRpcException or InvalidOperationException)
            {
                Log($"codex: child {child} not exposed: {e.Message}");
                unexposedChildren[child] = parent;
                return false;
            }
        }
        if (known.Parent != parent) return false;
        unexposedChildren.TryRemove(child, out _);
        var association = new JsonObject { ["sessionId"] = child, ["cwd"] = known.Thread.Cwd };
        if (!string.IsNullOrWhiteSpace(title)) association["title"] = title;
        await Update(parent, RawUpdate("subagent_update", association));
        return true;
    }

    // A just-spawned thread can exist before its rollout header is persisted. Only this
    // specific transient failure is retried; all other errors retain the ordinary fallback.
    async Task<JsonNode?> ReadNativeChildAsync(string child, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    return await app.SendRequestAsync(CodexMethods.Client.ThreadRead,
                        new JsonObject { ["threadId"] = child, ["includeTurns"] = false }, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new IOException("Timed out reading native child metadata."); }
            }
            catch (JsonRpcException e) when (attempt < 9 && e.Message.Contains("rollout", StringComparison.OrdinalIgnoreCase)
                                               && e.Message.Contains("is empty", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    static SessionUpdate RawUpdate(string tag, JsonObject fields)
    {
        fields["sessionUpdate"] = tag;
        return new SessionUpdate.Unknown(tag, fields);
    }

    Task NativeChildStateAsync(string child, string state) =>
        NativeChildStateAsync(child, new JsonObject { ["state"] = state });

    async Task NativeChildStateAsync(string child, JsonObject state, bool historical = false)
    {
        if (!nativeChildren.TryGetValue(child, out var entry)) return;
        if (!historical) nativeChildStates[child] = state;
        await SendNativeChildStateAsync(child, entry.Parent, state, live: !historical);
    }

    Task SendNativeChildStateAsync(string child, string parent, JsonObject state, bool snapshot = false, bool live = false)
    {
        var update = new JsonObject { ["sessionId"] = child, ["state"] = state.DeepClone() };
        if (snapshot) update["_meta"] = new JsonObject { [GorynychProtocol.MetaSubagentSnapshot] = true };
        else if (live && markSubagentLiveState) update["_meta"] = new JsonObject { [GorynychProtocol.MetaSubagentLiveState] = true };
        return Update(parent, RawUpdate("subagent_update", update));
    }

    Task NativeChildTurnCompletedAsync(string child, string status, bool historical = false)
    {
        var reason = status switch { "completed" => "end_turn", "interrupted" => "cancelled", "failed" => "error", _ => null };
        return reason is null ? Task.CompletedTask
            : NativeChildStateAsync(child, new JsonObject { ["state"] = "idle", ["stopReason"] = reason }, historical);
    }

    Task RefreshNativeChildStatesAsync(string root, CancellationToken ct) => EnqueueNativeEvent(async () =>
    {
        foreach (var (child, state) in nativeChildStates)
        {
            ct.ThrowIfCancellationRequested();
            if (nativeChildren.TryGetValue(child, out var entry) && NativeRootOf(child) == root)
                await SendNativeChildStateAsync(child, entry.Parent, state, snapshot: true);
        }
    });

    Task NativeChildMessageAsync(string child, UserMessageThreadItem message) => Update(child,
        RawUpdate("session_message", new JsonObject
        {
            ["messageId"] = message.Id, ["recipientSessionId"] = child,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = UserText(message) }),
        }));

    async Task NativeCollaborationAsync(string parent, CollabAgentToolCallThreadItem call, CancellationToken ct)
    {
        if (call.Status.Value != "completed" || call.SenderThreadId != parent ||
            call.Tool.Value is not ("spawnAgent" or "sendInput" or "sendMessage" or "followupTask")) return;
        foreach (var child in call.ReceiverThreadIds.Distinct(StringComparer.Ordinal))
        {
            if (call.Tool.Value == "spawnAgent")
                await AnnounceNativeChildAsync(parent, child, null, ct);
            if (!nativeChildren.ContainsKey(child) || string.IsNullOrEmpty(call.Prompt)) continue;
            await Update(parent, RawUpdate("session_message", new JsonObject
            {
                ["messageId"] = $"{call.Id}:to:{child}", ["senderSessionId"] = parent,
                ["recipientSessionId"] = child,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = call.Prompt }),
            }));
        }
    }

    async Task ReplayNativeChildAsync(string parent, ThreadItem item, HashSet<string> seen, CancellationToken ct)
    {
        if (item is CollabAgentToolCallThreadItem call)
        {
            await NativeCollaborationAsync(parent, call, ct);
            if (call.Tool.Value == "spawnAgent" && call.Status.Value == "completed" && call.SenderThreadId == parent)
                foreach (var receiver in call.ReceiverThreadIds)
                    if (nativeChildren.ContainsKey(receiver) && seen.Count < 128 && seen.Add(receiver))
                        await ReplayChildTranscriptAsync(receiver, seen, ct);
            return;
        }
        if (item is not SubAgentActivityThreadItem a || a.Kind.Value != "started" || seen.Count >= 128 || seen.Contains(a.AgentThreadId)) return;
        if (!await AnnounceNativeChildAsync(parent, a.AgentThreadId, a.AgentPath, ct)) return;
        seen.Add(a.AgentThreadId);
        await ReplayChildTranscriptAsync(a.AgentThreadId, seen, ct);
    }

    async Task ReplayChildTranscriptAsync(string child, HashSet<string> seen, CancellationToken ct)
    {
        try
        {
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var turns = From(await app.SendRequestAsync(CodexMethods.Client.ThreadTurnsList, ToNode(new ThreadTurnsListParams
                { ThreadId = child, SortDirection = SortDirection.Asc, ItemsView = TurnItemsView.Full, Limit = 50, Cursor = cursor },
                    CodexJsonContext.Default.ThreadTurnsListParams), ct), CodexJsonContext.Default.ThreadTurnsListResponse);
                foreach (var turn in turns.Data)
                {
                    if (turn.Status == TurnStatus.InProgress) continue;
                    foreach (var childItem in turn.Items)
                    {
                        await ReplayNativeChildAsync(child, childItem, seen, ct);
                        if (childItem is UserMessageThreadItem user) await NativeChildMessageAsync(child, user);
                        else if (ReplayOf(childItem) is { } update) await Update(child, update);
                    }
                    await NativeChildTurnCompletedAsync(child, turn.Status.Value, historical: true);
                }
                cursor = turns.NextCursor;
                if (cursor is null) return;
            }
            Log($"codex: child {child} replay reached its page limit");
        }
        catch (Exception e) when (e is IOException or JsonRpcException or InvalidOperationException)
        { Log($"codex: child {child} replay incomplete: {e.Message}"); }
    }

    // A child can continue or gain turns after the root's last journaled turn. Its spawn
    // need not be in the root pages after the mark. The catalog supplies those older edges.
    async Task ReplayOlderNativeChildrenAsync(string root, HashSet<string> seen, CancellationToken ct)
    {
        try
        {
            var descendants = new List<Protocol.Thread>();
            string? cursor = null;
            do
            {
                var listed = From(await app.SendRequestAsync(CodexMethods.Client.ThreadList, ToNode(new ThreadListParams
                {
                    AncestorThreadId = root, SourceKinds = [ThreadSourceKind.SubAgentThreadSpawn],
                    Limit = 64, Cursor = cursor,
                }, CodexJsonContext.Default.ThreadListParams), ct), CodexJsonContext.Default.ThreadListResponse);
                descendants.AddRange(listed.Data.Take(128 - descendants.Count));
                if (listed.NextCursor == cursor || listed.Data.Count == 0) break;
                cursor = listed.NextCursor;
            } while (cursor is not null && descendants.Count < 128);

            var byParent = descendants.Where(thread => thread.ParentThreadId is not null)
                .ToLookup(thread => thread.ParentThreadId!, StringComparer.Ordinal);
            var parents = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            parents.Enqueue(root);
            while (parents.TryDequeue(out var parent))
                foreach (var child in byParent[parent])
                {
                    if (!visited.Add(child.Id)) continue;
                    if (!seen.Contains(child.Id) && seen.Count < 128 &&
                        await AnnounceNativeChildAsync(parent, child.Id, child.Name, ct))
                    {
                        seen.Add(child.Id);
                        await ReplayChildTranscriptAsync(child.Id, seen, ct);
                    }
                    if (nativeChildren.GetValueOrDefault(child.Id)?.Parent == parent) parents.Enqueue(child.Id);
                }
        }
        catch (Exception e) when (e is IOException or JsonRpcException or InvalidOperationException)
        { Log($"codex: could not list older children of {root}: {e.Message}"); }
    }
}
