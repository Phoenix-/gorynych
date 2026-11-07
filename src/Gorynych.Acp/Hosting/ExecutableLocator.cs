namespace Gorynych.Acp.Hosting;

/// <summary>Finds an executable on PATH the way a shell would, honouring PATHEXT on Windows.</summary>
public static class ExecutableLocator
{
    public static string? Find(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var hasExtension = Path.HasExtension(name);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim('"'), name); }
            catch (ArgumentException) { continue; }
            if (hasExtension && File.Exists(candidate)) return candidate;
            foreach (var ext in extensions)
                if (File.Exists(candidate + ext)) return candidate + ext;
        }
        return null;
    }
}
