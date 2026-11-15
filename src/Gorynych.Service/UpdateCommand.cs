using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Gorynych.Acp;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;
using Gorynych.Service.Hosts;

namespace Gorynych.Service;

/// <summary>
/// <c>update &lt;local-archive-or-url&gt; [--dry-run] [--install &lt;dir&gt;]</c>: updates only Gorynych.
/// The package is downloaded and extracted first, its layout, version, OS and architecture are
/// checked, and only then is the active install touched: new files are staged, the running service
/// is restarted onto them (adapter hosts keep their turns), readiness is verified, and a failure is
/// rolled back. Harness installs, including npm packages, are not touched.
/// </summary>
internal static class UpdateCommand
{
    public const int BusyExitCode = 4;
    public const int FailedExitCode = 1;

    internal sealed record ServiceInfo(JsonObject Status)
    {
        public string? ProcessPath => (string?)Status["processPath"];
        public string? Version => (string?)Status["version"];
        public string? Build => (string?)Status["build"];
        public int ProcessId => (int?)Status["processId"] ?? 0;
        public bool Hosts => (bool?)Status["hosts"] == true;
    }

    internal sealed record ActivationResult(bool Ok, string? Error, ServiceInfo? Service);

    /// <summary>Test seams: a fake status, activation or service start, so rollback can be tested without a live install.</summary>
    internal sealed record Hooks(
        Func<CancellationToken, Task<ServiceInfo?>> Status,
        Func<UpdatePlan, CancellationToken, Task<ActivationResult>> Activate,
        Func<UpdatePlan, CancellationToken, Task<bool>> StartService);

    internal sealed record UpdatePlan(
        string Source,
        string InstallDirectory,
        string ServiceEntry,
        GorynychPackage Package,
        ServiceInfo? Running,
        string? DiskVersion,
        string? DiskBuild,
        bool Hosted,
        bool UpToDate,
        IReadOnlyList<string> Blockers,
        IReadOnlyList<PackageFile> Files,
        long TotalBytes);

    public static async Task<int> RunAsync(CommandLine line)
    {
        var source = line.Arguments[0];
        var install = line.Value("--install");
        var pipe = line.Value("--pipe") ?? GorynychProtocol.DefaultPipeName;
        var data = ManagementCommand.DataDirectory(line);
        return await RunAsync(source, line.Flag("--dry-run"), install, pipe, data, hooks: null, CancellationToken.None);
    }

