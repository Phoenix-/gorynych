using System.Diagnostics;
using System.Xml.Linq;
using Gorynych.Acp.Hosting;
using Gorynych.Outside;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// How the bridge and the outside server start the service, the logon task they run, and the
/// service executable's command line (docs/service-protocol.md, Endpoint; docs/external-clients.md).
/// </summary>
[TestClass]
public sealed class ServiceLaunchTests
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [TestMethod]
    public void TheTaskStartsTheService_WhenThereIsOne_AndOtherwiseNothingIsStartedFromAnSshLogin()
    {
        Assert.AreEqual(ServiceLaunch.Way.RunTask, ServiceLaunch.Decide(taskExists: true, remoteLogon: true), "ssh, with the task");
        Assert.AreEqual(ServiceLaunch.Way.RunTask, ServiceLaunch.Decide(taskExists: true, remoteLogon: false), "the person's session, with the task");
        Assert.AreEqual(ServiceLaunch.Way.Refuse, ServiceLaunch.Decide(taskExists: false, remoteLogon: true), "ssh, no task");
        Assert.AreEqual(ServiceLaunch.Way.StartProcess, ServiceLaunch.Decide(taskExists: false, remoteLogon: false), "as before");
    }

    [TestMethod]
    public void TheRefusal_SaysWhatToRun_WithThePipeWhenItIsNotTheDefault()
    {
        var plain = ServiceLaunch.Refusal(GorynychProtocol.DefaultPipeName, defaultPipe: true);
        StringAssert.EndsWith(plain, "Gorynych.Service.exe task install");
        StringAssert.Contains(plain, "ssh");
        StringAssert.EndsWith(ServiceLaunch.Refusal("my pipe", defaultPipe: false), "Gorynych.Service.exe task install --pipe \"my pipe\"");
    }

    [TestMethod]
    public void TheTask_RunsTheServiceAtTheUsersLogon_InTheirSession_WithALimitedToken_OnceAtATime_WithNoLimits()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var exe = OperatingSystem.IsWindows() ? @"C:\Program Files\Go & Co\Gorynych.Service.exe" : "/opt/go & co/Gorynych.Service";
        var xml = LogonTask.Xml(sid, exe, ["--pipe", "gorynych-acp-x", "--data", @"C:\My Data\G", "--claude-model", "a\"b", "--no-peers"]);
        Assert.AreEqual("UTF-16", xml.Declaration?.Encoding, "as Task Scheduler exports a task");
        // Parsed back from its text, as schtasks reads it.
        var task = XDocument.Parse(xml.ToString()).Root!;
        Assert.AreEqual(Ns + "Task", task.Name);

        var logon = task.Element(Ns + "Triggers")!.Elements().Single();
        Assert.AreEqual(Ns + "LogonTrigger", logon.Name, "at logon, and nothing else");
        Assert.AreEqual(sid, logon.Element(Ns + "UserId")?.Value, "this user's logon only");
        Assert.AreEqual("true", logon.Element(Ns + "Enabled")?.Value);

        var principal = task.Element(Ns + "Principals")!.Elements().Single();
        Assert.AreEqual(sid, principal.Element(Ns + "UserId")?.Value);
        Assert.AreEqual("InteractiveToken", principal.Element(Ns + "LogonType")?.Value, "in the user's own session, with no password");
        Assert.AreEqual("LeastPrivilege", principal.Element(Ns + "RunLevel")?.Value, "never elevated");

        var settings = task.Element(Ns + "Settings")!;
        Assert.AreEqual("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")?.Value);
        Assert.AreEqual("PT0S", settings.Element(Ns + "ExecutionTimeLimit")?.Value, "no time limit (the default stops it after 72 hours)");
        Assert.AreEqual("false", settings.Element(Ns + "DisallowStartIfOnBatteries")?.Value);
        Assert.AreEqual("false", settings.Element(Ns + "StopIfGoingOnBatteries")?.Value);
        Assert.AreEqual("true", settings.Element(Ns + "AllowStartOnDemand")?.Value, "the bridge runs it");
        Assert.AreEqual("true", settings.Element(Ns + "Enabled")?.Value);
        Assert.AreEqual("4", settings.Element(Ns + "Priority")?.Value, "normal CPU, I/O and memory priority (the default, 7, is low)");

        var actions = task.Element(Ns + "Actions")!;
        Assert.AreEqual((string?)principal.Attribute("id"), (string?)actions.Attribute("Context"));
        var exec = actions.Elements().Single();
        Assert.AreEqual(Ns + "Exec", exec.Name);
        Assert.AreEqual(exe, exec.Element(Ns + "Command")?.Value);
        Assert.AreEqual(Path.GetDirectoryName(exe), exec.Element(Ns + "WorkingDirectory")?.Value);
        Assert.AreEqual("--pipe gorynych-acp-x --data \"C:\\My Data\\G\" --claude-model \"a\\\"b\" --no-peers",
            exec.Element(Ns + "Arguments")?.Value, "quoted as the C runtime splits a command line");

        var bare = XDocument.Parse(LogonTask.Xml(sid, exe, []).ToString()).Root!;
        Assert.IsNull(bare.Descendants(Ns + "Arguments").SingleOrDefault(), "no options, no arguments");
    }

    [TestMethod]
    public void TheTask_IsNamedForTheUserAndPipe_AsTheServicesMutex()
    {
        Assert.AreEqual("Gorynych.Service.gorynych-acp-x", LogonTask.Name("gorynych-acp-x"));
        Assert.AreNotEqual(LogonTask.Name("a"), LogonTask.Name("b"));
    }

    [TestMethod]
    public void NoTask_IsFoundForAPipeNobodyRegistered()
    {
        Assert.IsFalse(LogonTask.Exists("gorynych-test-" + Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void TheCommandLine_TakesOnlyWhatACommandKnows_AndHelpIsNoCommandToRun()
    {
        var serve = CommandLine.Parse([]);
        Assert.IsNull(serve.Command, "no arguments: the service, as before");
        serve = CommandLine.Parse(["--pipe", "p", "--console", "--no-peers", "--unload-after", "0.5"]);
        Assert.AreEqual("p", serve.Value("--pipe"));
        Assert.IsTrue(serve.Flag("--console"));
        Assert.AreEqual("0.5", serve.Value("--unload-after"));

        var acp = CommandLine.Parse(["acp", "--agent", "kimi", "--pipe", "p", "--data", "d", "--no-channel", "--claude-transport", "tui"]);
        Assert.AreEqual("acp", acp.Command);
        Assert.AreEqual("kimi", acp.Value("--agent"));
        CollectionAssert.AreEqual(new[] { "--data", "d", "--no-channel", "--claude-transport", "tui" }, acp.ServiceArguments("--pipe"),
            "what the bridge passes on to a service it starts, in order");

        var install = CommandLine.Parse(["task", "install", "--pipe", "p", "--data", "d", "--no-peers"]);
        Assert.AreEqual(("task", "install"), (install.Command, install.Action));
        CollectionAssert.AreEqual(new[] { "--pipe", "p", "--data", "d", "--no-peers" }, install.ServiceArguments());
        Assert.AreEqual("uninstall", CommandLine.Parse(["task", "uninstall", "--pipe", "p"]).Action);
        Assert.AreEqual("t", CommandLine.Parse(["peers", "--pipe", "p", "--token", "t", "--reply-url", "u"]).Value("--token"));
        Assert.AreEqual("p", CommandLine.Parse(["shutdown", "--pipe", "p"]).Value("--pipe"));
        Assert.AreEqual("--help", CommandLine.Parse(["--claude-model", "--help"]).Value("--claude-model"), "a value is a value");

        foreach (var help in new[] { new[] { "--help" }, ["-h"], ["/?"], ["help"], ["acp", "--agent", "kimi", "-?"], ["task", "--help"], ["task", "install", "--help"] })
            Assert.IsNull(Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(help)).Problem, string.Join(' ', help));

        foreach (var (wrong, problem) in new[]
        {
            (new[] { "--bogus" }, "unknown option '--bogus'"),
            (["serve"], "unknown command 'serve'"),
            (["--pipe"], "--pipe needs a value"),
            (["--pipe", "a", "--pipe", "b"], "--pipe is given twice"),
            (["acp", "--pipe", "p"], "acp needs --agent"),
            (["acp", "--agent", "kimi", "--console"], "acp does not take '--console'"),
            (["shutdown", "--data", "d"], "shutdown does not take '--data'"),
            (["task"], "task takes install or uninstall"),
            (["task", "start"], "task takes install or uninstall, not 'start'"),
            (["task", "install", "--console"], "task install does not take '--console'"),
            (["task", "uninstall", "--data", "d"], "task uninstall does not take '--data'"),
            (["--claude-transport", "pty"], "--claude-transport takes headless or tui, not 'pty'"),
            (["--claude-channels", "yes"], "--claude-channels takes ask, accept or decline, not 'yes'"),
            (["task", "install", "--unload-after", "-1"], "--unload-after takes a number of minutes, 0 or more, not '-1'"),
        })
            Assert.AreEqual(problem, Assert.ThrowsExactly<UsageException>(() => CommandLine.Parse(wrong)).Problem, string.Join(' ', wrong));
    }

    /// <summary>The executable itself: a wrong command line or <c>--help</c> prints the usage and exits with 2, and never opens the pipe.</summary>
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task TheExecutable_PrintsTheUsage_ForHelpAndUnknownArguments_AndRunsNoService()
    {
        foreach (var extra in new[] { "--help", "--bogus" })
        {
            var pipe = "gorynych-test-" + Guid.NewGuid().ToString("N")[..8];
            using var process = Process.Start(new ProcessStartInfo(BridgeTests.ServiceExe(), ["--pipe", pipe, "--data", TestDirs.New(), extra])
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            })!;
            try
            {
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.IsTrue(process.WaitForExit(10_000), $"{extra}: it exits instead of serving");
                Assert.AreEqual(CommandLine.UsageExitCode, process.ExitCode, extra);
                StringAssert.Contains(await stderr, "Usage:", extra);
                if (extra == "--bogus") StringAssert.Contains(await stderr, "unknown option '--bogus'");
                Assert.IsNull(await ServicePipe.ConnectAsync(pipe, TimeSpan.FromMilliseconds(500)), $"{extra}: no service on the pipe");
            }
            finally
            {
                if (!process.HasExited) process.Kill();
            }
        }
    }
}
