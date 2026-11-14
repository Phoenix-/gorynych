using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service;

namespace Gorynych.Tests;

[TestClass]
public sealed class HostConfigurationTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    static JsonArray Options(string model) => new(
        new JsonObject
        {
            ["id"] = "model", ["name"] = "Model", ["category"] = "model", ["type"] = "select",
            ["currentValue"] = model,
            ["options"] = new JsonArray(new[] { "k2", "k3", "k4" }
                .Select(id => (JsonNode)new JsonObject { ["value"] = id, ["name"] = id }).ToArray()),
        },
        new JsonObject
        {
            ["id"] = "mode", ["name"] = "Permissions", ["category"] = "mode", ["type"] = "select",
            ["currentValue"] = "read-only",
            ["options"] = new JsonArray(new JsonObject { ["value"] = "read-only", ["name"] = "Read only" }),
        });

    static JsonObject Configuration(string model) => new()
    {
        ["configOptions"] = Options(model),
        ["modes"] = new JsonObject
        {
            ["currentModeId"] = "default",
            ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Default" },
                new JsonObject { ["id"] = "plan", ["name"] = "Plan" }),
        },
        ["models"] = new JsonObject
        {
            ["currentModelId"] = model,
            ["availableModels"] = new JsonArray(new JsonObject { ["modelId"] = model, ["name"] = model }),
        },
    };

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    [DataRow("initial")]
    [DataRow("chosen")]
    [DataRow("offline")]
    [DataRow("cleared")]
    public async Task ConfigurationSurvivesAConductorRestart_DuringARunningTurn(string change)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modeAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modeAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = 0;
        var adapter = new ScriptedAdapter("dsh", async (_, request, ct) =>
        {
            switch (request.Method)
            {
                case AcpMethods.Agent.Initialize:
                    return ScriptedAgent.Init(list: true, load: true, resume: true);
                case AcpMethods.Agent.SessionList:
                    return new JsonObject { ["sessions"] = new JsonArray() };
                case AcpMethods.Agent.SessionNew:
                    var result = Configuration("k2");
                    result["sessionId"] = "s" + ++sequence;
                    return result;
                case AcpMethods.Agent.SessionSetConfigOption:
                    if ((string?)request.Params?["value"] == "bad")
                        throw new JsonRpcException(JsonRpcException.InvalidParams, "invalid model");
                    return new JsonObject { ["configOptions"] = Options("k3") };
                case AcpMethods.Agent.SessionSetMode:
                    modeAsked.TrySetResult();
                    await modeAnswer.Task.WaitAsync(ct);
                    return new JsonObject(); // ACP's set_mode need not send an update.
                case AcpMethods.Agent.SessionPrompt:
                    started.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return new JsonObject { ["stopReason"] = "end_turn" };
                case AcpMethods.Agent.SessionClose:
                    return new JsonObject();
                default:
                    throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method);
            }
        });
        var data = TestDirs.New();
        var hosts = new InProcessHosts(_ => adapter);
        var service = hosts.Conductor(data, adapter);
        hosts.Service = service;
        await using var first = await TestClient.ConnectAsync(service, "zed");
        var sid = await HostTests.NewSession(first);
        var other = await HostTests.NewSession(first);
        var expected = Configuration("k2");
        Task<JsonNode?>? settingMode = null;
        if (change != "initial")
        {
            await first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = sid, ["configId"] = "model", ["value"] = "k3" });
            expected["configOptions"] = Options("k3");
            await Assert.ThrowsAsync<JsonRpcException>(() => first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = sid, ["configId"] = "model", ["value"] = "bad" }));
            settingMode = first.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetMode,
                new JsonObject { ["sessionId"] = sid, ["modeId"] = "plan" });
            await modeAsked.Task.WaitAsync(Wait);
        }
        var prompt = HostTests.Prompt(first, sid, "keep working");
        await started.Task.WaitAsync(Wait);
        var host = hosts["dsh"].Host;
        await HostTests.RestartAsync(service);
        await HostTests.GoneAsync(service, null, hosts);
        await first.DisposeAsync();
        try { await prompt.WaitAsync(Wait); } catch (IOException) { }

        // Updates while no service runs must enter the snapshot too. A successful set_mode
        // answer after an older mode notification must win over that notification's replay.
        if (change != "initial")
        {
            await adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            {
                ["sessionId"] = sid,
                ["update"] = new JsonObject { ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = "default" },
            });
            modeAnswer.TrySetResult();
            await LinkHarness.Until(() => (string?)host.Report()["sessionStates"]?[sid]?["modes"]?["currentModeId"] == "plan", "the mode answer");
            expected["modes"]!["currentModeId"] = "plan";
            try { await settingMode!.WaitAsync(Wait); } catch (IOException) { }
        }
        if (change is "offline" or "cleared")
        {
            var options = change == "cleared" ? new JsonArray() : Options("k4");
            await adapter.Current!.Rpc.SendNotificationAsync(AcpMethods.Client.SessionUpdate, new JsonObject
            {
                ["sessionId"] = sid,
                ["update"] = new JsonObject { ["sessionUpdate"] = "config_option_update", ["configOptions"] = options.DeepClone() },
            });
            expected["configOptions"] = options;
            await LinkHarness.Until(() => JsonNode.DeepEquals(expected, host.Report()["sessionStates"]?[sid]), "the offline settings");
        }

        await using var next = HostTests.Next(data, hosts, null, adapter);
        try
        {
            await using var again = await TestClient.ConnectAsync(next, "zed-reconnected");
            foreach (var method in new[] { AcpMethods.Agent.SessionLoad, AcpMethods.Agent.SessionResume })
            {
                var response = await again.Rpc.SendRequestAsync(method,
                    new JsonObject { ["sessionId"] = sid, ["cwd"] = Path.GetTempPath(), ["mcpServers"] = new JsonArray() }).WaitAsync(Wait);
                foreach (var key in new[] { "configOptions", "modes", "models" })
                    Assert.IsTrue(JsonNode.DeepEquals(expected[key], response?[key]), $"{method}: {key} ({change}): {response}");
                Assert.AreEqual("running", (string?)response?["_meta"]?[GorynychProtocol.MetaTurn]);
            }
            Assert.IsTrue(JsonNode.DeepEquals(Configuration("k2"), next.Feed(other).State()), "another session's settings stay separate");
            Assert.AreEqual(1, adapter.Starts, "the agent stayed alive");
            lock (adapter.Methods)
            {
                CollectionAssert.DoesNotContain(adapter.Methods, AcpMethods.Agent.SessionLoad);
                CollectionAssert.DoesNotContain(adapter.Methods, AcpMethods.Agent.SessionResume);
            }
            release.TrySetResult();
            await LinkHarness.Until(() => !next.Feed(sid).TurnRunning, "the original turn's end");
        }
        finally
        {
            release.TrySetResult();
            modeAnswer.TrySetResult();
            await hosts["dsh"].Agent.DisposeAsync();
        }
    }
}
