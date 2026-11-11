using System.Security.AccessControl;
using System.Security.Principal;

namespace Gorynych.Service;

/// <summary>
/// The service's data folder holds every session's journal, the peer tokens in <c>sessions.json</c>,
/// the host token and the logs: it is its user's alone. Under <c>%LOCALAPPDATA%</c> it inherits what
/// the user profile grants others, such as <c>CodexSandboxUsers: ReadAndExecute</c>, which Codex's
/// Windows sandbox adds so that the commands it runs can read the profile. So at each start the service
/// gives the folder a protected DACL: the user and SYSTEM, full control, inherited by all inside, and
/// nothing from the parent. On Unix the folder is 0700.
/// </summary>
/// <remarks>
/// <para>
/// Closing a folder takes it from every other account, so the service closes only a folder that is
/// its own: the default one, a new or empty one, or one an earlier start of the service used. A
/// drive's root, the user profile, the application data folders above the default one and the
/// system's folders are refused whatever they hold (<see cref="Refusal"/>).
/// </para>
/// <para>
/// Setting the folder's DACL makes Windows compute again what each item inside inherits, but it
/// passes over an item it may not change without a word, and never comes back to it. So the service
/// goes over all inside itself, closes what is still open, and notes in <see cref="ClosedFile"/> that
/// all of it is closed only when none was left open; until then each start goes over it again.
/// Neither Windows nor that pass follows a junction or a link inside, nor changes it: what it leads
/// to is not the data folder's.
/// </para>
/// <para>
/// The agents run as the user and read what Gorynych keeps for them there (Claude's per-session
/// <c>settings.json</c> and <c>mcp.json</c>, DSH's patch file); so do the adapter hosts and the MCP
/// servers agents start. Codex's sandbox runs the model's shell commands with a restricted token,
/// which needs the folder to grant its group too: denied now, as it should be. On Linux Codex's
/// sandbox runs as the user, so 0700 keeps out other users only.
/// </para>
/// </remarks>
static class DataFolder
{
    /// <summary>Names a folder as a Gorynych data folder; the service writes it at its first start there.</summary>
    public const string IdentityFile = ".gorynych-data";

    /// <summary>
    /// On Windows, written once every item inside was closed, with the DACL they were given; while it
    /// is missing or says another, each start goes over the folder again.
    /// </summary>
    public const string ClosedFile = ".closed";

    /// <summary>What an earlier service left in its data folder before <see cref="IdentityFile"/> was written.</summary>
    static readonly string[] EarlierSigns = ["service.json"];

    static StringComparison Case => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>The data folder when no <c>--data</c> names one.</summary>
    public static string DefaultPath() =>
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gorynych"));

