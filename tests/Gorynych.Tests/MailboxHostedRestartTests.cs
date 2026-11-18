using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class MailboxHostedRestartTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [TestMethod]
    [DataRow(false, false, DisplayName = "accepted message queued behind a surviving native turn")]
    [DataRow(true, false, DisplayName = "mailbox turn already handed to a surviving host")]
    [DataRow(false, true, DisplayName = "delegation retry across restart behind a surviving native turn")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task HostedRestart_PreservesTheQueue_AndNeverSilentlyReplaysAnUncertainHandoff(bool inFlight, bool delegation)
    {
        var directory = TestDirs.New();
        var recipient = new LifecycleAgent("claude", canDelete: false);
        var sender = new LifecycleAgent("kimi", canDelete: true);
        var hosts = new InProcessHosts(name => name == "claude" ? recipient.Adapter : sender.Adapter);
        Conductor Next()
        {
            var next = new Conductor(new ConductorOptions { DataDirectory = directory, ServiceId = "mailbox-restart",
                Hosts = hosts, HostToken = InProcessHosts.Token, PeerServerCommand = ["svc.exe", "peers", "--pipe", "p"] },
                [recipient.Adapter, sender.Adapter]);
            hosts.Service = next;
            return next;
        }
        var service = Next();
        var client = await TestClient.ConnectAsync(service);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PromptResponse> Prompt(TestClient connection, string id, string text) => connection.Rpc.RequestAsync<PromptRequest, PromptResponse>(
            AcpMethods.Agent.SessionPrompt, new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });
        async Task<string> New(string agent)
        {
            var id = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew,
                new NewSessionRequest { Cwd = directory, Meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent } })).SessionId;
            await Prompt(client, id, "native history");
            return id;
        }
        try
        {
            var to = await New("claude");
            var from = await New("kimi");
            service.Store.Update(to, "claude", r => { r.ParentSessionId = from; r.ReportToParent = true; });
            const string marker = "HOSTED_RESTART_MAILBOX_PAYLOAD";
            const string secondMarker = "HOSTED_RESTART_QUEUED_AFTER_INFLIGHT";
            recipient.BeforePrompt = async id =>
            {
                string text;
                lock (recipient.Prompts) text = recipient.Prompts.Last(p => p.SessionId == id).Text;
                if (id == to && (inFlight ? text.Contains(marker, StringComparison.Ordinal) : text == "busy"))
                { entered.TrySetResult(); await hold.Task; }
            };
            Task<PromptResponse>? busy = null;
            if (!inFlight)
            {
                busy = Prompt(client, to, "busy");
                await entered.Task.WaitAsync(Wait);
            }
            var messageId = Guid.NewGuid().ToString("N");
            var method = delegation ? GorynychProtocol.MethodDelegateTask : GorynychProtocol.MethodSendMessage;
            JsonObject Message() => new()
            {
                ["sessionId"] = to, ["text"] = marker, ["_meta"] = new JsonObject
                { [GorynychProtocol.MetaPeerToken] = service.Store.Get(from)!.PeerToken,
                    [GorynychProtocol.MetaMessageRequestId] = messageId },
            };
            var receipt = await client.Rpc.SendRequestAsync(method, Message()).WaitAsync(Wait);
            Assert.AreEqual("accepted", (string?)receipt!["status"]);
            if (delegation) Assert.AreEqual(messageId, (string?)receipt["assignmentId"]);
            if (inFlight) await entered.Task.WaitAsync(Wait);
            if (inFlight)
            {
                var queued = await client.Rpc.SendRequestAsync(GorynychProtocol.MethodSendMessage, new JsonObject
                {
                    ["sessionId"] = to, ["text"] = secondMarker, ["_meta"] = new JsonObject
                    { [GorynychProtocol.MetaPeerToken] = service.Store.Get(from)!.PeerToken,
                        [GorynychProtocol.MetaMessageRequestId] = Guid.NewGuid().ToString("N") },
                }).WaitAsync(Wait);
                Assert.AreEqual("accepted", (string?)queued!["status"]);
            }
            var restart = await HostTests.RestartAsync(service);
            Assert.AreEqual(true, (bool?)restart["restarted"]);
            await HostTests.GoneAsync(service, null, hosts);
            if (busy is not null) await Assert.ThrowsAsync<Exception>(async () => await busy.WaitAsync(Wait));
            await client.DisposeAsync();
            service = Next();
            client = await TestClient.ConnectAsync(service);
            await LinkHarness.Until(() => service.Feed(to).Adopted is not null,
                "the new conductor adopts the turn still running in its surviving host");

            if (delegation)
            {
                var retry = await client.Rpc.SendRequestAsync(method, Message()).WaitAsync(Wait);
                Assert.AreEqual(messageId, (string?)retry!["assignmentId"]);
                Assert.AreEqual(messageId, (string?)retry["messageId"]);
            }

            if (inFlight)
                await LinkHarness.Until(() =>
                {
                    lock (sender.Prompts) return sender.Prompts.Any(p => p.SessionId == from &&
                        p.Text.Contains("processing is uncertain", StringComparison.Ordinal));
                }, "explicit uncertainty notice after hosted mailbox handoff");

            hold.TrySetResult();
            if (!inFlight)
                await LinkHarness.Until(() =>
                {
                    lock (recipient.Prompts) return recipient.Prompts.Any(p => p.Text.Contains(marker, StringComparison.Ordinal));
                }, "queued accepted message delivered after hosted restart");
            else
                await LinkHarness.Until(() =>
                {
                    lock (recipient.Prompts) return recipient.Prompts.Any(p => p.Text.Contains(secondMarker, StringComparison.Ordinal));
                }, "second queued message follows the surviving mailbox turn");
            await Prompt(client, to, "post-restart barrier").WaitAsync(Wait);
            // BackgroundTurnsIdleAsync also tracks the host's perpetual stale-generation sweep.
            // Await the reports' actual turn completions instead of waiting for that loop to exit.
            await LinkHarness.Until(() => HostTests.EndMarks(service, from) == (inFlight ? 5 : 4),
                "all three child reports (and any recovery notice) completed in the parent");
            lock (recipient.Prompts)
            {
                Assert.AreEqual(1, recipient.Prompts.Count(p => p.SessionId == to && p.Text.Contains(marker, StringComparison.Ordinal)),
                    "accepted payload reaches the surviving native agent once, never a blind replay");
                if (delegation) StringAssert.Contains(recipient.Prompts.Single(p => p.SessionId == to && p.Text.Contains(marker, StringComparison.Ordinal)).Text,
                    $"assignment_id {messageId}");
                if (inFlight) Assert.AreEqual(1, recipient.Prompts.Count(p => p.SessionId == to && p.Text.Contains(secondMarker, StringComparison.Ordinal)));
            }
            Assert.AreEqual(4, HostTests.EndMarks(service, to), "each native turn ends exactly once across adoption");
            lock (sender.Prompts)
                Assert.AreEqual(3, sender.Prompts.Count(p => p.SessionId == from && p.Text.StartsWith("[Report", StringComparison.Ordinal)),
                    "each completed child turn reports once; the initial history predates its parent relation");
            Assert.AreEqual(2, hosts.Launches, "both adapter hosts survived; this was not a cold-start-only recovery test");
        }
        finally
        {
            hold.TrySetResult();
            await client.DisposeAsync();
            await service.DisposeAsync();
        }
    }
}
