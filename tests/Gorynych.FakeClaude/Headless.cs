// The headless face of the fake: `claude -p --input-format stream-json --output-format stream-json`,
// shaped like Claude Code 2.1.302's stream (see research/claude-headless-spec.md). It answers
// `initialize` and the other control requests, replays every user message it takes, and runs a
// turn per prompt. A message that arrives during a "slow" turn joins it. Prompts:
//   "write"     asks can_use_tool for Write;           "pick a color" asks AskUserQuestion;
//   "peers"     asks for list_agents and ask_agent;     "think"    streams a thought first;
//   "slow"      runs a Bash call that waits for an interrupt or 1.5 s ("hold": messages that arrive
//               meanwhile stay queued instead of joining);  "swallow" is taken in silence: no
//               replay, no turn, as a message Claude drops;
//   "on your own" is followed by a turn of Claude's own, as when a background task ends.
// An opinion request (its text names `message_id "…"`) is answered through the reply route the
// --mcp-config gives the gorynych server, as the peers server would. Files in the folder change
// the account: `signed-out` (no login, as `--bare` gives), `no-account`, `api-key` (initialize
// shows ANTHROPIC_API_KEY), `api-key-init` (only system/init shows it), `auth-token` (a token but
// no plan) and `bedrock` (another provider); `deaf` stops reading stdin after initialize;
// `no-bypass` starts in `default` whatever --permission-mode says, as a Claude whose settings turn
// bypassPermissions off; `force-bypass` starts in it whatever was asked; `no-leave` refuses to leave
// it; `models-small` offers only the default model. As Claude 2.1.302: bypassPermissions asks for
// no tool, a running process enters it only when it started in it or with
// --allow-dangerously-skip-permissions, and a PreToolUse hook registered in initialize is called
// back (hook_callback) before a matching tool in every mode, with the mode (`no-mode` leaves it out).
// Precedence as Claude's documentation gives it: a hook's deny, then its ask (which asks through
// can_use_tool even where an allow rule matches), then its allow; then allow rules (`permissions.allow`
// of --settings and of the folder's .claude/settings.json); then the mode: bypassPermissions and auto
// let the call run, the others ask. Prompt `gorynych tools` calls ask_agent, archive_session and
// restore_session.
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

