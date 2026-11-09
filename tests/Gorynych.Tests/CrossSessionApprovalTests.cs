using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Outside;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Who approves the gorynych tools (docs/service-protocol.md, "Approvals"). None of them asks the
/// person, with one exception the service handles the same for every agent: a session that is not
/// in full access asking one that is. The asked session's mode is the one it has when it is asked:
/// its current mode if it is loaded, else the one it is loaded in. Also the pre-allow of an
/// approval that names its tool call by ID alone (docs/adapter-contract.md, §8).
/// </summary>
[TestClass]
public sealed class CrossSessionApprovalTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(params IAgentAdapter[] adapters) => Conductor(TestDirs.New(), TimeSpan.Zero, adapters); // a session its client left is unloaded at once

    static Conductor Conductor(string data, TimeSpan unloadAfter, params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = data,
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
        UnloadAfter = unloadAfter,
    }, adapters);

    static async Task<string> NewPromptedAsync(TestClient client, string agent, string text)
    {
        var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
        {
            Cwd = Cwd,
            McpServers = [],
            Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
        })).SessionId;
        await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });
        return id;
    }

    /// <summary>A client picks the session's mode, <c>ask</c> or <c>full</c>.</summary>
    static Task SetModeAsync(TestClient client, string sessionId, string mode) =>
        client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
            new JsonObject { ["sessionId"] = sessionId, ["configId"] = "mode", ["value"] = mode });

    /// <summary><c>_gorynych/opinion</c> as the peers server sends it for <c>ask_agent</c>.</summary>
    static Task<JsonNode?> AskAsync(TestClient client, string token, string target) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, new JsonObject
        {
            ["sessionId"] = target,
            ["text"] = "Is it done?",
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });

    /// <summary><c>_gorynych/archive</c> as the peers server sends it for <c>archive_session</c> and <c>restore_session</c>.</summary>
    static Task<JsonNode?> ArchiveAsync(TestClient client, string token, string target, bool archived) =>
        client.Rpc.SendRequestAsync(GorynychProtocol.MethodArchive, new JsonObject
        {
            ["sessionId"] = target,
            ["archived"] = archived,
            ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = token },
        });

    static async Task<bool> IsArchivedAsync(TestClient client, string sessionId)
    {
        var list = await client.Rpc.RequestAsync<ListSessionsRequest, ListSessionsResponse>(AcpMethods.Agent.SessionList, new ListSessionsRequest
        {
            Meta = new JsonObject { [GorynychProtocol.MetaIncludeArchived] = true },
        });
        return list.Sessions.Single(s => s.SessionId == sessionId).Meta?[GorynychProtocol.MetaArchived] is JsonValue v && v.GetValue<bool>();
    }

    static int PromptsOf(LifecycleAgent agent, string sessionId)
    {
        lock (agent.Prompts) return agent.Prompts.Count(p => p.SessionId == sessionId);
    }

    static Func<RequestPermissionRequest, Task<RequestPermissionOutcome>> Choose(string optionId) =>
        _ => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome { OptionId = optionId });

    static string Mode(bool full) => full ? "full" : "ask";

    // ---------------------------------------------------------------- ask_agent: the matrix

    /// <summary>
    /// Every pairing of the asking and the asked session's modes, the asked one loaded or not. A
    /// session that is not loaded is checked in the mode it is loaded in, which its agent changed
    /// since it last had it (as DSH's modes file, or a Claude session's saved choice, may differ
    /// from what the service last saw).
    /// </summary>
    [TestMethod]
    [DataRow(false, false, true)]
    [DataRow(false, false, false)]
    [DataRow(true, true, true)]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    [DataRow(false, true, false)]
    public async Task AskAgent_AsksThePerson_OnlyWhenASessionOutsideFullAccessAsksOneInIt(bool askerFull, bool targetFull, bool targetLoaded)
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true, Answer = "almost" };
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        string target;
        await using (var owner = await TestClient.ConnectAsync(conductor, "owner"))
        {
            target = await NewPromptedAsync(owner, "kimi", "hello");
            // Loaded: the client picks the mode. Not loaded: it was last seen in the other mode.
            await SetModeAsync(owner, target, Mode(targetLoaded ? targetFull : !targetFull));
            if (!targetLoaded)
            {
                await owner.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = target });
                await conductor.CleanupsIdleAsync();
                lock (kimi.Modes) kimi.Modes[target] = Mode(targetFull); // changed in the agent meanwhile
            }
        }
        await using var client = await TestClient.ConnectAsync(conductor);
        if (targetLoaded)
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, AcpJson.ToNode(new ResumeSessionRequest { SessionId = target, Cwd = Cwd }));
        Assert.AreEqual(targetLoaded, conductor.Agents["kimi"].IsLive(target));
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        await SetModeAsync(client, asker, Mode(askerFull));
        client.Approve = Choose("ask");

        var answer = await AskAsync(client, dsh.Tokens[asker], target);

        StringAssert.Contains((string?)answer?["text"], "almost");
        Assert.AreEqual(2, PromptsOf(kimi, target), "asked");
        if (!askerFull && targetFull)
        {
            var question = client.Approvals.Single();
            Assert.AreEqual(asker, question.SessionId, "asked on the calling session");
            StringAssert.StartsWith(question.ToolCall.Title, "Ask session_id " + target);
            CollectionAssert.AreEqual(new[] { "ask", "decline" }, question.Options.Select(o => o.OptionId).ToArray());
            var card = client.Snapshot().Last(u => (string?)u["toolCallId"] == question.ToolCall.ToolCallId);
            Assert.AreEqual("completed", (string?)card["status"]);
        }
        else Assert.AreEqual(0, client.Approvals.Count, "nothing asked");
    }

    [TestMethod]
    public async Task AskAgent_IntoFullAccess_Declined_SendsNothing_AndSaysWhy()
    {
        var claude = new LifecycleAgent("claude", canDelete: false) { ReportsModes = true };
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        await using var conductor = Conductor(claude.Adapter, dsh.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "claude", "hi");
        var target = await NewPromptedAsync(client, "dsh", "hello");
        await SetModeAsync(client, target, "full");
        client.Approve = Choose("decline");

        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(client, claude.Tokens[asker], target));

        Assert.AreEqual(JsonRpcException.InvalidRequest, declined.Code);
        Assert.AreEqual($"The user declined to let this session ask session_id {target}, so it was not asked: that session runs in " +
            "full access and this one does not, so the user approves each such question. Go on without its opinion, or ask the user.", declined.Message);
        Assert.AreEqual(1, PromptsOf(dsh, target), "the asked session got nothing");
        var question = client.Approvals.Single();
        var card = client.Snapshot().Last(u => (string?)u["toolCallId"] == question.ToolCall.ToolCallId);
        Assert.AreEqual("failed", (string?)card["status"]);
        var shown = client.Snapshot().First(u => (string?)u["toolCallId"] == question.ToolCall.ToolCallId);
        StringAssert.Contains((string?)shown["content"]![0]!["content"]!["text"], "That session runs in full access");

        // Back out of full access: asked at once, as the mode is read at each question.
        await SetModeAsync(client, target, "ask");
        await AskAsync(client, claude.Tokens[asker], target);
        Assert.AreEqual(1, client.Approvals.Count);
        Assert.AreEqual(2, PromptsOf(dsh, target));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "the asked session goes into full access while the question waits")]
    [DataRow(true, DisplayName = "the asking session leaves full access while the question waits")]
    public async Task AQuestionWaitingForTheAskedSessionsTurn_IsCheckedAgainBeforeItIsSent(bool askerLeaves)
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        var target = await NewPromptedAsync(client, "kimi", "hello");
        if (askerLeaves)
        {
            // Both in full access when it asks: nothing to ask then.
            await SetModeAsync(client, asker, "full");
            await SetModeAsync(client, target, "full");
        }
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kimi.BeforePrompt = async id =>
        {
            string text;
            lock (kimi.Prompts) text = kimi.Prompts.Last(p => p.SessionId == id).Text;
            if (id != target || text != "busy") return;
            running.SetResult();
            await hold.Task;
        };
        var busy = client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
            new PromptRequest { SessionId = target, Prompt = [new TextContent { Text = "busy" }] });
        await running.Task;
        client.Approve = Choose("decline");

        var asking = AskAsync(client, dsh.Tokens[asker], target);
        await Task.Delay(300);
        Assert.IsFalse(asking.IsCompleted, "waits for the running turn");
        Assert.AreEqual(0, client.Approvals.Count, "nothing to ask when it asked");
        await SetModeAsync(client, askerLeaves ? asker : target, askerLeaves ? "ask" : "full");
        hold.SetResult();
        await busy;

        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => asking);
        StringAssert.Contains(declined.Message, "declined to let this session ask session_id " + target);
        StringAssert.StartsWith(client.Approvals.Single().ToolCall.Title, "Ask session_id " + target);
        Assert.AreEqual(2, PromptsOf(kimi, target), "its own turns only: the question was not sent");
    }

    [TestMethod]
    public async Task ADeclinedQuestion_UnloadsTheSessionItLoaded_AtOnce()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        var data = TestDirs.New();
        string target;
        await using (var before = Conductor(data, TimeSpan.FromHours(1), dsh.Adapter, kimi.Adapter))
        {
            await using var owner = await TestClient.ConnectAsync(before, "owner");
            target = await NewPromptedAsync(owner, "kimi", "hello");
        }
        lock (kimi.Modes) kimi.Modes[target] = "full";
        await using var conductor = Conductor(data, TimeSpan.FromHours(1), dsh.Adapter, kimi.Adapter); // after a restart: not loaded
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        Assert.IsFalse(conductor.Agents["kimi"].IsLive(target));
        client.Approve = Choose("decline");

        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(client, dsh.Tokens[asker], target));

        Assert.IsFalse(conductor.Agents["kimi"].IsLive(target), "closed in its agent at once, not after its idle hour");
        CollectionAssert.AreEqual(new[] { AcpMethods.Agent.SessionResume, AcpMethods.Agent.SessionClose }, kimi.CallsFor(target)[^2..]);
        Assert.AreEqual(1, PromptsOf(kimi, target), "asked nothing");

        // Loaded before the question (a client opened it and left): it stays for its idle time.
        await using (var other = await TestClient.ConnectAsync(conductor, "other"))
            await other.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, AcpJson.ToNode(new ResumeSessionRequest { SessionId = target, Cwd = Cwd }));
        Assert.IsTrue(conductor.Agents["kimi"].IsLive(target));
        await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(client, dsh.Tokens[asker], target));
        Assert.IsTrue(conductor.Agents["kimi"].IsLive(target), "the question did not load it, so it is not closed for it");
    }

    [TestMethod]
    public async Task AskAgent_IntoFullAccess_ToASessionOnlyItsAgentListsYet_IsAskedAboutToo()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true, existing: ["kimi-native"], titles: new() { ["kimi-native"] = "From Kimi's own CLI" })
        {
            ReportsModes = true,
        };
        lock (kimi.Modes) kimi.Modes["kimi-native"] = "full";
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");

        client.Approve = Choose("decline");
        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(client, dsh.Tokens[asker], "kimi-native"));

        StringAssert.Contains(declined.Message, "declined to let this session ask session_id kimi-native");
        StringAssert.StartsWith(client.Approvals.Single().ToolCall.Title, "Ask session_id kimi-native");
        Assert.AreEqual(0, PromptsOf(kimi, "kimi-native"), "the session made in Kimi's own CLI got nothing");
    }

    [TestMethod]
    public async Task AskAgent_IntoFullAccess_WaitsForAClient_WhenNoneIsAttached()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true, Answer = "later" };
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        string asker, asked;
        await using (var first = await TestClient.ConnectAsync(conductor, "first"))
        {
            asker = await NewPromptedAsync(first, "dsh", "hi");
            asked = await NewPromptedAsync(first, "kimi", "hello");
            await SetModeAsync(first, asked, "full");
        }
        await using var peers = await TestClient.ConnectAsync(conductor, "peers");

        var asking = AskAsync(peers, dsh.Tokens[asker], asked);
        await Task.Delay(300);

        Assert.IsFalse(asking.IsCompleted, "not asked, and not allowed, while nobody can answer");
        Assert.AreEqual(0, peers.Approvals.Count);
        Assert.AreEqual(1, PromptsOf(kimi, asked));
        await using var later = await TestClient.ConnectAsync(conductor, "later");
        later.Approve = Choose("ask");
        await later.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, AcpJson.ToNode(new ResumeSessionRequest { SessionId = asker, Cwd = Cwd }));
        var answer = await asking.WaitAsync(TimeSpan.FromSeconds(10));
        StringAssert.Contains((string?)answer?["text"], "later");
        StringAssert.StartsWith(later.Approvals.Single().ToolCall.Title, "Ask session_id " + asked);
    }

    [TestMethod]
    public async Task AskAgent_IntoFullAccess_FromAnAgentOutsideGorynych_IsLeftToItsHost()
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var conductor = Conductor(kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var target = await NewPromptedAsync(client, "kimi", "hello");
        await SetModeAsync(client, target, "full");

        var answer = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodMcpMessage, new JsonObject
        {
            ["serverId"] = OutsideTools.ServerName,
            ["requestId"] = "1",
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "ask_agent", ["arguments"] = new JsonObject { ["session_id"] = target, ["question"] = "Done?" } },
            ["_meta"] = new JsonObject
            {
                [GorynychProtocol.MetaOutside] = new JsonObject
                {
                    ["client"] = new JsonObject { ["name"] = "probe-host", ["title"] = "Probe Host", ["version"] = "1" },
                    ["folder"] = Cwd,
                },
            },
        });

        Assert.IsFalse((bool)answer!["result"]!["isError"]!);
        Assert.AreEqual(0, client.Approvals.Count, "the host approved its own tool call");
        Assert.AreEqual(2, PromptsOf(kimi, target));
    }

    [TestMethod]
    public void FullAccess_IsReadFromTheMarkOnTheCurrentModeValue()
    {
        static JsonObject Marked() => new() { [GorynychProtocol.MetaFullAccess] = true };
        static JsonObject Select(string current, JsonArray options) => new()
        {
            ["configOptions"] = new JsonArray(new JsonObject
            {
                ["id"] = "permissions", ["name"] = "Permissions", ["category"] = "mode", ["type"] = "select",
                ["currentValue"] = current, ["options"] = options.DeepClone(),
            }),
        };
        var flat = new JsonArray(
            new JsonObject { ["value"] = "ask", ["name"] = "Ask" },
            new JsonObject { ["value"] = "full", ["name"] = "Full access", ["_meta"] = Marked() });
        Assert.IsTrue(Service.Conductor.IsFullAccess(Select("full", flat)));
        Assert.IsFalse(Service.Conductor.IsFullAccess(Select("ask", flat)));

        var grouped = new JsonArray(new JsonObject
        {
            ["group"] = "danger", ["name"] = "Danger",
            ["options"] = new JsonArray(new JsonObject { ["value"] = "yolo", ["name"] = "YOLO", ["_meta"] = Marked() }),
        });
        Assert.IsTrue(Service.Conductor.IsFullAccess(Select("yolo", grouped)), "values in groups");

        var model = Select("full", flat);
        model["configOptions"]![0]!["category"] = "model";
        Assert.IsFalse(Service.Conductor.IsFullAccess(model), "only the mode option counts");

        var legacy = new JsonObject
        {
            ["modes"] = new JsonObject
            {
                ["currentModeId"] = "yolo",
                ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "yolo", ["name"] = "YOLO", ["_meta"] = Marked() }),
            },
        };
        Assert.IsTrue(Service.Conductor.IsFullAccess(legacy), "session modes too");
        Assert.IsFalse(Service.Conductor.IsFullAccess([]), "an agent that reports no state is not in full access");
        var unmarked = Select("full", flat);
        unmarked["configOptions"]![0]!["options"]![1]!.AsObject().Remove("_meta");
        Assert.IsFalse(Service.Conductor.IsFullAccess(unmarked), "a value counts only by its mark, not by its name");
    }

    // ---------------------------------------------------------------- archive and restore

    [TestMethod]
    public async Task ArchiveAndRestore_AreNeverAsked()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { ReportsModes = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { ReportsModes = true };
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        var target = await NewPromptedAsync(client, "kimi", "hello");
        await SetModeAsync(client, target, "full");
        var token = dsh.Tokens[asker];

        await ArchiveAsync(client, token, target, archived: true);
        Assert.IsTrue(await IsArchivedAsync(client, target));
        await ArchiveAsync(client, token, target, archived: false);
        Assert.IsFalse(await IsArchivedAsync(client, target));

        Assert.AreEqual(0, client.Approvals.Count);
    }

    // ---------------------------------------------------------------- an agent that asks for MCP tools

    [TestMethod]
    [DataRow("mcp__gorynych__list_agents")]
    [DataRow("mcp__gorynych__ask_agent")]
    [DataRow("mcp__gorynych__archive_session")]
    [DataRow("mcp__gorynych__restore_session")]
    [DataRow("mcp__gorynych__reply")]
    [DataRow("mcp__gorynych__spawn_session")]
    public async Task EveryGorynychTool_IsPreAllowed_ForAnAgentThatAsks(string tool)
    {
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        RequestPermissionResponse? answered = null;
        kimi.BeforePrompt = async id =>
        {
            var agent = kimi.Adapter.Current!;
            answered = await agent.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, new RequestPermissionRequest
            {
                SessionId = id,
                ToolCall = new ToolCallUpdate { ToolCallId = "call-1", Title = tool },
                Options =
                [
                    new PermissionOption { OptionId = "approve", Name = "Approve once", Kind = PermissionOptionKind.AllowOnce },
                    new PermissionOption { OptionId = "approve-always", Name = "Approve for this session", Kind = PermissionOptionKind.AllowAlways },
                    new PermissionOption { OptionId = "reject", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
                ],
            });
        };
        await using var conductor = Conductor(kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);

        await NewPromptedAsync(client, "kimi", "use it");

        Assert.AreEqual("approve", ((SelectedPermissionOutcome)answered!.Outcome).OptionId);
        Assert.AreEqual(0, client.Approvals.Count);
    }

    [TestMethod]
    public async Task AnApprovalNamingItsToolCallByIdAlone_IsPreAllowed_ByThePublishedTitle()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false);
        RequestPermissionResponse? answered = null;
        dsh.BeforePrompt = async id =>
        {
            var agent = dsh.Adapter.Current!;
            await agent.Update(id, new ToolCall { ToolCallId = "call-1", Title = "mcp__gorynych__list_agents", Kind = ToolKind.Other, Status = ToolCallStatus.InProgress });
            answered = await agent.Rpc.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>(AcpMethods.Client.SessionRequestPermission, new RequestPermissionRequest
            {
                SessionId = id,
                ToolCall = new ToolCallUpdate { ToolCallId = "call-1" },
                Options =
                [
                    new PermissionOption { OptionId = "allow-once", Name = "Allow once", Kind = PermissionOptionKind.AllowOnce },
                    new PermissionOption { OptionId = "reject-once", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
                ],
            });
        };
        await using var conductor = Conductor(dsh.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);

        await NewPromptedAsync(client, "dsh", "list them");

        Assert.AreEqual("allow-once", ((SelectedPermissionOutcome)answered!.Outcome).OptionId);
        Assert.AreEqual(0, client.Approvals.Count, "not asked: the call was published as mcp__gorynych__list_agents");
    }
}
