using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Tests;

/// <summary>
/// An in-process stand-in for <c>codex app-server</c> (Codex 0.167's wire, as the probes in
/// research/probes/codex recorded it), for the Codex adapter's contract tests. Threads live in
/// memory; a turn's prompt picks what Codex does:
/// <c>run</c> a command (asking first), <c>patch</c> a file change (asking first), <c>mcp</c> a
/// gorynych tool call (asking first), <c>think</c> reasoning, <c>plan</c> a plan, <c>usage</c>
/// token usage, <c>name</c> a generated title, <c>ask</c> request_user_input, <c>slow</c> a command
/// whose approval waits until the turn is interrupted, <c>fail</c> a failed turn; anything else
/// answers "ok". Also: <c>wait</c> runs until interrupted, <c>stuck</c> never ends, not even when
/// interrupted, <c>closed</c> unloads the thread mid-turn (<c>thread/closed</c>), <c>perm</c> asks for
/// more permissions until answered or interrupted, <c>form</c> is an MCP server's own form, and
/// <c>late</c> sends a file change's approval before its <c>item/started</c>.
/// </summary>
sealed class FakeAppServer
{
    public sealed class FakeThread
    {
        public required string Id { get; init; }
        public required string Cwd { get; init; }
        public string? Name { get; set; }
        public bool Archived { get; set; }
        public bool Durable { get; set; }
        public bool Loaded { get; set; }
        public bool OpenElsewhere { get; set; }
        public long UpdatedAt { get; set; } = 1_794_000_000;
        public List<(string TurnId, JsonObject Item)> Items { get; } = [];
        public JsonObject Settings { get; set; } = DefaultSettings();
    }

    public static JsonObject DefaultSettings() => new()
    {
        ["approvalPolicy"] = "on-request",
        ["approvalsReviewer"] = "user",
        ["model"] = "gpt-fake",
        ["modelProvider"] = "openai",
        ["reasoningEffort"] = "medium",
        ["sandbox"] = new JsonObject { ["type"] = "workspaceWrite" },
    };

    readonly List<(string Method, JsonObject Params)> calls = [];
    readonly ConcurrentDictionary<string, TaskCompletionSource> interrupts = new();
    int next;

    public ConcurrentDictionary<string, FakeThread> Threads { get; } = new();

    /// <summary>The MCP servers of the user's own Codex config, as <c>config/read</c> shows them.</summary>
    public HashSet<string> UserServers { get; set; } = ["other-tool"];

    /// <summary>The answers the adapter gave to this server's requests, in order.</summary>
    public List<(string Method, JsonNode? Answer)> Answers { get; } = [];

    /// <summary>The page size of <c>thread/items/list</c> and <c>thread/list</c>, small so paging is exercised.</summary>
    public int PageSize { get; set; } = 2;

    public JsonRpcConnection? Rpc { get; private set; }

    public string[] Methods()
    {
        lock (calls) return calls.Select(c => c.Method).ToArray();
    }

    public JsonObject Last(string method)
    {
        lock (calls) return calls.Last(c => c.Method == method).Params;
    }

    public JsonObject[] All(string method)
    {
        lock (calls) return calls.Where(c => c.Method == method).Select(c => c.Params).ToArray();
    }

    /// <summary>When set, <c>turn/start</c> answers only once it completes.</summary>
    public TaskCompletionSource? HoldTurnStart { get; set; }

    /// <summary><c>thread/unsubscribe</c> fails, as when app-server is going away.</summary>
    public bool FailUnsubscribe { get; set; }

    /// <summary>The ID of the turn <c>turn/start</c> started last.</summary>
    public string? LastTurnId { get; private set; }

