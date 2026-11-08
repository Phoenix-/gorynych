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
    TaskCompletionSource cancelSignal = NewSignal();
    readonly CancellationTokenSource lifetime = new();
    int turnCount;
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

    /// <summary>
    /// The MCP servers the ACP client gave this session (<c>session/new</c>, <c>session/resume</c>),
    /// passed to Claude through the per-launch <c>--mcp-config</c>; they apply from the next launch.
    /// </summary>
    public List<McpServer> McpServers { get; set; } = [];

    /// <summary>
    /// The text of the ACP <c>systemPrompt</c> (append mode) the client gave this session in
    /// <c>session/new</c> or its latest <c>session/resume</c>, or null; Claude gets it through the
    /// per-launch <c>--append-system-prompt</c>, after the channel's part, from the next launch.
    /// </summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Message IDs this session's user messages already use (see <see cref="UserMessageId"/>).</summary>
    readonly HashSet<string> userMessageIds = new(StringComparer.Ordinal);

    public bool IsRunning => pty is { Exited.IsCompleted: false };

    string Folder => Path.Combine(options.DataDirectory, "sessions", Id);

    // ---------------------------------------------------------------- prompts

    /// <param name="cancelled">True once the client cancelled after sending this prompt.</param>
    public async Task<StopReason> PromptAsync(string text, Func<bool> cancelled, CancellationToken ct)
    {
        await promptGate.WaitAsync(ct);
        try
        {
            if (cancelled()) return StopReason.Cancelled;
            var current = new Turn(text);
            if (IsRunning)
            {
                // Paste only into an idle composer; a cancel stops the wait.
                while (!idle.Task.IsCompleted)
                {
                    Task wake;
                    lock (stateGate) wake = Task.WhenAny(idle.Task, cancelSignal.Task);
                    await wake.WaitAsync(ct);
                    if (cancelled()) return StopReason.Cancelled;
                }
                if (cancelled()) return StopReason.Cancelled;
            }
            Task<int> exited;
            if (!IsRunning) // not started yet, or it ended while we waited
            {
                lock (stateGate) { turn = current; idle = NewSignal(); turnCount++; }
                Launch(text);
                exited = pty!.Exited;
                // A launch that never reaches the prompt (a dialog nobody answers) must not hang forever.
                _ = WatchStartupAsync(current, pty);
            }
            else
            {
                lock (stateGate) { turn = current; idle = NewSignal(); turnCount++; }
                await PasteAsync(text);
                exited = pty!.Exited;
            }
            await using var registration = ct.Register(() => current.TryComplete(StopReason.Cancelled));
            var finished = await Task.WhenAny(current.Task, exited);
            if (finished == exited && !current.Task.IsCompleted)
            {
                // The Stop hook may still be on its way when the process exits right after it.
                await Task.WhenAny(current.Task, Task.Delay(1500));
                if (!current.Task.IsCompleted)
                    throw new JsonRpcException(JsonRpcException.InternalError,
                        $"Claude exited with code {exited.Result} before the turn ended. Last screen: {ScreenTailText()}");
            }
            return await current.Task;
        }
        finally
        {
            lock (stateGate) turn = null;
            promptGate.Release();
        }
    }

    async Task WatchStartupAsync(Turn current, ConPty launched)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(options.StartupTimeoutSeconds), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (sawPrompt || current.Task.IsCompleted || !ReferenceEquals(pty, launched)) return;
        var screenText = ScreenTailText();
        // Stop the stuck process, so the next prompt starts a fresh one instead of waiting forever.
        launched.Kill();
        await Task.WhenAny(launched.Exited, Task.Delay(5000));
        SetIdle();
        current.TryFail(new JsonRpcException(JsonRpcException.InternalError,
            $"Claude did not start the turn within {options.StartupTimeoutSeconds} s. Last screen: {screenText}"));
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
        int generation;
        bool busy;
        lock (stateGate)
        {
            current = turn;
            generation = turnCount;
            busy = !idle.Task.IsCompleted;
            var wake = cancelSignal;
            cancelSignal = NewSignal();
            wake.TrySetResult();
        }
        if (IsRunning && (current is not null || busy))
        {
            pty!.Write("\x1b"); // Esc interrupts the turn in the TUI
            // Stop may not follow an interrupt; treat the session as idle shortly after,
            // unless another turn has started meanwhile.
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                lock (stateGate)
                    if (turnCount == generation) idle.TrySetResult();
            }, TaskScheduler.Default);
        }
        current?.TryComplete(StopReason.Cancelled);
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
        File.WriteAllText(settingsPath, ClaudeHooks.Settings(agent.HookUrl(Id, Token)).ToJsonString(AcpJson.TextOptions));

        var args = new List<string> { "--settings", settingsPath, "--permission-mode", options.PermissionMode };
        if (options.Model is { Length: > 0 } model) args.AddRange(["--model", model]);
        var mcpPath = Path.Combine(Folder, "mcp.json");
        if (agent.Channel is { } channel)
        {
            File.WriteAllText(mcpPath, channel.McpConfig(Id, Token, McpServers).ToJsonString(AcpJson.TextOptions));
            args.AddRange(["--mcp-config", mcpPath, "--dangerously-load-development-channels", "server:" + ChannelHost.ServerName]);
        }
        else if (McpServers.Count > 0)
        {
            File.WriteAllText(mcpPath, Claude.McpServers.Config(McpServers).ToJsonString(AcpJson.TextOptions));
            args.AddRange(["--mcp-config", mcpPath]);
        }
        var appended = string.Join("\n\n", new[] { agent.Channel is null ? null : ChannelHost.SystemPrompt, SystemPrompt }
            .Where(t => !string.IsNullOrEmpty(t)));
        if (appended.Length > 0) args.AddRange(["--append-system-prompt", appended]);
        args.AddRange(resume ? ["--resume", Id] : ["--session-id", Id]);
        args.AddRange(["--", prompt]);
        var commandLine = TerminalText.Quote(exe) + " " + TerminalText.JoinArguments(args);

        sawPrompt = false;
        acceptedChannelWarning = false;
        screen.Clear();
        var previous = pty;
        pty = null;
        previous?.Dispose();
        agent.Log($"claude {Id}: launching in {Cwd} ({(resume ? "resume" : "new")})");
        pty = ConPty.Start(commandLine, Cwd, LaunchEnvironment());
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
        var request = ClaudeHooks.TrustFolder(Id, Cwd);
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

    /// <param name="ct">Cancelled when Claude abandons the hook call (timeout, answer in the TUI, interrupt).</param>
    public async Task<JsonObject> OnHookAsync(JsonObject e, CancellationToken ct)
    {
        var name = ClaudeHooks.Name(e);
        if ((string?)e["transcript_path"] is { Length: > 0 } path) transcript?.Follow(path);
        if (name == "PermissionRequest") return await OnPermissionAsync(e, ct);
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
                resume = true; // the conversation now exists in Claude's own store
                var prompt = (string?)e["prompt"] ?? "";
                Turn? current;
                lock (stateGate) { current = turn; idle = idle.Task.IsCompleted ? NewSignal() : idle; }
                var promptId = (string?)e["prompt_id"];
                if (current is { Confirmed: false } && !ClaudeHooks.IsChannelMessage(prompt))
                {
                    current.Confirmed = true; // the client's own prompt: ACP does not echo it back
                    UserMessageId(promptId);  // its prompt_id is taken, even though the service names that message
                }
                else
                {
                    var meta = ClaudeHooks.IsChannelMessage(prompt) ? agent.Channel?.MetaFor(Id, prompt) : null;
                    await agent.EmitAsync(Id, ClaudeHooks.ForeignPrompt(prompt, UserMessageId(promptId), meta));
                }
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
                // Claude gives these texts no message ID of their own, so each is a new message.
                if (current is { HadText: false } && !string.IsNullOrEmpty(last))
                    await agent.EmitAsync(Id, ClaudeHooks.TextChunk(last, Guid.NewGuid().ToString()));
                if (name == "StopFailure")
                    await agent.EmitAsync(Id, ClaudeHooks.TextChunk($"\n[Claude stopped with an error: {(string?)e["error"] ?? (string?)e["reason"] ?? "unknown"}]",
                        Guid.NewGuid().ToString()));
                await agent.TouchAsync(Id);
                SetIdle();
                // Only a turn Claude has started ends here; a late Stop of an interrupted turn
                // must not end the prompt pasted after it.
                if (current is { Confirmed: true }) current.TryComplete(StopReason.EndTurn);
                break;
            }
            default:
                agent.Log($"claude {Id}: {name}");
                break;
        }
    }

    async Task<JsonObject> OnPermissionAsync(JsonObject e, CancellationToken hookAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hookAborted, lifetime.Token);
        var ct = linked.Token;
        var tool = (string?)e["tool_name"] ?? "";
        // Answering an opinion request and looking who is there need no approval; asking another
        // agent (ask_agent) does, as it does for every agent (see docs/service-protocol.md).
        if (ChannelHost.PreAllowedTools.Contains(tool)) return ClaudeHooks.AllowReply();
        string toolCallId;
        lock (stateGate)
            toolCallId = toolIds.TryGetValue(ToolKey(e), out var known) ? known : $"permission-{++permissionCount}";
        try
        {
            if (tool == "AskUserQuestion" && e["tool_input"] is JsonObject input)
            {
                if (!agent.ClientHasForms)
                    return ClaudeHooks.DenyReply("The user's client cannot show questions; ask in plain text instead.");
                var response = await agent.ElicitAsync(ClaudeHooks.QuestionForm(Id, toolCallId, input), ct);
                return ClaudeHooks.AnsweredInput(input, response) is { } answered
                    ? ClaudeHooks.AllowReply(answered)
                    : ClaudeHooks.DenyReply("The user chose not to answer.");
            }
            var decision = await agent.RequestPermissionAsync(ClaudeHooks.Permission(Id, toolCallId, e), ct);
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

    /// <summary>The ACP <c>messageId</c> of a user message Claude reports itself; see <see cref="ClaudeHooks.UserMessageId"/>.</summary>
    string UserMessageId(string? promptId)
    {
        lock (stateGate) return ClaudeHooks.UserMessageId(userMessageIds, promptId);
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask DisposeAsync()
    {
        lock (stateGate) turn?.TryComplete(StopReason.Cancelled);
        lifetime.Cancel();
        if (transcript is not null) await transcript.DisposeAsync();
        var running = pty;
        pty = null;
        if (running is not null) await Task.Run(running.Dispose);
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
