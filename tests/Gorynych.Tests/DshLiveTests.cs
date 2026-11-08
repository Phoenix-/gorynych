using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Adapters.Dsh;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// Opt-in check of the DSH adapter against the real <c>dsh --profile acp</c> on this machine, with the
/// user's own DeepSeek key, the real gorynych MCP server and a real service pipe. Run with
/// <c>GORYNYCH_LIVE=1</c>. Four short turns on <c>deepseek-v4-flash</c> at effort low, in a temporary
/// folder. DSH cannot delete a session over ACP, so the session stays in <c>~/.dsh</c>; its ID is
/// appended to <c>%TEMP%/gorynych-tests/dsh-live-sessions.txt</c>.
/// </summary>
[TestClass]
[TestCategory("Live")]
public sealed partial class DshLiveTests
{
    static void RequireLive()
    {
        if (Environment.GetEnvironmentVariable("GORYNYCH_LIVE") != "1") Assert.Inconclusive("set GORYNYCH_LIVE=1 to run live agents");
        if (DshAdapter.FindPackage() is null) Assert.Inconclusive("dsh is not on PATH");
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("the ACL check is Windows only");
    }

    static string ServiceExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Gorynych.slnx")))
                return Directory.EnumerateFiles(Path.Combine(dir.FullName, "src", "Gorynych.Service", "bin"), "Gorynych.Service.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First();
        throw new InvalidOperationException("repository root not found");
    }