    /// <summary>
    /// Why <paramref name="path"/> may not be the data folder, for the person who named it; null when
    /// it may. The service would close it to every other account, so it takes only the default folder,
    /// a new or empty one, or one that is a Gorynych data folder already, and never a drive's root, the
    /// user profile, the application data folders or the system's folders.
    /// </summary>
    public static string? Refusal(string path)
    {
        var full = Trim(Path.GetFullPath(path));
        if (Same(full, Trim(DefaultPath()))) return null;
        string? why;
        try
        {
            why = Forbidden(full);
            if (why is null && LinkTarget(full) is { } target && Forbidden(target) is { } there) why = $"it leads to {target}: {there}";
            if (why is null && File.Exists(full)) why = "it is a file";
            if (why is null && Directory.Exists(full)) why = Others(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            why = $"it cannot be looked into ({e.Message})";
        }
        return why is null ? null
            : $"will not take {full} for its data folder: {why}. The service makes its data folder its user's alone, with all inside, " +
              $"which would shut every other account out of it; give --data a new or empty folder, or leave --data out for the default, {DefaultPath()}";
    }

    /// <summary>Notes in a new data folder that it is Gorynych's, so that a later start knows it for its own.</summary>
    public static void Claim(string path)
    {
        var identity = Path.Combine(path, IdentityFile);
        try
        {
            if (!File.Exists(identity))
                File.WriteAllText(identity, "Gorynych's data folder: the service keeps its sessions, tokens and logs here, for this user alone.\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Closes <paramref name="path"/> to everyone but the user (and SYSTEM, on Windows). Returns what
    /// it did, or why it could not, for the log; null when the folder was closed already. It never
    /// throws: a folder it could not close is said, and the service runs on.
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
        catch (Exception e)
        {
            return (true, $"the data folder {path} is not closed to other accounts, which may read the sessions' journals and tokens: {e.Message} ({e.GetType().Name})");
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
        var closedFile = Path.Combine(path, ClosedFile);
        var had = folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var rootClosed = string.Equals(had, wanted, StringComparison.OrdinalIgnoreCase);
        if (rootClosed && ReadOrNull(closedFile) == wanted) return null;
        if (!rootClosed)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(wanted, AccessControlSections.Access);
            // SetNamedSecurityInfo underneath: what each item inside inherits is computed again, but an
            // item it may not change is passed over without a word, hence the pass below.
            folder.SetAccessControl(security);
        }
        var (left, links, opened) = CloseInside(folder, user);
        var linksSaid = links == 0 ? "" : $"; {links} junction{(links == 1 ? "" : "s")} or link{(links == 1 ? "" : "s")} inside {(links == 1 ? "was" : "were")} left as {(links == 1 ? "it was" : "they were")}, and not followed";
        if (left.Count > 0)
        {
            try { File.Delete(closedFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return (true, $"the data folder {path} is closed to everyone but this user and SYSTEM, but {left.Count} item{(left.Count == 1 ? "" : "s")} inside could not be, " +
                $"and other accounts may still read {(left.Count == 1 ? "it" : "them")}: {string.Join("; ", left.Take(3))}{(left.Count > 3 ? "; …" : "")}; the next start tries again{linksSaid}");
        }
        File.WriteAllText(closedFile, wanted);
        return (false, (rootClosed
            ? $"went over the data folder {path} again: all inside is closed to everyone but this user and SYSTEM{(opened > 0 ? $" ({opened} item{(opened == 1 ? " was" : "s were")} still open)" : "")}"
            : $"closed the data folder {path} to everyone but this user and SYSTEM, with all inside (it had {had}){(opened > 0 ? $"; {opened} item{(opened == 1 ? "" : "s")} inside had to be closed one by one" : "")}") + linksSaid);
    }

    /// <summary>
    /// Goes over all inside <paramref name="root"/>, parents before what they hold, and gives each item
    /// that lets another account in no DACL of its own, so that it takes the folder's. Junctions and
    /// links are neither followed nor changed. Returns the items left open, with why, the links passed
    /// over, and how many items it had to close.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static (List<string> Left, int Links, int Opened) CloseInside(DirectoryInfo root, SecurityIdentifier user)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var left = new List<string>();
        int links = 0, opened = 0;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var folders = new Stack<DirectoryInfo>([root]);
        while (folders.TryPop(out var folder))
        {
            List<FileSystemInfo> items;
            try { items = folder.EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{Path.GetRelativePath(root.FullName, folder.FullName)}: {e.Message}");
                continue;
            }
            foreach (var item in items)
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    links++;
                    continue;
                }
                try
                {
                    if (!Closed(SecurityOf(item), user, system))
                    {
                        opened++;
                        // A new object each time: an ObjectSecurity writes only what changed since it was last written.
                        FileSystemSecurity inherit = item is DirectoryInfo ? new DirectorySecurity() : new FileSecurity();
                        inherit.SetSecurityDescriptorSddlForm("D:AI", AccessControlSections.Access);
                        if (item is DirectoryInfo d) d.SetAccessControl((DirectorySecurity)inherit);
                        else ((FileInfo)item).SetAccessControl((FileSecurity)inherit);
                        if (!Closed(SecurityOf(item), user, system))
                        {
                            left.Add($"{Path.GetRelativePath(root.FullName, item.FullName)}: still lets others in");
                            continue;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException)
                {
                    left.Add($"{Path.GetRelativePath(root.FullName, item.FullName)}: {e.Message}");
                    continue;
                }
                if (item is DirectoryInfo sub) folders.Push(sub);
            }
        }
        return (left, links, opened);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static FileSystemSecurity SecurityOf(FileSystemInfo item) => item is DirectoryInfo d
        ? d.GetAccessControl(AccessControlSections.Access)
        : ((FileInfo)item).GetAccessControl(AccessControlSections.Access);

    /// <summary>Whether no one but the user and SYSTEM is let in, by an ACE of the item's own or an inherited one.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool Closed(FileSystemSecurity security, SecurityIdentifier user, SecurityIdentifier system) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(r => r.AccessControlType != AccessControlType.Allow || r.IdentityReference.Equals(user) || r.IdentityReference.Equals(system));

    static string? ReadOrNull(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Why a folder may never be closed, whatever it holds; null for one that may be.</summary>
    static string? Forbidden(string full)
    {
        if (Path.GetPathRoot(full) is not { } root || Same(full, Trim(root))) return "it is the root of a drive or volume";
        foreach (var (folder, name) in Above())
            if (Holds(full, folder)) return Same(full, folder) ? $"it is {name}" : $"it holds {name} ({folder})";
        foreach (var (folder, name) in Within())
            if (Holds(folder, full)) return Same(full, folder) ? $"it is {name}" : $"it is inside {name} ({folder})";
        return null;
    }

    /// <summary>The folders whose own data folders are inside them: neither they nor a folder above them may be closed.</summary>
    static IEnumerable<(string Folder, string Name)> Above()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<(string, string)> all = OperatingSystem.IsWindows()
            ?
            [
                (home, "the user profile"),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%PROGRAMDATA%"),
            ]
            :
            [
                (home, "the home folder"),
                (Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "", "$XDG_DATA_HOME"),
                (home.Length > 0 ? Path.Combine(home, ".local", "share") : "", "~/.local/share"),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "the user's application data folder"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "the user's configuration folder"),
            ];
        return all.Where(f => Path.IsPathFullyQualified(f.Item1)).Select(f => (Trim(Path.GetFullPath(f.Item1)), f.Item2));
    }

    /// <summary>The system's folders: neither they nor a folder inside them may be closed.</summary>
    static IEnumerable<(string Folder, string Name)> Within()
    {
        if (!OperatingSystem.IsWindows()) return [];
        IEnumerable<(string, string)> all =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "the Windows folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)"),
        ];
        return all.Where(f => Path.IsPathFullyQualified(f.Item1)).Select(f => (Trim(Path.GetFullPath(f.Item1)), f.Item2));
    }

    /// <summary>Why a folder that exists is not one the service may take, by what it holds; null when it is empty or Gorynych's.</summary>
    static string? Others(string full)
    {
        if (Path.Exists(Path.Combine(full, IdentityFile)) || EarlierSigns.Any(s => Path.Exists(Path.Combine(full, s)))) return null;
        var first = Directory.EnumerateFileSystemEntries(full).Take(4).Select(Path.GetFileName).ToList();
        if (first.Count == 0) return null;
        return $"it holds other things ({string.Join(", ", first.Take(3))}{(first.Count > 3 ? ", …" : "")}) and is not a Gorynych data folder";
    }

    /// <summary>Where a folder that is a junction, mount point or link finally leads; null for a plain one.</summary>
    static string? LinkTarget(string full)
    {
        if (!Directory.Exists(full)) return null;
        var info = new DirectoryInfo(full);
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        return info.ResolveLinkTarget(returnFinalTarget: true) is { } target ? Trim(Path.GetFullPath(target.FullName)) : null;
    }

    static bool Holds(string outer, string inner) =>
        Same(outer, inner) || inner.StartsWith(outer.EndsWith(Path.DirectorySeparatorChar) ? outer : outer + Path.DirectorySeparatorChar, Case);

    static bool Same(string a, string b) => string.Equals(a, b, Case);

    static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}
