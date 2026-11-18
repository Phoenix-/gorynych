using System.Text.Json.Nodes;

namespace Gorynych.Service;

// This file is service-owned; agents cannot opt themselves into automatic prompts.
internal sealed record ReplyReminderPolicy(string ProjectKey, string GroupId, TimeSpan Delay, TimeSpan Overdue, TimeSpan Sweep)
{
    internal static ReplyReminderPolicy? Load(string path, out string status)
    {
        if (!File.Exists(path)) { status = "disabled: no reply-reminders.json"; return null; }
        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new FormatException("Expected an object");
            if (obj.Any(p => p.Key is not ("projectKey" or "groupId" or "delaySeconds" or "overdueSeconds" or "sweepSeconds")))
                throw new FormatException("Unknown policy field");
            var project = (string?)obj["projectKey"]; var group = (string?)obj["groupId"];
            if (string.IsNullOrWhiteSpace(project) || project.Length > 1024 || string.IsNullOrWhiteSpace(group) || group.Length > 1024)
                throw new FormatException("projectKey and groupId are required");
            var delay = (int?)obj["delaySeconds"] ?? 120;
            var overdue = (int?)obj["overdueSeconds"] ?? 600;
            var sweep = (int?)obj["sweepSeconds"] ?? 30;
            if (delay is < 1 or > 86400 || overdue is < 1 or > 604800 || sweep is < 1 or > 3600)
                throw new FormatException("Invalid reminder intervals");
            status = "enabled";
            return new(project, group, TimeSpan.FromSeconds(delay), TimeSpan.FromSeconds(overdue), TimeSpan.FromSeconds(sweep));
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidOperationException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        { status = "disabled: invalid reply-reminders.json (" + e.GetType().Name + ")"; return null; }
    }
}
