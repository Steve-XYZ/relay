namespace Relay.Worker.Sandbox;

/// <summary>
/// Runs agent commands in an isolated Docker container with the job worktree mounted
/// at /workspace. Resource limits and (optionally) network isolation are enforced by Docker.
/// Environment variables travel through an env-file so arbitrary prompts pass safely.
/// </summary>
public sealed class DockerSandbox(SandboxOptions options, ILogger<DockerSandbox> logger) : ISandbox
{
    private string _containerName = "";
    private string _workspace = "";
    private IReadOnlyDictionary<string, string> _env = new Dictionary<string, string>();

    public async Task StartAsync(string workspace, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var assets = SandboxAssets.EnsureExtracted();
        _workspace = Path.GetFullPath(workspace);
        _env = env;
        _containerName = $"relay-sbx-{Path.GetFileName(_workspace)}";

        var envFile = Path.Combine(Path.GetTempPath(), $"relay-env-{Path.GetFileName(_workspace)}.txt");
        await File.WriteAllLinesAsync(envFile,
            _env.Select(kv => $"{kv.Key}={kv.Value.Replace("\n", "\\n")}"), ct);

        await RunDocker(["rm", "-f", _containerName], ignoreFailure: true, ct);

        var argv = new List<string>
        {
            "run", "-d", "--name", _containerName,
            "--network", options.NoNetwork ? "none" : "bridge",
            "--memory", options.Memory,
            "--cpus", options.Cpus,
            "--pids-limit", "512",
            "--user", $"{Uid()}:{Gid()}",
            "-v", $"{_workspace}:/workspace",
            "-v", $"{assets}:/relay-assets:ro",
            "--env-file", envFile,
            "-w", "/workspace",
            options.Image,
            "sleep", "infinity",
        };
        await RunDocker(argv, ignoreFailure: false, ct);
        logger.LogInformation("sandbox container {Name} started (image {Image})", _containerName, options.Image);
    }

    public Task<int> ExecAsync(string command, Action<string>? onLine, CancellationToken ct) =>
        ProcessRunner.RunAsync("docker",
            ["exec", _containerName, "bash", "-lc", command],
            null, null, onLine, ct).ContinueWith(t => t.Result.ExitCode, ct);

    private async Task RunDocker(IReadOnlyList<string> argv, bool ignoreFailure, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("docker", argv, null, null,
            line => logger.LogDebug("[docker] {Line}", line), CancellationToken.None);
        if (result.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException($"docker {string.Join(' ', argv)} failed ({result.ExitCode}): {result.Tail}");
    }

    private static string Uid() => Id("-u");
    private static string Gid() => Id("-g");

    private static string Id(string arg)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("id", arg)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(2000);
        return output.Length > 0 ? output : "0";
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerName.Length == 0) return;
        try
        {
            await RunDocker(["rm", "-f", _containerName], ignoreFailure: true, CancellationToken.None);
            logger.LogInformation("sandbox container {Name} removed", _containerName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to remove sandbox container {Name}", _containerName);
        }
    }
}
