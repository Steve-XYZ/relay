using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Relay.Core;
using Relay.Worker.Sandbox;


namespace Relay.Worker;

public sealed class WorkerOptions
{
    public string ServerUrl { get; set; } = "http://localhost:8080";
    public string WorkerName { get; set; } = Environment.MachineName;
    public int LeaseSeconds { get; set; } = 15;
    public int HeartbeatSeconds { get; set; } = 5;
    public int ClaimIntervalSeconds { get; set; } = 2;
    public string WorkspaceRoot { get; set; } = Path.Combine(Path.GetTempPath(), "relay-workspaces");
    public string SandboxMode { get; set; } = "docker";
    public string SandboxImage { get; set; } = "relay-sandbox:latest";
    public bool SandboxNoNetwork { get; set; } = true;
    public bool CreatePr { get; set; }
}

/// <summary>
/// Claims jobs and drives the QUEUED -> PREPARING -> RUNNING -> VALIDATING -> COMPLETED
/// pipeline. The worker never writes durable state directly: everything goes through
/// leased internal API calls, so a worker that dies mid-flight cannot corrupt the record.
/// </summary>
public sealed partial class WorkerLoop(
    IOptions<WorkerOptions> options,
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    GitWorkspace git,
    ILogger<WorkerLoop> logger) : BackgroundService
{
    private enum StopReason { None, CancelRequested, BudgetExceeded, LeaseLost, AgentFailed }

    private sealed class LeaseState
    {
        public required Job Job { get; init; }
        public required Guid Token { get; init; }
        public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public long LastCheckpointToolCall { get; set; }
        public Usage PendingDelta { get; set; } = new();
        public StopReason Stop { get; set; }
        public string? StopDetail { get; set; }
        public readonly object Gate = new();

        public string ShortId => Job.ShortId;
    }

    private readonly WorkerOptions _options = options.Value;

    private HttpClient Client() => httpClientFactory.CreateClient("relay-worker");

    /// <summary>Subscribed once; forwards workspace milestones into the durable event log.</summary>
    private void OnGitMilestone(object? _, GitWorkspace.MilestoneArgs m) =>
        _ = FireAndForget(() => EmitAsync(m.JobId, [JobEvent.Milestone(m.Name, m.Detail)], CancellationToken.None));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("worker {Name} polling {Server} for jobs", _options.WorkerName, _options.ServerUrl);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await TryClaimAsync(stoppingToken);
                if (claimed is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.ClaimIntervalSeconds), stoppingToken);
                    continue;
                }
                await RunJobAsync(claimed.Job, claimed.LeaseToken, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "claim loop error; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { /* shutting down */ }
            }
        }
    }

    private async Task<ClaimResponse?> TryClaimAsync(CancellationToken ct)
    {
        var response = await Client().PostAsJsonAsync("/internal/jobs/claim",
            new ClaimRequest
            {
                WorkerId = WorkerIdentity.Id,
                WorkerName = _options.WorkerName,
                LeaseSeconds = _options.LeaseSeconds,
            }, Json.Default, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimResponse>(Json.Default, ct);
    }

    // ---- pipeline ----

    private async Task RunJobAsync(Job job, Guid token, CancellationToken ct)
    {
        var state = new LeaseState { Job = job, Token = token };
        logger.LogInformation("[{Short}] claimed (attempt {Attempt}, resume from checkpoint {Cp})",
            state.ShortId, job.Attempt, job.ResumeFromCheckpoint);

        using var activity = WorkerTelemetry.ActivitySource.StartActivity("relay.job");
        activity?.SetTag("job.id", state.ShortId);
        activity?.SetTag("job.attempt", job.Attempt);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = HeartbeatLoopAsync(state, heartbeatCts.Token);

        ISandbox? sandbox = null;
        try
        {
            git.MilestoneEmitted += (_, m) =>
                _ = FireAndForget(() => EmitAsync(m.JobId, [JobEvent.Milestone(m.Name, m.Detail)], ct));

            // PREPARING: clone/worktree, start sandbox
            await git.PrepareAsync(_options.WorkspaceRoot, job, ct);
            await CheckpointAsync(state, "workspace_ready", new Dictionary<string, string> { ["worktree"] = git.TreeDir }, ct);

            sandbox = CreateSandbox(job);
            var env = BuildAgentEnvironment(job);
            await sandbox.StartAsync(git.TreeDir, env, ct);
            await EmitAsync(job.Id, [JobEvent.Milestone("sandbox_started", sandbox.GetType().Name)], ct);

            await TransitionOrAbortAsync(state, JobStatus.Running, ct);
            if (state.Stop != StopReason.None) return;
            await EmitAsync(job.Id, [JobEvent.Milestone("agent_started")], ct);

            // RUNNING
            var exitCode = await sandbox.ExecAsync(ResolveAgentCommand(job), line => OnAgentLine(state, line), ct);
            Console.WriteLine($"[{state.ShortId}] agent exited with code {exitCode}");

            if (state.Stop == StopReason.LeaseLost)
            {
                logger.LogWarning("[{Short}] lease lost mid-run; stopping without touching state", state.ShortId);
                return;
            }
            if (state.Stop == StopReason.AgentFailed && exitCode != 0)
            {
                state.StopDetail ??= $"agent exited with code {exitCode}";
            }

            switch (state.Stop)
            {
                case StopReason.CancelRequested:
                    await TerminalByLeaseAsync(state, JobStatus.Cancelled, "cancelled by user", ct);
                    return;
                case StopReason.BudgetExceeded:
                    await FailByLeaseAsync(state, state.StopDetail ?? "budget exceeded", ct);
                    return;
                case StopReason.AgentFailed:
                    await FailByLeaseAsync(state, state.StopDetail ?? $"agent exited with code {exitCode}", ct);
                    return;
            }

            // VALIDATING
            if (!await ValidateAsync(state, sandbox, ct)) return;

            // finalize: commit + diff (+ optional PR)
            var refreshed = await RefreshJobAsync(job.Id, ct) ?? job;
            await git.CommitIfChangedAsync(refreshed, CancellationToken.None);
            var (stat, files) = await git.DiffSummaryAsync(refreshed, CancellationToken.None);
            await EmitAsync(job.Id, [JobEvent.Milestone("diff_ready", $"{files.Count} file(s)")], ct);

            string? prUrl = _options.CreatePr ? await git.TryCreatePullRequestAsync(refreshed, CancellationToken.None) : null;

            // Ship any usage the last periodic tick missed before closing out.
            await TickAsync(state, CancellationToken.None);

            var result = new JobResult
            {
                Branch = GitWorkspace.BranchPrefix + job.ShortId,
                DiffStat = stat,
                ChangedFiles = files,
                PrUrl = prUrl,
                TestsPassed = true,
            };

            var response = await Client().PostAsJsonAsync($"/internal/jobs/{job.Id}/complete",
                new CompleteRequest { LeaseToken = state.Token, Result = result }, Json.Default, ct);

            if (response.IsSuccessStatusCode)
            {
                await EmitAsync(job.Id, [JobEvent.Milestone("result_ready")], ct);
                LogCompletion(job, files.Count, result);
            }
            else
            {
                logger.LogWarning("[{Short}] completion rejected ({Code}); job moved on without us", state.ShortId, response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown mid-job: do nothing. The lease expires, the server marks the job
            // INTERRUPTED and requeues it. That is the recovery story working as designed.
            logger.LogInformation("[{Short}] worker shutting down; server will recover the job via lease expiry", state.ShortId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Short}] pipeline failure", state.ShortId);
            try { await FailByLeaseAsync(state, ex.Message, ct); }
            catch (Exception failEx) { logger.LogError(failEx, "[{Short}] could not report failure", state.ShortId); }
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch { /* loop exits on cancellation */ }
            if (sandbox is not null) await sandbox.DisposeAsync();
        }
    }

    public async Task EmitMilestoneAsync(Guid jobId, string name, string detail, CancellationToken ct) =>
        await EmitAsync(jobId, [JobEvent.Milestone(name, detail)], ct);

    private async Task<bool> ValidateAsync(LeaseState state, ISandbox sandbox, CancellationToken ct)
    {
        await TransitionOrAbortAsync(state, JobStatus.Validating, ct);
        if (state.Stop != StopReason.None) return false;
        await EmitAsync(state.Job.Id, [JobEvent.Milestone("validation_started")], ct);

        var testCommand = ResolveTestCommand(state.Job);
        var output = new List<string>();
        var exitCode = await sandbox.ExecAsync(testCommand, line => output.Add(line), ct);
        var tail = string.Join("\n", output.TakeLast(30));

        if (exitCode != 0)
        {
            await EmitAsync(state.Job.Id, [JobEvent.Error($"tests failed:\n{tail}")], ct);
            await FailByLeaseAsync(state, $"tests failed (exit {exitCode})", ct);
            return false;
        }

        var lastLine = output.LastOrDefault(l => l.Length > 0) ?? "";
        await EmitAsync(state.Job.Id, [JobEvent.Milestone("tests_passed", lastLine)], ct);
        return true;
    }

    // ---- agent output parsing ----

    private void OnAgentLine(LeaseState state, string line)
    {
        Console.WriteLine($"[{state.ShortId}] {line}");

        var progress = ProgressRegex().Match(line);
        if (progress.Success)
        {
            var n = long.Parse(progress.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            lock (state.Gate) state.PendingDelta += new Usage { ToolCalls = 1 };

            _ = FireAndForget(async () =>
            {
                await EmitAsync(state.Job.Id,
                    [new JobEvent { Seq = 0, Kind = EventKind.Progress.ToWire(), Message = line, Data = new Dictionary<string, string> { ["tool_call"] = n.ToString() } }],
                    CancellationToken.None);
                if (n >= state.LastCheckpointToolCall + 4)
                {
                    state.LastCheckpointToolCall = n;
                    await CheckpointAsync(state, $"tool_call_{n}", new Dictionary<string, string> { ["tool_call"] = n.ToString() }, CancellationToken.None);
                }
            });
        }
        else
        {
            var usageMatch = UsageRegex().Match(line);
            if (usageMatch.Success)
            {
                lock (state.Gate) state.PendingDelta += new Usage
                {
                    TokensIn = ParseLong(usageMatch.Groups["tin"].Value),
                    TokensOut = ParseLong(usageMatch.Groups["tout"].Value),
                    CostUsd = decimal.TryParse(usageMatch.Groups["cost"].Value, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0m,
                };
                _ = FireAndForget(() => EmitAsync(state.Job.Id,
                    [new JobEvent { Seq = 0, Kind = EventKind.Usage.ToWire(), Message = line }], CancellationToken.None));
            }
            else
            {
                _ = FireAndForget(() => EmitAsync(state.Job.Id, [JobEvent.Log(line)], CancellationToken.None));
            }
        }

        CheckBudget(state);
    }

    private void CheckBudget(LeaseState state)
    {
        if (state.Stop != StopReason.None) return;
        Usage snapshot;
        lock (state.Gate) snapshot = state.PendingDelta;

        var elapsed = DateTimeOffset.UtcNow - state.StartedUtc;
        var verdict = BudgetMeter.Evaluate(snapshot, state.Job.Budget, elapsed);
        if (verdict.Exceeded)
        {
            state.Stop = StopReason.BudgetExceeded;
            state.StopDetail = verdict.Reason;
            logger.LogWarning("[{Short}] budget stop: {Reason}", state.ShortId, verdict.Reason);
        }
    }

    // ---- heartbeats ----

    /// <summary>
    /// Extends the lease periodically, shipping usage deltas. A rejected heartbeat means we are
    /// a zombie worker (server reassigned the job): stop immediately without writing anything.
    /// </summary>
    private async Task HeartbeatLoopAsync(LeaseState state, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatSeconds)));
        try
        {
            // Ship an initial heartbeat immediately so short jobs still report usage.
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            await TickAsync(state, ct);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await TickAsync(state, ct);
            }
        }
        catch (OperationCanceledException) { /* normal exit */ }
    }

    private async Task TickAsync(LeaseState state, CancellationToken ct)
    {
        Usage delta;
        bool hasDelta;
        lock (state.Gate)
        {
            delta = state.PendingDelta;
            state.PendingDelta = new Usage();
            hasDelta = delta.TotalTokens > 0 || delta.ToolCalls > 0 || delta.Retries > 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/jobs/{state.Job.Id}/heartbeat")
        {
            Content = JsonContent.Create(new HeartbeatRequest
            {
                LeaseToken = state.Token,
                UsageDelta = hasDelta ? delta : null,
                ExtendSeconds = _options.LeaseSeconds,
            }, options: Json.Default),
        };
        using var response = await Client().SendAsync(request, ct);
        if ((int)response.StatusCode == 409)
        {
            state.Stop = StopReason.LeaseLost;
            logger.LogWarning("[{Short}] heartbeat rejected ({{Code}}); standing down", state.ShortId);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            // Transient server fault: keep the pipeline alive, re-ship the delta next tick.
            lock (state.Gate) state.PendingDelta += delta;
            logger.LogWarning("[{Short}] heartbeat error ({Code}); retrying", state.ShortId, response.StatusCode);
            return;
        }

        var outcome = await response.Content.ReadFromJsonAsync<HeartbeatOutcome>(Json.Default, ct);
        if (outcome is { CancelRequested: true })
        {
            state.Stop = StopReason.CancelRequested;
            logger.LogInformation("[{Short}] cancellation requested via heartbeat", state.ShortId);
        }
    }

    // ---- helpers ----

    private string ResolveAgentCommand(Job job) => job.Agent.Trim().ToLowerInvariant() switch
    {
        "" or "mock" => $"bash {AssetsPath("mock-agent.sh")}",
        _ => job.Agent.Replace("{PROMPT}", "$PROMPT"),
    };

    private string ResolveTestCommand(Job job) =>
        !string.IsNullOrWhiteSpace(job.TestCommand) ? job.TestCommand!
        : IsMock(job.Agent) ? $"bash {AssetsPath("mock-tests.sh")}"
        : throw new InvalidOperationException($"No test_command provided for non-mock agent '{job.Agent}'");

    /// <summary>Inside containers assets mount at /relay-assets; process mode reads the extracted temp dir.</summary>
    private string AssetsPath(string fileName) =>
        _options.SandboxMode == "docker"
            ? $"/relay-assets/{fileName}"
            : Path.Combine(SandboxAssets.EnsureExtracted(), fileName);

    private static bool IsMock(string agent) =>
        string.IsNullOrWhiteSpace(agent) || agent.Trim().Equals("mock", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, string> BuildAgentEnvironment(Job job) => new()
    {
        ["PROMPT"] = job.Prompt,
        ["RESUME_FROM"] = job.ResumeFromCheckpoint.ToString(),
        ["RELAY_JOB_ID"] = job.ShortId,
    };

    private ISandbox CreateSandbox(Job job)
    {
        if (_options.SandboxMode != "docker")
            return ActivatorUtilities.CreateInstance<ProcessSandbox>(services);

        var dockerOptions = new SandboxOptions
        {
            Mode = "docker",
            Image = _options.SandboxImage,
            NoNetwork = IsMock(job.Agent) || _options.SandboxNoNetwork,
        };
        return ActivatorUtilities.CreateInstance<DockerSandbox>(services, dockerOptions);
    }

    private async Task<Job?> RefreshJobAsync(Guid jobId, CancellationToken ct)
    {
        var response = await Client().GetAsync($"/api/jobs/{jobId}", ct);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<Job>(Json.Default, ct)
            : null;
    }

    private async Task TransitionOrAbortAsync(LeaseState state, JobStatus to, CancellationToken ct)
    {
        using var response = await Client().PostAsJsonAsync($"/internal/jobs/{state.Job.Id}/transition",
            new TransitionRequest { LeaseToken = state.Token, To = to.ToWire() }, Json.Default, ct);
        if (response.IsSuccessStatusCode) return;

        logger.LogWarning("[{Short}] transition to {To} rejected ({Code})",
            state.ShortId, to.ToWire(), response.StatusCode);

        // 409 means another owner owns the job now (or the move is illegal): stand down.
        // 5xx is a server fault: surface it through the normal failure path instead of stalling.
        if ((int)response.StatusCode == 409)
        {
            state.Stop = StopReason.LeaseLost;
            return;
        }
        throw new InvalidOperationException($"transition to {to.ToWire()} failed with {(int)response.StatusCode}");
    }

    private Task TerminalByLeaseAsync(LeaseState state, JobStatus status, string reason, CancellationToken ct) =>
        TransitionOrAbortAsync(state, status, ct);

    private async Task FailByLeaseAsync(LeaseState state, string reason, CancellationToken ct)
    {
        logger.LogWarning("[{Short}] failing: {Reason}", state.ShortId, reason);
        using var response = await Client().PostAsJsonAsync($"/internal/jobs/{state.Job.Id}/fail",
            new FailRequest { LeaseToken = state.Token, Reason = reason }, Json.Default, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("[{Short}] failure report rejected ({Code})", state.ShortId, response.StatusCode);
    }

    private async Task EmitAsync(Guid jobId, IReadOnlyList<JobEvent> events, CancellationToken ct)
    {
        try
        {
            using var response = await Client().PostAsJsonAsync($"/internal/jobs/{jobId}/events",
                new AppendEventsRequest { LeaseToken = Guid.Empty, Events = events }, Json.Default, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[{Short}] failed to emit events", jobId.ToString()[..8]);
        }
    }

    private async Task CheckpointAsync(LeaseState state, string label, Dictionary<string, string>? data, CancellationToken ct)
    {
        try
        {
            using var response = await Client().PostAsJsonAsync($"/internal/jobs/{state.Job.Id}/checkpoints",
                new CheckpointRequest { LeaseToken = state.Token, Label = label, Data = data }, Json.Default, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Short}] checkpoint {Label} failed", state.ShortId, label);
        }
    }

    private void LogCompletion(Job job, int fileCount, JobResult result)
    {
        logger.LogInformation("[{Short}] COMPLETED branch={Branch} files={Files} pr={Pr}",
            job.ShortId, result.Branch, fileCount, result.PrUrl ?? "(none)");
    }

    [GeneratedRegex(@"\[relay:progress\]\s+tool_call\s+(\d+)")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"\[relay:usage\]\s+tokens_in=(?<tin>\d+)\s+tokens_out=(?<tout>\d+)(?:\s+cost_usd=(?<cost>[\d.]+))?")]
    private static partial Regex UsageRegex();

    private static long ParseLong(string value) =>
        long.TryParse(value, out var v) ? v : 0;

    private static Task FireAndForget(Func<Task> action) =>
        Task.Run(async () =>
        {
            try { await action(); } catch { /* event delivery is best-effort */ }
        });
}

public static class WorkerIdentity
{
    public static Guid Id { get; } = Guid.NewGuid();
}
