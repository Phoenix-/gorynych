using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Hosts;

if (args is [var hostMode, var pipe, var cwd, var executable, var ledgerPath, var slot] && hostMode is "completion-host" or "peer-host")
{
    // Isolated fixture: an actual AdapterHost and headless adapter in this OS process.
    var nativePilot = false;
    var processes = new List<Gorynych.Adapters.Claude.ClaudeProcess>(); var processGate = new object();
    var adapter = new Gorynych.Adapters.Claude.ClaudeAdapter(new Gorynych.Adapters.Claude.ClaudeAdapterOptions
    {
        DataDirectory = Path.Combine(cwd, "claude-host"), Executable = executable, FetchCatalog = false,
        ProjectsDirectory = nativePilot ? null : Path.Combine(Path.GetTempPath(), "gorynych-fake-claude", "projects"),
        ClaudeStatePath = nativePilot ? null : Path.Combine(cwd, "no-claude-state.json"), HostedPromptObservation = true,
        HostedCompletionGuarded = true, HostedPeerTerminal = hostMode == "peer-host" && !File.Exists(Path.Combine(cwd, "disable-peer-cap")), DisableForceKillForTests = true,
        PeerTerminalWireOverrideForTests = hostMode == "peer-host" && File.Exists(Path.Combine(cwd, "malformed-peer-wire"))
            ? marker => { marker["nativeUuid"] = "invalid"; return marker; } : null,
        GuardedReceiptLimitForTests = File.Exists(Path.Combine(cwd, "small-guarded-cap")) ? 2 : 64,
        BeforeGuardedWriteForTests = async () =>
        {
            if (!File.Exists(Path.Combine(cwd, "hold-guarded-write"))) return;
            File.WriteAllText(Path.Combine(cwd, "guarded-write-entered"), "");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(cwd, "release-guarded-write"))) await Task.Delay(20, timeout.Token);
        },
        NativeProcessStartedForTests = p => { lock (processGate) processes.Add(p); },
        ChannelServerCommand = nativePilot ? null : ["gorynych-peers-not-started"], StartupTimeoutSeconds = 20, StallMs = nativePilot ? int.MaxValue : 1500,
        CloseTimeoutMs = 3000, InterruptGraceMs = 3000,
    });
    var processAgent = await adapter.StartAsync(CancellationToken.None);
    async Task<(Stream Input, Stream Output)> Connect(CancellationToken ct)
    {
        var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        try { await client.ConnectAsync(ct); return (client, client); }
        catch { await client.DisposeAsync(); throw; }
    }
    void FailOnce(string name)
    {
        var marker = Path.Combine(cwd, name);
        if (!File.Exists(marker)) return;
        File.Delete(marker);
        throw new IOException("Injected test-only host ledger fault");
    }
    var processHost = new AdapterHost(processAgent, Connect, new AdapterHostOptions
    {
        Adapter = "claude", Token = "0123456789abcdef0123456789abcdef-test", Slot = slot,
        Generation = new HostGeneration("build-1", []),
        CompletionHostLedger = hostMode == "completion-host" ? new CompletionHostLedger(ledgerPath) : null,
        ReportStore = hostMode == "peer-host" ? new ReportStore(Path.Combine(ledgerPath, "reports")) : null,
        PeerTerminal = hostMode == "peer-host" && !File.Exists(Path.Combine(cwd, "disable-peer-cap")),
        CaptureOpened = hostMode == "peer-host" && File.Exists(Path.Combine(cwd, "fail-peer-capture")) ? capture =>
        {
            var write = capture.WriteFrame;
            capture.WriteFrame = frame => { if (frame[0] == 3) throw new IOException("Injected peer capture seal failure"); write(frame); };
        } : null,
        BeforeProducerEvidence = hostMode == "peer-host" && File.Exists(Path.Combine(cwd, "fail-peer-evidence")) ? () =>
        {
            File.WriteAllText(Path.Combine(cwd, "peer-evidence-failed"), "");
            throw new IOException("Injected peer producer evidence failure");
        } : null,
        BeforeCompletionAckRecordForTests = () => FailOnce("fail-completion-ack-record"),
        BeforeCompletionRetireRecordForTests = () => FailOnce("fail-completion-retire-record"),
        BeforeCompletionHandlerForTests = async method =>
        {
            if (method != GorynychProtocol.MethodHostCompletionGuardedAck || !File.Exists(Path.Combine(cwd, "hold-ack-before-handler"))) return;
            File.WriteAllText(Path.Combine(cwd, "ack-before-handler-entered"), "");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(cwd, "release-ack-before-handler"))) await Task.Delay(20, timeout.Token);
        },
        AfterCompletionHandlerForTests = async method =>
        {
            var kind = method == GorynychProtocol.MethodHostCompletionGuardedStatus ? "status" :
                method == GorynychProtocol.MethodHostCompletionGuardedAck ? "ack" : null;
            if (kind is null || !File.Exists(Path.Combine(cwd, $"hold-{kind}-handler")) ||
                File.Exists(Path.Combine(cwd, $"{kind}-handler-entered"))) return;
            File.WriteAllText(Path.Combine(cwd, $"{kind}-handler-entered"), "");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(cwd, $"release-{kind}-handler"))) await Task.Delay(20, timeout.Token);
        },
        AfterCompletionHandlerFinallyForTests = method =>
        {
            if (method == GorynychProtocol.MethodHostCompletionGuardedStatus && File.Exists(Path.Combine(cwd, "status-handler-entered")))
                File.WriteAllText(Path.Combine(cwd, "status-handler-finished"), "");
        },
        RetryDelays = [TimeSpan.FromMilliseconds(50)],
    });
    Console.WriteLine("HOST_STARTED");
    _ = Task.Run(async () =>
    {
        while (await Console.In.ReadLineAsync() is { } command)
        {
            if (command == "STOP_HOST" && hostMode == "peer-host")
            {
                Console.WriteLine("HOST_STOPPING");
                processHost.StopForTests();
                return;
            }
            if (command != "CLOSE_NATIVE") continue;
            Gorynych.Adapters.Claude.ClaudeProcess[] owned; lock (processGate) owned = processes.ToArray();
            var ok = true;
            foreach (var process in owned)
            {
                try { if (!process.Exited.IsCompleted && !await process.CloseInputAsync(TimeSpan.FromSeconds(5))) { ok = false; continue; }
                    await process.Exited.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception e) when (e is TimeoutException or ObjectDisposedException or InvalidOperationException) { ok = false; }
            }
            Console.WriteLine(ok ? "NATIVE_EXITED" : "NATIVE_STILL_RUNNING");
        }
    });
    var result = await processHost.RunAsync();
    await processAgent.DisposeAsync();
    Console.WriteLine("HOST_EXITED");
    Environment.ExitCode = result;
    return;
}

