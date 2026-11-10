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

    static readonly Lazy<string> thisBuild = new(ReadThisBuild);

    /// <summary>
    /// This process's build: its version and the start of the SHA-256 of what it runs, read once, at
    /// the start of the process, before an update can rename its executable aside and copy another in
    /// its place: the Native AOT executable, or with the runtime the executable and every assembly of
    /// Gorynych next to it.
    /// </summary>
    public static string ThisBuild => thisBuild.Value;

    static string ReadThisBuild()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<string>();
        if (Environment.ProcessPath is { } exe) files.Add(exe);
        var folder = AppContext.BaseDirectory;
        if (Directory.Exists(folder))
            files.AddRange(Directory.EnumerateFiles(folder, "Gorynych.*.dll").Order(StringComparer.OrdinalIgnoreCase));
        var buffer = new byte[81920];
        foreach (var file in files)
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                for (int n; (n = stream.Read(buffer)) > 0;) hash.AppendData(buffer, 0, n);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return $"{AgentHost.Version}+{Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant()}";
    }
}
