using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Agents that run MCP tools without asking the person (<c>gorynych/unaskedMcpTools</c>, DSH): the
/// service asks on the calling session before <c>ask_agent</c>, <c>archive_session</c> and
/// <c>restore_session</c>, as it asks before every spawn. Other agents ask for their tools themselves,
/// so the service asks nothing for them (docs/adapter-contract.md, §6.2). Also the pre-allow of an
/// approval that names its tool call by ID alone (§8).
/// </summary>
[TestClass]
public sealed class UnaskedMcpToolsTests
{
    static readonly string Cwd = Path.GetTempPath();

    static Conductor Conductor(params IAgentAdapter[] adapters) => new(new ConductorOptions
    {
        DataDirectory = TestDirs.New(),
        ServiceId = "t",
        PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"],
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

    [TestMethod]
    public async Task AskAgent_FromAnAgentThatAsksNothing_AsksThePersonFirst_AndADeclineAsksNoOne()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { UnaskedMcpTools = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true) { Answer = "almost" };
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        var asked = await NewPromptedAsync(client, "kimi", "hello");

        client.Approve = Choose("decline");
        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => AskAsync(client, dsh.Tokens[asker], asked));
        StringAssert.Contains(declined.Message, "declined to ask session_id " + asked);
        var question = client.Approvals.Single();
        Assert.AreEqual(asker, question.SessionId, "asked on the calling session");
        StringAssert.StartsWith(question.ToolCall.Title, "Ask session_id " + asked);
        CollectionAssert.AreEqual(new[] { "ask", "decline" }, question.Options.Select(o => o.OptionId).ToArray());
        Assert.AreEqual(1, PromptsOf(kimi, asked), "the asked session got nothing");
        var card = client.Snapshot().Last(u => (string?)u["toolCallId"] == question.ToolCall.ToolCallId);
        Assert.AreEqual("failed", (string?)card["status"]);

        client.Approve = Choose("ask");
        var answer = await AskAsync(client, dsh.Tokens[asker], asked);
        StringAssert.Contains((string?)answer?["text"], "almost");
        Assert.AreEqual(2, client.Approvals.Count);
        Assert.AreEqual(2, PromptsOf(kimi, asked), "asked as a turn once allowed");
    }

    [TestMethod]
    public async Task AskAgent_FromAnAgentThatAsksItself_IsNotAskedAgain()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { UnaskedMcpTools = true, Answer = "yes" };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asked = await NewPromptedAsync(client, "dsh", "hi");
        var asker = await NewPromptedAsync(client, "kimi", "hello");

        var answer = await AskAsync(client, kimi.Tokens[asker], asked);

        StringAssert.Contains((string?)answer?["text"], "yes");
        Assert.AreEqual(0, client.Approvals.Count, "Kimi asks for its own MCP tools");
    }

    [TestMethod]
    public async Task ArchiveAndRestore_FromAnAgentThatAsksNothing_AskThePerson_ButNotWhenNothingWouldChange()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { UnaskedMcpTools = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var asker = await NewPromptedAsync(client, "dsh", "hi");
        var target = await NewPromptedAsync(client, "kimi", "hello");
        var token = dsh.Tokens[asker];

        client.Approve = Choose("decline");
        var declined = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => ArchiveAsync(client, token, target, archived: true));
        StringAssert.Contains(declined.Message, "declined to archive");
        Assert.IsFalse(await IsArchivedAsync(client, target));
        StringAssert.StartsWith(client.Approvals.Single().ToolCall.Title, "Archive session_id " + target);

        client.Approve = Choose("archive");
        await ArchiveAsync(client, token, target, archived: true);
        Assert.IsTrue(await IsArchivedAsync(client, target));
        Assert.AreEqual(2, client.Approvals.Count);

        await ArchiveAsync(client, token, target, archived: true); // already archived: nothing to ask
        Assert.AreEqual(2, client.Approvals.Count);

        client.Approve = Choose("restore");
        await ArchiveAsync(client, token, target, archived: false);
        Assert.IsFalse(await IsArchivedAsync(client, target));
        Assert.AreEqual(3, client.Approvals.Count);
        CollectionAssert.AreEqual(new[] { "restore", "decline" }, client.Approvals[2].Options.Select(o => o.OptionId).ToArray());
    }

    [TestMethod]
    public async Task Archive_FromAnAgentThatAsksItself_IsNotAskedAgain()
    {
        var dsh = new LifecycleAgent("dsh", canDelete: false) { UnaskedMcpTools = true };
        var kimi = new LifecycleAgent("kimi", canDelete: true);
        await using var conductor = Conductor(dsh.Adapter, kimi.Adapter);
        await using var client = await TestClient.ConnectAsync(conductor);
        var target = await NewPromptedAsync(client, "dsh", "hi");
        var asker = await NewPromptedAsync(client, "kimi", "hello");

        await ArchiveAsync(client, kimi.Tokens[asker], target, archived: true);

        Assert.IsTrue(await IsArchivedAsync(client, target));
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