    /// <summary>A thread made earlier in Codex itself, with a turn of history.</summary>
    public FakeThread Existing(string id, string cwd, string? name = null, bool archived = false, bool openElsewhere = false, JsonObject? settings = null)
    {
        var t = new FakeThread { Id = id, Cwd = cwd, Name = name, Archived = archived, Durable = true, OpenElsewhere = openElsewhere, Settings = settings ?? DefaultSettings() };
        t.Items.Add(("turn-0", new JsonObject
        {
            ["type"] = "userMessage", ["id"] = $"{id}-u1", ["clientId"] = null,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Fix the flaky test" }),
        }));
        t.Items.Add(("turn-0", new JsonObject { ["type"] = "reasoning", ["id"] = $"{id}-rs1", ["summary"] = new JsonArray("Looking at the test") }));
        t.Items.Add(("turn-0", Command($"{id}-exec1", cwd, "completed", "passed\n")));
        t.Items.Add(("turn-0", new JsonObject { ["type"] = "agentMessage", ["id"] = $"{id}-msg1", ["text"] = "Fixed it." }));
        Threads[id] = t;
        return t;
    }

    public Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        var link = new DuplexLink();
        var (si, so) = link.AgentEnd;
        var rpc = new JsonRpcConnection(si, so, "fake-app-server") { RequestHandler = HandleAsync, NotificationHandler = (_, _) => Task.CompletedTask };
        Rpc = rpc;
        rpc.Start();
        var (ci, co) = link.ClientEnd;
        return Task.FromResult(new AgentInstance(ci, co, rpc.Completion, () => rpc.DisposeAsync()));
    }

    // ---------------------------------------------------------------- requests from the adapter

    async Task<JsonNode?> HandleAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var p = request.Params as JsonObject ?? [];
        lock (calls) calls.Add((request.Method, (JsonObject)p.DeepClone()));
        if (request.Method == "turn/start" && HoldTurnStart is { } hold) await hold.Task;
        return await AnswerAsync(request.Method, p);
    }

    Task<JsonNode?> AnswerAsync(string method, JsonObject p)
    {
        switch (method)
        {
            case "initialize":
                return Done(new JsonObject { ["userAgent"] = "fake/0.167.0", ["codexHome"] = "C:\\fake", ["platformFamily"] = "windows", ["platformOs"] = "windows" });
            case "model/list":
                return Done(new JsonObject
                {
                    ["data"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "gpt-fake", ["model"] = "gpt-fake", ["displayName"] = "GPT fake", ["description"] = "A fake model", ["hidden"] = false,
                        ["isDefault"] = true, ["defaultReasoningEffort"] = "medium",
                        ["supportedReasoningEfforts"] = new JsonArray(
                            new JsonObject { ["reasoningEffort"] = "low", ["description"] = "fast" },
                            new JsonObject { ["reasoningEffort"] = "medium", ["description"] = "balanced" }),
                    }, new JsonObject
                    {
                        ["id"] = "gpt-hidden", ["model"] = "gpt-hidden", ["displayName"] = "Hidden", ["description"] = "", ["hidden"] = true,
                        ["isDefault"] = false, ["defaultReasoningEffort"] = "low", ["supportedReasoningEfforts"] = new JsonArray(),
                    }),
                });
            case "config/read":
                return Done(new JsonObject
                {
                    ["config"] = new JsonObject { ["mcp_servers"] = new JsonObject(UserServers.Select(n => KeyValuePair.Create(n, (JsonNode?)new JsonObject { ["command"] = n }))) },
                });
            case "thread/start":
            {
                var t = new FakeThread { Id = $"thread-{Interlocked.Increment(ref next)}", Cwd = (string?)p["cwd"] ?? "", Loaded = true };
                Threads[t.Id] = t;
                return Done(ThreadResponse(t));
            }
            case "thread/resume":
            {
                var id = (string)p["threadId"]!;
                if (!Threads.TryGetValue(id, out var t) || !t.Durable) throw new JsonRpcException(-32600, $"no rollout found for thread id {id}");
                if (t.OpenElsewhere) throw new JsonRpcException(-32600, $"thread {id} already has an active writer");
                if (t.Archived) throw new JsonRpcException(-32600, $"session {id} is archived. Run `codex unarchive {id}` to unarchive it first.");
                t.Loaded = true;
                return Done(ThreadResponse(t));
            }
            case "thread/list":
            {
                var cwd = (string?)p["cwd"];
                var archived = p["archived"] is JsonValue a && a.TryGetValue<bool>(out var on) && on;
                var all = Threads.Values.Where(t => t.Durable && t.Archived == archived && (cwd is null || t.Cwd == cwd))
                    .OrderByDescending(t => t.UpdatedAt).ThenBy(t => t.Id).ToList();
                return Done(Page(all.Select(t => (JsonNode)ThreadJson(t)).ToList(), (string?)p["cursor"]));
            }
            case "thread/items/list":
            {
                var t = Threads[(string)p["threadId"]!];
                var entries = t.Items.Select(i => (JsonNode)new JsonObject { ["turnId"] = i.TurnId, ["item"] = i.Item.DeepClone() }).ToList();
                return Done(Page(entries, (string?)p["cursor"]));
            }
            case "thread/loaded/list":
                return Done(new JsonObject { ["data"] = new JsonArray(Threads.Values.Where(t => t.Loaded).Select(t => (JsonNode?)t.Id).ToArray()) });
            case "thread/unsubscribe":
            {
                if (FailUnsubscribe) throw new JsonRpcException(JsonRpcException.InternalError, "app-server is shutting down");
                var id = (string)p["threadId"]!;
                if (Threads.TryGetValue(id, out var t))
                {
                    t.Loaded = false;
                    if (!t.Durable) Threads.TryRemove(id, out _); // a thread with no turn has no rollout to come back to
                }
                return Done(new JsonObject { ["status"] = "unsubscribed" });
            }
            case "thread/delete":
            {
                var id = (string)p["threadId"]!;
                if (!Threads.TryRemove(id, out _)) throw new JsonRpcException(-32600, $"no rollout found for thread id {id}");
                return Done(new JsonObject());
            }
            case "thread/name/set":
            {
                var t = Threads[(string)p["threadId"]!];
                t.Name = (string?)p["name"];
                _ = Notify("thread/name/updated", new JsonObject { ["threadId"] = t.Id, ["threadName"] = t.Name });
                return Done(new JsonObject());
            }
            case "turn/start":
            {
                var t = Threads[(string)p["threadId"]!];
                if (!t.Loaded) throw new JsonRpcException(-32600, $"thread not found: {t.Id}");
                if ((string?)p["model"] == "gpt-hidden") throw new JsonRpcException(-32600, "model gpt-hidden is not available to this account");
                var turnId = $"turn-{Interlocked.Increment(ref next)}";
                LastTurnId = turnId;
                interrupts[turnId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(() => RunTurnAsync(t, turnId, p));
                return Done(new JsonObject { ["turn"] = new JsonObject { ["id"] = turnId, ["items"] = new JsonArray(), ["status"] = "inProgress" } });
            }
            case "turn/interrupt":
                if (interrupts.TryGetValue((string)p["turnId"]!, out var stop)) stop.TrySetResult();
                return Done(new JsonObject());
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, $"fake app-server: {method}");
        }
    }

    static Task<JsonNode?> Done(JsonNode node) => Task.FromResult<JsonNode?>(node);

    JsonObject Page(List<JsonNode> all, string? cursor)
    {
        var from = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = all.Skip(from).Take(PageSize).ToList();
        var result = new JsonObject { ["data"] = new JsonArray(page.Select(n => n.DeepClone()).ToArray()) };
        if (from + PageSize < all.Count) result["nextCursor"] = (from + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    static JsonObject ThreadJson(FakeThread t) => new()
    {
        ["id"] = t.Id, ["sessionId"] = t.Id, ["cwd"] = t.Cwd, ["name"] = t.Name, ["preview"] = "", ["cliVersion"] = "0.167.0",
        ["createdAt"] = t.UpdatedAt, ["updatedAt"] = t.UpdatedAt, ["ephemeral"] = false, ["modelProvider"] = "openai",
        ["source"] = "vscode", ["originator"] = "gorynych", ["projectId"] = null, ["status"] = new JsonObject { ["type"] = t.Loaded ? "idle" : "notLoaded" },
        ["turns"] = new JsonArray(),
    };

    static JsonObject ThreadResponse(FakeThread t)
    {
        var r = (JsonObject)t.Settings.DeepClone();
        r["cwd"] = t.Cwd;
        r["thread"] = ThreadJson(t);
        return r;
    }

    static JsonObject Command(string id, string cwd, string status, string? output) => new()
    {
        ["type"] = "commandExecution", ["id"] = id, ["command"] = "\"pwsh.exe\" -Command \"cmd /c echo hi\"", ["cwd"] = cwd,
        ["commandActions"] = new JsonArray(new JsonObject { ["type"] = "unknown", ["command"] = "cmd /c echo hi" }),
        ["status"] = status, ["aggregatedOutput"] = output, ["exitCode"] = status == "completed" ? 0 : null,
    };

    // ---------------------------------------------------------------- a turn

    Task Notify(string method, JsonObject p) => Rpc!.SendNotificationAsync(method, p);

    async Task Item(FakeThread t, string turnId, JsonObject started, JsonObject? completed = null)
    {
        await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = started.DeepClone(), ["startedAtMs"] = 1 });
        var done = completed ?? started;
        await Notify("item/completed", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = done.DeepClone(), ["completedAtMs"] = 2 });
        t.Items.Add((turnId, done));
    }

    int sent;

    /// <summary>The ID of this server's last request to the adapter (JsonRpcConnection numbers them from 1).</summary>
    public int LastRequestId => Volatile.Read(ref sent);

    async Task<JsonNode?> Ask(string method, JsonObject p)
    {
        Interlocked.Increment(ref sent);
        var answer = await Rpc!.SendRequestAsync(method, p);
        lock (Answers) Answers.Add((method, answer?.DeepClone()));
        return answer;
    }

    async Task RunTurnAsync(FakeThread t, string turnId, JsonObject p)
    {
        var shown = string.Join("\n\n", (p["input"] as JsonArray ?? []).Select(i => (string?)i?["text"]));
        var text = (string?)(p["input"] as JsonArray)?[0]?["text"] ?? ""; // the script is in the first input only: a spawn's second one says where it came from
        var status = "completed";
        JsonObject? error = null;
        await Notify("turn/started", new JsonObject { ["threadId"] = t.Id, ["turn"] = new JsonObject { ["id"] = turnId, ["items"] = new JsonArray(), ["status"] = "inProgress" } });
        await Item(t, turnId, new JsonObject
        {
            ["type"] = "userMessage", ["id"] = $"{turnId}-u", ["clientId"] = p["clientUserMessageId"]?.DeepClone(),
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = shown }),
        });
        var answer = "ok";
        if (text.Contains("think"))
        {
            var id = $"rs-{turnId}";
            await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = id }, ["startedAtMs"] = 1 });
            await Notify("item/reasoning/summaryTextDelta", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = id, ["delta"] = "First", ["summaryIndex"] = 0 });
            await Notify("item/reasoning/summaryPartAdded", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = id, ["summaryIndex"] = 1 });
            await Notify("item/reasoning/summaryTextDelta", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = id, ["delta"] = "Second", ["summaryIndex"] = 1 });
            await Notify("item/completed", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["completedAtMs"] = 2,
                ["item"] = new JsonObject { ["type"] = "reasoning", ["id"] = id, ["summary"] = new JsonArray("First", "Second") },
            });
        }
        if (text.Contains("plan"))
            await Notify("turn/plan/updated", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId,
                ["plan"] = new JsonArray(new JsonObject { ["step"] = "Read the test", ["status"] = "completed" }, new JsonObject { ["step"] = "Fix it", ["status"] = "inProgress" }),
            });
        if (text.Contains("run") || text.Contains("slow"))
        {
            var id = $"exec-{turnId}";
            await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = Command(id, t.Cwd, "inProgress", null), ["startedAtMs"] = 1 });
            var asking = Ask("item/commandExecution/requestApproval", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = id, ["startedAtMs"] = 1, ["command"] = "\"pwsh.exe\" -Command \"cmd /c echo hi\"",
                ["cwd"] = t.Cwd, ["reason"] = null, ["commandActions"] = Command(id, t.Cwd, "inProgress", null)["commandActions"]!.DeepClone(),
                ["availableDecisions"] = new JsonArray("accept", new JsonObject
                {
                    ["acceptWithExecpolicyAmendment"] = new JsonObject { ["execpolicy_amendment"] = new JsonArray("cmd", "/c", "echo") },
                }, "acceptForSession", "decline", "cancel"),
            });
            if (text.Contains("slow"))
            {
                await interrupts[turnId].Task;
                await Notify("serverRequest/resolved", new JsonObject { ["threadId"] = t.Id, ["requestId"] = LastRequestId });
                await Notify("item/completed", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = Command(id, t.Cwd, "failed", null), ["completedAtMs"] = 2 });
                status = "interrupted";
                _ = asking.ContinueWith(_ => { }, TaskScheduler.Default);
            }
            else
            {
                var decision = (string?)(await asking)?["decision"];
                var ran = decision is "accept" or "acceptForSession";
                await Notify("item/completed", new JsonObject
                {
                    ["threadId"] = t.Id, ["turnId"] = turnId, ["completedAtMs"] = 2,
                    ["item"] = Command(id, t.Cwd, ran ? "completed" : "declined", ran ? "hi\n" : null),
                });
                answer = ran ? "It printed hi." : "Not run.";
            }
        }
        if (text.Contains("patch"))
        {
            var id = $"patch-{turnId}";
            JsonObject Change(string s) => new()
            {
                ["type"] = "fileChange", ["id"] = id, ["status"] = s,
                ["changes"] = new JsonArray(new JsonObject { ["path"] = Path.Combine(t.Cwd, "note.txt"), ["kind"] = new JsonObject { ["type"] = "add" }, ["diff"] = "hi\n" }),
            };
            Task<JsonNode?> Approval() => Ask("item/fileChange/requestApproval", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = id, ["startedAtMs"] = 1, ["reason"] = null, ["grantRoot"] = null,
            });
            Task<JsonNode?> asking;
            if (text.Contains("late"))
            {
                // the approval overtakes the item: app-server's requests and notifications are handled apart
                asking = Approval();
                await Task.Delay(100);
                await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = Change("inProgress"), ["startedAtMs"] = 1 });
            }
            else
            {
                await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = Change("inProgress"), ["startedAtMs"] = 1 });
                asking = Approval();
            }
            var decision = (string?)(await asking)?["decision"];
            await Notify("item/completed", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["completedAtMs"] = 2, ["item"] = Change(decision is "accept" or "acceptForSession" ? "completed" : "declined"),
            });
        }
        if (text.Contains("mcp"))
        {
            var id = $"mcp-{turnId}";
            var args = new JsonObject { ["message_id"] = "mk7q2-1", ["text"] = "Spaces." };
            JsonObject Call(string s, JsonObject? result) => new()
            {
                ["type"] = "mcpToolCall", ["id"] = id, ["server"] = "gorynych", ["tool"] = "reply", ["arguments"] = args.DeepClone(), ["status"] = s, ["result"] = result,
            };
            await Notify("item/started", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["item"] = Call("inProgress", null), ["startedAtMs"] = 1 });
            var action = (string?)(await Ask("mcpServer/elicitation/request", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["serverName"] = "gorynych", ["mode"] = "form", ["message"] = "Allow the gorynych MCP server to run tool \"reply\"?",
                ["requestedSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                ["_meta"] = new JsonObject
                {
                    ["codex_approval_kind"] = "mcp_tool_call", ["persist"] = new JsonArray("session", "always"), ["tool_name"] = "reply", ["tool_params"] = args.DeepClone(),
                },
            }))?["action"];
            await Notify("item/completed", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["completedAtMs"] = 2,
                ["item"] = action == "accept"
                    ? Call("completed", new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "delivered" }) })
                    : Call("failed", null),
            });
        }
        if (text.Contains("ask"))
        {
            var answers = await Ask("item/tool/requestUserInput", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = $"ask-{turnId}", ["isBlocking"] = true,
                ["questions"] = new JsonArray(new JsonObject
                {
                    ["id"] = "color", ["header"] = "Color", ["question"] = "Which color?",
                    ["options"] = new JsonArray(new JsonObject { ["label"] = "Red", ["description"] = "warm" }, new JsonObject { ["label"] = "Blue", ["description"] = "cold" }),
                }),
            });
            answer = "you chose " + ((string?)answers?["answers"]?["color"]?["answers"]?[0] ?? "nothing");
        }
        if (text.Contains("usage"))
            await Notify("thread/tokenUsage/updated", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId,
                ["tokenUsage"] = new JsonObject
                {
                    ["modelContextWindow"] = 200_000,
                    ["last"] = Usage(1200, 300), ["total"] = Usage(5000, 900),
                },
            });
        if (text.Contains("name"))
        {
            t.Name = "Generated title";
            await Notify("thread/name/updated", new JsonObject { ["threadId"] = t.Id, ["threadName"] = t.Name });
        }
        if (text.Contains("form"))
        {
            var answered = await Ask("mcpServer/elicitation/request", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["serverName"] = "other-tool", ["mode"] = "form", ["message"] = "Which branch?",
                ["requestedSchema"] = new JsonObject
                {
                    ["type"] = "object", ["properties"] = new JsonObject { ["branch"] = new JsonObject { ["type"] = "string", ["title"] = "Branch" } },
                },
            });
            answer = "branch " + ((string?)answered?["content"]?["branch"] ?? "none");
        }
        if (text.Contains("perm"))
        {
            var asking = Ask("item/permissions/requestApproval", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = $"perm-{turnId}", ["startedAtMs"] = 1, ["cwd"] = t.Cwd, ["reason"] = "Fetch the docs",
                ["permissions"] = new JsonObject { ["network"] = new JsonObject { ["enabled"] = true } },
            });
            if (await Task.WhenAny(asking, interrupts[turnId].Task) != asking)
            {
                await Notify("serverRequest/resolved", new JsonObject { ["threadId"] = t.Id, ["requestId"] = LastRequestId });
                status = "interrupted";
            }
            else answer = "scope " + (string?)(await asking)?["scope"];
        }
        if (text.Contains("wait"))
        {
            await interrupts[turnId].Task;
            status = "interrupted";
        }
        if (text.Contains("stuck")) await new TaskCompletionSource().Task; // never ends, interrupted or not
        if (text.Contains("closed"))
        {
            t.Loaded = false;
            await Notify("thread/closed", new JsonObject { ["threadId"] = t.Id });
            return; // and no turn/completed
        }
        if (text.Contains("fail"))
        {
            status = "failed";
            error = new JsonObject { ["message"] = "boom" };
        }
        if (status == "completed")
        {
            var msg = $"msg-{turnId}";
            await Notify("item/started", new JsonObject
            {
                ["threadId"] = t.Id, ["turnId"] = turnId, ["startedAtMs"] = 1, ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = msg, ["text"] = "" },
            });
            foreach (var part in new[] { answer[..(answer.Length / 2)], answer[(answer.Length / 2)..] })
                await Notify("item/agentMessage/delta", new JsonObject { ["threadId"] = t.Id, ["turnId"] = turnId, ["itemId"] = msg, ["delta"] = part });
            await Item(t, turnId, new JsonObject { ["type"] = "agentMessage", ["id"] = msg, ["text"] = answer, ["phase"] = "final_answer" });
        }
        t.Durable = true;
        await Notify("turn/completed", new JsonObject
        {
            ["threadId"] = t.Id, ["turn"] = new JsonObject { ["id"] = turnId, ["items"] = new JsonArray(), ["status"] = status, ["error"] = error },
        });
    }

    static JsonObject Usage(long input, long output) => new()
    {
        ["inputTokens"] = input, ["outputTokens"] = output, ["cachedInputTokens"] = 0, ["reasoningOutputTokens"] = 0, ["totalTokens"] = input + output,
    };
}