    [LibraryImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetNamedSecurityInfo(string name, int type, int info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ToSddl(IntPtr descriptor, int revision, int info, out IntPtr text, IntPtr length);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    /// <summary>A folder's DACL and mandatory label as SDDL, which DSH's ACL sandbox would change.</summary>
    static string Sddl(string path)
    {
        const int dacl = 0x4, label = 0x10;
        Assert.AreEqual(0u, GetNamedSecurityInfo(path, 1, dacl | label, 0, 0, 0, 0, out var descriptor));
        try
        {
            Assert.IsTrue(ToSddl(descriptor, 1, dacl | label, out var text, 0));
            try { return Marshal.PtrToStringUni(text)!; }
            finally { LocalFree(text); }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [TestMethod]
    [Timeout(600_000, CooperativeCancellation = true)]
    public async Task Dsh_PeerTools_AskBeforeAskAgent_ReadOnlyShell_AndApprovedFileWrites_WithoutTouchingTheAcl()
    {
        RequireLive();
        var dir = TestDirs.New();
        var folder = Path.Combine(dir, "work");
        Directory.CreateDirectory(folder);
        var pipe = "gorynych-live-" + Guid.NewGuid().ToString("N")[..8];
        var log = new List<string>();
        var echo = new LifecycleAgent("echo", canDelete: true) { Answer = "pong" };
        await using var conductor = new Conductor(new ConductorOptions
        {
            DataDirectory = dir,
            ServiceId = "live",
            PeerServerCommand = [ServiceExe(), "peers", "--pipe", pipe],
        }, [new DshAdapter(new DshAdapterOptions
        {
            DataDirectory = Path.Combine(dir, "dsh"),
            Instructions = PeersServer.SystemPrompt,
            Log = m => { lock (log) log.Add(m); },
        }), echo.Adapter]);
        using var stop = new CancellationTokenSource();
        var listening = PipeListener.RunAsync(conductor, pipe, stop.Token);
        try
        {
            await using var client = await TestClient.ConnectAsync(conductor);
            client.Approve = r => Task.FromResult<RequestPermissionOutcome>(new SelectedPermissionOutcome
            {
                OptionId = r.ToolCall.Title?.StartsWith("Ask ", StringComparison.Ordinal) == true
                    ? "decline"
                    : r.Options.First(o => o.Kind == PermissionOptionKind.AllowOnce).OptionId,
            });
            var target = (await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = folder, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "echo" },
            })).SessionId;
            await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                new PromptRequest { SessionId = target, Prompt = [new TextContent { Text = "hello" }] });

            var before = Sddl(folder);
            var created = await client.Rpc.RequestAsync<NewSessionRequest, NewSessionResponse>(AcpMethods.Agent.SessionNew, new NewSessionRequest
            {
                Cwd = folder, McpServers = [], Meta = new JsonObject { [GorynychProtocol.MetaAgent] = "dsh" },
            });
            var id = created.SessionId;
            Directory.CreateDirectory(Path.GetDirectoryName(SessionsFile)!);
            File.AppendAllText(SessionsFile, $"{DateTime.Now:O}\t{id}\t{folder}\n");
            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionSetConfigOption,
                new JsonObject { ["sessionId"] = id, ["configId"] = "reasoning_effort", ["value"] = "low" });

            async Task<PromptResponse> PromptAsync(string text)
            {
                var response = await client.Rpc.RequestAsync<PromptRequest, PromptResponse>(AcpMethods.Agent.SessionPrompt,
                    new PromptRequest { SessionId = id, Prompt = [new TextContent { Text = text }] });
                Console.WriteLine($"> {text}\n< {response.StopReason}: {LastAnswer()}");
                return response;
            }
            string LastAnswer() => (string?)client.Snapshot().LastOrDefault(u => (string?)u["sessionUpdate"] == "agent_message_chunk")?["content"]?["text"] ?? "";
            JsonObject[] Calls(string title) => client.Snapshot().Where(u => (string?)u["title"] == title).ToArray();

            // 1. The real peers server through the real pipe: list_agents shows the echo session.
            await PromptAsync("Call the tool mcp__gorynych__list_agents once, then reply with one word: FOUND if it lists a session of agent echo, else NONE.");
            var listCall = Calls("mcp__gorynych__list_agents").FirstOrDefault();
            Assert.IsNotNull(listCall, "called list_agents: " + string.Join(" | ", log));
            var listed = client.Snapshot().Last(u => (string?)u["toolCallId"] == (string?)listCall["toolCallId"]);
            Assert.AreEqual("completed", (string?)listed["status"]);
            StringAssert.Contains(listed["content"]?.ToJsonString(), target, "the list came from the service");
            Assert.AreEqual(0, client.Approvals.Count, "list_agents asks nobody");

            // 2. ask_agent from DSH, which asks nothing itself: the service asks the person, who declines.
            await PromptAsync($"Call the tool mcp__gorynych__ask_agent once with session_id \"{target}\" and question \"ping\". Then reply in one short sentence with what the tool returned.");
            var ask = client.Approvals.SingleOrDefault(a => a.ToolCall.Title?.StartsWith("Ask ", StringComparison.Ordinal) == true);
            Assert.IsNotNull(ask, "the person was asked before ask_agent");
            Assert.AreEqual(id, ask.SessionId);
            lock (echo.Prompts) Assert.AreEqual(1, echo.Prompts.Count, "the declined question reached no one");

            // 3. The shell cannot write, and its escalation is refused without asking.
            var asked = client.Approvals.Count;
            var shell = await PromptAsync("Using your pwsh tool, run exactly: Set-Content -Path blocked.txt -Value x . If it is denied, retry the same " +
                "pwsh command once with sandbox_permissions \"workspace-write\" and a justification, even though you were told it is refused. " +
                "Then reply in one short sentence with what the second call returned.");
            Assert.AreEqual(StopReason.EndTurn, shell.StopReason, "the turn ends normally after the refusal");
            Assert.IsFalse(File.Exists(Path.Combine(folder, "blocked.txt")), "pwsh wrote nothing");
            Assert.AreEqual(asked, client.Approvals.Count, "no shell escalation reached the person");
            var escalated = client.Snapshot().Any(u => (string?)u["sessionUpdate"] == "tool_call" && u["rawInput"]?["sandbox_permissions"] is not null);
            lock (log) Console.WriteLine($"escalation tried: {escalated}; refused: {log.Any(l => l.Contains("refused the sandbox escalation", StringComparison.Ordinal))}");

            // 4. A file-tool write asks the person, who allows it; the folder's ACL and label stay as they were.
            await PromptAsync("Create the file note.txt containing exactly the word hi, with your write tool. Then reply DONE.");
            var write = client.Approvals.Skip(asked).FirstOrDefault();
            Assert.IsNotNull(write, "the write asked: " + string.Join(" | ", log));
            Assert.AreEqual(ToolKind.Edit, write.ToolCall.Kind);
            StringAssert.Contains(write.ToolCall.Title, "note.txt");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "note.txt")), "written once allowed");
            Assert.AreEqual(before, Sddl(folder), "DSH's ACL sandbox changed nothing on the folder");
            Console.WriteLine($"dsh session {id}; approvals: {string.Join(" | ", client.Approvals.Select(a => a.ToolCall.Title))}");

            await client.Rpc.SendRequestAsync(AcpMethods.Agent.SessionClose, new JsonObject { ["sessionId"] = id });
        }
        finally
        {
            stop.Cancel();
            await listening;
        }
    }

    static string SessionsFile => Path.Combine(Path.GetTempPath(), "gorynych-tests", "dsh-live-sessions.txt");
}
