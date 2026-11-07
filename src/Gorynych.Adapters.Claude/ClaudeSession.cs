using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude.Pty;

namespace Gorynych.Adapters.Claude;

/// <summary>
/// One Claude Code session: the unmodified interactive <c>claude</c> in a pseudo console,
/// started with per-launch flags only. Hooks feed the ACP stream; a prompt is the launch
/// argument the first time and a bracketed paste plus Enter after <c>Stop</c> later on.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ClaudeSession : IAsyncDisposable
{
    readonly ClaudeAgent agent;
    readonly ClaudeAdapterOptions options;
    readonly SemaphoreSlim promptGate = new(1, 1);
    readonly SemaphoreSlim emitGate = new(1, 1);
    readonly Lock stateGate = new();
    readonly Dictionary<string, string> toolIds = new(StringComparer.Ordinal);
    readonly ScreenTail screen = new();
    ConPty? pty;
    TranscriptWatcher? transcript;
    Turn? turn;
    TaskCompletionSource idle = NewSignal();
    bool resume;
    bool sawPrompt;
    bool acceptedChannelWarning;
    bool askedTrust;
    int permissionCount;

    public ClaudeSession(ClaudeAgent agent, ClaudeAdapterOptions options, string id, string cwd, bool resume)
    {
        this.agent = agent;
        this.options = options;
        Id = id;
        Cwd = cwd;
        this.resume = resume;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        screen.Changed += OnScreenChanged;
    }

    public string Id { get; }
    public string Cwd { get; }

    /// <summary>The secret in this session's hook URL, so other local processes cannot post events.</summary>
    public string Token { get; }

    public bool IsRunning => pty is { Exited.IsCompleted: false };

    string Folder => Path.Combine(options.DataDirectory, "sessions", Id);

    // ---------------------------------------------------------------- prompts

    public async Task<StopReason> PromptAsync(string text, CancellationToken ct)
    {
        await promptGate.WaitAsync(ct);
        try
        {
            var current = new Turn(text);
            Task<int> exited;
            if (!IsRunning)
            {
                lock (stateGate) { turn = current; idle = NewSignal(); }
                Launch(text);
                exited = pty!.Exited;
                // A launch that never reaches the prompt (a dialog nobody answers) must not hang forever.
                _ = WatchStartupAsync(current);
            }
            else
            {
                await idle.Task.WaitAsync(ct);
                lock (stateGate) { turn = current; idle = NewSignal(); }
                await PasteAsync(text);
                exited = pty!.Exited;
            }
            await using var registration = ct.Register(() => current.TryComplete(StopReason.Cancelled));
            var finished = await Task.WhenAny(current.Task, exited);
            if (finished == exited && !current.Task.IsCompleted)
                throw new JsonRpcException(JsonRpcException.InternalError,
                    $"Claude exited with code {exited.Result} before the turn ended. Last screen: {ScreenTailText()}");
            return await current.Task;
        }
        finally
        {
            lock (stateGate) turn = null;
            promptGate.Release();
        }
    }

    async Task WatchStartupAsync(Turn current)
    {
        await Task.Delay(TimeSpan.FromSeconds(options.StartupTimeoutSeconds));
        if (sawPrompt || current.Task.IsCompleted) return;
        current.TryFail(new JsonRpcException(JsonRpcException.InternalError,
            $"Claude did not start the turn within {options.StartupTimeoutSeconds} s. Last screen: {ScreenTailText()}"));
    }

    async Task PasteAsync(string text)
    {
        // Bracketed paste keeps a multi-line message in one piece; Enter submits it.
        var safe = text.Replace("\x1b[201~", "", StringComparison.Ordinal).ReplaceLineEndings("\n");
        pty!.Write("\x1b[200~" + safe + "\x1b[201~");
        await Task.Delay(options.PasteEnterDelayMs);
        pty.Write("\r");
    }

    public void Cancel()
    {
        Turn? current;
        lock (stateGate) current = turn;
        if (current is null || !IsRunning) return;
        pty!.Write("\x1b"); // Esc interrupts the turn in the TUI
        current.TryComplete(StopReason.Cancelled);
        // Stop may not follow an interrupt; treat the session as idle shortly after.
        _ = Task.Delay(1500).ContinueWith(_ => SetIdle(), TaskScheduler.Default);
    }

    void SetIdle()
    {
        lock (stateGate) idle.TrySetResult();
    }

    // ---------------------------------------------------------------- launch

    void Launch(string prompt)
    {
        var exe = options.Executable ?? ExecutableLocator.Find("claude")
            ?? throw new JsonRpcException(JsonRpcException.InternalError, "claude was not found on PATH");
        Directory.CreateDirectory(Folder);
        var settingsPath = Path.Combine(Folder, "settings.json");
        File.WriteAllText(settingsPath, ClaudeHooks.Settings(agent.HookUrl(Id, Token)).ToJsonString());

        var args = new List<string> { "--settings", settingsPath, "--permission-mode", options.PermissionMode };
        if (options.Model is { Length: > 0 } model) args.AddRange(["--model", model]);
        if (agent.Channel is { } channel)
        {
            var mcpPath = Path.Combine(Folder, "mcp.json");
            File.WriteAllText(mcpPath, channel.McpConfig(Id, Token).ToJsonString());
            args.AddRange(["--mcp-config", mcpPath, "--dangerously-load-development-channels", "server:" + ChannelHost.ServerName,
                "--append-system-prompt", ChannelHost.SystemPrompt]);
        }
        args.AddRange(resume ? ["--resume", Id] : ["--session-id", Id]);
        args.AddRange(["--", prompt]);
        var commandLine = TerminalText.Quote(exe) + " " + TerminalText.JoinArguments(args);

        sawPrompt = false;
        acceptedChannelWarning = false;
        screen.Clear();
        agent.Log($"claude {Id}: launching in {Cwd} ({(resume ? "resume" : "new")})");
        pty = ConPty.Start(commandLine, Cwd, LaunchEnvironment());
        resume = true; // from now on the conversation exists in Claude's own store
        var current = pty;
        _ = Task.Run(() => PumpOutput(current));
        _ = current.Exited.ContinueWith(t => OnExited(current, t.Result), TaskScheduler.Default);
        transcript ??= new TranscriptWatcher(TranscriptTitles.ExpectedPath(Cwd, Id), title => _ = agent.OnTitleAsync(Id, title));
    }

    void PumpOutput(ConPty source)
    {
        var buffer = new byte[16 * 1024];
        using var raw = options.KeepTerminalLog
            ? new FileStream(Path.Combine(Folder, "terminal.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite)
            : null;
        try
        {
            int n;
            while ((n = source.Output.Read(buffer, 0, buffer.Length)) > 0)
            {
                screen.Append(buffer.AsSpan(0, n));
                if (raw is not null && raw.Length < 4 * 1024 * 1024) { raw.Write(buffer, 0, n); raw.Flush(); }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    void OnExited(ConPty source, int code)
    {
        agent.Log($"claude {Id}: exited with code {code}");
        lock (stateGate)
        {
            if (!ReferenceEquals(pty, source)) return;
            idle.TrySetResult();
        }
    }

    Dictionary<string, string> LaunchEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value ?? "";
        // Started from inside another Claude Code session (for example the desktop app),
        // the child would otherwise attach to that session instead of the user's own login.
        var nested = env.ContainsKey("CLAUDECODE");
        foreach (var key in env.Keys.ToList())
        {
            if (key.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_AGENT_SDK", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("CLAUDE_PREVIEW_", StringComparison.OrdinalIgnoreCase)
                || key is "CLAUDE_PID" or "CLAUDE_EFFORT"
                || (nested && key is "ANTHROPIC_BASE_URL" or "MCP_CONNECTION_NONBLOCKING" or "MCP_SERVER_CONNECTION_BATCH_SIZE"))
                env.Remove(key);
        }
        return env;
    }

    // ---------------------------------------------------------------- screens that need a key

    void OnScreenChanged()
    {
        if (sawPrompt) return;
        var text = screen.Text;
        if (agent.Channel is not null && !acceptedChannelWarning && ChannelWarning().IsMatch(text))
        {
            acceptedChannelWarning = true;
            agent.Log($"claude {Id}: accepting the development-channels notice");
            _ = Task.Delay(300).ContinueWith(_ => pty?.Write("\r"), TaskScheduler.Default);
            return;
        }
        if (!askedTrust && TrustDialog().IsMatch(text))
        {
            askedTrust = true;
            _ = AskTrustAsync();
        }
    }

    /// <summary>Claude asks whether to trust a new folder; that is the person's call, so ask them.</summary>
    async Task AskTrustAsync()
    {
        var request = new RequestPermissionRequest
        {
            SessionId = Id,
            ToolCall = new ToolCallUpdate
            {
                ToolCallId = "trust-folder",
                Title = $"Claude Code asks whether to trust the folder {Cwd}",
                Kind = ToolKind.Other,
                Status = ToolCallStatus.Pending,
                Locations = [new ToolCallLocation { Path = Cwd }],
            },
            Options =
            [
                new PermissionOption { OptionId = ClaudeHooks.AllowOptionId, Name = "Trust this folder", Kind = PermissionOptionKind.AllowOnce },
                new PermissionOption { OptionId = ClaudeHooks.DenyOptionId, Name = "Cancel", Kind = PermissionOptionKind.RejectOnce },
            ],
        };
        try
        {
            var response = await agent.RequestPermissionAsync(request, CancellationToken.None);
            if (ClaudeHooks.IsAllowed(response.Outcome)) pty?.Write("\r");
            else pty?.Kill();
        }
        catch (Exception e) when (e is IOException or JsonRpcException)
        {
            pty?.Kill();
        }
    }

    [GeneratedRegex(@"(?i)development\s+channel|dangerously-load-development-channels")]
    private static partial Regex ChannelWarning();

    [GeneratedRegex(@"(?i)trust\s+(the\s+files\s+in\s+)?this\s+folder")]
    private static partial Regex TrustDialog();

    string ScreenTailText()
    {
        var t = screen.Text.Trim();
        return t.Length <= 600 ? t : "…" + t[^600..];
    }

    // ---------------------------------------------------------------- hooks

    public async Task<JsonObject> OnHookAsync(JsonObject e)
    {
        var name = ClaudeHooks.Name(e);
        if ((string?)e["transcript_path"] is { Length: > 0 } path) transcript?.Follow(path);
        if (name == "PermissionRequest") return await OnPermissionAsync(e);
        await emitGate.WaitAsync();
        try { await OnEventAsync(name, e); }
        finally { emitGate.Release(); }
        return [];
    }

    async Task OnEventAsync(string? name, JsonObject e)
    {
        switch (name)
        {
            case "UserPromptSubmit":
            {
                sawPrompt = true;
                var prompt = (string?)e["prompt"] ?? "";
                Turn? current;
                lock (stateGate) { current = turn; idle = idle.Task.IsCompleted ? NewSignal() : idle; }
                if (current is { Confirmed: false } && !ClaudeHooks.IsChannelMessage(prompt))
                    current.Confirmed = true; // the client's own prompt: ACP does not echo it back
                else
                    await agent.EmitAsync(Id, ClaudeHooks.ForeignPrompt(prompt));
                break;
            }
            case "PreToolUse":
            {
                var call = ClaudeHooks.ToolCall(e);
                lock (stateGate) toolIds[ToolKey(e)] = call.ToolCallId;
                await agent.EmitAsync(Id, call);
                break;
            }
            case "PostToolUse":
                await agent.EmitAsync(Id, ClaudeHooks.ToolCallFinished(e, failed: false));
                break;
            case "PostToolUseFailure":
                await agent.EmitAsync(Id, ClaudeHooks.ToolCallFinished(e, failed: true));
                break;
            case "MessageDisplay":
                lock (stateGate) if (turn is { } t) t.HadText = true;
                await agent.EmitAsync(Id, ClaudeHooks.MessageChunk(e));
                break;
            case "Stop":
            case "StopFailure":
            {
                Turn? current;
                lock (stateGate) current = turn;
                var last = (string?)e["last_assistant_message"];
                if (current is { HadText: false } && !string.IsNullOrEmpty(last))
                    await agent.EmitAsync(Id, ClaudeHooks.TextChunk(last));
                if (name == "StopFailure")
                    await agent.EmitAsync(Id, ClaudeHooks.TextChunk($"\n[Claude stopped with an error: {(string?)e["error"] ?? (string?)e["reason"] ?? "unknown"}]"));
                await agent.TouchAsync(Id);
                SetIdle();
                current?.TryComplete(StopReason.EndTurn);
                break;
            }
            default:
                agent.Log($"claude {Id}: {name}");
                break;
        }
    }

    async Task<JsonObject> OnPermissionAsync(JsonObject e)
    {
        var tool = (string?)e["tool_name"] ?? "";
        // The reply tool of our own channel answers another agent's opinion request; it needs no approval.
        if (agent.Channel is not null && tool == ChannelHost.ReplyToolName) return ClaudeHooks.AllowReply();
        string toolCallId;
        lock (stateGate)
            toolCallId = toolIds.TryGetValue(ToolKey(e), out var known) ? known : $"permission-{++permissionCount}";
        try
        {
            if (tool == "AskUserQuestion" && e["tool_input"] is JsonObject input)
            {
                var updated = input.DeepClone().AsObject();
                var answers = new JsonObject();
                foreach (var q in input["questions"] as JsonArray ?? [])
                {
                    if (q is not JsonObject question) continue;
                    var response = await agent.RequestPermissionAsync(ClaudeHooks.Question(Id, toolCallId, question), CancellationToken.None);
                    if (ClaudeHooks.ChosenLabel(question, response.Outcome) is not { } label)
                        return ClaudeHooks.DenyReply("The user chose not to answer.");
                    answers[(string?)question["question"] ?? ""] = label;
                }
                updated["answers"] = answers;
                return ClaudeHooks.AllowReply(updated);
            }
            var decision = await agent.RequestPermissionAsync(ClaudeHooks.Permission(Id, toolCallId, e), CancellationToken.None);
            if (ClaudeHooks.IsAllowed(decision.Outcome)) return ClaudeHooks.AllowReply();
            await agent.EmitAsync(Id, ClaudeHooks.ToolCallStatusUpdate(toolCallId, ToolCallStatus.Failed, "Denied in Gorynych."));
            return ClaudeHooks.DenyReply("The user denied this tool call in Gorynych.");
        }
        catch (Exception ex) when (ex is IOException or JsonRpcException or OperationCanceledException)
        {
            agent.Log($"claude {Id}: approval failed: {ex.Message}");
            return ClaudeHooks.DenyReply("Gorynych could not reach the user for approval.");
        }
    }

    static string ToolKey(JsonObject e) => (string?)e["tool_name"] + "\n" + e["tool_input"]?.ToJsonString();

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        lock (stateGate) turn?.TryComplete(StopReason.Cancelled);
        if (transcript is not null) await transcript.DisposeAsync();
        pty?.Dispose();
    }

    /// <summary>One prompt turn sent by the ACP client.</summary>
    sealed class Turn(string text)
    {
        readonly TaskCompletionSource<StopReason> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get; } = text;
        public bool Confirmed { get; set; }
        public bool HadText { get; set; }
        public Task<StopReason> Task => done.Task;
        public void TryComplete(StopReason reason) => done.TrySetResult(reason);
        public void TryFail(Exception e) => done.TrySetException(e);
    }
}
