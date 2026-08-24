namespace Relay.Worker.Sandbox;

/// <summary>
/// Runs agent commands directly on the host (dev/CI mode without Docker).
/// Isolation is limited to the working directory; documented trade-off, not for production.
/// </summary>
public sealed class ProcessSandbox(ILogger<ProcessSandbox> logger) : ISandbox
{
    private string _workspace = "";
    private IReadOnlyDictionary<string, string> _env = new Dictionary<string, string>();

    public Task StartAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        _workspace = workspace;
        _env = env;
        logger.LogInformation("process sandbox active in {Workspace} (no container isolation)", workspace);
        return Task.CompletedTask;
    }

    public Task<int> ExecAsync(string command, Action<string>? onLine, CancellationToken ct) =>
        ProcessRunner.RunAsync("/bin/bash", ["-lc", command], _workspace, _env, onLine, ct)
            .ContinueWith(t => t.Result.ExitCode, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
