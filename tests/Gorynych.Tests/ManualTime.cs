namespace Gorynych.Tests;

/// <summary>A clock a test moves by hand, for the service's timers (<c>ConductorOptions.Time</c>).</summary>
sealed class ManualTime : TimeProvider
{
    readonly Lock gate = new();
    readonly List<Timer> timers = [];
    DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return now;
    }

    // Elapsed time (GetElapsedTime) moves with the clock too, in ticks.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (gate) return now.UtcTicks;
    }

    /// <summary>Timers set and not fired yet.</summary>
    public int Waiting
    {
        get { lock (gate) return timers.Count; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on and fires the timers that come due, on this thread.</summary>
    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock (gate)
        {
            now += by;
            due = timers.Where(t => t.Due <= now).OrderBy(t => t.Due).ToList();
            foreach (var t in due) timers.Remove(t);
        }
        foreach (var t in due) t.Fire();
    }

    /// <summary>A one-shot timer, as <c>Task.Delay</c> sets them.</summary>
    sealed class Timer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                clock.timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock.now + dueTime;
                clock.timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock.gate) clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
