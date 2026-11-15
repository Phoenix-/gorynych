using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gorynych.Service;

/// <summary>A file inside a staged package, relative to the package root.</summary>
internal sealed record PackageFile(string RelativePath, string FullPath, long Length);

/// <summary>
/// A checked Gorynych package: what it is, which platform it runs on, and the files an update would
/// copy. Only Gorynych's own package layout is accepted; harness installers are out of scope.
/// </summary>
internal sealed record GorynychPackage(
    string Root,
    string Version,
    string? Build,
    string Os,
    string Arch,
    string ServiceEntry,
    IReadOnlyList<PackageFile> Files,
    bool FromManifest);

/// <summary>A package that is not a supported Gorynych package, with the reason in words.</summary>
internal sealed class PackageException(string message) : Exception(message);

/// <summary>
/// Downloads and stages a package, rejecting anything ambiguous or unsafe before the active install
/// is touched: archive entries outside the target, links and devices, too many or too large entries,
/// a missing or multiple package roots, and a wrong OS or architecture.
/// </summary>
internal sealed class StagedPackage : IDisposable
{
    const long MaxDownload = 1L << 30;   // 1 GiB
    const long MaxExtracted = 1L << 30;  // 1 GiB
    const int MaxEntries = 20_000;

    StagedPackage(string directory, GorynychPackage package)
    {
        Directory = directory;
        Package = package;
    }

