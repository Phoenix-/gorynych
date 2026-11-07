using System.Diagnostics;
using Gorynych.Acp.Hosting;

namespace Gorynych.Adapters.Kimi;

public sealed class KimiAdapterOptions
{
    /// <summary>The <c>kimi</c> executable; found on PATH when null.</summary>
    public string? Executable { get; init; }

    /// <summary>Receives the agent's stderr lines.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Kimi Code speaks ACP natively (<c>kimi acp</c>), so this adapter only runs the
/// unmodified binary under the user's own login and hands its stdio to the service.
/// </summary>
public sealed class KimiAdapter(KimiAdapterOptions options) : IAgentAdapter
{
    public string Name => "kimi";
    public string DisplayName => "Kimi Code";

    public Task<AgentInstance> StartAsync(CancellationToken ct)
    {
        var exe = options.Executable ?? ExecutableLocator.Find("kimi")
            ?? throw new InvalidOperationException("kimi was not found on PATH");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        psi.ArgumentList.Add("acp");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("kimi acp did not start");
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
                options.Log?.Invoke("kimi: " + line);
        }, CancellationToken.None);
        var exited = process.WaitForExitAsync(CancellationToken.None);
        return Task.FromResult(new AgentInstance(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, exited, async () =>
        {
            try { process.StandardInput.Close(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            if (await Task.WhenAny(exited, Task.Delay(3000)) != exited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        }));
    }
}
