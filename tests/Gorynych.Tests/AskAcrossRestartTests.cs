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
        readonly TimeSpan reclaimWait, reclaimKeep, reclaimSweep;
        readonly Func<string, Task>? askWaitEnded;

        public Rig(bool channel, TimeSpan? reclaimWait = null, TimeSpan? reclaimKeep = null, TimeSpan? reclaimSweep = null,
            Func<string, Task>? askWaitEnded = null)
        {
            this.askWaitEnded = askWaitEnded;
            Asked = new LifecycleAgent("claude", canDelete: false) { HasChannel = channel };
            Hosts = new InProcessHosts(name => name == "claude" ? Asked.Adapter : Asker.Adapter);
            this.reclaimWait = reclaimWait ?? TimeSpan.FromSeconds(15);
            this.reclaimKeep = reclaimKeep ?? TimeSpan.FromMinutes(10);
            this.reclaimSweep = reclaimSweep ?? TimeSpan.FromMinutes(1);
            Service = Next();
        }

        public Conductor Next()
        {
            var service = new Conductor(new ConductorOptions
            {
                DataDirectory = Data, ServiceId = "test-service", Hosts = Hosts, HostToken = InProcessHosts.Token,
                PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"], ReclaimWait = reclaimWait, ReclaimKeep = reclaimKeep, ReclaimSweep = reclaimSweep, AskWaitEnded = askWaitEnded,
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

        public async Task<string> NewPromptedAsync(string agent, string text)
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

        /// <summary>What the asker's peers server sends: the question, or with <paramref name="reclaim"/> the answer to it; <paramref name="from"/> asks as another session.</summary>
        public JsonObject Ask(string askId, bool reclaim = false, string? from = null)
        {
            var meta = new JsonObject { [GorynychProtocol.MetaPeerToken] = Token(from ?? AskerId), [GorynychProtocol.MetaAskId] = askId };
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
        var stranger = await rig.NewPromptedAsync("kimi", "Tidy the changelog");
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

        // Another session that names the question takes nothing: the host kept who asked.
        var foreign = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-1", reclaim: true, from: stranger)).WaitAsync(Wait));
        Assert.IsNotNull(foreign.ErrorData?[GorynychProtocol.MetaAskLost], foreign.Message);
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

    /// <summary>
    /// The asker's link to the service drops while the session answers (no restart): the question goes on,
    /// and the asker that comes back waits for the answer and takes it, once; another session takes nothing.
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow(true, DisplayName = "by the channel")]
    [DataRow(false, DisplayName = "in a turn")]
    public async Task AnAskerWhoseLinkDropped_TakesTheAnswer_WhenItComesBack_Once(bool channel)
    {
        await using var rig = new Rig(channel);
        await rig.StartAsync();
        var stranger = await rig.NewPromptedAsync("kimi", "Tidy the changelog");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (channel) rig.Asked.HoldOpinions = hold;
        else
            rig.Asked.BeforePrompt = async id =>
            {
                if (id == rig.AskedId && rig.OpinionPrompts() > 0) await hold.Task;
            };
        var peers = await TestClient.ConnectAsync(rig.Service, "peers");
        var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-5"));
        await LinkHarness.Until(() => channel ? rig.Asked.Opinions.Count == 1 : rig.OpinionPrompts() == 1, "the question in the session");
        await peers.DisposeAsync(); // its link drops; the service runs on
        await Assert.ThrowsAsync<Exception>(() => asking.WaitAsync(Wait), "its asker lost the link");

        await using var back = await TestClient.ConnectAsync(rig.Service, "peers-again");
        var reclaiming = back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-5", reclaim: true));
        var foreign = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-5", reclaim: true, from: stranger)).WaitAsync(Wait));
        Assert.IsNotNull(foreign.ErrorData?[GorynychProtocol.MetaAskLost], "not another session's: " + foreign.Message);
        await Task.Delay(300);
        Assert.IsFalse(reclaiming.IsCompleted, "it waits for the answer still being given");
        hold.SetResult();

        Assert.AreEqual("ok", (string?)(await reclaiming.WaitAsync(Wait))!["text"]);
        Assert.AreEqual(1, channel ? rig.Asked.Opinions.Count : rig.OpinionPrompts(), "the session was asked once");
        var again = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => back.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-5", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(again.ErrorData?[GorynychProtocol.MetaAskLost], "given once: " + again.Message);
    }

    /// <summary>An asker that withdraws its question on a link that stays: it is cancelled, and nobody takes an answer to it.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AQuestionItsAskerWithdrew_HasNoAnswerToTake()
    {
        await using var rig = new Rig(channel: true);
        await rig.StartAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Asked.HoldOpinions = hold;
        await using var peers = await TestClient.ConnectAsync(rig.Service, "peers");
        using var withdraw = new CancellationTokenSource();
        var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-6"), withdraw.Token);
        await LinkHarness.Until(() => rig.Asked.Opinions.Count == 1, "the question in the session");

        withdraw.Cancel(); // $/cancel_request
        await Assert.ThrowsAsync<OperationCanceledException>(() => asking);

        // Read right behind the withdrawal, on the same link: withdrawn already, with the agent's answer still held.
        var lost = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-6", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(lost.ErrorData?[GorynychProtocol.MetaAskLost], lost.Message);
        Assert.IsFalse(hold.Task.IsCompleted);

        // An answer the agent gives after it is no answer to take either.
        hold.SetResult();
        await Task.Delay(200);
        var later = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-6", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(later.ErrorData?[GorynychProtocol.MetaAskLost], later.Message);
    }

    /// <summary>
    /// The withdrawal is in place as the service reads the cancellation, not when the asker's own call gets
    /// round to it: here that call is held at its cancelled wait, the question is withdrawn meanwhile, and
    /// the answer the agent gives after it is no answer to take (a reclaim took one, once in three full runs).
    /// </summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAnswerThatCameAfterTheWithdrawal_IsNotTaken_HoweverLateTheAskersCallEnds()
    {
        var waitEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var goOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig(channel: true, askWaitEnded: async _ =>
        {
            waitEnded.TrySetResult();
            await goOn.Task;
        });
        await rig.StartAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Asked.HoldOpinions = hold;
        await using var peers = await TestClient.ConnectAsync(rig.Service, "peers");
        using var withdraw = new CancellationTokenSource();
        var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-8"), withdraw.Token);
        await LinkHarness.Until(() => rig.Asked.Opinions.Count == 1, "the question in the session");

        withdraw.Cancel();
        await waitEnded.Task.WaitAsync(Wait); // the service read the cancellation; the asker's call is held
        await LinkHarness.Until(() => rig.Service.QuestionEnded("ask-8"), "withdrawn while the asker's call is held, with no answer yet");
        hold.SetResult(); // the agent answers after it
        goOn.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => asking);

        var lost = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-8", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(lost.ErrorData?[GorynychProtocol.MetaAskLost], lost.Message);
    }

    /// <summary>An answer no asker comes back for is dropped after its time on a timer, with nothing else happening.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAnswerNoAskerCameBackFor_IsDropped_OnATimer()
    {
        await using var rig = new Rig(channel: true, reclaimWait: TimeSpan.FromMilliseconds(200),
            reclaimKeep: TimeSpan.FromMilliseconds(300), reclaimSweep: TimeSpan.FromMilliseconds(100));
        await rig.StartAsync();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Asked.HoldOpinions = hold;
        var peers = await TestClient.ConnectAsync(rig.Service, "peers");
        var asking = peers.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-7"));
        await LinkHarness.Until(() => rig.Asked.Opinions.Count == 1, "the question in the session");
        await peers.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(() => asking.WaitAsync(Wait));
        Assert.AreEqual(1, rig.Service.KeptQuestions, "kept while it is answered");
        hold.SetResult(); // answered, and nobody comes for it

        await LinkHarness.Until(() => rig.Service.KeptQuestions == 0, "dropped once its time is up");
        await using var late = await TestClient.ConnectAsync(rig.Service, "peers-late");
        var lost = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => late.Rpc.SendRequestAsync(GorynychProtocol.MethodOpinion, rig.Ask("ask-7", reclaim: true)).WaitAsync(Wait));
        Assert.IsNotNull(lost.ErrorData?[GorynychProtocol.MetaAskLost], lost.Message);
    }
}
