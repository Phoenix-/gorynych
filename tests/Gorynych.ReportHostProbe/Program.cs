using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;
using Gorynych.Service.Hosts;

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
