using System.Diagnostics;
using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;
using Gorynych.Acp.JsonRpc;

namespace Gorynych.Adapters.Dsh;

public sealed class DshAdapterOptions
{
    /// <summary>The <c>@deepseek-ai/dsh</c> package folder (the one with <c>lib/bin.js</c>); found from the <c>dsh</c> on PATH when null.</summary>
    public string? Package { get; init; }

    /// <summary>The <c>node</c> executable; found on PATH when null.</summary>
    public string? Node { get; init; }

    /// <summary>
    /// Gorynych's folder for DSH: it holds the per-launch patch and is DSH's working folder, so no
    /// repository's <c>.env</c> reaches DSH's environment.
    /// </summary>
    public required string DataDirectory { get; init; }

    /// <summary>The service's instructions for the peer tools, appended to DSH's system prompt; null without the peers server.</summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// How long a DSH session stays loaded once it is idle with no client
    /// (<c>agentCapabilities._meta["gorynych/unloadAfter"]</c>): a session has one writer, so while
    /// Gorynych holds it, <c>dsh web</c> cannot open it. Opening one again costs about 0.2 s.
    /// </summary>
    public TimeSpan UnloadAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Connects to an agent instead of starting DSH, given the patch file's path. For tests (a fake DSH).</summary>
    public Func<string, CancellationToken, Task<AgentInstance>>? Launch { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>
/// The DeepSeek Harness adapter: DSH speaks ACP itself (<c>dsh --profile acp</c>), so this adapter
/// starts it with a per-launch patch and puts a thin ACP proxy between it and the service
/// (docs/adapters.md, "DeepSeek Harness").
/// </summary>
public sealed class DshAdapter(DshAdapterOptions options) : IAgentAdapter
{
    /// <summary>The DSH versions this adapter was checked against; another one is used, with a warning.</summary>
    public static readonly string[] CheckedVersions = ["0.2.2-rc.1"];

    /// <summary>The patch file's name in <see cref="DshAdapterOptions.DataDirectory"/>.</summary>
    public const string PatchFile = "gorynych.patch.yml";

    public string Name => "dsh";
    public string DisplayName => "DeepSeek Harness";

    public async Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var patch = Path.Combine(options.DataDirectory, PatchFile);
        AtomicFile.WriteAllText(patch, DshPatch.Text(options.Instructions));
        var dsh = options.Launch is { } launch ? await launch(patch, ct) : StartDsh(patch);
        var link = new DuplexLink();
        var proxy = new DshProxy(options, link.AgentEnd, dsh);
        proxy.Start();
        var (input, output) = link.ClientEnd;
        return new AgentInstance(input, output, proxy.Completion, async () =>
        {
            // DSH's stdin ends, DSH drains and exits, and its output ending completes the proxy.
            await dsh.DisposeAsync();
            try { await output.DisposeAsync(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        });
    }

    AgentInstance StartDsh(string patch)
    {
        var package = options.Package ?? FindPackage() ?? throw new InvalidOperationException("dsh was not found on PATH (npm install -g @deepseek-ai/dsh)");
        var bin = Path.Combine(package, "lib", "bin.js");
        if (!File.Exists(bin)) throw new InvalidOperationException($"dsh: {bin} not found");
        var node = options.Node ?? ExecutableLocator.Find("node") ?? throw new InvalidOperationException("node was not found on PATH; DeepSeek Harness needs it");
        var version = VersionOf(package);
        options.Log?.Invoke($"dsh: {package} version {version ?? "unknown"}");
        if (version is null || !CheckedVersions.Contains(version))
            options.Log?.Invoke($"dsh: warning: version {version ?? "unknown"} is not one Gorynych was checked against ({string.Join(", ", CheckedVersions)}); DSH is a preview and changes its ACP surface");
        var psi = new ProcessStartInfo(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.DataDirectory,
        };
        foreach (var arg in new[] { bin, "--profile", "acp", "--patch", patch }) psi.ArgumentList.Add(arg);
        // DSH exports only after explicit feedback, which ACP cannot give; this makes it certain.
        psi.Environment["DSH_TELEMETRY_DISABLED"] = "1";
        var process = Process.Start(psi) ?? throw new InvalidOperationException("dsh --profile acp did not start");
        options.Log?.Invoke($"dsh: started {node} {bin} --profile acp, pid {process.Id}");
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
                options.Log?.Invoke("dsh: " + line);
        }, CancellationToken.None);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        return new AgentInstance(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, exited, async () =>
        {
            try { process.StandardInput.Close(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            // DSH bounds its own shutdown at 5 s.
            if (await Task.WhenAny(exited, Task.Delay(6000)) != exited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        });
    }

    /// <summary>
    /// The <c>@deepseek-ai/dsh</c> package behind the <c>dsh</c> on PATH: on Windows npm's shim sits
    /// next to <c>node_modules</c>; elsewhere <c>bin/dsh</c> links to the package's <c>lib/bin.js</c>.
    /// </summary>
    internal static string? FindPackage()
    {
        var found = ExecutableLocator.Find("dsh");
        if (found is null) return null;
        try
        {
            if (File.ResolveLinkTarget(found, returnFinalTarget: true) is { } target && target.Name == "bin.js")
                return Path.GetDirectoryName(Path.GetDirectoryName(target.FullName));
        }
        catch (IOException) { }
        var prefix = Path.GetDirectoryName(found)!;
        foreach (var candidate in new[]
        {
            Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh"),
            Path.Combine(prefix, "..", "lib", "node_modules", "@deepseek-ai", "dsh"),
        })
            if (File.Exists(Path.Combine(candidate, "lib", "bin.js"))) return Path.GetFullPath(candidate);
        return null;
    }

    static string? VersionOf(string package)
    {
        try { return (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(package, "package.json")))?["version"]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }
}
