using System.Text.Json.Nodes;
using Gorynych.Acp.Hosting;

namespace Gorynych.Service;

/// <summary>Live aggregate activity, independent of history and individual prompt responses.</summary>
internal sealed class SessionActivity(string epoch, Action<JsonObject>? changed = null)
{
    readonly Lock gate = new();
    int work;
    string native = "idle";
    string state = "idle";
    JsonArray? background;
    JsonArray? backgroundKnown;
    long revision;

    public JsonObject Snapshot()
    {
        lock (gate) return SnapshotLocked();
    }

    JsonObject SnapshotLocked() => new() { ["state"] = state, ["epoch"] = epoch, ["revision"] = revision, ["background"] = background?.DeepClone(), ["backgroundKnown"] = backgroundKnown?.DeepClone() };

    public void ReportKnownBackground(BackgroundWorkSnapshot? tasks)
    {
        JsonObject? snapshot = null;
        var next = tasks?.ToJson();
        lock (gate)
        {
            if (!JsonNode.DeepEquals(backgroundKnown, next))
            {
                backgroundKnown = next;
                revision++;
                snapshot = SnapshotLocked();
            }
        }
        if (snapshot is not null) changed?.Invoke(snapshot);
    }

    /// <summary>Replaces the live background snapshot; null means it is unavailable.</summary>
    public void ReportBackground(BackgroundWorkSnapshot? tasks)
    {
        JsonObject? snapshot = null;
        var next = tasks?.ToJson();
        lock (gate)
        {
            if (!JsonNode.DeepEquals(background, next))
            {
                background = next;
                revision++;
                snapshot = SnapshotLocked();
            }
        }
        if (snapshot is not null) changed?.Invoke(snapshot);
    }

    public void Begin() => Change(() => work++);
    public void End() => Change(() => { if (work > 0) work--; });

    /// <summary>Only an authoritative adapter observation may declare the whole workflow blocked.</summary>
    public void Report(string value)
    {
        if (value is "running" or "idle" or "requires_action") Change(() => native = value);
    }

    void Change(Action update)
    {
        JsonObject? snapshot = null;
        lock (gate)
        {
            update();
            var next = native == "requires_action" ? native : work > 0 || native == "running" ? "running" : "idle";
            if (next != state)
            {
                state = next;
                revision++;
                snapshot = SnapshotLocked();
            }
        }
        // Never call the conductor under this lock: listing takes the store lock before reading us.
        // Concurrent publishers may arrive out of order; clients reconcile by epoch and revision.
        if (snapshot is not null) changed?.Invoke(snapshot);
    }
}
