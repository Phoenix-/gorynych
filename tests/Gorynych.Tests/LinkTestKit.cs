using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Acp.Link;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// An agent whose turns a test holds open. A prompt sends <c>started:&lt;text&gt;</c>, runs
/// <see cref="During"/>, waits for <see cref="Release"/> while <see cref="Hold"/> is set (or ends
/// <c>cancelled</c> on the session's cancel), sends <c>done:&lt;text&gt;</c> and ends.
/// </summary>
sealed class TurnAgent(string name) : IAgentAdapter
{
    readonly ConcurrentDictionary<string, CancellationTokenSource> cancels = new(StringComparer.Ordinal);
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int sessions;

    public string Name => name;
    public string DisplayName => name;
    public ConcurrentQueue<string> Prompts { get; } = new();
    public ScriptedAgent? Current { get; private set; }
    public bool Hold { get; set; }
    public Func<ScriptedAgent, string, CancellationToken, Task>? During { get; set; }

    /// <summary>No <c>started:</c> chunk: the turn sends nothing until it ends.</summary>
    public bool Quiet { get; set; }

    /// <summary>The params of every <c>session/resume</c> the service sent.</summary>
    public ConcurrentQueue<JsonObject> Resumes { get; } = new();

    /// <summary>When set, <c>session/list</c> and <c>session/new</c> wait for it.</summary>
    public TaskCompletionSource? SlowGate { get; set; }

    /// <summary>When set, <c>session/resume</c> waits for it.</summary>
    public TaskCompletionSource? ResumeGate { get; set; }

    /// <summary>How many <c>session/list</c> and <c>session/new</c> requests came.</summary>
    public int Lists;
    public int News;

    /// <summary>How many <c>session/cancel</c> notifications came.</summary>
    public int Cancels;

    /// <summary>It takes <c>session/close</c>, as every real adapter does; without it a session cannot leave a host on its own.</summary>
    public bool Closes { get; set; }

    /// <summary>How long a close takes (Codex answers once the app-server has let go of the thread).</summary>
    public TimeSpan CloseDelay { get; set; }

    /// <summary>What each instance was asked, in order: <c>verb:instance:sessionId</c>, the instance its number among <see cref="Instances"/>.</summary>
    public ConcurrentQueue<string> Events { get; } = new();

    /// <summary>Every instance started, the first first: each host runs one.</summary>
    public List<ScriptedAgent> Instances { get; } = [];

    readonly ConcurrentDictionary<string, ScriptedAgent> openIn = new(StringComparer.Ordinal);

    /// <summary>The instance that opened the session last.</summary>
    public ScriptedAgent AgentOf(string sessionId) => openIn[sessionId];

    /// <summary>The number of the instance that opened the session last.</summary>
    public int InstanceOf(string sessionId)
    {
        lock (Instances) return Instances.IndexOf(openIn[sessionId]);
    }

    public string[] EventsOf(string sessionId) => Events.Where(e => e.EndsWith(":" + sessionId, StringComparison.Ordinal)).ToArray();

    void Note(ScriptedAgent agent, string verb, string sessionId)
    {
        int index;
        lock (Instances) index = Instances.IndexOf(agent);
        Events.Enqueue($"{verb}:{index}:{sessionId}");
    }

    /// <summary>The prompts held now end, and later ones do not wait.</summary>
    public void Release() => release.TrySetResult();

    public Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        var link = new DuplexLink();
        var (ai, ao) = link.AgentEnd;
        var agent = new ScriptedAgent(new JsonRpcConnection(ai, ao, name + "-agent"));
        agent.Rpc.RequestHandler = (req, token) => HandleAsync(agent, req);
        agent.Rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Agent.SessionCancel) Interlocked.Increment(ref Cancels);
            if (method == AcpMethods.Agent.SessionCancel && (string?)p?["sessionId"] is { } sid && cancels.TryGetValue(sid, out var cancel)) cancel.Cancel();
            return Task.CompletedTask;
        };
        agent.Rpc.Start();
        Current = agent;
        lock (Instances) Instances.Add(agent);
        var (ci, co) = link.ClientEnd;
        return Task.FromResult(new AgentInstance(ci, co, agent.Rpc.Completion, () => agent.Rpc.DisposeAsync()));
    }

    async Task<JsonNode?> HandleAsync(ScriptedAgent agent, JsonRpcRequest req)
    {
        switch (req.Method)
        {
            case AcpMethods.Agent.Initialize:
                return ScriptedAgent.Init(close: Closes);
            case AcpMethods.Agent.SessionNew:
                Interlocked.Increment(ref News);
                if (SlowGate is { } slowNew) await slowNew.Task;
                var created = $"{name}-{Interlocked.Increment(ref sessions)}";
                openIn[created] = agent;
                Note(agent, "new", created);
                return AcpJson.ToNode(new NewSessionResponse { SessionId = created });
            case AcpMethods.Agent.SessionList:
                Interlocked.Increment(ref Lists);
                if (SlowGate is { } slowList) await slowList.Task;
                return AcpJson.ToNode(new ListSessionsResponse());
            case AcpMethods.Agent.SessionResume:
                if (req.Params is JsonObject resumed)
                {
                    Resumes.Enqueue((JsonObject)resumed.DeepClone());
                    if ((string?)resumed["sessionId"] is { } back)
                    {
                        openIn[back] = agent;
                        Note(agent, "resume", back);
                    }
                }
                if (ResumeGate is { } slowResume) await slowResume.Task;
                return new JsonObject();
            case AcpMethods.Agent.SessionClose:
                var closing = (string?)req.Params?["sessionId"] ?? "";
                Note(agent, "close", closing);
                if (CloseDelay > TimeSpan.Zero) await Task.Delay(CloseDelay);
                Note(agent, "closed", closing);
                return new JsonObject();
            case AcpMethods.Agent.SessionLoad:
                return new JsonObject();
            case AcpMethods.Agent.SessionPrompt:
                var prompt = req.ParamsAs<PromptRequest>();
                var sid = prompt.SessionId;
                var text = prompt.Prompt.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
                Prompts.Enqueue(text);
                Note(agent, "prompt", sid);
                using (var cancel = new CancellationTokenSource())
                {
                    cancels[sid] = cancel;
                    try
                    {
                        if (!Quiet) await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"started:{text} " } });
                        if (During is { } during) await during(agent, sid, cancel.Token);
                        if (Hold) await release.Task.WaitAsync(cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.Cancelled });
                    }
                    finally
                    {
                        cancels.TryRemove(sid, out _);
                    }
                }
                await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"done:{text} " } });
                return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
            default:
                throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
        }
    }
}

