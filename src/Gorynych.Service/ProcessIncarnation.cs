using System.Globalization;

namespace Gorynych.Service;

// Linux's Process.StartTime is derived from a wall-clock conversion and can
// differ across independent reads. procfs start ticks and boot ID are stable
// kernel identities; inability to read either must never prove process exit.
internal static class ProcessIncarnation
{
    internal static string? Read(int pid)
    {
        if (pid <= 0) return null;
        if (!OperatingSystem.IsLinux()) return AgentHost.ProcessStartedAt(pid);
        try
        {
            return ParseLinux(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(),
                File.ReadAllText($"/proc/{pid}/stat"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    internal static string? ParseLinux(string boot, string stat)
    {
        if (!Guid.TryParseExact(boot, "D", out var id)) return null;
        var end = stat.LastIndexOf(')');
        if (end < 0 || end + 2 >= stat.Length || stat[end + 1] != ' ') return null;
        var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
        return $"linux:{id:N}:{ticks.ToString(CultureInfo.InvariantCulture)}";
    }

    internal static bool ValidLinux(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 3 && parts[0] == "linux" &&
            Guid.TryParseExact(parts[1], "N", out var id) && parts[1] == id.ToString("N") &&
            ulong.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
            parts[2] == ticks.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool DefinitelyExited(int pid, string saved)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited) return true;
            return ProvenDifferent(saved, Read(pid));
        }
        catch (ArgumentException) { return true; }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or
            UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }

    // A legacy Linux wall-time string is not comparable with the kernel token.
    internal static bool ProvenDifferent(string saved, string? current, bool? linux = null) =>
        current is not null && ((linux ?? OperatingSystem.IsLinux())
            ? ValidLinux(saved) && ValidLinux(current) && saved != current
            : !saved.StartsWith("linux:", StringComparison.Ordinal) && saved != current);
}
