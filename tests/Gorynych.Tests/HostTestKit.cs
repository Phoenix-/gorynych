using System.Collections.Concurrent;
using Gorynych.Acp.Hosting;
using Gorynych.Service;
using Gorynych.Service.Hosts;

namespace Gorynych.Tests;

/// <summary>
/// Adapter hosts in the test's process: each runs a fake adapter and dials whichever conductor
/// <see cref="Service"/> names now, as a host process dials the host pipe of the service that runs.
/// </summary>
sealed class InProcessHosts(Func<string, IAgentAdapter> adapters) : IHostLauncher
{
    public const string Token = "0123456789abcdef0123456789abcdef-test";

    readonly ConcurrentDictionary<string, Running> hosts = new(StringComparer.Ordinal);
    int launches;

    /// <summary>The conductor the hosts reach now; null: none runs.</summary>
    public Conductor? Service { get; set; }

    public TimeSpan OrphanIdle { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan OrphanBusy { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(10);
    public List<string> Said { get; } = [];
    public int Launches => Volatile.Read(ref launches);

    public sealed record Running(AdapterHost Host, Task<int> Run, AgentInstance Agent);

    public Running this[string adapter] => hosts[adapter];

    public bool IsAlive(string adapter) => hosts.TryGetValue(adapter, out var running) && !running.Run.IsCompleted;

    public void Launch(string adapter)
    {
        Interlocked.Increment(ref launches);
        var agent = adapters(adapter).StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var host = new AdapterHost(agent, ConnectAsync, new AdapterHostOptions
        {
            Adapter = adapter, Token = Token, RetryDelays = [TimeSpan.FromMilliseconds(50)],
            OrphanIdle = OrphanIdle, OrphanBusy = OrphanBusy, CancelGrace = CancelGrace,
            Log = m => { lock (Said) Said.Add(m); },
        });
        hosts[adapter] = new Running(host, host.RunAsync(), agent);
    }

    readonly List<Wire> wires = [];

    Task<(Stream, Stream)> ConnectAsync(CancellationToken ct)
    {
        var to = Service ?? throw new IOException("no conductor on the host pipe");
        var wire = new Wire(to, "host", host: true);
        lock (wires) wires.Add(wire);
        return Task.FromResult<(Stream, Stream)>(wire.LinkEnd);
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
    /// The host dies, as its process would: its connection drops, and its agent goes with it. Or only
    /// its connection drops (<paramref name="agentToo"/> false), and the host lives on.
    /// </summary>
    public async Task KillAsync(string adapter, bool agentToo = true)
    {
        var running = hosts[adapter];
        BreakAll();
        if (agentToo) await running.Agent.DisposeAsync();
    }

    public string Log()
    {
        lock (Said) return string.Join("\n", Said);
    }

    /// <summary>A conductor on <paramref name="data"/> that runs its adapters in these hosts.</summary>
    public Conductor Conductor(string data, params IAgentAdapter[] names) => new(new ConductorOptions
    {
        DataDirectory = data, ServiceId = "test-service", Hosts = this, HostToken = Token,
    }, names);
}
