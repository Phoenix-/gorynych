// Compiled into both Gorynych.Service (the stdio bridge) and Gorynych.Outside, which start the
// service the same way when its pipe is missing.
using System.Diagnostics;
using System.Security.Principal;

namespace Gorynych.Outside;

/// <summary>How the stdio bridge and the outside server start the service when its pipe is missing.</summary>
internal static class ServiceLaunch
{
    public enum Way
    {
        /// <summary>Run the logon task, which starts the service in the person's session with a limited token.</summary>
        RunTask,
        /// <summary>Start the service as a detached process, as the person's own session allows.</summary>
        StartProcess,
        /// <summary>Start nothing: a service started from here would run elevated, in session 0 (<see cref="Refusal"/>).</summary>
        Refuse,
    }

    /// <summary>
    /// The logon task, once the person has registered it for this pipe, always starts the
    /// service, from an ssh login too. Without it, a process outside the person's session (see
    /// <see cref="IsRemoteLogon"/>) starts nothing: the service would run elevated, in session 0,
    /// where it neither belongs nor can show anything. Anywhere else the service is started as a
    /// detached process.
    /// </summary>
    public static Way Decide(bool taskExists, bool remoteLogon) =>
        taskExists ? Way.RunTask : remoteLogon ? Way.Refuse : Way.StartProcess;

    /// <summary>
    /// Whether this process runs outside the person's own Windows session: in session 0, or with
    /// the NETWORK group in its token, as everything an ssh server starts does. Never on Linux and
    /// macOS.
    /// </summary>
    public static bool IsRemoteLogon()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using (var self = Process.GetCurrentProcess())
            if (self.SessionId == 0) return true;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null));
    }

    /// <summary>Why nothing was started, and what to do about it.</summary>
    public static string Refusal(string pipe, bool defaultPipe) =>
        $"no service runs on pipe {pipe}, and this process runs outside the user's Windows session (an ssh login or session 0), " +
        "where a service would run in session 0, and elevated for an administrator. Register the logon task once, which " +
        "starts the service in the user's session: " +
        $"Gorynych.Service.exe task install{(defaultPipe ? "" : " --pipe " + DetachedProcess.Quote(pipe))}";

    /// <summary>Why the task's service did not answer in time.</summary>
    public static string TaskTimeout(string pipe, int seconds) =>
        $"the logon task {LogonTask.Name(pipe)} did not open the pipe within {seconds} seconds. It starts the service only " +
        "while the user is signed in to Windows on this machine (a locked session will do); Task Scheduler shows its last result";
}
