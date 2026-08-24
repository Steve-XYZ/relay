namespace Relay.Worker.Sandbox;

public interface ISandbox : IAsyncDisposable
{
    Task StartAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct);

    /// <summary>Runs a shell command inside the sandbox, streaming output lines.</summary>
    Task<int> ExecAsync(string command, Action<string>? onLine, CancellationToken ct);
}

public sealed class SandboxOptions
{
    public string Mode { get; set; } = "docker";
    public string Image { get; set; } = "relay-sandbox:latest";
    public string Memory { get; set; } = "2g";
    public string Cpus { get; set; } = "2";
    public bool NoNetwork { get; set; } = true;
}
