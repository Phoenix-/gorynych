using System.Security.Cryptography;
using System.Text;

namespace Gorynych.Service.Hosts;

/// <summary>
/// What a host runs: the build of its executable and the adapter options it was started with
/// (<c>--claude-*</c> and the like). A host of another generation than the one the conductor would
/// start now is stale: its sessions move to a host of the current one as they go idle, and it exits
/// once it has none (docs/service-protocol.md, "Updating adapters").
/// </summary>
public sealed class HostGeneration(string build, IReadOnlyList<string> options)
{
    public string Build { get; } = build;
    public IReadOnlyList<string> Options { get; } = options;

    /// <summary>A short fingerprint of both, which names the generation's adapter-wide host (its slot) and its mutex.</summary>
    public string Key { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(build + "\n" + string.Join("\n", options))))[..12].ToLowerInvariant();

    public bool Same(HostGeneration? other) => other is not null && other.Key == Key;

    public override string ToString() => Options.Count == 0 ? Build : $"{Build} {string.Join(' ', Options)}";

    static readonly Lock gate = new();
    static string? thisBuild; // under gate once set

    /// <summary>
    /// This process's build: its version and the start of the SHA-256 of what it runs, read once
    /// (<see cref="ReadAtStart"/>): the Native AOT executable, or with the runtime the executable and
    /// every assembly of Gorynych next to it.
    /// </summary>
    public static string ThisBuild => Volatile.Read(ref thisBuild) ?? ReadAtStart(null).Build;

    /// <summary>
    /// Reads this process's build, first thing at the start of the service and of a host, before an
    /// update can rename the executable aside and copy another in its place; later calls return it.
    /// A file that cannot be read is tried again a few times (a scanner may hold it a moment); one that
    /// still cannot is not left out, which could make two builds look the same: the build is then one
    /// of this process's own, which no other host or service has, so its hosts are current to it alone
    /// and the sessions of any other move (<paramref name="warn"/> says why).
    /// </summary>
    public static (string Build, TimeSpan Took) ReadAtStart(Action<string>? warn)
    {
        lock (gate)
        {
            if (thisBuild is { } known) return (known, TimeSpan.Zero);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var build = Read(out var unread);
            if (unread is not null)
            {
                build = $"{AgentHost.Version}+unread.{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
                warn?.Invoke($"its build could not be read ({unread}); it runs as build {build}, which no other process has, " +
                    "so the sessions of hosts of other processes move to its hosts once idle");
            }
            Volatile.Write(ref thisBuild, build);
            return (build, watch.Elapsed);
        }
    }

    /// <summary>The build, or null and in <paramref name="unread"/> what could not be read.</summary>
    static string Read(out string? unread)
    {
        unread = null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<string>();
        if (Environment.ProcessPath is { } exe) files.Add(exe);
        else unread = "the path of its executable is not known";
        var folder = AppContext.BaseDirectory;
        if (Directory.Exists(folder))
            files.AddRange(Directory.EnumerateFiles(folder, "Gorynych.*.dll").Order(StringComparer.OrdinalIgnoreCase));
        var buffer = new byte[81920];
        foreach (var file in files)
            for (var attempt = 1; ; attempt++)
            {
                using var part = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    for (int n; (n = stream.Read(buffer)) > 0;) part.AppendData(buffer, 0, n);
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                    hash.AppendData(part.GetHashAndReset());
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 3)
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                    unread ??= $"{Path.GetFileName(file)}: {e.Message}";
                    break;
                }
            }
        return $"{AgentHost.Version}+{Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant()}";
    }
}