static partial class FakeHeadless
{
    static readonly SemaphoreSlim WriteGate = new(1, 1);
    static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> Pending = new();
    static readonly Channel<JsonObject> Inbox = Channel.CreateUnbounded<JsonObject>();
    static TaskCompletionSource interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    static string mode = "default";
    static bool canBypass;
    static string cwdNow = "";
    static readonly List<(Regex Matcher, string Id)> PreToolUse = [];
    static readonly List<string> AllowRules = [];
    static string model = "claude-fake-1";
    static int messages;
    static int tools;
    static int asks;

    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var sessionId = Arg("--session-id") ?? Arg("--resume") ?? throw new ArgumentException("--session-id or --resume is required");
        var cwd = Environment.CurrentDirectory;
        var root = Path.Combine(Path.GetTempPath(), "gorynych-fake-claude");
        var folder = new string(Path.GetFullPath(cwd).TrimEnd('\\', '/').Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        var transcript = Path.Combine(root, "projects", folder, sessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(Path.Combine(root, sessionId + ".args.json"), new JsonArray(args.Select(a => (JsonNode)a).ToArray()).ToJsonString());
        var controls = Path.Combine(root, sessionId + ".controls.jsonl");
        mode = Arg("--permission-mode") ?? "default";
        if (mode == "bypassPermissions" && File.Exists(Path.Combine(cwd, "no-bypass"))) mode = "default";
        if (File.Exists(Path.Combine(cwd, "force-bypass"))) mode = "bypassPermissions";
        cwdNow = cwd;
        foreach (var settings in new[] { Arg("--settings"), Path.Combine(cwd, ".claude", "settings.json") })
            if (settings is not null && File.Exists(settings) && JsonNode.Parse(File.ReadAllText(settings))?["permissions"]?["allow"] is JsonArray allow)
                AllowRules.AddRange(allow.Select(r => (string?)r).OfType<string>());
        canBypass = mode == "bypassPermissions" || args.Contains("--allow-dangerously-skip-permissions") || args.Contains("--dangerously-skip-permissions");
        model = Arg("--model") ?? model;
        var replyUrl = ReplyUrl(Arg("--mcp-config"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Console.OutputEncoding = new UTF8Encoding(false);
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var reading = Task.Run(async () =>
        {
            while (await stdin.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || JsonNode.Parse(line) is not JsonObject m) continue;
                switch ((string?)m["type"])
                {
                    case "control_response":
                        if (m["response"] is JsonObject r && (string?)r["request_id"] is { } id && Pending.TryRemove(id, out var waiting)) waiting.TrySetResult(r);
                        break;
                    case "control_request":
                        File.AppendAllText(controls, m["request"]!.ToJsonString() + "\n");
                        await OnControlAsync(m, cwd, transcript, sessionId);
                        // A Claude that stops reading its input: writes to it block once the pipe is full.
                        if ((string?)m["request"]?["subtype"] == "initialize" && File.Exists(Path.Combine(cwd, "deaf")))
                            await Task.Delay(Timeout.Infinite);
                        break;
                    case "user":
                        await Inbox.Writer.WriteAsync(m);
                        break;
                }
            }
            Inbox.Writer.TryComplete();
        });

        while (await Inbox.Reader.WaitToReadAsync())
        {
            if (!Inbox.Reader.TryRead(out var first)) continue;
            await TurnAsync(first, sessionId, http, replyUrl);
        }
        await reading;
        return 0;
    }

    static async Task OnControlAsync(JsonObject m, string cwd, string transcript, string sessionId)
    {
        var id = (string?)m["request_id"] ?? "";
        var request = m["request"] as JsonObject ?? [];
        switch ((string?)request["subtype"])
        {
            case "initialize":
                JsonNode? account = File.Exists(Path.Combine(cwd, "signed-out"))
                    ? new JsonObject { ["tokenSource"] = "none", ["apiProvider"] = "firstParty" }
                    : File.Exists(Path.Combine(cwd, "api-key"))
                        ? new JsonObject { ["tokenSource"] = "claude.ai", ["apiKeySource"] = "ANTHROPIC_API_KEY", ["apiProvider"] = "firstParty" }
                        : new JsonObject { ["email"] = "fake@example.com", ["subscriptionType"] = "Claude Max", ["apiProvider"] = "firstParty" };
                if (File.Exists(Path.Combine(cwd, "auth-token")))
                    account = new JsonObject { ["tokenSource"] = "ANTHROPIC_AUTH_TOKEN", ["apiProvider"] = "firstParty" };
                if (File.Exists(Path.Combine(cwd, "bedrock")))
                    account = new JsonObject { ["subscriptionType"] = "Claude Max", ["apiProvider"] = "bedrock" };
                if (File.Exists(Path.Combine(cwd, "no-account"))) account = null;
                foreach (var hook in (request["hooks"]?["PreToolUse"] as JsonArray ?? []).OfType<JsonObject>())
                    foreach (var callback in (hook["hookCallbackIds"] as JsonArray ?? []).Select(c => (string?)c).OfType<string>())
                        PreToolUse.Add((new Regex((string?)hook["matcher"] ?? ".*"), callback));
                await RespondAsync(id, new JsonObject
                {
                    ["commands"] = new JsonArray(),
                    ["agents"] = new JsonArray(),
                    ["models"] = File.Exists(Path.Combine(cwd, "models-small"))
                        ? JsonNode.Parse("""[{"value":"default","resolvedModel":"claude-fake-1","displayName":"Default (recommended)","supportedEffortLevels":["low","high"]}]""")
                        : JsonNode.Parse("""
                        [{"value":"default","resolvedModel":"claude-fake-1","displayName":"Default (recommended)","supportedEffortLevels":["low","high"],"supportsAutoMode":true},
                         {"value":"fast","resolvedModel":"claude-fake-2","displayName":"Fast","supportedEffortLevels":["low"]}]
                        """),
                    ["account"] = account,
                    ["current_permission_mode"] = mode,
                    ["claude_code_version"] = "fake",
                });
                break;
            case "set_model":
                model = (string?)request["model"] == "fast" ? "claude-fake-2" : "claude-fake-1";
                await RespondAsync(id, []);
                break;
            case "set_permission_mode":
                if ((mode == "bypassPermissions" && (string?)request["mode"] != "bypassPermissions" && File.Exists(Path.Combine(cwd, "no-leave")))
                    || ((string?)request["mode"] == "bypassPermissions" && !canBypass))
                {
                    await WriteAsync(new JsonObject
                    {
                        ["type"] = "control_response",
                        ["response"] = new JsonObject
                        {
                            ["subtype"] = "error", ["request_id"] = id,
                            ["error"] = "Cannot set permission mode to bypassPermissions because the session was not launched with --dangerously-skip-permissions",
                        },
                    });
                    break;
                }
                mode = (string?)request["mode"] ?? mode;
                await RespondAsync(id, new JsonObject { ["mode"] = mode });
                break;
            case "apply_flag_settings":
                await RespondAsync(id, []);
                break;
            case "rename_session":
                File.AppendAllText(transcript, new JsonObject { ["type"] = "custom-title", ["customTitle"] = (string?)request["title"], ["sessionId"] = sessionId }.ToJsonString() + "\n");
                await RespondAsync(id, []);
                break;
            case "interrupt":
            {
                // cancel_queued drops what waits in the queue and lists it.
                var cancelled = new JsonArray();
                if ((bool?)request["cancel_queued"] == true)
                    while (Inbox.Reader.TryRead(out var queued))
                        cancelled.Add((JsonNode?)(string?)queued["uuid"]);
                interrupted.TrySetResult();
                await RespondAsync(id, new JsonObject { ["still_queued"] = new JsonArray(), ["cancelled"] = cancelled });
                break;
            }
            default:
                await WriteAsync(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = id, ["error"] = "not in the fake" },
                });
                break;
        }
    }