if (args is ["wait-for-exit"])
{
    await Console.In.ReadLineAsync();
    return;
}

var data = args[0]; var hostId = args[1]; var session = args[2]; var promptId = args[3]; var mode = args[4];
if (mode is "note-settled" or "note-unsettled")
{
    Console.WriteLine("READY");
    var request = JsonNode.Parse(await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("missing note claim"))!;
    var report = (string)request["reportId"]!; var attempt = (string)request["attemptId"]!;
    var generation = new HostGeneration("note-probe", []);
    var wire = new DuplexLink(); var fake = new JsonRpcConnection(wire.AgentEnd.Input, wire.AgentEnd.Output, "note-fake");
    fake.RequestHandler = (call, _) =>
    {
        if (call.Method == AcpMethods.Agent.Initialize) return Task.FromResult<JsonNode?>(new JsonObject { ["protocolVersion"] = 1,
            ["agentCapabilities"] = new JsonObject(), ["_meta"] = new JsonObject { [GorynychProtocol.CapReportNote] = true, [GorynychProtocol.CapReportNoteAck] = true } });
        if (call.Method == GorynychProtocol.MethodReportNote)
        {
            File.AppendAllText(Path.Combine(data, "probe-note-inputs"), "one\n");
            var facts = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt, ["nativeMessageId"] = Guid.NewGuid().ToString(),
                ["revision"] = 1L, ["state"] = mode == "note-settled" ? "cycle_result" : "written", ["writeStarted"] = true,
                ["writeCompleted"] = true, ["taken"] = mode == "note-settled", ["cycleResult"] = mode == "note-settled" ? "ended" : null };
            return Task.FromResult<JsonNode?>(new JsonObject { ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = facts } });
        }
        return Task.FromResult<JsonNode?>(new JsonObject());
    };
    fake.Start();
    var agent = new AgentInstance(wire.ClientEnd.Input, wire.ClientEnd.Output, fake.Completion, () => fake.DisposeAsync());
    var link = new DuplexLink(); var controller = new JsonRpcConnection(link.AgentEnd.Input, link.AgentEnd.Output, "note-controller"); controller.Start();
    var actualHost = new AdapterHost(agent, _ => Task.FromResult<(Stream, Stream)>((link.ClientEnd.Input, link.ClientEnd.Output)),
        new AdapterHostOptions { Adapter = "probe", Token = "test", HostId = hostId, Generation = generation, ReportStore = new ReportStore(data) });
    _ = actualHost.RunAsync();
    await controller.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 });
    await controller.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() });
    await controller.SendRequestAsync(GorynychProtocol.MethodHostReportNote, new JsonObject { ["sessionId"] = session, ["text"] = "probe note",
        ["_meta"] = new JsonObject { [GorynychProtocol.MetaReportNote] = new JsonObject { ["reportId"] = report, ["attemptId"] = attempt } } });
    Console.WriteLine("NOTE_WRITTEN");
    await Console.In.ReadLineAsync();
    Console.WriteLine("SELF_EXIT");
    Environment.Exit(43); // owned test host exits without AdapterHost cleanup
    return;
}
Console.WriteLine("READY");
var reportId = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("missing report ID");
var nativeWire = new DuplexLink();
var native = new JsonRpcConnection(nativeWire.AgentEnd.Input, nativeWire.AgentEnd.Output, "probe-native");
var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
native.RequestHandler = async (request, _) =>
{
    if (request.Method == AcpMethods.Agent.Initialize) return new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject() };
    if (request.Method == AcpMethods.Agent.SessionPrompt)
    {
        await native.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject { ["sessionId"] = session,
            ["update"] = new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "process-output-😀" } } });
        nativeStarted.TrySetResult();
        if (mode == "unsealed") await never.Task;
        if (mode == "gap-failed") throw new JsonRpcException(JsonRpcException.InternalError, "native failure at seal");
        return new JsonObject { ["stopReason"] = mode == "gap-cancelled" ? "cancelled" : "end_turn" };
    }
    return new JsonObject();
};
native.Start();
var instance = new AgentInstance(nativeWire.ClientEnd.Input, nativeWire.ClientEnd.Output, native.Completion, () => native.DisposeAsync());
var serviceWire = new DuplexLink();
var conductor = new JsonRpcConnection(serviceWire.AgentEnd.Input, serviceWire.AgentEnd.Output, "probe-conductor"); conductor.Start();
var host = new AdapterHost(instance, _ => Task.FromResult<(Stream, Stream)>((serviceWire.ClientEnd.Input, serviceWire.ClientEnd.Output)),
    new AdapterHostOptions { Adapter = "probe", Token = "test", HostId = hostId, ReportStore = new ReportStore(data),
        BeforeProducerEvidence = mode.StartsWith("gap-", StringComparison.Ordinal) ? () =>
        {
            Console.WriteLine("SEALED_NO_EVIDENCE");
            // Deliberate self-exit of this test-only process: no managed finally/Dispose runs.
            Environment.Exit(42);
        } : null,
        CaptureOpened = mode == "failed" ? capture =>
        {
            var original = capture.WriteFrame;
            capture.WriteFrame = frame => { if (frame[0] == 3) throw new IOException("injected host seal failure"); original(frame); };
        } : null });
var run = host.RunAsync();
await conductor.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1 });
await conductor.SendRequestAsync(GorynychProtocol.MethodHostReplay, new JsonObject { ["after"] = new JsonObject() });
var prompt = conductor.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject { ["sessionId"] = session,
    ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "work" }),
    ["_meta"] = new JsonObject { [GorynychProtocol.MetaPromptId] = promptId, [GorynychProtocol.MetaReportId] = reportId } });
await nativeStarted.Task;
if (mode == "unsealed")
{
    Console.WriteLine("STARTED");
    await Console.In.ReadLineAsync();
}
else await prompt;
await conductor.SendRequestAsync(GorynychProtocol.MethodHostStop, new JsonObject());
await run;
never.TrySetResult();
await conductor.DisposeAsync();
Console.WriteLine("EXITED");
