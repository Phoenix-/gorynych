using System.Security.AccessControl;
using System.Security.Principal;

namespace Gorynych.Service;

/// <summary>
/// The service's data folder holds every session's journal, the peer tokens in <c>sessions.json</c>,
/// the host token and the logs: it is its user's alone. Under <c>%LOCALAPPDATA%</c> it inherits what
/// the user profile grants others, such as <c>CodexSandboxUsers: ReadAndExecute</c>, which Codex's
/// Windows sandbox adds so that the commands it runs can read the profile. So at each start the service
/// gives the folder a protected DACL: the user and SYSTEM, full control, inherited by all inside, and
/// nothing from the parent. Setting it rewrites what the files inside inherit, so an older install is
/// closed too; a folder that already has it is left alone. On Unix the folder is 0700.
/// </summary>
/// <remarks>
/// The agents run as the user and read what Gorynych keeps for them there (Claude's per-session
/// <c>settings.json</c> and <c>mcp.json</c>, DSH's patch file); so do the adapter hosts and the MCP
/// servers agents start. Codex's sandbox runs the model's shell commands with a restricted token,
/// which needs the folder to grant its group too: denied now, as it should be. On Linux Codex's
/// sandbox runs as the user, so 0700 keeps out other users only.
/// </remarks>
static class DataFolder
{
    /// <summary>
    /// Closes <paramref name="path"/> to everyone but the user (and SYSTEM, on Windows). Returns what
    /// it did, or why it could not, for the log; null when the folder was closed already.
    /// </summary>
    public static (bool Failed, string Said)? Protect(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ProtectWindows(path);
            const UnixFileMode Own = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var mode = File.GetUnixFileMode(path);
            if (mode == Own) return null;
            File.SetUnixFileMode(path, Own);
            return (false, $"the data folder {path} was {Convert.ToString((int)mode, 8)}; now 700, its user's alone");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PlatformNotSupportedException or ArgumentException)
        {
            return (true, $"the data folder {path} could not be closed to other accounts, which may read the sessions' journals and tokens: {e.Message}");
        }
    }

    /// <summary>The DACL the folder gets: the user and SYSTEM, full control, inherited by all inside, protected from the parent's.</summary>
    internal static string Dacl(string userSid) => $"D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;{userSid})";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static (bool, string)? ProtectWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("the process has no user SID");
        var folder = new DirectoryInfo(path);
        var wanted = Dacl(user.Value);
        var had = folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        if (string.Equals(had, wanted, StringComparison.OrdinalIgnoreCase)) return null;
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(wanted, AccessControlSections.Access);
        // SetNamedSecurityInfo underneath: what each file and folder inside inherits is computed again.
        folder.SetAccessControl(security);
        return (false, $"closed the data folder {path} to everyone but this user and SYSTEM, with all inside (it had {had})");
    }
}
