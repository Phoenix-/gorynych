using System.Collections.Concurrent;
using Gorynych.Acp.Hosting;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

/// <summary>
/// Adapter hosts in the test's process: each runs a fake adapter and dials whichever conductor
/// <see cref="Service"/> names now, as a host process dials the host pipe of the service that runs.
/// Each host has its slot, and runs the <see cref="Build"/> it was started with, as a process runs
/// the executable it was started from.
/// </summary>
sealed class InProcessHosts(Func<string, IAgentAdapter> adapters) : IHostLauncher
{
    public const string Token = "0123456789abcdef0123456789abcdef-test";

    readonly ConcurrentDictionary<string, Running> hosts = new(StringComparer.Ordinal); // adapter/slot -> the host
    readonly ConcurrentQueue<Running> started = new();
    int launches;

    /// <summary>The conductor the hosts reach now; null: none runs.</summary>
    public Conductor? Service { get; set; }

    public TimeSpan OrphanIdle { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan OrphanBusy { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(10);
    public long BufferBytes { get; set; } = 32 << 20;
    public List<string> Said { get; } = [];
    public int Launches => Volatile.Read(ref launches);

    /// <summary>What the hosts it starts from now on run, as the executable on disk; an update changes it.</summary>
    public string Build { get; set; } = "build-1";

    /// <summary>The adapter options the hosts it starts from now on run with, and a conductor made now expects.</summary>
    public Func<string, IReadOnlyList<string>> Options { get; set; } = _ => [];

    /// <summary>A conductor made now tells hosts' generations apart (<see cref="ConductorOptions.HostGeneration"/>).</summary>
    public bool Generations { get; set; }

    /// <summary>The adapters with a host per session, for a conductor made now.</summary>
    public HashSet<string> SessionHosts { get; } = new(StringComparer.Ordinal);

    public sealed record Running(string Adapter, string Slot, string Build, AdapterHost Host, Task<int> Run, AgentInstance Agent);

    /// <summary>The adapter's host started last.</summary>
    public Running this[string adapter] => started.Last(r => r.Adapter == adapter);

    /// <summary>The adapter's hosts, in the order they were started.</summary>
    public Running[] Of(string adapter) => started.Where(r => r.Adapter == adapter).ToArray();

    /// <summary>The adapter's hosts that run now.</summary>
    public Running[] Alive(string adapter) => Of(adapter).Where(r => !r.Run.IsCompleted).ToArray();

    public bool IsAlive(string adapter, string slot) => hosts.TryGetValue(adapter + "/" + slot, out var running) && !running.Run.IsCompleted;

    /// <summary>While set, a launch of a slot it picks starts nothing, as a host that exits before it connects.</summary>
    public Func<string, bool>? FailLaunch { get; set; }

    public void Launch(string adapter, string slot)
    {
        Interlocked.Increment(ref launches);
        if (FailLaunch?.Invoke(slot) == true) return;
        var key = adapter + "/" + slot;
        var agent = adapters(adapter).StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var host = new AdapterHost(agent, ct => ConnectAsync(key, ct), new AdapterHostOptions
        {
            Adapter = adapter, Token = Token, RetryDelays = [TimeSpan.FromMilliseconds(50)], Slot = slot,
            Generation = new HostGeneration(Build, Options(adapter)),
            OrphanIdle = OrphanIdle, OrphanBusy = OrphanBusy, CancelGrace = CancelGrace, BufferBytes = BufferBytes,
            Log = m => { lock (Said) Said.Add(m); },
        });
        var running = new Running(adapter, slot, Build, host, host.RunAsync(), agent);
        hosts[adapter + "/" + slot] = running;
        started.Enqueue(running);
    }

    readonly List<Wire> wires = [];

    /// <summary>While set, a host that dials a conductor waits for it, as a host that comes back late.</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    /// <summary>
    /// While set, what a host writes to a conductor it dials from now on and this picks waits, while
    /// what it writes after goes on, until <see cref="LetGoAsync"/> (<see cref="Wire.HoldBackFromService"/>).
    /// </summary>
    public Func<string, bool>? HoldBack { get; set; }

    /// <summary>What <see cref="HoldBack"/> held reaches the conductors now, and nothing more waits.</summary>
    public async Task LetGoAsync()
    {
        HoldBack = null;
        Wire[] all;
        lock (wires) all = [.. wires];
        foreach (var wire in all) await wire.LetGoToServiceAsync();
    }

    readonly ConcurrentDictionary<string, Wire> lastWireOf = new(StringComparer.Ordinal); // adapter/slot -> its connection now

    /// <summary>The connection the host made last, to the conductor it reaches now.</summary>
    public Wire WireOf(Running host) => lastWireOf[host.Adapter + "/" + host.Slot];

    async Task<(Stream, Stream)> ConnectAsync(string key, CancellationToken ct)
    {
        if (ConnectGate is { } gate) await gate.Task.WaitAsync(ct);
        var to = Service ?? throw new IOException("no conductor on the host pipe");
        var wire = new Wire(to, "host", host: true);
        if (HoldBack is { } which) wire.HoldBackFromService(which);
        lock (wires) wires.Add(wire);
        lastWireOf[key] = wire;
        return wire.LinkEnd;
    }

    /// <summary>The newest connection a host made, for pausing one direction.</summary>
    public Wire LastWire
    {
        get { lock (wires) return wires[^1]; }
    }

    /// <summary>Every host's connection drops, as when the conductor's process is killed.</summary>
    public void BreakAll()
    {
        lock (wires)
            foreach (var w in wires) w.Break();
    }

    /// <summary>
    /// The adapter's host started last dies, as its process would: its connection drops, and its agent
    /// goes with it. Or only its connection drops (<paramref name="agentToo"/> false), and the host lives on.
    /// </summary>
    public async Task KillAsync(string adapter, bool agentToo = true)
    {
        var running = this[adapter];
        BreakAll();
        if (agentToo) await running.Agent.DisposeAsync();
    }

    public string Log()
    {
        lock (Said) return string.Join("\n", Said);
    }

    /// <summary>A conductor on <paramref name="data"/> that runs its adapters in these hosts, and expects the build they run now.</summary>
    public Conductor Conductor(string data, params IAgentAdapter[] names)
    {
        var build = Build;
        var options = Options;
        return new(new ConductorOptions
        {
            DataDirectory = data, ServiceId = "test-service", Hosts = this, HostToken = Token,
            HostGeneration = Generations ? a => new HostGeneration(build, options(a)) : null,
            SessionHosts = new HashSet<string>(SessionHosts, StringComparer.Ordinal),
        }, names);
    }
}
