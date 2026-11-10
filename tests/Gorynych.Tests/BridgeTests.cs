using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The stdio bridge (<c>Gorynych.Service acp --agent X</c>) driven the way a plain ACP client
/// such as Zed drives an agent: no <c>_meta</c>, no forms, no fs, no terminal.
/// </summary>
[TestClass]
public sealed class BridgeTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    internal static string ServiceExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin"), TestDirs.Exe("Gorynych.Service"), SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task PlainClient_GetsTheScopedAgent_AFormAsAnApproval_AndHistory()
    {
        var elicited = new List<string>();
        var fake = new ScriptedAdapter("fake", async (agent, req, ct) =>
        {
            switch (req.Method)
            {
                case AcpMethods.Agent.Initialize: return ScriptedAgent.Init();
                case AcpMethods.Agent.SessionList:
                    return AcpJson.ToNode(new ListSessionsResponse { Sessions = [new SessionInfo { SessionId = "fake-1", Cwd = Path.GetTempPath() }] });
                case AcpMethods.Agent.SessionNew: return AcpJson.ToNode(new NewSessionResponse { SessionId = "fake-1" });
                case AcpMethods.Agent.SessionResume: return new JsonObject();
                case AcpMethods.Agent.SessionPrompt:
                    var sid = req.ParamsAs<PromptRequest>().SessionId;
                    await agent.Update(sid, new Plan { Entries = [new PlanEntry { Content = "Look", Priority = PlanEntryPriority.High, Status = PlanEntryStatus.InProgress }] });
                    await agent.Update(sid, new ToolCall { ToolCallId = "t1", Title = "Read README.md", Kind = ToolKind.Read, Status = ToolCallStatus.Pending });
                    await agent.Update(sid, new SessionUpdateToolCallUpdate { ToolCallId = "t1", Status = ToolCallStatus.Completed });
                    await agent.Update(sid, new UsageUpdate { Used = 1200, Size = 200000 });
                    // A question, as the Claude adapter asks it: a form with one choice field.
                    var r = await agent.Rpc.RequestAsync<CreateElicitationRequest, CreateElicitationResponse>(AcpMethods.Client.ElicitationCreate,
                        new ElicitationFormMode
                        {
                            Message = "Which color?",
                            SessionId = sid,
                            ToolCallId = "t2",
                            RequestedSchema = new ElicitationSchema
                            {
                                Type = ElicitationSchemaType.Object,
                                Properties = new()
                                {
                                    ["q1"] = new StringPropertySchema
                                    {
                                        Title = "Color", Description = "Which color?",
                                        OneOf = [new EnumOption { Const = "Red", Title = "Red" }, new EnumOption { Const = "Blue", Title = "Blue", Description = "cold" }],
                                    },
                                },
                                Required = ["q1"],
                            },
                        }, ct);
                    elicited.Add(r is ElicitationAcceptAction { Content: { } c } ? (string)c["q1"]! : r.Tag ?? "?");
                    await agent.Update(sid, new SessionUpdateAgentMessageChunk { Content = new TextContent { Text = "done" } });
                    return AcpJson.ToNode(new PromptResponse { StopReason = StopReason.EndTurn });
                default: throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            }
        });
        var other = new ScriptedAdapter("other", (agent, req, ct) => Task.FromResult(req.Method switch
        {
            AcpMethods.Agent.Initialize => ScriptedAgent.Init(),
            AcpMethods.Agent.SessionList => AcpJson.ToNode(new ListSessionsResponse
            {
                Sessions = [new SessionInfo { SessionId = "other-1", Cwd = @"C:\w", Title = "not mine" }],
            }),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method),
        }));
        var pipe = "gorynych-bridge-" + Guid.NewGuid().ToString("N")[..8];
        await using var conductor = new Conductor(new ConductorOptions { DataDirectory = TestDirs.New(), ServiceId = "bridge-test" }, [other, fake]);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        try
        {
            string sessionId;
            await using (var zed = new PlainClient("acp", "--agent", "fake", "--pipe", pipe, "--data", TestDirs.New()))
            {
                var init = await zed.InitializeAsync();
                Assert.AreEqual("Gorynych: fake", (string?)init!["agentInfo"]!["title"]);
                Assert.IsTrue((bool)init["agentCapabilities"]!["loadSession"]!);
                Assert.IsNotNull(init["agentCapabilities"]!["sessionCapabilities"]!["delete"], "Zed sends session/delete only when it is advertised");

                var created = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew,
                    new JsonObject { ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
                sessionId = (string)created!["sessionId"]!;
                Assert.AreEqual("fake-1", sessionId, "a session made without _meta goes to the bridge's agent");

                var list = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait);
                Assert.AreEqual(0, list!["sessions"]!.AsArray().Count, "a draft (never prompted) is not listed, nor the other agent's session");

                var result = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionPrompt, new JsonObject
                {
                    ["sessionId"] = sessionId,
                    ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "go" }),
                }).WaitAsync(Wait);
                Assert.AreEqual("end_turn", (string?)result!["stopReason"]);
                list = await zed.Rpc.SendRequestAsync(AcpMethods.Agent.SessionList, new JsonObject()).WaitAsync(Wait);
                CollectionAssert.AreEqual(new[] { "fake-1" }, list!["sessions"]!.AsArray().Select(s => (string)s!["sessionId"]!).ToArray(),
                    "the list shows only the bridge's agent");

                var question = zed.Approvals.Single();
                Assert.AreEqual("Which color?", question.ToolCall.Title);
                CollectionAssert.AreEqual(new[] { "Red", "Blue — cold", "Skip" }, question.Options.Select(o => o.Name).ToArray());
                CollectionAssert.AreEqual(new[] { "Blue" }, elicited, "the approval answer became the form's content");
                CollectionAssert.IsSubsetOf(new[] { "plan", "tool_call", "tool_call_update", "usage_update", "agent_message_chunk" }, zed.Kinds());
                Assert.IsTrue(await zed.ExitsAsync(), "the bridge exits when the client closes stdin");
            }

            await using (var again = new PlainClient("acp", "--agent", "fake", "--pipe", pipe, "--data", TestDirs.New()))
            {
                await again.InitializeAsync();
                await again.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
                {
                    ["sessionId"] = sessionId, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray(),
                }).WaitAsync(Wait);
                CollectionAssert.AreEqual(
                    new[] { "user_message_chunk", "session_info_update", "plan", "tool_call", "tool_call_update", "usage_update", "agent_message_chunk" },
                    again.Kinds(), "session/load replays the history to a plain client");
            }
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Bridge_StartsTheService_WhichOutlivesIt()
    {
        var pipe = "gorynych-bridge-" + Guid.NewGuid().ToString("N")[..8];
        var data = TestDirs.New();
        if (Outside.ServiceLaunch.IsRemoteLogon())
        {
            // An ssh login or session 0 (a CI runner may run there), and no logon task for this pipe:
            // the bridge starts no service, which would run elevated there, and says what to do.
            await using var refused = new PlainClient("acp", "--agent", "claude", "--pipe", pipe, "--data", data);
            Assert.IsTrue(await refused.ExitsAsync());
            for (var i = 0; i < 50 && !refused.Stderr.ToString().Contains("task install --pipe " + pipe, StringComparison.Ordinal); i++)
                await Task.Delay(100);
            StringAssert.Contains(refused.Stderr.ToString(), "task install --pipe " + pipe, "the refusal");
            Assert.IsNull(await ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(500)), "no service was started");
            return;
        }
        string serviceId;
        await using (var first = new PlainClient("acp", "--agent", "claude", "--pipe", pipe, "--data", data))
        {
            var init = await first.InitializeAsync();
            serviceId = (string)init!["_meta"]![GorynychProtocol.MetaServiceId]!;
            Assert.AreEqual("Gorynych: Claude Code", (string?)init["agentInfo"]!["title"]);
            Assert.IsTrue(await first.ExitsAsync());
            StringAssert.Contains(first.Stderr.ToString(), "starting the service");
        }
        await using (var second = new PlainClient("acp", "--agent", "kimi", "--pipe", pipe, "--data", data))
        {
            var init = await second.InitializeAsync();
            Assert.AreEqual(serviceId, (string?)init!["_meta"]![GorynychProtocol.MetaServiceId], "the same service is still running");
            Assert.AreEqual("Gorynych: Kimi Code", (string?)init["agentInfo"]!["title"]);
            Assert.IsFalse(second.Stderr.ToString().Contains("starting the service"));
            await second.Rpc.SendRequestAsync(GorynychProtocol.MethodShutdown, new JsonObject()).WaitAsync(Wait);
        }
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Shutdown_StopsTheRunningService_AndSaysSoWhenNoneRuns()
    {
        var pipe = "gorynych-shutdown-" + Guid.NewGuid().ToString("N")[..8];
        var service = Process.Start(new ProcessStartInfo(ServiceExe(), ["--pipe", pipe, "--data", TestDirs.New()])
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            // Started once its pipe answers. Not through a bridge: one that finds no pipe yet starts a
            // service of its own, which would serve the pipe after this one.
            await using (var ready = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(30)))
                Assert.IsNotNull(ready, "the service opened its pipe");

            var (code, stderr) = await RunAsync("shutdown", "--pipe", pipe);
            Assert.AreEqual(0, code, stderr);
            StringAssert.Contains(stderr, "the service has stopped");
            Assert.IsTrue(service.HasExited, "it waited for the process to end");

            (code, stderr) = await RunAsync("shutdown", "--pipe", pipe);
            Assert.AreEqual(1, code, stderr);
            StringAssert.Contains(stderr, $"no service is running on pipe {pipe}");
        }
        finally
        {
            if (!service.HasExited) service.Kill();
        }
    }

    static async Task<(int Code, string Stderr)> RunAsync(params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo(ServiceExe(), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Wait * 3);
        return (process.ExitCode, await stderr);
    }

    [TestMethod]
    public void ScopeInitialize_AddsTheAgent_AndKeepsTheClientsOwnMeta()
    {
        var scope = AcpBridge.LinkOptions("kimi").InitializeMeta;
        var p = Gorynych.Acp.Link.ResumableLink.AddMeta(JsonNode.Parse("""{"protocolVersion":1,"_meta":{"zed":true}}""")!.AsObject(), scope);
        Assert.AreEqual("kimi", (string?)p["_meta"]!["gorynych/agent"]);
        Assert.IsTrue((bool)p["_meta"]!["zed"]!);
        var own = Gorynych.Acp.Link.ResumableLink.AddMeta(JsonNode.Parse("""{"_meta":{"gorynych/agent":"codex"}}""")!.AsObject(), scope);
        Assert.AreEqual("codex", (string?)own["_meta"]!["gorynych/agent"], "the client's own choice wins");
        Assert.AreEqual("kimi", (string?)Gorynych.Acp.Link.ResumableLink.AddMeta(null, scope)["_meta"]!["gorynych/agent"]);
    }
}