    public string Directory { get; }
    public GorynychPackage Package { get; }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static async Task<StagedPackage> StageAsync(string source, CancellationToken ct)
    {
        var working = Path.Combine(Path.GetTempPath(), "gorynych-update", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(working);
        try
        {
            var archive = Path.Combine(working, "package");
            if (File.Exists(source))
                CopyLocal(Path.GetFullPath(source), archive);
            else if (Path.IsPathRooted(source))
                throw new PackageException($"the local archive '{source}' does not exist");
            else if (TryUri(source, out var uri))
            {
                Console.Error.WriteLine($"gorynych update: downloading {uri}");
                await DownloadAsync(uri, archive, ct);
            }
            else
                throw new PackageException($"the local archive '{source}' does not exist");
            var extracted = Path.Combine(working, "staged");
            System.IO.Directory.CreateDirectory(extracted);
            Extract(archive, extracted);
            var package = Identify(extracted);
            return new StagedPackage(working, package);
        }
        catch
        {
            try { System.IO.Directory.Delete(working, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    static bool TryUri(string source, out Uri uri)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out parsed) && parsed.Scheme.Length > 1)
            throw new PackageException($"unsupported source scheme '{parsed.Scheme}': use a local archive, http or https");
        uri = null!;
        return false;
    }

    static async Task DownloadAsync(Uri uri, string destination, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new PackageException($"the download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        if (response.Content.Headers.ContentLength is { } length && length > MaxDownload)
            throw new PackageException($"the download is {length} bytes, larger than the {MaxDownload} byte limit");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyBoundedAsync(input, output, MaxDownload, ct);
    }

    static void CopyLocal(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        CopyBounded(input, output, MaxDownload);
    }

    static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        for (int n; (n = await input.ReadAsync(buffer, ct)) > 0;)
        {
            total += n;
            if (total > limit) throw new PackageException($"the download is larger than the {limit} byte limit");
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
        }
    }

    static void CopyBounded(Stream input, Stream output, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        for (int n; (n = input.Read(buffer)) > 0;)
        {
            total += n;
            if (total > limit) throw new PackageException($"the archive is larger than the {limit} byte limit");
            output.Write(buffer, 0, n);
        }
    }

    static void Extract(string archive, string destination)
    {
        using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        var read = stream.Read(magic);
        stream.Position = 0;
        if (read >= 4 && magic[0] == (byte)'P' && magic[1] == (byte)'K')
            ExtractZip(stream, destination);
        else if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
            ExtractTar(stream, destination);
        else if (read >= 2 && magic[0] == (byte)'B' && magic[1] == (byte)'Z')
            throw new PackageException("bzip2 archives are not supported; use .zip or .tar.gz");
        else
            throw new PackageException("the input is not a .zip or .tar.gz archive");
    }

    static void ExtractZip(Stream stream, string destination)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaxEntries) throw new PackageException($"the archive has more than {MaxEntries} entries");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            // Unix mode in the external attributes: a symlink could lead outside the staging folder.
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new PackageException($"the archive contains a symlink: {entry.FullName}");
            if (mode is 0x2000 or 0x6000 or 0x1000) throw new PackageException($"the archive contains a device or FIFO: {entry.FullName}");
            var relative = SafeRelative(entry.FullName);
            total += entry.Length;
            if (total > MaxExtracted) throw new PackageException($"the archive expands to more than {MaxExtracted} bytes");
            var target = Path.Combine(destination, relative);
            EnsureUnder(destination, target, entry.FullName);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, output, MaxExtracted);
        }
    }

    static void ExtractTar(Stream stream, string destination)
    {
        using var reader = new TarReader(stream, leaveOpen: true);
        long total = 0;
        var count = 0;
        while (reader.GetNextEntry() is { } entry)
        {
            if (++count > MaxEntries) throw new PackageException($"the archive has more than {MaxEntries} entries");
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink or TarEntryType.CharacterDevice
                or TarEntryType.BlockDevice or TarEntryType.Fifo)
                throw new PackageException($"the archive contains a link or device entry: {entry.Name}");
            if (entry.EntryType is TarEntryType.Directory) continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                throw new PackageException($"the archive contains an unsupported entry: {entry.Name}");
            var relative = SafeRelative(entry.Name);
            if (entry.DataStream is null) continue;
            total += entry.Length;
            if (total > MaxExtracted) throw new PackageException($"the archive expands to more than {MaxExtracted} bytes");
            var target = Path.Combine(destination, relative);
            EnsureUnder(destination, target, entry.Name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(entry.DataStream, output, MaxExtracted);
        }
    }

    /// <summary>Turns an archive name into a relative path, rejecting anything that could leave the staging root.</summary>
    internal static string SafeRelative(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new PackageException("the archive contains an empty entry name");
        var normalized = name.Replace('\\', '/');
        if (normalized.Length > 1024) throw new PackageException($"the archive entry name is too long: {normalized[..80]}...");
        if (normalized.StartsWith('/') || normalized.Contains('\0') || (normalized.Length >= 2 && normalized[1] == ':'))
            throw new PackageException($"the archive contains an absolute path: {name}");
        var parts = new List<string>();
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") throw new PackageException($"the archive contains a parent path: {name}");
            if (part.Contains(':')) throw new PackageException($"the archive contains an unsupported path: {name}");
            parts.Add(part);
        }
        if (parts.Count == 0) throw new PackageException($"the archive contains an empty path: {name}");
        var relative = string.Join('/', parts);
        // Windows reserved names would make the copy unpredictable.
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
                    && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))))
                throw new PackageException($"the archive contains a reserved name: {name}");
        }
        return relative;
    }

    static void EnsureUnder(string root, string target, string entry)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(target);
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PackageException($"the archive entry leaves the staging folder: {entry}");
    }

    static GorynychPackage Identify(string root)
    {
        var manifests = Find(root, "gorynych-package.json", 2);
        if (manifests.Count > 1) throw new PackageException($"the archive has {manifests.Count} package manifests; it is ambiguous");
        GorynychPackage package;
        if (manifests.Count == 1)
            package = FromManifest(Path.GetDirectoryName(manifests[0])!);
        else
        {
            var services = Find(root, "Gorynych.Service.exe", 2)
                .Concat(Find(root, "Gorynych.Service", 2))
                .Select(p => Path.GetFullPath(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (services.Count == 0)
                throw new PackageException("the archive has no Gorynych package manifest and no Gorynych.Service binary");
            if (services.Count > 1)
                throw new PackageException($"the archive has {services.Count} Gorynych service binaries; it is ambiguous");
            package = FromBinary(Path.GetDirectoryName(services[0])!, Path.GetFileName(services[0]));
        }
        if (package.Files.Count == 0) throw new PackageException("the package has no files");
        CheckPlatform(package.Os, package.Arch);
        return package;
    }

    static List<string> Find(string root, string name, int depth)
    {
        var found = new List<string>();
        void Walk(string directory, int level)
        {
            if (level > depth) return;
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(directory)) if (Path.GetFileName(file) == name) found.Add(file);
                foreach (var child in System.IO.Directory.EnumerateDirectories(directory)) Walk(child, level + 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Walk(root, 0);
        return found;
    }

    static GorynychPackage FromManifest(string root)
    {
        var path = Path.Combine(root, "gorynych-package.json");
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new PackageException($"the package manifest is not readable: {e.Message}");
        }
        using (document)
        {
            var rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object) throw new PackageException("the package manifest is not an object");
            if (!rootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var versionNumber) || versionNumber != 1)
                throw new PackageException("the package manifest has an unsupported schemaVersion");
            if (!rootElement.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String
                || !string.Equals(product.GetString(), "gorynych", StringComparison.OrdinalIgnoreCase))
                throw new PackageException("the package manifest does not name Gorynych as its product");
            var version = Text(rootElement, "version") ?? throw new PackageException("the package manifest has no version");
            var os = (Text(rootElement, "os") ?? throw new PackageException("the package manifest has no os")).ToLowerInvariant();
            var arch = (Text(rootElement, "arch") ?? throw new PackageException("the package manifest has no arch")).ToLowerInvariant();
            var entry = Text(rootElement, "entry") ?? DefaultServiceName(os);
            entry = SafeRelative(entry);
            if (!File.Exists(Path.Combine(root, entry))) throw new PackageException($"the package manifest's entry '{entry}' is not in the archive");
            if (rootElement.TryGetProperty("files", out var filesElement))
            {
                if (filesElement.ValueKind != JsonValueKind.Object) throw new PackageException("the package manifest's files is not an object");
                foreach (var file in filesElement.EnumerateObject())
                {
                    var relative = SafeRelative(file.Name);
                    var full = Path.Combine(root, relative);
                    if (!File.Exists(full)) throw new PackageException($"the manifest lists a missing file: {relative}");
                    if (file.Value.ValueKind != JsonValueKind.String) throw new PackageException($"the manifest's hash for {relative} is not a string");
                    var expected = file.Value.GetString()!.Trim();
                    var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full)));
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new PackageException($"the package file {relative} does not match its manifest hash");
                }
            }
            var build = Text(rootElement, "build");
            var list = FilesOf(root);
            if (!list.Any(f => f.RelativePath == entry)) throw new PackageException($"the package entry '{entry}' is not among its files");
            return new GorynychPackage(root, version, build ?? ComputeBuild(root, entry, version), os, arch, entry, list, true);
        }
    }

    static GorynychPackage FromBinary(string root, string entry)
    {
        var candidate = Path.Combine(root, entry);
        var kind = IdentifyBinary(candidate) ?? throw new PackageException(
            $"the archive's '{entry}' is not a recognizable Windows PE, Linux ELF or macOS Mach-O binary");
        string? version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(candidate);
            version = info.ProductVersion ?? info.FileVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        if (string.IsNullOrWhiteSpace(version)) version = "unknown";
        var list = FilesOf(root);
        return new GorynychPackage(root, version, ComputeBuild(root, entry, version), kind.Os, kind.Arch, entry, list, false);
    }

    static string DefaultServiceName(string os) => os == "windows" ? "Gorynych.Service.exe" : "Gorynych.Service";

    static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text : null;

    static List<PackageFile> FilesOf(string root)
    {
        var list = new List<PackageFile>();
        void Walk(string directory)
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var info = new FileInfo(file);
                list.Add(new PackageFile(relative, file, info.Length));
            }
            foreach (var child in System.IO.Directory.EnumerateDirectories(directory)) Walk(child);
        }
        Walk(root);
        list.Sort((a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return list;
    }

    /// <summary>The same build <see cref="Hosts.HostGeneration.OnDisk"/> computes: the entry and the Gorynych dlls next to it.</summary>
    static string ComputeBuild(string root, string entry, string version)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = new List<string> { Path.Combine(root, entry) };
        files.AddRange(System.IO.Directory.EnumerateFiles(root, "Gorynych.*.dll").Order(StringComparer.OrdinalIgnoreCase));
        var buffer = new byte[81920];
        foreach (var file in files)
        {
            using var part = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            for (int n; (n = stream.Read(buffer)) > 0;) part.AppendData(buffer, 0, n);
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(part.GetHashAndReset());
        }
        return $"{version}+{Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant()}";
    }

    static void CheckPlatform(string os, string arch)
    {
        var currentOs = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
        if (!string.Equals(os, currentOs, StringComparison.OrdinalIgnoreCase))
            throw new PackageException($"the package is for {os}, but this is {currentOs}; an update cannot cross platforms");
        var currentArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown",
        };
        if (!string.Equals(arch, currentArch, StringComparison.OrdinalIgnoreCase))
            throw new PackageException($"the package is for {arch}, but this process is {currentArch}; an update cannot cross architectures");
    }

    /// <summary>The OS and architecture a native service binary is built for, or null when it is not one.</summary>
    internal static (string Os, string Arch)? IdentifyBinary(string path)
    {
        Span<byte> header = stackalloc byte[64];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(header);
        if (read >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
        {
            if (read < 0x40) return null;
            var peOffset = BitConverter.ToInt32(header[0x3C..0x40]);
            if (peOffset < 0 || peOffset > 1 << 20) return null;
            Span<byte> pe = stackalloc byte[6];
            stream.Position = peOffset;
            if (stream.Read(pe) < 6 || pe[0] != (byte)'P' || pe[1] != (byte)'E' || pe[2] != 0 || pe[3] != 0) return null;
            var machine = BitConverter.ToUInt16(pe[4..6]);
            var arch = machine switch { 0x8664 => "x64", 0xAA64 => "arm64", 0x014C => "x86", _ => "unknown" };
            return ("windows", arch);
        }
        if (read >= 20 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        {
            if (header[4] != 2) return null; // 32-bit
            if (header[5] != 1) return null; // big-endian
            var machine = BitConverter.ToUInt16(header[18..20]);
            var arch = machine switch { 0x3E => "x64", 0xB7 => "arm64", _ => "unknown" };
            return ("linux", arch);
        }
        if (read >= 8)
        {
            var magic = BitConverter.ToUInt32(header[0..4]);
            var cpu = BitConverter.ToUInt32(header[4..8]);
            if (magic == 0xFEEDFACF)
            {
                var arch = cpu switch { 0x01000007 => "x64", 0x0100000C => "arm64", _ => "unknown" };
                return ("macos", arch);
            }
            if (magic is 0xCAFEBABE or 0xBEBAFECA) return null; // a fat binary is ambiguous here
        }
        return null;
    }
}
