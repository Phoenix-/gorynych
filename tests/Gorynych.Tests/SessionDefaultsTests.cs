using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Claude;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// The service's per-agent defaults for new sessions and a spawn_session's explicit
/// config_options (docs/service-protocol.md, "Default session configuration"): explicit choices
/// beat the service defaults, the native harness's choices are the last fallback, model goes
/// before effort, and nothing is applied when an existing session is loaded or resumed.
/// </summary>
[TestClass]
public sealed class SessionDefaultsTests
{
    static readonly string Cwd = Path.GetTempPath();

    static SessionDefaults DefaultsFor(string agent, params (string Id, string Value)[] choices)
    {
        var config = new JsonObject();
        foreach (var (id, value) in choices) config[id] = value;
        return SessionDefaults.Parse(new JsonObject
        {
            ["agents"] = new JsonObject { [agent] = new JsonObject { ["configOptions"] = config } },
        }.ToJsonString(), "test defaults");
    }

    static Conductor ConductorFor(string dir, ConfigAgent agent, SessionDefaults? defaults = null, bool peers = false) =>
        new(new ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "test",
            SessionDefaults = defaults,
            PeerServerCommand = peers ? ["gorynych-test", "peers", "--pipe", "p"] : null,
        }, [agent.Adapter]);

    static Task<JsonNode?> NewAsync(TestClient client, string agent = "agent", JsonObject? configOptions = null)
    {
        var meta = new JsonObject { [GorynychProtocol.MetaAgent] = agent };
        if (configOptions is not null) meta[GorynychProtocol.MetaConfigOptions] = configOptions;
        return client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionNew, new JsonObject
        {
            ["cwd"] = Cwd,
            ["mcpServers"] = new JsonArray(),
            ["_meta"] = meta,
        });
    }

    static string Current(JsonNode? response, string id) =>
        (string)response!["configOptions"]!.AsArray().Single(o => (string?)o!["id"] == id)!["currentValue"]!;

    static Task<JsonNode?> SpawnAsync(TestClient client, ConfigAgent agent, string parent, JsonObject arguments)
    {
        arguments["_meta"] = new JsonObject { [GorynychProtocol.MetaPeerToken] = agent.TokenOf(parent) };
        return client.Rpc.SendRequestAsync(GorynychProtocol.MethodSpawn, arguments);
    }

    [TestMethod]
    public async Task NewSession_AppliesModelFirst_ThenDependentEffort_AndReturnsConfirmedState()
    {
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("reasoning_effort", "high"), ("model", "large"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewAsync(client);
        var id = (string)created!["sessionId"]!;

        CollectionAssert.AreEqual(new[] { "model:large", "reasoning_effort:high" },
            agent.Sets(id).Select(s => $"{s.Id}:{s.Value}").ToArray(),
            "the model goes first even when the defaults name effort first; effort is then checked against it");
        Assert.AreEqual("large", Current(created, "model"));
        Assert.AreEqual("high", Current(created, "reasoning_effort"), "an effort the model supports is kept");
        Assert.AreEqual("200k", Current(created, "context_size"), "untouched native choices are reported as they are");
        Assert.AreEqual(0, agent.Prompts(id).Length);
    }

    [TestMethod]
    public async Task ExplicitCreationChoice_BeatsServiceDefaults_ForTheSameOption()
    {
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("model", "small"), ("reasoning_effort", "medium"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewAsync(client, configOptions: new JsonObject
        {
            ["reasoning_effort"] = "high",
            ["model"] = "large",
        });
        var id = (string)created!["sessionId"]!;

        CollectionAssert.AreEqual(new[] { "model:large", "reasoning_effort:high" },
            agent.Sets(id).Select(s => $"{s.Id}:{s.Value}").ToArray(),
            "the explicit choices replace the defaults for the same IDs");
        Assert.AreEqual("large", Current(created, "model"));
        Assert.AreEqual("high", Current(created, "reasoning_effort"));
    }

    [TestMethod]
    public async Task ClientChoiceAfterSessionNew_WinsOverTheServiceDefault()
    {
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("model", "small"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewAsync(client);
        var id = (string)created!["sessionId"]!;
        Assert.AreEqual("small", Current(created, "model"));

        // Zed applies its own default_config_options after session/new; that is an ordinary change.
        var changed = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption, new JsonObject
        {
            ["sessionId"] = id, ["configId"] = "model", ["value"] = "large",
        });
        Assert.AreEqual("large", Current(changed, "model"));
        Assert.AreEqual("large", Current(conductor.Feed(id).State(), "model"));
    }

    [TestMethod]
    public async Task OpaqueNativeModelValue_IsPassedThroughUnchanged()
    {
        // DSH's model is one JSON-encoded provider/model pair, not a token Gorynych may parse.
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("model", ConfigAgent.OpaqueModel), ("reasoning_effort", "high"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewAsync(client);
        var id = (string)created!["sessionId"]!;
        Assert.AreEqual(ConfigAgent.OpaqueModel, Current(created, "model"));
        Assert.AreEqual(ConfigAgent.OpaqueModel, agent.Sets(id).Single(s => s.Id == "model").Value,
            "the exact native string reaches the agent unchanged");
    }

    [TestMethod]
    public async Task NoDefaults_LeavesTheHarnessesOwnChoicesUntouched()
    {
        var agent = new ConfigAgent();
        await using var conductor = ConductorFor(TestDirs.New(), agent);
        await using var client = await TestClient.ConnectAsync(conductor);

        var created = await NewAsync(client);
        var id = (string)created!["sessionId"]!;

        Assert.AreEqual(0, agent.Sets(id).Length, "no set_config_option without explicit choices or defaults");
        Assert.AreEqual("small", Current(created, "model"));
        Assert.AreEqual("medium", Current(created, "reasoning_effort"));
    }

    [TestMethod]
    public async Task ChangedEffortChoices_AreCheckedAfterTheModelChanges_AndNoUnsupportedValueIsSent()
    {
        var agent = new ConfigAgent();
        // "medium" is valid for the native model "small" but the default model "large" offers only low/high.
        var defaults = DefaultsFor("agent", ("model", "large"), ("reasoning_effort", "medium"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => NewAsync(client));
        StringAssert.Contains(error.Message, "reasoning_effort");
        StringAssert.Contains(error.Message, "\"medium\"");
        StringAssert.Contains(error.Message, "low", "...and names the values the new model does offer");

        var id = agent.LastCreated!;
        CollectionAssert.AreEqual(new[] { "model:large" }, agent.Sets(id).Select(s => $"{s.Id}:{s.Value}").ToArray(),
            "the unsupported effort is refused after the model change, before it is sent");
        CollectionAssert.Contains(agent.Closes(id), id);
        CollectionAssert.Contains(agent.Deletes(id), id, "the failed new session is cleaned up");
        Assert.AreEqual(0, agent.Prompts(id).Length);
    }

    [TestMethod]
    public async Task MissingNativeOption_FailsClearly_AndNoDefaultSessionStillWorks()
    {
        var agent = new ConfigAgent { AdvertisesOptions = false };
        var defaults = DefaultsFor("agent", ("model", "large"));
        await using (var conductor = ConductorFor(TestDirs.New(), agent, defaults))
        await using (var client = await TestClient.ConnectAsync(conductor))
        {
            var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => NewAsync(client));
            StringAssert.Contains(error.Message, "No native config option");
            var id = agent.LastCreated!;
            CollectionAssert.Contains(agent.Closes(id), id);
            CollectionAssert.Contains(agent.Deletes(id), id);
        }

        // The same agent with no relevant defaults continues exactly as before (the TUI fallback's path).
        await using var plain = ConductorFor(TestDirs.New(), agent);
        await using var plainClient = await TestClient.ConnectAsync(plain);
        var created = await NewAsync(plainClient);
        Assert.AreEqual(0, agent.Sets((string)created!["sessionId"]!).Length);
    }

    [TestMethod]
    public async Task LoadAndResume_DoNotApplyDefaultsAgain()
    {
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("model", "large"), ("reasoning_effort", "high"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults);
        await using var client = await TestClient.ConnectAsync(conductor);
        var created = await NewAsync(client);
        var id = (string)created!["sessionId"]!;
        var sets = agent.Sets(id).Length;

        var loaded = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
        {
            ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray(),
        });
        var resumed = await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionResume, new JsonObject
        {
            ["sessionId"] = id, ["cwd"] = Cwd,
        });

        Assert.AreEqual("large", Current(loaded, "model"));
        Assert.AreEqual("high", Current(resumed, "reasoning_effort"));
        Assert.AreEqual(sets, agent.Sets(id).Length, "load and resume never overwrite an existing session's choices");
    }

    [TestMethod]
    public async Task Restart_ReadsTheDefaultsAgain_ForNewSessions_ButNotForExistingOnes()
    {
        var dir = TestDirs.New();
        File.WriteAllText(Path.Combine(dir, SessionDefaults.FileName), new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["agent"] = new JsonObject
                {
                    ["configOptions"] = new JsonObject { ["model"] = "large", ["reasoning_effort"] = "high" },
                },
            },
        }.ToJsonString());
        var agent = new ConfigAgent();

        string id;
        await using (var conductor = ConductorFor(dir, agent, SessionDefaults.Load(dir)))
        await using (var client = await TestClient.ConnectAsync(conductor))
        {
            id = (string)(await NewAsync(client))!["sessionId"]!;
            Assert.AreEqual("large", agent.ModelOf(id));
            Assert.AreEqual("high", agent.EffortOf(id));
        }

        var before = agent.Sets(id).Length;
        await using var restarted = ConductorFor(dir, agent, SessionDefaults.Load(dir));
        await using var after = await TestClient.ConnectAsync(restarted);
        var loaded = await after.Rpc.SendRequestAsync(AcpMethods.Agent.SessionLoad, new JsonObject
        {
            ["sessionId"] = id, ["cwd"] = Cwd, ["mcpServers"] = new JsonArray(),
        });
        Assert.AreEqual(before, agent.Sets(id).Length, "the existing session keeps what it had");
        Assert.AreEqual("large", Current(loaded, "model"));

        var created = await NewAsync(after);
        var next = (string)created!["sessionId"]!;
        Assert.AreNotEqual(id, next);
        CollectionAssert.AreEqual(new[] { "model:large", "reasoning_effort:high" },
            agent.Sets(next).Select(s => $"{s.Id}:{s.Value}").ToArray(),
            "a new session after the restart gets the file's defaults");
    }

    [TestMethod]
    public void DefaultsFile_LoadsOpaqueDSHValues_AndRejectsMalformedShapes()
    {
        var dir = TestDirs.New();
        var dshModel = "[\"deepseek-official\",\"deepseek-flash\"]";
        File.WriteAllText(Path.Combine(dir, SessionDefaults.FileName), new JsonObject
        {
            ["agents"] = new JsonObject
            {
                ["dsh"] = new JsonObject
                {
                    ["configOptions"] = new JsonObject { ["model"] = dshModel, ["reasoning_effort"] = "high" },
                },
            },
        }.ToJsonString());

        var defaults = SessionDefaults.Load(dir);
        CollectionAssert.AreEqual(new[]
        {
            new SessionConfigChoice("model", dshModel),
            new SessionConfigChoice("reasoning_effort", "high"),
        }, defaults.For("dsh").ToArray(), "the JSON-encoded model pair stays one opaque string");
        Assert.AreEqual(0, defaults.For("claude").Count);

        Assert.ThrowsExactly<InvalidDataException>(() => SessionDefaults.Parse("""{"agents":{"dsh":{"configOptions":{"model":42}}}}""", "bad"));
        Assert.ThrowsExactly<InvalidDataException>(() => SessionDefaults.Parse("""{"agents":[],"typo":{}}""", "bad"));
        Assert.ThrowsExactly<InvalidDataException>(() => SessionDefaults.Parse("not json", "bad"));
        Assert.AreEqual(0, SessionDefaults.Load(TestDirs.New()).For("dsh").Count, "a missing file means no defaults");
    }

    [TestMethod]
    public async Task Spawn_AppliesExplicitConfigBeforePrompt_AndReturnsTheConfirmedValues()
    {
        var agent = new ConfigAgent();
        await using var conductor = ConductorFor(TestDirs.New(), agent, peers: true);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = (string)(await NewAsync(client))!["sessionId"]!;

        var spawned = await SpawnAsync(client, agent, parent, new JsonObject
        {
            ["agent"] = "agent",
            ["prompt"] = "write the tests",
            ["config_options"] = new JsonObject { ["reasoning_effort"] = "high", ["model"] = "large" },
        });
        var child = (string)spawned!["sessionId"]!;

        CollectionAssert.AreEqual(new[] { "model:large", "reasoning_effort:high" },
            agent.Sets(child).Select(s => $"{s.Id}:{s.Value}").ToArray());
        var config = spawned["config"]!.AsObject();
        Assert.AreEqual("large", (string?)config["model"]);
        Assert.AreEqual("high", (string?)config["reasoning_effort"]);
        Assert.AreEqual("200k", (string?)config["context_size"], "the response also carries untouched native values");
        for (var i = 0; i < 500 && agent.Prompts(child).Length == 0; i++) await Task.Delay(10);
        var events = agent.Events().Where(e => e.Session == child).ToArray();
        Assert.IsTrue(Array.FindIndex(events, e => e.Kind == "prompt") > Array.FindLastIndex(events, e => e.Kind == "set"),
            "the first prompt starts only after the config is confirmed");
    }

    [TestMethod]
    public async Task Spawn_ServiceDefaultFillsOnlyWhatTheSpawnDidNotChoose()
    {
        var agent = new ConfigAgent();
        var defaults = DefaultsFor("agent", ("model", "small"), ("reasoning_effort", "medium"));
        await using var conductor = ConductorFor(TestDirs.New(), agent, defaults, peers: true);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = (string)(await NewAsync(client))!["sessionId"]!;

        var spawned = await SpawnAsync(client, agent, parent, new JsonObject
        {
            ["agent"] = "agent",
            ["prompt"] = "write the tests",
            ["config_options"] = new JsonObject { ["model"] = "large", ["reasoning_effort"] = "high" },
        });
        var child = (string)spawned!["sessionId"]!;
        CollectionAssert.AreEqual(new[] { "model:large", "reasoning_effort:high" },
            agent.Sets(child).Select(s => $"{s.Id}:{s.Value}").ToArray(),
            "the spawn's explicit values replace both defaults");
    }

    [TestMethod]
    public async Task Spawn_UnsupportedValue_FailsBeforePrompt_AndCleansUp()
    {
        var agent = new ConfigAgent();
        await using var conductor = ConductorFor(TestDirs.New(), agent, peers: true);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = (string)(await NewAsync(client))!["sessionId"]!;

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(client, agent, parent, new JsonObject
        {
            ["agent"] = "agent",
            ["prompt"] = "write the tests",
            ["config_options"] = new JsonObject { ["model"] = "huge" },
        }));
        StringAssert.Contains(error.Message, "model");
        StringAssert.Contains(error.Message, "\"huge\"");
        var child = agent.LastCreated!;
        Assert.AreNotEqual(parent, child);
        CollectionAssert.Contains(agent.Closes(child), child);
        CollectionAssert.Contains(agent.Deletes(child), child, "a spawn that fails before its prompt leaves no session");
        Assert.AreEqual(0, agent.Prompts(child).Length);
        Assert.IsNull(conductor.Store.Get(child), "nothing was published or stored");
    }

    [TestMethod]
    public async Task Spawn_ConfigOptionsCannotSmuggleAPermissionMode()
    {
        var agent = new ConfigAgent();
        await using var conductor = ConductorFor(TestDirs.New(), agent, peers: true);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = (string)(await NewAsync(client))!["sessionId"]!;

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(client, agent, parent, new JsonObject
        {
            ["agent"] = "agent",
            ["prompt"] = "write the tests",
            ["config_options"] = new JsonObject { ["mode"] = "full-access" },
        }));
        StringAssert.Contains(error.Message, "Permission modes use the mode parameter");
        var child = agent.LastCreated!;
        Assert.AreEqual(0, agent.Sets(child).Length, "neither a model option nor the permission mode was set");
        Assert.IsFalse(agent.Events().Any(e => e.Session == child && e.Kind == "mode"), "no permission mode was changed");
        Assert.AreEqual(0, agent.Prompts(child).Length);
        CollectionAssert.Contains(agent.Deletes(child), child);
    }

    [TestMethod]
    public async Task Spawn_WrongConfigOptionsShape_IsRefusedBeforeCreatingAnything()
    {
        var agent = new ConfigAgent();
        await using var conductor = ConductorFor(TestDirs.New(), agent, peers: true);
        await using var client = await TestClient.ConnectAsync(conductor);
        var parent = (string)(await NewAsync(client))!["sessionId"]!;
        var before = agent.LastCreated;

        var error = await Assert.ThrowsExactlyAsync<JsonRpcException>(() => SpawnAsync(client, agent, parent, new JsonObject
        {
            ["agent"] = "agent",
            ["prompt"] = "write the tests",
            ["config_options"] = new JsonArray("model"),
        }));
        Assert.AreEqual(JsonRpcException.InvalidParams, error.Code);
        StringAssert.Contains(error.Message, "must be a JSON object");
        Assert.AreEqual(before, agent.LastCreated, "nothing was created");
    }

    static string FakeClaude()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
            {
                var config = AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                var exe = Path.Combine(dir.FullName, "tests", "Gorynych.FakeClaude", "bin", config, "net10.0", TestDirs.Exe("Gorynych.FakeClaude"));
                return File.Exists(exe) ? exe : throw new AssertInconclusiveException($"build Gorynych.FakeClaude first: {exe}");
            }
        throw new AssertInconclusiveException("repository root not found for Gorynych.FakeClaude");
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ClaudeHeadless_WaitsForTheCatalogFetch_WhenADefaultNeedsTheModelSelector()
    {
        var dir = TestDirs.New();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ClaudeAdapter(new ClaudeAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "claude"),
            Executable = FakeClaude(),
            StartupTimeoutSeconds = 30,
            CatalogFetchStarted = () => held.Task,
        });
        var defaults = DefaultsFor("claude", ("reasoning_effort", "low"), ("model", "fast"));
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "test",
            SessionDefaults = defaults,
        }, [adapter]);
        await using var client = await TestClient.ConnectAsync(conductor);

        var creating = NewAsync(client, "claude");
        await Task.Delay(300);
        Assert.IsFalse(creating.IsCompleted, "session/new waits for the catalog fetch already under way");

        held.SetResult();
        var created = await creating.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.AreEqual("fast", Current(created, "model"));
        Assert.AreEqual("low", Current(created, "reasoning_effort"));
    }

    /// <summary>
    /// A scripted agent with model, context size, effort and permission mode config options. A model
    /// change changes the efforts it offers, as Claude and Codex do; every set is checked and the
    /// full options are returned, as ACP requires.
    /// </summary>
    sealed class ConfigAgent
    {
        sealed class State
        {
            public string Model = "small";
            public string Effort = "medium";
            public string Context = "200k";
            public string Mode = "ask";
        }

        public sealed record Event(string Session, string Kind, string? Id, string? Value);

        readonly Lock gate = new();
        readonly Dictionary<string, State> sessions = new(StringComparer.Ordinal);
        readonly List<Event> events = [];
        readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);
        int next;

        public ConfigAgent()
        {
            Adapter = new ScriptedAdapter("agent", HandleAsync);
        }

        /// <summary>DSH's model value: one opaque JSON-encoded provider/model pair.</summary>
        public const string OpaqueModel = "[\"deepseek-official\",\"deepseek-flash\"]";

        public ScriptedAdapter Adapter { get; }

        /// <summary>When false the agent advertises no options, like Claude's TUI transport.</summary>
        public bool AdvertisesOptions { get; set; } = true;

        public string? LastCreated { get; private set; }

        public string TokenOf(string session)
        {
            lock (gate) return tokens[session];
        }

        public Event[] Events()
        {
            lock (gate) return [.. events];
        }

        public (string Id, string Value)[] Sets(string session)
        {
            lock (gate) return events.Where(e => e.Session == session && e.Kind == "set").Select(e => (e.Id!, e.Value!)).ToArray();
        }

        public string[] Closes(string session)
        {
            lock (gate) return events.Where(e => e.Session == session && e.Kind == "close").Select(e => e.Session).ToArray();
        }

        public string[] Deletes(string session)
        {
            lock (gate) return events.Where(e => e.Session == session && e.Kind == "delete").Select(e => e.Session).ToArray();
        }

        public string[] Prompts(string session)
        {
            lock (gate) return events.Where(e => e.Session == session && e.Kind == "prompt").Select(e => e.Session).ToArray();
        }

        public string ModelOf(string session)
        {
            lock (gate) return sessions[session].Model;
        }

        public string EffortOf(string session)
        {
            lock (gate) return sessions[session].Effort;
        }

        Task<JsonNode?> HandleAsync(ScriptedAgent agent, JsonRpcRequest request, CancellationToken ct) => request.Method switch
        {
            AcpMethods.Agent.Initialize => Task.FromResult(ScriptedAgent.Init(list: true, resume: true, load: true, close: true, delete: true)),
            AcpMethods.Agent.SessionNew => Task.FromResult<JsonNode?>(New(request)),
            AcpMethods.Agent.SessionSetConfigOption => Task.FromResult<JsonNode?>(Set(request)),
            AcpMethods.Agent.SessionPrompt => Task.FromResult<JsonNode?>(Prompt(request)),
            AcpMethods.Agent.SessionList => Task.FromResult<JsonNode?>(List()),
            AcpMethods.Agent.SessionResume or AcpMethods.Agent.SessionLoad => Task.FromResult<JsonNode?>(StateOf((string)request.Params!["sessionId"]!)),
            AcpMethods.Agent.SessionClose => Task.FromResult<JsonNode?>(Close(request)),
            AcpMethods.Agent.SessionDelete => Task.FromResult<JsonNode?>(Delete(request)),
            AcpMethods.Agent.SessionSetMode => Task.FromResult<JsonNode?>(SetMode(request)),
            _ => throw new JsonRpcException(JsonRpcException.MethodNotFound, request.Method),
        };

        JsonNode New(JsonRpcRequest request)
        {
            string id;
            JsonArray? options = null;
            lock (gate)
            {
                id = "a" + ++next;
                sessions[id] = new State();
                LastCreated = id;
                events.Add(new(id, "new", null, null));
                if (request.Params?["mcpServers"] is JsonArray servers)
                    foreach (var server in servers.OfType<JsonObject>())
                        if ((string?)server["name"] == "gorynych" && server["args"] is JsonArray args && args.Count > 0)
                            tokens[id] = (string)args[args.Count - 1]!;
                if (AdvertisesOptions) options = Options(sessions[id]);
            }
            var response = new JsonObject { ["sessionId"] = id };
            if (options is not null) response["configOptions"] = options;
            return response;
        }

        JsonNode Set(JsonRpcRequest request)
        {
            var id = (string)request.Params!["sessionId"]!;
            var configId = (string)request.Params!["configId"]!;
            var value = (string)request.Params!["value"]!;
            lock (gate)
            {
                events.Add(new(id, "set", configId, value));
                if (!sessions.TryGetValue(id, out var state)) throw new JsonRpcException(JsonRpcException.ResourceNotFound, id);
                switch (configId)
                {
                    case "model":
                        if (value is not ("small" or "large" or OpaqueModel))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, $"no model {value}");
                        state.Model = value;
                        if (!Efforts(state.Model).Contains(state.Effort)) state.Effort = Efforts(state.Model)[0];
                        break;
                    case "reasoning_effort":
                        if (!Efforts(state.Model).Contains(value))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, $"{state.Model} has no effort {value}");
                        state.Effort = value;
                        break;
                    case "context_size":
                        if (value is not ("200k" or "1m"))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, $"no context size {value}");
                        state.Context = value;
                        break;
                    case "mode":
                        if (value is not ("ask" or "full-access"))
                            throw new JsonRpcException(JsonRpcException.InvalidParams, $"no mode {value}");
                        state.Mode = value;
                        break;
                    default:
                        throw new JsonRpcException(JsonRpcException.InvalidParams, $"no option {configId}");
                }
                return new JsonObject { ["configOptions"] = Options(state) };
            }
        }

        JsonNode Prompt(JsonRpcRequest request)
        {
            var id = (string)request.Params!["sessionId"]!;
            lock (gate) events.Add(new(id, "prompt", null, null));
            return new JsonObject { ["stopReason"] = "end_turn" };
        }

        JsonNode List()
        {
            lock (gate)
                return new JsonObject
                {
                    ["sessions"] = new JsonArray(sessions.Keys.Select(id => (JsonNode)new JsonObject
                    {
                        ["sessionId"] = id, ["cwd"] = Cwd,
                    }).ToArray()),
                };
        }

        JsonNode StateOf(string id)
        {
            lock (gate)
                return new JsonObject { ["sessionId"] = id, ["configOptions"] = Options(sessions[id]) };
        }

        JsonNode Close(JsonRpcRequest request)
        {
            var id = (string)request.Params!["sessionId"]!;
            lock (gate) events.Add(new(id, "close", null, null));
            return new JsonObject();
        }

        JsonNode Delete(JsonRpcRequest request)
        {
            var id = (string)request.Params!["sessionId"]!;
            lock (gate)
            {
                events.Add(new(id, "delete", null, null));
                sessions.Remove(id);
            }
            return new JsonObject();
        }

        JsonNode SetMode(JsonRpcRequest request)
        {
            var id = (string)request.Params!["sessionId"]!;
            lock (gate)
            {
                events.Add(new(id, "mode", null, (string?)request.Params?["modeId"]));
                sessions[id].Mode = (string)request.Params!["modeId"]!;
            }
            return new JsonObject();
        }

        static string[] Efforts(string model) => model is "large" or OpaqueModel ? ["low", "high"] : ["low", "medium"];

        static JsonArray Options(State state) => new(
            Select("model", "Model", "model", state.Model,
                new JsonObject { ["value"] = "small", ["name"] = "Small" },
                new JsonObject { ["value"] = "large", ["name"] = "Large" },
                new JsonObject { ["value"] = OpaqueModel, ["name"] = "DeepSeek Flash" }),
            Select("context_size", "Context", "model_config", state.Context,
                new JsonObject { ["value"] = "200k", ["name"] = "200K" },
                new JsonObject { ["value"] = "1m", ["name"] = "1M" }),
            Select("reasoning_effort", "Effort", "thought_level", state.Effort,
                Efforts(state.Model).Select(v => new JsonObject { ["value"] = v, ["name"] = v }).ToArray()),
            Select("mode", "Permissions", "mode", state.Mode,
                new JsonObject { ["value"] = "ask", ["name"] = "Ask" },
                new JsonObject
                {
                    ["value"] = "full-access", ["name"] = "Full access",
                    ["_meta"] = new JsonObject { [GorynychProtocol.MetaFullAccess] = true },
                }));

        static JsonObject Select(string id, string name, string category, string current, params JsonObject[] values) => new()
        {
            ["type"] = "select",
            ["id"] = id,
            ["name"] = name,
            ["category"] = category,
            ["currentValue"] = current,
            ["options"] = new JsonArray(values.Select(v => (JsonNode)v.DeepClone()).ToArray()),
        };
    }
}