    static async Task TurnAsync(JsonObject first, string sessionId, HttpClient http, string? replyUrl)
    {
        interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = Text(first);
        if (prompt.Contains("swallow", StringComparison.OrdinalIgnoreCase)) return;
        var apiKey = File.Exists(Path.Combine(Environment.CurrentDirectory, "api-key-init")) ? "ANTHROPIC_API_KEY" : "none";
        await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["model"] = model, ["permissionMode"] = mode, ["apiKeySource"] = apiKey });
        await ReplayAsync(first, sessionId);
        string answer;
        var aborted = false;
        var joined = new List<string>();
        if (prompt.Contains("write", StringComparison.OrdinalIgnoreCase))
        {
            var input = new JsonObject { ["file_path"] = Path.Combine(Environment.CurrentDirectory, "fake.txt"), ["content"] = "hi" };
            var toolUse = await ToolUseAsync(sessionId, "Write", input);
            var decision = await AskAsync("Write", input, toolUse);
            var allowed = (string?)decision["behavior"] == "allow";
            await ToolResultAsync(sessionId, toolUse, allowed ? "File created" : "denied", !allowed,
                allowed ? new JsonObject { ["type"] = "create", ["filePath"] = (string)input["file_path"]! } : null);
            answer = allowed ? "wrote it" : "not allowed";
        }
        else if (prompt.Contains("pick a color", StringComparison.OrdinalIgnoreCase))
        {
            var input = JsonNode.Parse("""
                {"questions":[{"question":"Which color?","header":"Color","multiSelect":false,
                  "options":[{"label":"Red","description":"warm"},{"label":"Blue","description":"cold"}]}]}
                """)!.AsObject();
            var toolUse = await ToolUseAsync(sessionId, "AskUserQuestion", input);
            var decision = await AskAsync("AskUserQuestion", input, toolUse);
            var chosen = (string?)decision["updatedInput"]?["answers"]?["Which color?"];
            var allowed = (string?)decision["behavior"] == "allow" && chosen is not null;
            await ToolResultAsync(sessionId, toolUse, allowed ? $"answered {chosen}" : "no answer", !allowed, null);
            answer = allowed ? "you chose " + chosen : "no answer";
        }
        else if (prompt.Contains("gorynych tools", StringComparison.OrdinalIgnoreCase))
        {
            var verdicts = new List<string>();
            foreach (var (tool, input) in new[]
            {
                ("mcp__gorynych__ask_agent", new JsonObject { ["session_id"] = "other", ["question"] = "tabs?" }),
                ("mcp__gorynych__archive_session", new JsonObject { ["session_id"] = "other" }),
                ("mcp__gorynych__restore_session", new JsonObject { ["session_id"] = "other" }),
            })
            {
                var toolUse = await ToolUseAsync(sessionId, tool, input);
                var decision = await AskAsync(tool, input, toolUse);
                verdicts.Add($"{tool.Split("__")[2]}:{(string?)decision["behavior"]}");
                await ToolResultAsync(sessionId, toolUse, "ok", false, null);
            }
            answer = string.Join(" ", verdicts);
        }
        else if (prompt.Contains("peers", StringComparison.OrdinalIgnoreCase))
        {
            var verdicts = new List<string>();
            foreach (var (tool, input) in new[]
            {
                ("mcp__gorynych__list_agents", new JsonObject()),
                ("mcp__gorynych__ask_agent", new JsonObject { ["session_id"] = "other", ["question"] = "tabs?" }),
            })
            {
                var toolUse = await ToolUseAsync(sessionId, tool, input);
                var decision = await AskAsync(tool, input, toolUse);
                verdicts.Add($"{tool.Split("__")[2]}:{(string?)decision["behavior"]}");
                await ToolResultAsync(sessionId, toolUse, "ok", false, null);
            }
            answer = string.Join(" ", verdicts);
        }
        else if (prompt.Contains("slow", StringComparison.OrdinalIgnoreCase))
        {
            var toolUse = await ToolUseAsync(sessionId, "Bash", new JsonObject { ["command"] = "sleep 1" });
            // Waits for an interrupt or a while; a message that arrives meanwhile joins this turn.
            var wait = Task.Delay(1500);
            while (true)
            {
                var hold = prompt.Contains("hold", StringComparison.OrdinalIgnoreCase);
                var done = hold
                    ? await Task.WhenAny(interrupted.Task, wait)
                    : await Task.WhenAny(interrupted.Task, wait, Inbox.Reader.WaitToReadAsync().AsTask());
                if (done == interrupted.Task) { aborted = true; break; }
                if (done == wait) break;
                while (Inbox.Reader.TryRead(out var more))
                {
                    await ReplayAsync(more, sessionId);
                    joined.Add(Text(more));
                }
            }
            if (!aborted) await ToolResultAsync(sessionId, toolUse, "", false, new JsonObject { ["stdout"] = "slept", ["stderr"] = "" });
            answer = "slept" + string.Concat(joined.Select(j => " / also: " + j));
        }
        else answer = "echo: " + prompt;

        if (aborted)
        {
            await WriteAsync(new JsonObject
            {
                ["type"] = "result", ["subtype"] = "error_during_execution", ["is_error"] = true, ["session_id"] = sessionId,
                ["stop_reason"] = "tool_use", ["terminal_reason"] = "aborted_tools", ["errors"] = new JsonArray("[ede_diagnostic] aborted"),
            });
            return;
        }
        await AnswerAsync(sessionId, answer, prompt.Contains("think", StringComparison.OrdinalIgnoreCase));
        await ResultAsync(sessionId, answer);
        foreach (var asked in new[] { prompt }.Concat(joined))
            if (OpinionId().Match(asked) is { Success: true } opinion && replyUrl is not null)
                await http.PostAsync(replyUrl + "/reply", new StringContent(
                    new JsonObject { ["message_id"] = opinion.Groups[1].Value, ["text"] = "fake opinion" }.ToJsonString(), Encoding.UTF8, "application/json"));
        if (prompt.Contains("on your own", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["model"] = model, ["permissionMode"] = mode });
            await AnswerAsync(sessionId, "background done", false);
            await ResultAsync(sessionId, "background done");
        }
    }

    static Task ReplayAsync(JsonObject user, string sessionId)
    {
        var replay = (JsonObject)user.DeepClone();
        replay["isReplay"] = true;
        replay["session_id"] = sessionId;
        return WriteAsync(replay);
    }

    static async Task<string> ToolUseAsync(string sessionId, string tool, JsonObject input)
    {
        var id = $"toolu_fake{++tools}";
        await WriteAsync(new JsonObject
        {
            ["type"] = "assistant", ["session_id"] = sessionId, ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject
            {
                ["id"] = $"msg_fake{++messages}", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = tool, ["input"] = input.DeepClone() }),
            },
        });
        return id;
    }

    static async Task<JsonObject> AskAsync(string tool, JsonObject input, string toolUseId)
    {
        // Hooks first, in every mode.
        var decisions = new List<string>();
        foreach (var (matcher, callback) in PreToolUse.Where(h => h.Matcher.IsMatch(tool)).ToList())
        {
            var hookInput = new JsonObject
            {
                ["hook_event_name"] = "PreToolUse", ["tool_name"] = tool, ["tool_input"] = input.DeepClone(), ["tool_use_id"] = toolUseId,
            };
            if (!File.Exists(Path.Combine(cwdNow, "no-mode"))) hookInput["permission_mode"] = mode;
            var hookId = $"hook-{++asks}";
            var hookAnswer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending[hookId] = hookAnswer;
            await WriteAsync(new JsonObject
            {
                ["type"] = "control_request", ["request_id"] = hookId,
                ["request"] = new JsonObject { ["subtype"] = "hook_callback", ["callback_id"] = callback, ["input"] = hookInput, ["tool_use_id"] = toolUseId },
            });
            if ((string?)(await hookAnswer.Task)["response"]?["hookSpecificOutput"]?["permissionDecision"] is { } decision) decisions.Add(decision);
        }
        var allowed = new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input.DeepClone() };
        if (decisions.Contains("deny")) return new JsonObject { ["behavior"] = "deny", ["message"] = "denied by a hook" };
        if (!decisions.Contains("ask"))
        {
            if (decisions.Contains("allow")) return allowed;
            if (AllowRules.Any(r => r == tool || tool.StartsWith(r + "__", StringComparison.Ordinal))) return allowed;
            // bypassPermissions asks for no tool; auto's classifier lets these through.
            if (mode is "bypassPermissions" or "auto" && tool != "AskUserQuestion") return allowed;
        }
        var id = $"ask-{++asks}";
        var waiting = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = waiting;
        await WriteAsync(new JsonObject
        {
            ["type"] = "control_request", ["request_id"] = id,
            ["request"] = new JsonObject { ["subtype"] = "can_use_tool", ["tool_name"] = tool, ["input"] = input.DeepClone(), ["tool_use_id"] = toolUseId },
        });
        var response = await waiting.Task;
        return response["response"] as JsonObject ?? [];
    }

    static Task ToolResultAsync(string sessionId, string toolUseId, string text, bool error, JsonObject? structured)
    {
        var message = new JsonObject
        {
            ["type"] = "user", ["session_id"] = sessionId, ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = text, ["is_error"] = error }),
            },
        };
        if (structured is not null) message["tool_use_result"] = structured;
        return WriteAsync(message);
    }

    static async Task AnswerAsync(string sessionId, string answer, bool think)
    {
        var id = $"msg_fake{++messages}";
        await StreamAsync(sessionId, new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = id } });
        if (think)
            await StreamAsync(sessionId, new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "hmm" },
            });
        var half = answer.Length / 2;
        foreach (var piece in new[] { answer[..half], answer[half..] })
            await StreamAsync(sessionId, new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = piece },
            });
        await StreamAsync(sessionId, new JsonObject { ["type"] = "message_stop" });
        await WriteAsync(new JsonObject
        {
            ["type"] = "assistant", ["session_id"] = sessionId, ["parent_tool_use_id"] = null,
            ["message"] = new JsonObject { ["id"] = id, ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }) },
        });
    }

    static Task StreamAsync(string sessionId, JsonObject e) =>
        WriteAsync(new JsonObject { ["type"] = "stream_event", ["session_id"] = sessionId, ["parent_tool_use_id"] = null, ["event"] = e });

    static Task ResultAsync(string sessionId, string answer) => WriteAsync(new JsonObject
    {
        ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["session_id"] = sessionId,
        ["result"] = answer, ["stop_reason"] = "end_turn", ["terminal_reason"] = "completed",
    });

    static Task RespondAsync(string id, JsonObject response) => WriteAsync(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = id, ["response"] = response },
    });

    static async Task WriteAsync(JsonObject m)
    {
        await WriteGate.WaitAsync();
        try
        {
            await Console.Out.WriteAsync(m.ToJsonString() + "\n");
            await Console.Out.FlushAsync();
        }
        finally { WriteGate.Release(); }
    }

    static string Text(JsonObject user) => user["message"]?["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Select(b => (string?)b["type"] == "image" ? "[image]" : (string?)b["text"] ?? "")),
        _ => "",
    };

    static string? ReplyUrl(string? mcpConfig)
    {
        if (mcpConfig is null || !File.Exists(mcpConfig)) return null;
        var args = JsonNode.Parse(File.ReadAllText(mcpConfig))?["mcpServers"]?["gorynych"]?["args"] as JsonArray;
        var list = args?.Select(a => (string?)a).ToList() ?? [];
        var i = list.IndexOf("--reply-url");
        return i >= 0 && i + 1 < list.Count ? list[i + 1] : null;
    }

    [GeneratedRegex("message_id \"([^\"]+)\"")]
    private static partial Regex OpinionId();
}
