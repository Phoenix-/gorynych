using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// A question (<c>ask_agent</c>) whose service restarts while the asked session answers it: the
/// session answers on in its host, and the asker takes the answer from the next service by the
/// question's <c>gorynych/askId</c>, once, without the session being asked again
/// (docs/service-protocol.md, "Adapter hosts"). A question that lived only in the service is lost, said so.
/// </summary>
[TestClass]
public sealed class AskAcrossRestartTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    sealed class Rig : IAsyncDisposable
    {
        public readonly string Data = TestDirs.New();
        public readonly LifecycleAgent Asked;
        public readonly LifecycleAgent Asker = new("kimi", canDelete: true);
        public readonly InProcessHosts Hosts;
        public Conductor Service;
        public TestClient Zed = null!;
        public string AskedId = null!, AskerId = null!;
        readonly TimeSpan reclaimWait;

        public Rig(bool channel, TimeSpan? reclaimWait = null)
        {
            Asked = new LifecycleAgent("claude", canDelete: false) { HasChannel = channel };
            Hosts = new InProcessHosts(name => name == "claude" ? Asked.Adapter : Asker.Adapter);
            this.reclaimWait = reclaimWait ?? TimeSpan.FromSeconds(15);
            Service = Next();
        }

        public Conductor Next()
        {
            var service = new Conductor(new ConductorOptions
            {
                DataDirectory = Data, ServiceId = "test-service", Hosts = Hosts, HostToken = InProcessHosts.Token,
                PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"], ReclaimWait = reclaimWait,
            }, [Asked.Adapter, Asker.Adapter]);
            Hosts.Service = service;
            return service;
        }

        public async Task StartAsync()
        {
            Zed = await TestClient.ConnectAsync(Service, "zed");
            AskedId = await NewPromptedAsync("claude", "Coordinate the release");
            AskerId = await NewPromptedAsync("kimi", "Review the notes");
        }

        async Task<string> NewPromptedAsync(string agent, string text)
        {
            var id = (await Zed.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = Path.GetTempPath(), McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent },
            }).WaitAsync(Wait)).SessionId;
            await Zed.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] }).WaitAsync(Wait);
            return id;
        }

        public string Token(string sessionId) => Service.Store.Get(sessionId)!.PeerToken!;

        /// <summary>What the asker's peers server sends: the question, or with <paramref name="reclaim"/> the answer to it.</summary>
        public JsonObject Ask(string askId, bool reclaim = false)
        {
            var meta = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(AskerId), [GorynychProtocol.MetaAskId] = askId };
            if (reclaim) meta[GorynychProtocol.MetaReclaim] = true;
            return new JsonObject { ["sessionId"] = AskedId, ["text"] = "SQLite or Postgres?", ["_meta"] = meta };
        }

        public int OpinionPrompts() => Asked.Prompts.Count(p => p.SessionId == AskedId && p.Text.StartsWith("[Opinion request", StringComparison.Ordinal));

        /// <summary>The service restarts, and for a while there is none.</summary>
        public async Task RestartAsync()
        {
            await HostTests.RestartAsync(Service);
            await HostTests.GoneAsync(Service, null, Hosts);
        }

        public async ValueTask DisposeAsync()
        {
            if (Zed is not null) await Zed.DisposeAsync();
            await Service.DisposeAsync();
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, true, DisplayName = "by the channel, answered while no service runs")]
    [DataRow(true, false, DisplayName = "by the channel, answered after the next service took the host")]
    [DataRow(false, true, DisplayName = "in a turn, which ends while no service runs (its own text)")]
    [DataRow(false, false, DisplayName = "in a turn, answered with reply to the next service")]
    public async Task AQuestionInFlight_AcrossARestart_ReachesItsAsker_Once_WithoutAskingAgain(bool channel, bool whileNone)
    {
        await using var rig = new Rig(channel);
        await rig.StartAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (channel) rig.Asked.HoldOpinions = hold;
        else
            rig.Asked.BeforePrompt = async id =>
            {
                if (id == rig.AskedId && rig.OpinionPrompts() > 0) await hold.Task;
            };
        string asked;
        await using (var peers = await TestClient.ConnectAsync(rig.Service, "peers"))
        {
            var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-1"));
            await LinkHarness.Until(() => channel ? rig.Asked.Opinions.Count == 1 : rig.OpinionPrompts() == 1, "the question in the session");
            asked = channel ? "" : rig.Asked.Prompts.Last(p => p.SessionId == rig.AskedId).Text;
            await rig.RestartAsync();
            await Assert.ThrowsAsync<Exception>(() => asking.WaitAsync(Wait), "its asker lost the service");
        }

        if (whileNone)
        {
            hold.SetResult();
            await LinkHarness.Until(() => ((JsonArray)rig.Hosts["claude"].Host.Report()["asks"]!).Count == 0, "the answer kept in the host");
        }
        // A reply may reach the next service before the host that runs its turn does: held back so here.
        var hostBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!channel && !whileNone) rig.Hosts.ConnectGate = hostBack;
        else hostBack.SetResult();
        rig.Service = rig.Next();
        await using var back = await TestClient.ConnectAsync(rig.Service, "peers-again");
        var reclaiming = back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-1", reclaim: true));
        var expected = "ok"; // the agent's channel answer, or its turn's text
        if (!whileNone)
        {
            await Task.Delay(300);
            Assert.IsFalse(reclaiming.IsCompleted, "it waits for the answer");
            if (!channel)
            {
                // The session answers with reply, through its own peers server, to the next service.
                var messageId = System.Text.RegularExpressions.Regex.Match(asked, "message_id (m[^\\]\\s]+)").Groups[1].Value;
                await using var answering = await TestClient.ConnectAsync(rig.Service, "answering-peers");
                var replying = answering.Rpc.SendRequestAsync(GorynychProtocol.MethodReply, new JsonObject
                {
                    ["messageId"] = messageId, ["text"] = "SQLite, for one user.", ["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = rig.Token(rig.AskedId) },
                });
                await Task.Delay(300);
                Assert.IsFalse(replying.IsCompleted, "the reply waits for the host that runs its turn");
                rig.Hosts.ConnectGate = null;
                hostBack.SetResult();
                Assert.IsTrue((bool)(await replying.WaitAsync(Wait))!["delivered"]!, "the reply reached the next service");
                expected = "SQLite, for one user.";
            }
            hold.SetResult();
        }

        Assert.AreEqual(expected, (string?)(await reclaiming.WaitAsync(Wait))!["text"]);
        Assert.AreEqual(1, channel ? rig.Asked.Opinions.Count : rig.OpinionPrompts(), "the session was asked once");
        var again = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-1", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(again.ErrorData?[GorynychProtocol.MetaAskLost], "given once: " + again.Message);
        Assert.AreEqual(1, channel ? rig.Asked.Opinions.Count : rig.OpinionPrompts(), "and a reclaim never asks it");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQuestionThatLivedOnlyInTheService_IsLost_AndSaysToAskAgain()
    {
        // The question waits in the service for the asked session's running turn when it restarts.
        await using var rig = new Rig(channel: false, reclaimWait: TimeSpan.FromSeconds(1));
        await rig.StartAsync();
        var busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Asked.BeforePrompt = id => id == rig.AskedId && rig.Asked.Prompts.Last(p => p.SessionId == id).Text == "busy" ? busy.Task : Task.CompletedTask;
        var running = HostTests.Prompt(rig.Zed, rig.AskedId, "busy");
        await LinkHarness.Until(() => rig.Asked.Prompts.Any(p => p.Text == "busy"), "the running turn");
        await using (var peers = await TestClient.ConnectAsync(rig.Service, "peers"))
        {
            var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-2"));
            await Task.Delay(300);
            await rig.RestartAsync();
            await Assert.ThrowsAsync<Exception>(() => asking.WaitAsync(Wait));
        }
        rig.Service = rig.Next();
        await using var back = await TestClient.ConnectAsync(rig.Service, "peers-again");

        var lost = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-2", reclaim: true)).WaitAsync(Wait));

        Assert.IsNotNull(lost.ErrorData?[GorynychProtocol.MetaAskLost]);
        StringAssert.Contains(lost.Message, $"Ask again: ask_agent with session_id {rig.AskedId} and the same question.");
        busy.SetResult();
        await LinkHarness.Until(() => rig.Service.Feed(rig.AskedId).Adopted is null, "the running turn's end");
        Assert.AreEqual(0, rig.OpinionPrompts(), "never asked: not then, nor by the reclaim");
        _ = running;
    }
}