/// <summary>One connection of a link to a conductor, which a test can break as a dropped pipe breaks.</summary>
sealed class Wire
{
    readonly BrokenOnce[] ends;
    readonly BrokenOnce linkIn;
    readonly BrokenOnce serviceIn;

    /// <param name="said">Gets what the service writes to the link.</param>
    /// <param name="sent">Gets what the link writes to the service.</param>
    /// <param name="host">The conductor takes it as an adapter host's connection, not a client's.</param>
    public Wire(Conductor conductor, string name, Action<string>? said = null, Action<string>? sent = null, bool host = false)
    {
        var up = new System.IO.Pipelines.Pipe();
        var down = new System.IO.Pipelines.Pipe();
        linkIn = new BrokenOnce(down.Reader.AsStream(), reads: true);
        var linkOut = new BrokenOnce(up.Writer.AsStream()) { Tap = sent };
        serviceIn = new BrokenOnce(up.Reader.AsStream(), reads: true);
        var serviceOut = new BrokenOnce(down.Writer.AsStream()) { Tap = said };
        ends = [linkIn, linkOut, serviceIn, serviceOut];
        LinkEnd = (linkIn, linkOut);
        Serving = host ? conductor.ServeHostAsync(serviceIn, serviceOut, name) : conductor.ServeClientAsync(serviceIn, serviceOut, name);
    }

    /// <summary>The service reads nothing more until <see cref="Go"/>: what the link sends waits in the pipe.</summary>
    public void PauseService() => serviceIn.Pause();

    /// <summary>The link reads nothing more until <see cref="Go"/>: what the service sends waits in the pipe.</summary>
    public void PauseLink() => linkIn.Pause();

    public void Go()
    {
        serviceIn.Go();
        linkIn.Go();
    }

    public (Stream Input, Stream Output) LinkEnd { get; }
    public Task Serving { get; }

    /// <summary>
    /// Both directions end, as when the pipe or the process at the other end goes: nothing written
    /// from now on arrives, and both readers see the end, whatever was on its way.
    /// </summary>
    public void Break()
    {
        foreach (var end in ends) end.Break();
    }

    /// <summary>A stream that, once broken, fails every write and ends every read.</summary>
    sealed class BrokenOnce(Stream inner, bool reads = false) : Stream
    {
        readonly CancellationTokenSource broken = new();
        volatile TaskCompletionSource? paused;

        public void Pause() => paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Go() => Interlocked.Exchange(ref paused, null)?.TrySetResult();

        public void Break()
        {
            broken.Cancel();
            if (!reads) try { inner.Dispose(); } catch (Exception e) when (e is InvalidOperationException or IOException) { }
        }

