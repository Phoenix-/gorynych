using System.Text.Json.Nodes;

namespace Gorynych.Service;

/// <summary>Live aggregate activity, independent of history and individual prompt responses.</summary>
internal sealed class SessionActivity(string epoch, Action<JsonObject>? changed = null)
{
    readonly Lock gate = new();
    int work;
    string native = "idle";
    string state = "idle";
    long revision;

    public JsonObject Snapshot()
    {
        lock (gate) return SnapshotLocked();
    }

    JsonObject SnapshotLocked() => new() { ["state"] = state, ["epoch"] = epoch, ["revision"] = revision };

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