/// <summary>A plain ACP client on the bridge's stdio.</summary>
internal sealed class PlainClient : IAsyncDisposable
{
    readonly Process process;
    public JsonRpcConnection Rpc { get; }
    public List<JsonObject> Updates { get; } = [];
    public List<RequestPermissionRequest> Approvals { get; } = [];
    public StringBuilder Stderr { get; } = new();

    public PlainClient(params string[] args)
    {
        var psi = new ProcessStartInfo(BridgeTests.ServiceExe())
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        process = Process.Start(psi)!;
        process.ErrorDataReceived += (_, e) => { lock (Stderr) Stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        Rpc = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "plain-client");
        Rpc.NotificationHandler = (method, p) =>
        {
            if (method == AcpMethods.Client.SessionUpdate && p?["update"] is JsonObject u) lock (Updates) Updates.Add(u);
            return Task.CompletedTask;
        };
        Rpc.RequestHandler = (req, ct) =>
        {
            // A plain client knows approvals only; it picks the second option when there is one.
            if (req.Method != AcpMethods.Client.SessionRequestPermission)
                throw new JsonRpcException(JsonRpcException.MethodNotFound, req.Method);
            var r = req.ParamsAs<RequestPermissionRequest>();
            lock (Approvals) Approvals.Add(r);
            var pick = r.Options.Count > 2 ? r.Options[1] : r.Options[0];
            return Task.FromResult(AcpJson.ToNode(new RequestPermissionResponse { Outcome = new SelectedPermissionOutcome { OptionId = pick.OptionId } }));
        };
        Rpc.Start();
    }

    /// <summary><c>initialize</c> exactly as a client without extensions sends it.</summary>
    public Task<JsonNode?> InitializeAsync() =>
        Rpc.SendRequestAsync(AcpMethods.Agent.Initialize, new JsonObject { ["protocolVersion"] = 1, ["clientCapabilities"] = new JsonObject() }).WaitAsync(TimeSpan.FromSeconds(20));

    public string[] Kinds()
    {
        lock (Updates) return Updates.Select(u => (string)u["sessionUpdate"]!).ToArray();
    }

    public async Task<bool> ExitsAsync()
    {
        process.StandardInput.Close();
        return await Task.Run(() => process.WaitForExit(10_000));
    }

    public async ValueTask DisposeAsync()
    {
        await Rpc.DisposeAsync();
        if (!process.WaitForExit(5000)) process.Kill();
        process.Dispose();
    }
}