        public override bool CanRead => reads;
        public override bool CanSeek => false;
        public override bool CanWrite => !reads;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (broken.IsCancellationRequested) return 0;
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, broken.Token);
            try
            {
                var n = await inner.ReadAsync(buffer, both.Token);
                // What comes while paused is held, and lost if the pipe breaks meanwhile.
                if (paused is { } wait) await wait.Task.WaitAsync(both.Token);
                return broken.IsCancellationRequested ? 0 : n;
            }
            catch (OperationCanceledException) when (broken.IsCancellationRequested && !ct.IsCancellationRequested) { return 0; }
        }

        public Action<string>? Tap { get; init; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (broken.IsCancellationRequested) throw new IOException("the pipe is broken");
            Tap?.Invoke(Encoding.UTF8.GetString(buffer.Span));
            await inner.WriteAsync(buffer, ct);
            if (broken.IsCancellationRequested) throw new IOException("the pipe is broken");
        }

        public override Task FlushAsync(CancellationToken ct) =>
            broken.IsCancellationRequested ? Task.FromException(new IOException("the pipe is broken")) : inner.FlushAsync(ct);

        protected override void Dispose(bool disposing)
        {
            if (disposing) try { inner.Dispose(); } catch (Exception e) when (e is InvalidOperationException or IOException) { }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A plain ACP client ("Zed") on a <see cref="ResumableLink"/> to a conductor that a test can take
/// away (<see cref="Service"/> null: no pipe), replace (a restart) or cut off (<see cref="Break"/>).
/// </summary>
sealed class LinkHarness : IAsyncDisposable
{
    readonly DuplexLink zedLink = new();
    Wire? wire;
    int connects;

    public LinkHarness(Conductor service, ResumableLinkOptions? options = null)
    {
        Service = service;
        Zed = new JsonRpcConnection(zedLink.ClientEnd.Input, zedLink.ClientEnd.Output, "zed");
        Zed.Log = m => { lock (Said) Said.AppendLine("zed: " + m); };
        Zed.NotificationHandler = (method, p) =>
        {
            lock (Notices) Notices.Add(method);
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u) lock (Updates) Updates.Add(u);
            return Task.CompletedTask;
        };
        Zed.RequestHandler = async (req, ct) =>
        {
            if (req.Method != AcpMethods.Client.SessionRequestPermission) throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            var r = req.ParamsAs<RequestPermissionRequest>();
            lock (Approvals) Approvals.Add(r);
            var option = Approve is null ? r.Options[0].OptionId : await Approve(r, ct);
            return AcpJson.ToNode(new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = option } });
        };
        Zed.Start();
        Link = new ResumableLink(zedLink.AgentEnd.Input, zedLink.AgentEnd.Output, ConnectAsync, options ?? new ResumableLinkOptions
        {
            Log = m => { lock (Said) Said.AppendLine(m); },
            RetryDelays = [TimeSpan.FromMilliseconds(50)],
            GiveUpAfter = TimeSpan.FromSeconds(15),
            InitializeMeta = new JsonObject { [GorynychProtocol.MetaAgent] = "turns" },
        });
        Running = Link.RunAsync(Connect());
    }

    public JsonRpcConnection Zed { get; }
    public ResumableLink Link { get; }
    public Task<int> Running { get; }

    /// <summary>The conductor a connection reaches now; null: there is no pipe.</summary>
    public Conductor? Service { get; set; }

    /// <summary>While set, a new connection waits for it.</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    public Func<RequestPermissionRequest, CancellationToken, Task<string>>? Approve { get; set; }
    public List<string> Notices { get; } = [];
    public List<JsonObject> Updates { get; } = [];
    public List<RequestPermissionRequest> Approvals { get; } = [];

    /// <summary>What the link and Zed's connection said.</summary>
    public StringBuilder Said { get; } = new();

    public int Connects => Volatile.Read(ref connects);

    /// <summary>Everything the services wrote to the link.</summary>
    public StringBuilder ServiceSaid { get; } = new();

    public string Heard()
    {
        lock (ServiceSaid) return ServiceSaid.ToString();
    }

    /// <summary>Everything the link wrote to the services.</summary>
    public StringBuilder LinkSent { get; } = new();

    public string Sent()
    {
        lock (LinkSent) return LinkSent.ToString();
    }

    (Stream, Stream) Connect()
    {
        var n = Interlocked.Increment(ref connects);
        var to = Service ?? throw new IOException("no service on the pipe");
        wire = new Wire(to, $"link-{n}", s => { lock (ServiceSaid) ServiceSaid.Append(s); }, s => { lock (LinkSent) LinkSent.Append(s); });
        return wire.LinkEnd;
    }

    /// <summary>The current connection, for pausing one direction.</summary>
    public Wire Current => wire!;

    async Task<(Stream, Stream)> ConnectAsync(CancellationToken ct)
    {
        if (ConnectGate is { } gate) await gate.Task.WaitAsync(ct);
        return Connect();
    }

    /// <summary>The current connection drops.</summary>
    public void Break() => wire?.Break();

    public Task<JsonNode?> InitializeAsync() =>
        Zed.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() });

    public async Task<string> NewSessionAsync() =>
        (string)(await Zed.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }))!["sessionId"]!;

    public Task<JsonNode?> PromptAsync(string sessionId, string text) => Zed.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
    {
        ["sessionId"] = sessionId,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    });

    /// <summary>The agent's text Zed got, in order.</summary>
    public string Text()
    {
        lock (Updates) return string.Concat(Updates.Where(u => (string?)u["sessionUpdate"] == "agent_message_chunk").Select(u => (string?)u["content"]?["text"]));
    }

    public string[] Kinds()
    {
        lock (Updates) return Updates.Select(u => (string)u["sessionUpdate"]!).ToArray();
    }

    public string Log()
    {
        lock (Said) return Said.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await Zed.DisposeAsync();
        try { await Running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        Break();
    }

    public static async Task Until(Func<bool> condition, string what, int ms = 10_000)
    {
        for (var waited = 0; waited < ms; waited += 20)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"timed out waiting for {what}");
    }
}
