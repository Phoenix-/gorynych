using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Gorynych.Service;

namespace Gorynych.Tests;

/// <summary>
/// <c>update</c> (docs/getting-started.md, "Update"): a package is downloaded and staged first, its
/// layout, version, OS, architecture and archive paths are checked, the running build is compared,
/// dry-run mutates nothing, a busy service without hosts is refused, and an activation failure
/// restores the previous files. Only Gorynych is touched.
/// </summary>
[TestClass]
public sealed class UpdateTests
{
    static string ServiceName => OperatingSystem.IsWindows() ? "Gorynych.Service.exe" : "Gorynych.Service";
    static string CurrentOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
    static string CurrentArch => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch
    {
        "x64" => "x64",
        "arm64" => "arm64",
        var other => other,
    };

    static byte[] NativeImage(byte fill = 0x42)
    {
        var bytes = new byte[512];
        Array.Fill(bytes, fill);
        if (OperatingSystem.IsWindows())
        {
            bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
            bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
            BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, 0x84);
        }
        else if (OperatingSystem.IsLinux())
        {
            bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
            bytes[4] = 2; bytes[5] = 1; bytes[6] = 1;
            BitConverter.GetBytes((ushort)0x3E).CopyTo(bytes, 18);
        }
        else
        {
            BitConverter.GetBytes(0xFEEDFACFu).CopyTo(bytes, 0);
            BitConverter.GetBytes(0x01000007u).CopyTo(bytes, 4);
        }
        return bytes;
    }

    static string NewInstall(string? serviceBytes = null)
    {
        var dir = TestDirs.New();
        File.WriteAllBytes(Path.Combine(dir, ServiceName), serviceBytes is null ? NativeImage() : Convert.FromBase64String(serviceBytes));
        return dir;
    }

    static string Package(string? version = null, string? os = null, string? arch = null, string? build = null,
        Dictionary<string, byte[]>? extra = null, bool manifest = true)
    {
        var path = Path.Combine(TestDirs.New(), "gorynych.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var image = NativeImage(0x43);
        Add(zip, ServiceName, image);
        if (manifest)
        {
            var json = new JsonObject
            {
                ["schemaVersion"] = 1, ["product"] = "gorynych", ["version"] = version ?? "9.9.9",
                ["os"] = os ?? CurrentOs, ["arch"] = arch ?? CurrentArch,
                ["entry"] = ServiceName, ["build"] = build ?? "9.9.9+package",
            };
            Add(zip, "gorynych-package.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        foreach (var (name, bytes) in extra ?? []) Add(zip, name, bytes);
        return path;
    }

    static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    static UpdateCommand.Hooks NoService() => new(
        _ => Task.FromResult<UpdateCommand.ServiceInfo?>(null),
        (_, _) => Task.FromResult(new UpdateCommand.ActivationResult(false, "not expected", null)),
        (_, _) => Task.FromResult(true));

    static UpdateCommand.ServiceInfo Running(string install, string build, bool hosts, JsonArray? sessions = null) => new(new JsonObject
    {
        ["processId"] = 4242,
        ["processPath"] = Path.Combine(install, ServiceName),
        ["version"] = "1.0.0",
        ["build"] = build,
        ["hosts"] = hosts,
        ["restarting"] = false,
        ["maintenance"] = new JsonObject { ["stoppedAll"] = false, ["stoppedAgents"] = new JsonArray(), ["stopInProgress"] = false },
        ["sessions"] = sessions ?? new JsonArray(),
    });

    static async Task<int> Run(string package, string install, UpdateCommand.Hooks hooks, bool dryRun = false) =>
        await UpdateCommand.RunAsync(package, dryRun, install, "gorynych-test-" + Guid.NewGuid().ToString("N")[..8],
            TestDirs.New(), hooks, CancellationToken.None);

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALocalArchive_DryRun_ReportsThePlan_AndTouchesNothing()
    {
        var install = NewInstall();
        var before = File.ReadAllBytes(Path.Combine(install, ServiceName));
        var package = Package();
        var code = await Run(package, install, NoService(), dryRun: true);
        Assert.AreEqual(0, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(install, ServiceName)), "dry-run does not change the install");
        Assert.AreEqual(0, Directory.GetDirectories(install, ".gorynych-update-*").Length, "dry-run creates no staging folder in the install");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALocalArchive_IsStagedAndCopied_WithRollbackCopiesPreserved()
    {
        var install = NewInstall();
        var before = File.ReadAllBytes(Path.Combine(install, ServiceName));
        var package = Package();
        var code = await Run(package, install, NoService());
        Assert.AreEqual(0, code);
        var after = File.ReadAllBytes(Path.Combine(install, ServiceName));
        CollectionAssert.AreNotEqual(before, after, "the new service binary was installed");
        Assert.IsTrue(File.Exists(Path.Combine(install, "gorynych-package.json")), "the package contents were copied");
        var old = Directory.GetFiles(install).Where(f => f.Contains(".old", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.AreEqual(1, old.Count, "the old service binary is kept for rollback");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(old[0]));
        if (!OperatingSystem.IsWindows())
            Assert.IsTrue((File.GetUnixFileMode(Path.Combine(install, ServiceName)) & UnixFileMode.UserExecute) != 0,
                "the updated service is executable on Unix");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AHttpArchive_IsDownloadedAndApplied()
    {
        var install = NewInstall();
        var package = Package();
        using var server = new TinyHttp(File.ReadAllBytes(package));
        var code = await UpdateCommand.RunAsync(server.Url, dryRun: false, install,
            "gorynych-test-" + Guid.NewGuid().ToString("N")[..8], TestDirs.New(), NoService(), CancellationToken.None);
        Assert.AreEqual(0, code, server.Requests > 0 ? "the local test server served the package" : "no HTTP request");
        Assert.IsTrue(server.Requests > 0);
        CollectionAssert.AreEqual(NativeImage(0x43), File.ReadAllBytes(Path.Combine(install, ServiceName)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnArchivePathThatEscapes_IsRejected_AndNothingChanges()
    {
        var install = NewInstall();
        var before = File.ReadAllBytes(Path.Combine(install, ServiceName));
        var path = Path.Combine(TestDirs.New(), "evil.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, ServiceName, NativeImage(0x43));
            Add(zip, "../escape.txt", Encoding.UTF8.GetBytes("no"));
        }
        var code = await Run(path, install, NoService());
        Assert.AreEqual(1, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(install, ServiceName)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ASymlinkEntry_IsRejected()
    {
        var install = NewInstall();
        var path = Path.Combine(TestDirs.New(), "symlink.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, ServiceName, NativeImage());
            var link = zip.CreateEntry("Gorynych.Service.link");
            link.ExternalAttributes = unchecked((int)(0xA000u << 16));
            using var stream = link.Open();
            stream.Write(Encoding.UTF8.GetBytes("../../outside"));
        }
        Assert.AreEqual(1, await Run(path, install, NoService()));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AWrongPlatformOrArchitecture_IsRejectedBeforeTheInstallIsTouched()
    {
        var install = NewInstall();
        var otherOs = CurrentOs == "windows" ? "linux" : "windows";
        Assert.AreEqual(1, await Run(Package(os: otherOs), install, NoService()), "wrong OS");
        var otherArch = CurrentArch == "x64" ? "arm64" : "x64";
        Assert.AreEqual(1, await Run(Package(arch: otherArch), install, NoService()), "wrong architecture");
        Assert.AreEqual(0, Directory.GetFiles(install, "*", SearchOption.AllDirectories)
            .Count(f => f.Contains("gorynych-package.json", StringComparison.OrdinalIgnoreCase)), "nothing was copied");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnAmbiguousLayout_OrAnUnsupportedOne_IsRejected()
    {
        var install = NewInstall();
        var path = Path.Combine(TestDirs.New(), "ambiguous.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, ServiceName, NativeImage());
            Add(zip, "other/" + ServiceName, NativeImage());
        }
        Assert.AreEqual(1, await Run(path, install, NoService()));
        var empty = Path.Combine(TestDirs.New(), "empty.zip");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create)) Add(zip, "README.txt", Encoding.UTF8.GetBytes("no"));
        Assert.AreEqual(1, await Run(empty, install, NoService()));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AManifestHashThatDoesNotMatch_IsRejected()
    {
        var install = NewInstall();
        var path = Path.Combine(TestDirs.New(), "badhash.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(zip, ServiceName, NativeImage());
            var json = new JsonObject
            {
                ["schemaVersion"] = 1, ["product"] = "gorynych", ["version"] = "9.9.9",
                ["os"] = CurrentOs, ["arch"] = CurrentArch, ["entry"] = ServiceName,
                ["files"] = new JsonObject { [ServiceName] = new string('0', 64) },
            };
            Add(zip, "gorynych-package.json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        }
        Assert.AreEqual(1, await Run(path, install, NoService()));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ActiveWorkWithoutHosts_IsRefused_AndNothingIsChanged()
    {
        var install = NewInstall();
        var before = File.ReadAllBytes(Path.Combine(install, ServiceName));
        var sessions = new JsonArray
        {
            new JsonObject
            {
                ["agent"] = "codex", ["sessionId"] = "s1", ["turnRunning"] = true,
                ["blockers"] = new JsonArray("a turn is running"),
            },
        };
        var hooks = new UpdateCommand.Hooks(
            _ => Task.FromResult<UpdateCommand.ServiceInfo?>(Running(install, "1.0.0+old", hosts: false, sessions)),
            (_, _) => Task.FromResult(new UpdateCommand.ActivationResult(false, "not expected", null)),
            (_, _) => Task.FromResult(true));
        var code = await Run(Package(), install, hooks);
        Assert.AreEqual(UpdateCommand.BusyExitCode, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(install, ServiceName)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnActivationFailure_RollsTheInstallBack()
    {
        var install = NewInstall();
        var before = File.ReadAllBytes(Path.Combine(install, ServiceName));
        var hooks = new UpdateCommand.Hooks(
            _ => Task.FromResult<UpdateCommand.ServiceInfo?>(Running(install, "1.0.0+old", hosts: true)),
            (_, _) => Task.FromResult(new UpdateCommand.ActivationResult(false, "the new service did not answer", null)),
            (_, _) => Task.FromResult(true));
        var code = await Run(Package(), install, hooks);
        Assert.AreEqual(1, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(install, ServiceName)), "the old binary is back");
        Assert.IsFalse(File.Exists(Path.Combine(install, "gorynych-package.json")), "the new files were rolled back");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task AnActivationSuccess_VerifiesTheRunningBuild()
    {
        var install = NewInstall();
        var old = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(install, ServiceName), old);
        var hooks = new UpdateCommand.Hooks(
            _ => Task.FromResult<UpdateCommand.ServiceInfo?>(Running(install, "1.0.0+old", hosts: true)),
            (plan, _) => Task.FromResult(new UpdateCommand.ActivationResult(true, null,
                Running(install, plan.Package.Build!, hosts: true))),
            (_, _) => Task.FromResult(true));
        Assert.AreEqual(0, await Run(Package(), install, hooks));
        CollectionAssert.AreEqual(NativeImage(0x43), File.ReadAllBytes(Path.Combine(install, ServiceName)));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ALockedBinary_IsADryRunBlocker()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("lock probing with delete sharing is Windows-specific");
        var install = NewInstall();
        var path = Path.Combine(install, ServiceName);
        var before = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var code = await Run(Package(), install, NoService(), dryRun: true);
        Assert.AreEqual(UpdateCommand.BusyExitCode, code);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    /// <summary>A minimal HTTP/1.1 server for the local update tests; no external traffic.</summary>
    sealed class TinyHttp : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task serving;
        readonly byte[] body;
        int requests;

        public TinyHttp(byte[] body)
        {
            this.body = body;
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/gorynych.zip";
            serving = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public int Requests => Volatile.Read(ref requests);

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = client;
                    try
                    {
                        var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = new StringBuilder();
                        while (await reader.ReadLineAsync(stop.Token) is { } line && line.Length > 0) request.AppendLine(line);
                        Interlocked.Increment(ref requests);
                        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), stop.Token);
                        await stream.WriteAsync(body, stop.Token);
                        await stream.FlushAsync(stop.Token);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
                });
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            try { serving.Wait(TimeSpan.FromSeconds(2)); } catch (Exception e) when (e is AggregateException or TimeoutException) { }
            stop.Dispose();
        }
    }
}
