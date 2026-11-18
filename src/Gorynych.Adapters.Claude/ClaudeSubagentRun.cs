using System.Text.Json.Nodes;

namespace Gorynych.Adapters.Claude;

/// <summary>Run correlation for one native Claude task. A task can be resumed without changing identity.</summary>
internal sealed class ClaudeSubagentRun
{
    string? runId;
    string? toolId;
    readonly HashSet<string> seenTools = new(StringComparer.Ordinal);
    bool toolIsUnique;
    bool hasRun;
    bool answered;
    bool terminal;
    public bool IsActive => hasRun && !terminal;
    public long Version { get; private set; }

    /// <summary>Accepts a newer run; duplicate and out-of-order starts do not reset its evidence.</summary>
    public bool Start(string? next, string? tool = null)
    {
        if (hasRun)
        {
            if (next is not null && runId is not null)
            {
                if (string.CompareOrdinal(next, runId) <= 0) return false;
            }
            else if (tool is null || seenTools.Contains(tool)) return false;
        }
        runId = next;
        toolId = tool;
        toolIsUnique = tool is not null && seenTools.Add(tool);
        hasRun = true;
        Version++;
        answered = false;
        terminal = false;
        return true;
    }

    /// <summary>Records a directly observed child assistant response for this run.</summary>
    public void Answered() { if (hasRun && !terminal) answered = true; }

    public void Lost() { terminal = true; }

    /// <summary>
    /// A terminal task event is accepted only for the active run. Without a run ID, a unique
    /// tool ID or a single observed run is required to rule out a late event from another run.
    /// </summary>
    public (bool Accepted, string? Fallback) Finish(string? eventRun, string? status, string? summary, string? eventTool = null)
    {
        if (!hasRun || terminal) return (false, null);
        if (eventRun is not null ? eventRun != runId : eventTool is not null ? eventTool != toolId || !toolIsUnique : Version > 1) return (false, null);
        if (status is not ("completed" or "failed" or "stopped")) return (false, null);
        terminal = true;
        return (true, status == "completed" && !answered && !string.IsNullOrWhiteSpace(summary) ? summary : null);
    }
}
