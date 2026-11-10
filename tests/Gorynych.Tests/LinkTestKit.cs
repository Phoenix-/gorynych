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
            if (method == AcpMethods.Agent.SessionCancel && (string?)p?["sessionId"] is { } sid && cancels.TryGetValue(sid, out var cancel)) cancel.Cancel();
            return Task.CompletedTask;
        };
        agent.Rpc.Start();
        Current = agent;
        var (ci, co) = link.ClientEnd;
        return Task.FromResult(new AgentInstance(ci, co, agent.Rpc.Completion, () => agent.Rpc.DisposeAsync()));
    }

    async Task<JsonNode?> HandleAsync(ScriptedAgent agent, JsonRpcRequest req)
    {
        switch (req.Method)
        {
            case AcpMethods.Agent.Initialize:
                return ScriptedAgent.Init();
            case AcpMethods.Agent.SessionNew:
                return AcpJson.ToNode(new NewSessionResponse { SessionId = $"{name}-{Interlocked.Increment(ref sessions)}" });
            case AcpMethods.Agent.SessionList:
                return AcpJson.ToNode(new ListSessionsResponse());
            case AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad or AcpMethods.Agent.SessionClose:
                return new JsonObject();
            case AcpMethods.Agent.SessionPrompt:
                var prompt = req.ParamsAs<PromptRequest>();
                var sid = prompt.SessionId;
                var text = prompt.Prompt.OfType<TextContent>().FirstOrDefault()?.Text ?? "";
                Prompts.Enqueue(text);
                using (var cancel = new CancellationTokenSource())
                {
                    cancels[sid] = cancel;
                    try
                    {
                        await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = $"started:{text} " } });
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

    public Wire(Conductor conductor, string name)
    {
        var up = new System.IO.Pipelines.Pipe();
        var down = new System.IO.Pipelines.Pipe();
        var linkIn = new BrokenOnce(down.Reader.AsStream(), reads: true);
        var linkOut = new BrokenOnce(up.Writer.AsStream());
        var serviceIn = new BrokenOnce(up.Reader.AsStream(), reads: true);
        var serviceOut = new BrokenOnce(down.Writer.AsStream());
        ends = [linkIn, linkOut, serviceIn, serviceOut];
        LinkEnd = (linkIn, linkOut);
        Serving = conductor.ServeClientAsync(serviceIn, serviceOut, name);
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
            try { return await inner.ReadAsync(buffer, both.Token); }
            catch (OperationCanceledException) when (broken.IsCancellationRequested && !ct.IsCancellationRequested) { return 0; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (broken.IsCancellationRequested) throw new IOException("the pipe is broken");
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

    (Stream, Stream) Connect()
    {
        var n = Interlocked.Increment(ref connects);
        var to = Service ?? throw new IOException("no service on the pipe");
        wire = new Wire(to, $"link-{n}");
        return wire.LinkEnd;
    }

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
