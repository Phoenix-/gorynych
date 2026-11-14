using System.Globalization;
using System.Text;
using Gorynych.Adapters.Codex.Protocol;

namespace Gorynych.Adapters.Codex;

static class CodexUsage
{
    public static string Format(GetAccountRateLimitsResponse response)
    {
        var snapshots = response.RateLimitsByLimitId?
            .Where(p => p.Value is not null).Select(p => (Id: p.Key, Snapshot: p.Value!)).ToList();
        if (snapshots is not { Count: > 0 })
            snapshots = [(response.RateLimits.LimitId ?? "codex", response.RateLimits)];

        var text = new StringBuilder();
        foreach (var (id, snapshot) in snapshots) AppendSnapshot(text, id, snapshot);
        if (response.RateLimitResetCredits is { AvailableCount: > 0 } credits)
            Line(text, "Limit reset credits", $"{credits.AvailableCount} available");

        return text.Length > 0 ? text.ToString() : "No Codex usage limits are available for this account.";
    }

    static void AppendSnapshot(StringBuilder text, string id, RateLimitSnapshot snapshot)
    {
        var name = snapshot.LimitName ?? snapshot.LimitId ?? id;
        var prefix = string.IsNullOrWhiteSpace(name) ? "" : $"{name} ";
        AppendWindow(text, prefix, snapshot.Primary);
        AppendWindow(text, prefix, snapshot.Secondary);

        if (snapshot.Credits is { Unlimited: true })
            Line(text, $"{prefix}Credits", "unlimited");
        else if (!string.IsNullOrWhiteSpace(snapshot.Credits?.Balance))
            Line(text, $"{prefix}Credits", snapshot.Credits.Balance);

        if (snapshot.IndividualLimit is { } individual)
        {
            var remaining = Math.Clamp(individual.RemainingPercent, 0, 100);
            var reset = DateTimeOffset.FromUnixTimeSeconds(individual.ResetsAt).ToLocalTime();
            Line(text, $"{prefix}Individual spend limit",
                $"{remaining}% left ({individual.Used} of {individual.Limit} credits used; resets {FormatTime(reset)})");
        }
    }

    static void AppendWindow(StringBuilder text, string prefix, RateLimitWindow? window)
    {
        if (window is null) return;
        var remaining = Math.Clamp(100 - window.UsedPercent, 0, 100);
        var reset = window.ResetsAt is { } seconds
            ? $" (resets {FormatTime(DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime())})"
            : "";
        Line(text, $"{prefix}{WindowLabel(window.WindowDurationMins)}", $"{remaining}% left{reset}");
    }

    static string WindowLabel(long? minutes) => minutes switch
    {
        null => "Limit",
        < 60 => $"{minutes}m limit",
        10080 => "Weekly limit",
        _ when minutes % 10080 == 0 => $"{minutes / 10080}w limit",
        _ when minutes % 1440 == 0 => $"{minutes / 1440}d limit",
        _ when minutes % 60 == 0 => $"{minutes / 60}h limit",
        _ => $"{minutes}m limit",
    };

    static string FormatTime(DateTimeOffset time) => time.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    static void Line(StringBuilder text, string label, string value) => text.Append("**").Append(label).Append(":** ")
        .Append(value).Append("  \n");
}
