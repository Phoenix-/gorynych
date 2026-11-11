using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Acp.Link;

namespace Gorynych.Service.Remotes;

/// <summary>Timings of the connection to a remote service.</summary>
internal sealed class RemoteOptions
{
    /// <summary>The pauses before the attempts to connect again, from a few seconds to a few minutes; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>How long the remote's <c>initialize</c> may take: ssh logs in, and the bridge there may start its service.</summary>
    public TimeSpan Handshake { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a list waits for a remote's sessions; a later one's are left out of it.</summary>
    public TimeSpan ListWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an ID no remote had is not looked for in their lists again (unless a list changes first).</summary>
    public TimeSpan UnknownFor { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The connection to one remote service (docs/design.md, "Several machines"): its command (<c>ssh -T host
/// Gorynych.Service.exe acp</c>, the bridge there, unscoped) runs as long as this service does, through a
/// <see cref="ResumableLink"/>, which resumes the sessions the hub has open there after a drop. It starts again
/// after a drop, with a backoff, and after a link that ended. The hub (<see cref="RemoteHub"/>) is the link's
/// client: it speaks ACP and the extensions the remote announced, as one more client of that service.
/// </summary>
internal sealed class RemoteMachine : IAsyncDisposable
{
    readonly RemoteHub hub;
    readonly Func<CancellationToken, Task<(Stream Input, Stream Output)>> connect;
    readonly RemoteOptions options;
    readonly CancellationTokenSource stopping = new();
    Task? running;
    volatile JsonRpcConnection? rpc;
    int connected;
    string? lastError;
    DateTimeOffset since = DateTimeOffset.UtcNow;

    public RemoteMachine(RemoteEntry entry, RemoteHub hub, Func<CancellationToken, Task<(Stream Input, Stream Output)>>? connect, RemoteOptions options)
    {
        Entry = entry;
        this.hub = hub;
        this.options = options;
        this.connect = connect ?? (ct => StartAsync(entry, line => StderrLine(line), ct));
    }

    public RemoteEntry Entry { get; }

    public string Name => Entry.Name;

    /// <summary>Whether its service answers now: false while the link connects again.</summary>
    public bool Connected => Volatile.Read(ref connected) == 1;

    /// <summary>Since when it is connected, or not.</summary>
    public DateTimeOffset Since => since;

    /// <summary>Why it last failed to connect, or dropped; null once it is connected.</summary>
    public string? LastError => lastError;

    /// <summary>The remote's persistent service ID, once it answered.</summary>
    public string? ServiceId { get; private set; }

    /// <summary>The remote's own agents (its <c>gorynych/agents</c> without those it brings from its remotes: one hop).</summary>
    public IReadOnlyList<(string Name, string DisplayName)> Agents { get; private set; } = [];

    /// <summary>The remote's <c>agentCapabilities._meta</c>: the extensions it announced.</summary>
    public JsonObject Extensions { get; private set; } = [];

    /// <summary>Whether the remote announced the extension <paramref name="flag"/> (a <c>gorynych/…</c> capability).</summary>
    public bool Takes(string flag) => Extensions[flag] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<int>(out var n) && n > 0);

    /// <summary>The words for its sessions while it cannot be reached.</summary>
    public string NotConnected => $"{Name} is not connected" + (lastError is { } why ? $" ({why})" : "");

    public void Start() => running = Task.Run(RunAsync);

    /// <summary>A request to the remote service; fails at once when it is not connected.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (!Connected || rpc is not { } link) throw NotConnectedError();
        return await link.SendRequestAsync(method, @params, ct);
    }

    /// <summary>
    /// A request that may wait for the remote to come back (<c>session/close</c>, a detach): it goes through
    /// the link, which sends it when the connection is back; with no link at all, there is nothing to tell.
    /// </summary>
    public Task<JsonNode?> SendAsync(string method, JsonNode? @params) =>
        rpc is { } link ? link.SendRequestAsync(method, @params) : Task.FromResult<JsonNode?>(null);

    /// <summary>A notification to the remote (<c>session/cancel</c>); the link holds it while the connection is back on its way.</summary>
    public Task NotifyAsync(string method, JsonNode? @params) => rpc is { } link ? link.SendNotificationAsync(method, @params) : Task.CompletedTask;

    public JsonRpcException NotConnectedError() => new(JsonRpcException.InvalidRequest, NotConnected + ". Its sessions come back when it is reachable again.");

    async Task RunAsync()
    {
        var attempt = 0;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var first = await connect(stopping.Token);
                if (await RunLinkAsync(first)) attempt = 0; // it was connected: the next try comes soon again
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (SelfException e)
            {
                SetState(false, e.Message);
                ServiceLog.Warn($"remote {Name}: {e.Message}; not connecting to it again until remotes.json changes");
                return;
            }
            catch (Exception e)
            {
                SetState(false, lastErrorLine ?? e.Message);
                ServiceLog.Info($"remote {Name}: {lastError}");
            }
            var delay = options.Backoff[Math.Min(attempt++, options.Backoff.Count - 1)];
            try { await Task.Delay(delay, stopping.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs one link to the remote until it ends; true when it got as far as connected.</summary>
    async Task<bool> RunLinkAsync((Stream Input, Stream Output) first)
    {
        var duplex = new DuplexLink();
        var (linkIn, linkOut) = duplex.AgentEnd;
        var link = new ResumableLink(linkIn, linkOut, connect, new ResumableLinkOptions
        {
            Log = m => ServiceLog.Info($"remote {Name}: {m}"),
            RetryDelays = options.Backoff,
            GiveUpAfter = TimeSpan.MaxValue,
            // Its clients' links (Zed's bridges) follow the marks of their own prompts' ends.
            PassesMarks = true,
            HandshakeTimeout = options.Handshake,
            ConnectionChanged = up =>
            {
                SetState(up, up ? null : lastErrorLine ?? "the connection dropped");
                hub.OnConnectionChanged(this, up);
            },
            // The remote that came back may be another build (an update) with other agents and extensions,
            // or no longer the same service: its answer is taken again, as the first one was.
            CameBack = answer =>
            {
                try
                {
                    Take(answer);
                }
                catch (SelfException e)
                {
                    refused = e;
                    return e.Message;
                }
                ServiceLog.Info($"remote {Name}: back as service {ServiceId}; agents {string.Join(", ", Agents.Select(a => a.Name))}");
                hub.OnAgentsChanged();
                return null;
            },
        });
        lastErrorLine = null;
        var linkRun = link.RunAsync(first);
        var (hubIn, hubOut) = duplex.ClientEnd;
        var client = new JsonRpcConnection(hubIn, hubOut, $"remote {Name}")
        {
            Log = m => ServiceLog.Warn(m),
        };
        client.RequestHandler = (request, ct) => hub.OnRemoteRequestAsync(this, request, ct);
        client.NotificationHandler = (method, @params) => hub.OnRemoteNotificationAsync(this, method, @params);
        client.Start();
        var reached = false;
        try
        {
            var init = await client.SendRequestAsync(AcpMethods.Agent.Initialize, AcpJson.ToNode(new InitializeRequest
            {
                ProtocolVersion = AcpMethods.ProtocolVersion,
                ClientInfo = new Implementation { Name = "gorynych-hub", Title = "Gorynych hub", Version = AgentHost.Version },
                ClientCapabilities = new ClientCapabilities
                {
                    // The hub relays forms and notices to its own clients, and translates a form for one without them.
                    Elicitation = new ElicitationCapabilities { Form = new ElicitationFormCapabilities() },
                    Session = new ClientSessionCapabilities { Notices = new NoticeCapabilities() },
                },
                // One hop: the remote answers a hub with its own sessions and agents only.
                Meta = new JsonObject { [GorynychProtocol.MetaHub] = true },
            }), stopping.Token).WaitAsync(options.Handshake, stopping.Token);
            Take(init as JsonObject);
            rpc = client;
            reached = true;
            SetState(true, null);
            ServiceLog.Info($"remote {Name}: connected to service {ServiceId}; agents {string.Join(", ", Agents.Select(a => a.Name))}");
            hub.OnConnected(this);
            await Task.WhenAny(linkRun, client.Completion);
            if (refused is { } self) throw self;
        }
        finally
        {
            rpc = null;
            if (Connected) SetState(false, lastErrorLine ?? "the link to it ended");
            await client.DisposeAsync();
            try { duplex.ClientEnd.Output.Dispose(); } catch { }
            try { await linkRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            hub.OnConnectionChanged(this, false);
        }
        return reached;
    }

    /// <summary>What the remote answered to <c>initialize</c>: who it is, its agents, its extensions.</summary>
    void Take(JsonObject? init)
    {
        var meta = init?["_meta"] as JsonObject;
        ServiceId = (string?)meta?[GorynychProtocol.MetaServiceId];
        if (ServiceId is not null && ServiceId == hub.OwnServiceId)
            throw new SelfException($"{Name} is this service itself (service {ServiceId}): a service does not list its own sessions twice");
        // One hop: a remote's own agents only, never those it shows from its own remotes.
        Agents = (meta?[GorynychProtocol.MetaAgents] as JsonArray ?? []).OfType<JsonObject>()
            .Select(a => ((string?)a["name"] ?? "", (string?)a["displayName"] ?? (string?)a["name"] ?? ""))
            .Where(a => a.Item1.Length > 0 && !a.Item1.Contains('@')).ToList();
        Extensions = (init?["agentCapabilities"]?["_meta"] as JsonObject)?.DeepClone().AsObject() ?? [];
    }

    void SetState(bool up, string? error)
    {
        var was = Interlocked.Exchange(ref connected, up ? 1 : 0) == 1;
        if (was != up) since = DateTimeOffset.UtcNow;
        if (!up && error is not null) lastError = error;
        if (up) lastError = null;
    }

    string? lastErrorLine;

    /// <summary>What the remote's command says on stderr (ssh's errors, the bridge's own words): logged, and the last line kept as the reason it failed.</summary>
    void StderrLine(string line)
    {
        if (line.Length == 0) return;
        lastErrorLine = line.StartsWith("gorynych acp: ", StringComparison.Ordinal) ? line["gorynych acp: ".Length..] : line;
        ServiceLog.Info($"remote {Name}: {line}");
    }

    /// <summary>Starts the remote's command; its output and input are the connection, and ending the connection ends the process.</summary>
    static Task<(Stream Input, Stream Output)> StartAsync(RemoteEntry entry, Action<string> stderr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(entry.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in entry.Args) psi.ArgumentList.Add(arg);
        foreach (var (name, value) in entry.Env) psi.Environment[name] = value;
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new IOException($"{entry.Command} did not start");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException($"cannot run {entry.Command}: {e.Message}", e);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line) stderr(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        Stream output = new ProcessStream(process.StandardOutput.BaseStream, process);
        return Task.FromResult((output, process.StandardInput.BaseStream));
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if (rpc is { } link) await link.DisposeAsync();
        if (running is not null) try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
    }

    sealed class SelfException(string message) : Exception(message);

    SelfException? refused; // what came back is this service itself

    /// <summary>The remote command's output, which ends its process when the link lets the connection go.</summary>
    sealed class ProcessStream(Stream inner, Process process) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { inner.Dispose(); } catch { }
                process.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
