// Compiled into both Gorynych.Service (`task install|uninstall`, and the stdio bridge) and
// Gorynych.Outside, which run the task to start the service.
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace Gorynych.Outside;

/// <summary>
/// The Windows logon task that starts the service in the person's own session with a limited
/// token (docs/external-clients.md, "A remote machine"). It is registered with schtasks.exe on
/// the current user, so it needs no password, and the bridge and the outside server run it when
/// the service's pipe is missing.
/// </summary>
internal static class LogonTask
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The task's name: one per user and pipe, as the service's mutex (the default pipe has the user's name in it).</summary>
    public static string Name(string pipe) => "Gorynych.Service." + pipe;

    /// <summary>Whether the task for this pipe is registered; never on Linux and macOS.</summary>
    public static bool Exists(string pipe) => OperatingSystem.IsWindows() && Schtasks("/Query", "/TN", Name(pipe)).Code == 0;

    /// <summary>Starts the task for this pipe now; null when Task Scheduler took it, else what schtasks said.</summary>
    public static string? Run(string pipe)
    {
        var (code, output) = Schtasks("/Run", "/TN", Name(pipe));
        return code == 0 ? null : output.Length > 0 ? output : $"schtasks exited with {code}";
    }

    /// <summary>
    /// The task: at the user's logon, run <paramref name="exe"/> with <paramref name="args"/> in
    /// their interactive session (<c>InteractiveToken</c>) with the limited token
    /// (<c>LeastPrivilege</c>), so the service never runs elevated or in session 0, even when the
    /// task is installed or run from an elevated ssh login. A second start while it runs is
    /// ignored, it has no time limit and no battery rules, and it runs at normal priority (Task
    /// Scheduler's default, 7, would give it and its agents a low CPU, I/O and memory priority).
    /// </summary>
    /// <param name="userSid">The user's SID, which Task Scheduler takes as the user's ID.</param>
    public static XDocument Xml(string userSid, string exe, IReadOnlyList<string> args)
    {
        var arguments = string.Join(' ', args.Select(DetachedProcess.Quote));
        var exec = new XElement(Ns + "Exec", new XElement(Ns + "Command", exe));
        if (arguments.Length > 0) exec.Add(new XElement(Ns + "Arguments", arguments));
        if (Path.GetDirectoryName(exe) is { Length: > 0 } folder) exec.Add(new XElement(Ns + "WorkingDirectory", folder));
        return new XDocument(new XDeclaration("1.0", "UTF-16", null),
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description",
                        "Starts the Gorynych service at this user's logon, in their session with a limited token. " +
                        "Registered by Gorynych.Service task install; removed by Gorynych.Service task uninstall.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", userSid))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", userSid),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "LeastPrivilege"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "AllowStartOnDemand", "true"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(Ns + "Priority", "4")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"), exec)));
    }

    /// <summary>Registers the task for this pipe, or replaces it; 0 on success, else 1 with what schtasks said.</summary>
    public static (int Code, string Output) Install(string pipe, string exe, IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows()) return (1, NotWindows);
        string sid;
        using (var identity = WindowsIdentity.GetCurrent()) sid = identity.User!.Value;
        var file = Path.Combine(Path.GetTempPath(), $"gorynych-task-{Guid.NewGuid():N}.xml");
        try
        {
            // UTF-16 with a byte order mark, as Task Scheduler exports a task.
            using (var writer = new StreamWriter(file, false, new UnicodeEncoding(bigEndian: false, byteOrderMark: true)))
                Xml(sid, exe, args).Save(writer);
            var (code, output) = Schtasks("/Create", "/XML", file, "/TN", Name(pipe), "/F");
            return (code == 0 ? 0 : 1, output);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Removes the task for this pipe; 0 on success, else 1 with what schtasks said.</summary>
    public static (int Code, string Output) Uninstall(string pipe)
    {
        if (!OperatingSystem.IsWindows()) return (1, NotWindows);
        var (code, output) = Schtasks("/Delete", "/TN", Name(pipe), "/F");
        return (code == 0 ? 0 : 1, output);
    }

    const string NotWindows = "the logon task is Windows only; on Linux and macOS a systemd user unit and a launchd agent are planned";

    /// <summary>Runs schtasks.exe from the system folder; its exit code and its output, trimmed.</summary>
    static (int Code, string Output) Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return (-1, "schtasks did not finish within 30 seconds");
            }
            return (process.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, $"cannot run schtasks: {e.Message}");
        }
    }
}
