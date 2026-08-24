using System.Diagnostics;

namespace Relay.Worker;

/// <summary>Runs a process with argv-style arguments (no shell parsing), streaming merged output lines.</summary>
public static class ProcessRunner
{
    public sealed class Result(int exitCode, List<string> output)
    {
        public int ExitCode { get; } = exitCode;
        public IReadOnlyList<string> Output { get; } = output;
        public string Tail => string.Join("\n", Output.TakeLast(30));
    }

    public static async Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> argv, string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        var output = new List<string>();
        var sync = new object();

        void Handle(string line)
        {
            lock (sync) output.Add(line);
            onLine?.Invoke(line);
        }

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{fileName}'");

        var stdoutTask = Pump(process.StandardOutput, Handle, ct);
        var stderrTask = Pump(process.StandardError, Handle, ct);

        await using var _ = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        });

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);

        lock (sync)
            return new Result(process.ExitCode, output);
    }

    private static async Task Pump(StreamReader reader, Action<string> handle, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
            handle(line);
    }
}