    internal static async Task<int> RunAsync(string source, bool dryRun, string? install, string pipe, string dataDirectory,
        Hooks? hooks, CancellationToken ct)
    {
        StagedPackage staged;
        try
        {
            staged = await StagedPackage.StageAsync(source, ct);
        }
        catch (Exception e) when (e is PackageException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"gorynych update: {e.Message}");
            return FailedExitCode;
        }
        using (staged)
        {
            var package = staged.Package;
            Console.WriteLine($"gorynych update: package {package.Version}" + (package.Build is null ? "" : $" (build {package.Build})") +
                $", {package.Os}/{package.Arch}, {package.Files.Count} file(s), " + (package.FromManifest ? "manifest" : "legacy layout"));

            var status = hooks is null ? await QueryStatusAsync(pipe, ct) : await hooks.Status(ct);
            var directory = install is null ? InferInstall(status) : Path.GetFullPath(install);
            if (directory is null)
            {
                Console.Error.WriteLine("gorynych update: cannot infer the install folder (no running service and this executable is not Gorynych.Service). Pass --install <dir>.");
                return FailedExitCode;
            }
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"gorynych update: the install folder '{directory}' does not exist; update only installs over an existing Gorynych folder.");
                return FailedExitCode;
            }
            var serviceEntry = Path.Combine(directory, package.ServiceEntry);
            if (!File.Exists(serviceEntry))
            {
                Console.Error.WriteLine($"gorynych update: the install folder '{directory}' has no {package.ServiceEntry}; it is not a Gorynych install this package can update.");
                return FailedExitCode;
            }
            if (status is not null && status.ProcessPath is { } runningPath
                && !SameDirectory(Path.GetDirectoryName(runningPath), directory))
            {
                Console.Error.WriteLine($"gorynych update: the running service is at '{runningPath}', not in '{directory}'. " +
                    "Refusing to update a different install; pass the running service's folder or stop that service first.");
                return FailedExitCode;
            }

            var disk = HostGeneration.OnDisk(serviceEntry);
            var blockers = Blockers(status, directory, package.Files);
            var upToDate = (status?.Build is { } runningBuild && runningBuild == package.Build)
                || (status is null && disk.Build == package.Build);
            var plan = new UpdatePlan(source, directory, package.ServiceEntry, package, status, disk.Version, disk.Build,
                status?.Hosts == true, upToDate, blockers, package.Files, package.Files.Sum(f => f.Length));
            PrintPlan(plan);
            if (dryRun)
            {
                Console.WriteLine(blockers.Count == 0
                    ? "gorynych update: dry run; the plan is usable and nothing was changed."
                    : "gorynych update: dry run; resolve the blockers above first. Nothing was changed.");
                return blockers.Count == 0 ? 0 : BusyExitCode;
            }
            if (blockers.Count > 0)
            {
                Console.Error.WriteLine("gorynych update: refusing to change the install while the blockers above remain; nothing was changed or stopped.");
                return BusyExitCode;
            }
            if (upToDate)
            {
                Console.WriteLine("gorynych update: the running/on-disk build already matches the package; nothing to do.");
                return 0;
            }
            try
            {
                return await ApplyAsync(plan, pipe, dataDirectory, hooks, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageException)
            {
                Console.Error.WriteLine($"gorynych update: {e.Message}");
                return FailedExitCode;
            }
        }
    }

    static string? InferInstall(ServiceInfo? status)
    {
        if (status?.ProcessPath is { } path) return Path.GetDirectoryName(Path.GetFullPath(path));
        var exe = Environment.ProcessPath;
        if (exe is null) return null;
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Equals("Gorynych.Service", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(Path.GetFullPath(exe)) : null;
    }

    static bool SameDirectory(string? a, string b) =>
        a is not null && Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static List<string> Blockers(ServiceInfo? status, string directory, IReadOnlyList<PackageFile> files)
    {
        var blockers = new List<string>();
        if (status is not null)
        {
            if ((bool?)status.Status["restarting"] == true) blockers.Add("a restart is in progress");
            var maintenance = status.Status["maintenance"] as JsonObject;
            if ((bool?)maintenance?["stopInProgress"] == true) blockers.Add("a management stop is in progress");
            if (!status.Hosts)
            {
                var details = status.Status["sessionDetails"] as JsonArray ?? status.Status["sessions"] as JsonArray ?? [];
                var active = details.OfType<JsonObject>()
                    .Where(s => (bool?)s["turnRunning"] == true || ((s["blockers"] as JsonArray ?? []).Count > 0))
                    .Select(s => $"{(string?)s["agent"]} {(string?)s["sessionId"]}: {string.Join("; ", (s["blockers"] as JsonArray ?? []).Select(b => (string?)b).OfType<string>())}")
                    .ToList();
                if (active.Count > 0)
                {
                    blockers.Add($"this service runs adapters in its own process, so a restart would interrupt active work ({active.Count} session(s)):");
                    blockers.AddRange(active.Select(a => "  " + a));
                    blockers.Add("wait for that work to end (or start the service with --hosts on) before updating");
                }
            }
            var stopped = (maintenance?["stoppedAgents"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>().ToList();
            if (stopped.Count > 0 || (bool?)maintenance?["stoppedAll"] == true)
                Console.WriteLine($"gorynych update: note: {((bool?)maintenance?["stoppedAll"] == true ? "everything" : string.Join(", ", stopped))} " +
                    "stopped by a management command stays stopped after the update.");
        }
        foreach (var file in files)
        {
            var target = Path.Combine(directory, file.RelativePath);
            if (Directory.Exists(target))
            {
                blockers.Add($"the package file '{file.RelativePath}' would replace a folder");
                continue;
            }
            if (File.Exists(target) && !CanReplace(target))
                blockers.Add($"'{file.RelativePath}' is locked by a running process (a bridge, an outside server or an agent); close it or restart it, then try again");
        }
        return blockers;
    }

    /// <summary>
    /// Whether a file may be renamed or overwritten now. On Windows an open with delete sharing
    /// succeeds only when every holder allows a rename; probing that is read-only and safe in a dry run.
    /// </summary>
    static bool CanReplace(string path)
    {
        if (!OperatingSystem.IsWindows()) return true;
        // A rename needs DELETE access. Asking for it now proves the file can be moved aside, and
        // opening it with every share mode does not disturb the process that holds it.
        var handle = Windows.CreateFileW(path, Windows.GenericRead | Windows.Delete, Windows.ShareAll,
            nint.Zero, Windows.OpenExisting, Windows.FileAttributeNormal, nint.Zero);
        if (handle == -1 || handle == 0) return false;
        Windows.CloseHandle(handle);
        return true;
    }

    static class Windows
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint Delete = 0x00010000;
        internal const uint ShareAll = 0x1 | 0x2 | 0x4;
        internal const uint OpenExisting = 3;
        internal const uint FileAttributeNormal = 0x80;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern nint CreateFileW(string name, uint access, uint share, nint security,
            uint disposition, uint flags, nint template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }

    static void PrintPlan(UpdatePlan plan)
    {
        Console.WriteLine($"gorynych update: install {plan.InstallDirectory} ({plan.ServiceEntry})");
        Console.WriteLine($"  running: {(plan.Running is null ? "no service" : $"process {plan.Running.ProcessId}, version {plan.Running.Version ?? "unknown"}, build {plan.Running.Build ?? "unknown"}")}");
        Console.WriteLine($"  on disk: version {plan.DiskVersion ?? "unknown"}, build {plan.DiskBuild ?? "unknown"}");
        Console.WriteLine($"  package: version {plan.Package.Version}, build {plan.Package.Build ?? "unknown"}, {plan.Package.Files.Count} file(s), {plan.TotalBytes} bytes");
        Console.WriteLine($"  state: {(plan.UpToDate ? "already up to date" : plan.Hosted ? "hosted restart keeps running turns" : "no hosts; active work must be idle")}");
        var replaced = plan.Files.Count(f => File.Exists(Path.Combine(plan.InstallDirectory, f.RelativePath)));
        Console.WriteLine($"  plan: stage, keep {replaced} old file(s) as rollback copies, copy {plan.Files.Count} file(s), " +
            (plan.Running is null ? "leave the service stopped" : "restart the conductor and verify its build") + ", then let idle sessions move");
        if (plan.Blockers.Count > 0)
        {
            Console.Error.WriteLine("gorynych update: blockers:");
            foreach (var blocker in plan.Blockers) Console.Error.WriteLine("  " + blocker);
        }
    }

    static async Task<int> ApplyAsync(UpdatePlan plan, string pipe, string dataDirectory, Hooks? hooks, CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var staging = Path.Combine(plan.InstallDirectory, ".gorynych-update-" + stamp);
        var newFiles = Path.Combine(staging, "new");
        Directory.CreateDirectory(newFiles);
        var backups = new List<(string Target, string? Backup)>();
        try
        {
            // 1. Stage every new file in the target volume first.
            foreach (var file in plan.Files)
            {
                var staged = Path.Combine(newFiles, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.Copy(file.FullPath, staged, overwrite: true);
            }
            // 2. Rename the old files aside and copy the new ones in. A failure restores everything.
            foreach (var file in plan.Files)
            {
                var target = Path.Combine(plan.InstallDirectory, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = BackupName(target, stamp);
                    File.Move(target, backup);
                }
                backups.Add((target, backup));
                var staged = Path.Combine(newFiles, file.RelativePath);
                File.Move(staged, target);
                // A Unix package's executable bit is not carried by the extraction/copy path used
                // here: make the service and its companion programs runnable again.
                if (!OperatingSystem.IsWindows() && IsExecutable(file.RelativePath, plan.ServiceEntry))
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.WriteAllText(Path.Combine(staging, "plan.txt"),
                $"source {plan.Source}\npackage {plan.Package.Version} {plan.Package.Build}\n" +
                string.Join('\n', backups.Select(b => $"{b.Target} <- {b.Backup}")));

            // 3. Activate only after the files are in place.
            if (plan.Running is not null)
            {
                ServiceInfo? service;
                if (hooks is not null)
                {
                    var result = await hooks.Activate(plan, ct);
                    if (!result.Ok) throw new PackageException($"activation failed: {result.Error ?? "the new service did not answer"}");
                    service = result.Service;
                }
                else
                {
                    service = await ActivateAsync(plan, pipe, dataDirectory, ct);
                }
                if (service is null || service.Build is null)
                    throw new PackageException("the restarted service did not report its running build; readiness cannot be verified");
                if (service is not null)
                {
                    var disk = service.ProcessPath is { } actualPath
                        ? HostGeneration.OnDisk(actualPath)
                        : (Version: (string?)null, Build: (string?)null, Unread: (string?)null);
                    var actualBuild = service.Build;
                    if (plan.Package.Build is { } wanted && actualBuild is { } got && wanted != got)
                        throw new PackageException($"the restarted service runs build {got}, not the package's {wanted}");
                    Console.WriteLine($"gorynych update: verified service process {service.ProcessId}, version {service.Version ?? disk.Version ?? "unknown"}, build {actualBuild ?? "unknown"}");
                }
            }
            Console.WriteLine($"gorynych update: updated to {plan.Package.Version} (build {plan.Package.Build ?? "unknown"}); " +
                $"rollback copies kept in {staging} and next to the replaced files (stamp {stamp})");
            Console.WriteLine("gorynych update: adapter hosts of the old build keep their turns and their idle sessions move to the new build; harness updates were not touched.");
            return 0;
        }
        catch (Exception e)
        {
            var rollback = Rollback(plan, backups, staging);
            Console.Error.WriteLine($"gorynych update: {e.Message}");
            Console.Error.WriteLine(rollback
                ? "gorynych update: rolled back to the previous files; the old service can start again."
                : "gorynych update: rollback was incomplete; the output above names what could not be restored. Nothing was deleted.");
            if (rollback && plan.Running is not null)
            {
                try
                {
                    if (hooks is not null)
                    {
                        if (!await hooks.StartService(plan, CancellationToken.None))
                            Console.Error.WriteLine("gorynych update: the previous service could not be restarted after rollback.");
                    }
                    else
                    {
                        var current = await QueryStatusAsync(pipe, CancellationToken.None);
                        if (current is not null && current.Build != plan.Running.Build)
                        {
                            // Hosted restart leaves any turns in their hosts; never force a rollback restart.
                            var result = await AcpBridge.RestartAsync(pipe, wait: true,
                                timeout: Conductor.DefaultRestartTimeout, force: false);
                            if (result != 0) throw new PackageException("the conductor could not restart onto the restored files");
                            current = null;
                        }
                        if (current is null)
                        {
                            var exe = Path.Combine(plan.InstallDirectory, plan.ServiceEntry);
                            if (!await ManagementCommand.StartServiceAsync(pipe, dataDirectory, exe, TimeSpan.FromSeconds(30),
                                    ServiceOptions.Read(dataDirectory)))
                                throw new PackageException("the previous service did not open its pipe after rollback");
                        }
                        var restored = await WaitForStatusAsync(pipe, TimeSpan.FromSeconds(15), CancellationToken.None);
                        if (restored is null || restored.Build != plan.Running.Build)
                            throw new PackageException("the previous service's readiness/build could not be verified after rollback");
                        Console.Error.WriteLine($"gorynych update: previous service restored, process {restored.ProcessId}.");
                    }
                }
                catch (Exception recovery) when (recovery is IOException or PackageException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"gorynych update: rollback recovery failed: {recovery.Message}");
                }
            }
            return FailedExitCode;
        }
    }

    static bool IsExecutable(string relative, string serviceEntry) =>
        relative == serviceEntry
        || relative.StartsWith("outside/", StringComparison.Ordinal)
        || relative.StartsWith("tools/", StringComparison.Ordinal);

    static string BackupName(string target, string stamp)
    {
        var directory = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        if (name.StartsWith("Gorynych.Service", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(directory, $"Gorynych.Service.{stamp}.old.exe");
        return target + "." + stamp + ".old";
    }

    static bool Rollback(UpdatePlan plan, List<(string Target, string? Backup)> backups, string staging)
    {
        var ok = true;
        foreach (var (target, backup) in Enumerable.Reverse(backups))
        {
            try
            {
                if (backup is null)
                {
                    if (File.Exists(target)) File.Delete(target);
                    continue;
                }
                if (File.Exists(target))
                    File.Move(target, target + ".failed", overwrite: true); // keep what was activated
                if (File.Exists(backup)) File.Move(backup, target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
                Console.Error.WriteLine($"gorynych update: rollback of {target}: {e.Message}");
            }
        }
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return ok;
    }

    static async Task<ServiceInfo?> ActivateAsync(UpdatePlan plan, string pipe, string dataDirectory, CancellationToken ct)
    {
        Console.Error.WriteLine("gorynych update: asking the running service to restart; its hosts keep the running turns");
        var code = await AcpBridge.RestartAsync(pipe, wait: true, timeout: Conductor.DefaultRestartTimeout, force: false);
        if (code != 0)
            throw new PackageException($"the restart did not complete (exit code {code}); see the messages above");
        var service = await WaitForStatusAsync(pipe, TimeSpan.FromSeconds(15), ct);
        if (service is null)
        {
            // No bridge came back: start the new executable ourselves.
            var exe = Path.Combine(plan.InstallDirectory, plan.ServiceEntry);
            Console.Error.WriteLine($"gorynych update: no bridge started the service; starting {exe}");
            if (!await ManagementCommand.StartServiceAsync(pipe, dataDirectory, exe, TimeSpan.FromSeconds(30),
                    ServiceOptions.Read(dataDirectory)))
                throw new PackageException("the new service did not open its pipe within 30 s");
            service = await WaitForStatusAsync(pipe, TimeSpan.FromSeconds(15), ct)
                ?? throw new PackageException("the new service did not answer on its pipe");
        }
        return service;
    }

    internal static async Task<ServiceInfo?> QueryStatusAsync(string pipe, CancellationToken ct)
    {
        NamedPipeClientStream? stream;
        try { stream = await ServicePipe.ConnectAsync(pipe, TimeSpan.FromSeconds(1), ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (stream is null) return null;
        await using var rpc = new JsonRpcConnection(stream, stream, "gorynych-update");
        rpc.Start();
        try
        {
            await ManagementCommand.InitializeAsync(rpc, "gorynych-update");
            return await rpc.SendRequestAsync(GorynychProtocol.MethodStatus, new JsonObject(), ct) is JsonObject status
                ? new ServiceInfo(status) : null;
        }
        catch (Exception e) when (e is IOException or JsonRpcException) { return null; }
    }

    static async Task<ServiceInfo?> WaitForStatusAsync(string pipe, TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (await QueryStatusAsync(pipe, ct) is { } status) return status;
            await Task.Delay(200, ct);
        }
        return null;
    }
}
