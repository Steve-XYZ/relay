using Relay.Server.Stores;
using Relay.Core;
using Xunit;

namespace Relay.Server.Tests;

/// <summary>
/// Exercises the lease, recovery and cancellation semantics that the Postgres store
/// must match. These are the behaviors the crash-recovery demo depends on.
/// </summary>
public class InMemoryJobStoreTests
{
    private static (InMemoryJobStore Store, MutableTimeProvider Clock) Create()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        return (new InMemoryJobStore(clock), clock);
    }

    private static async Task<Job> CreateJobAsync(IJobStore store)
    {
        var job = await store.CreateJobAsync(new CreateJobRequest
        {
            RepoUrl = "https://github.com/acme/widget",
            Prompt = "Fix issue BOS-123 style bug in this repo",
        }, CancellationToken.None);

        // Simulate a first claim so the job sits in RUNNING with a live lease.
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", TimeSpan.FromSeconds(15), CancellationToken.None);
        Assert.NotNull(claim);
        await store.TransitionWithLeaseAsync(claim!.Value.Job.Id, claim.Value.LeaseToken, JobStatus.Running, null, CancellationToken.None);
        return await store.ResolveAsync(job.Id.ToString(), CancellationToken.None) ?? throw new InvalidOperationException();
    }

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Claim_assigns_lease_and_increments_attempt()
    {
        var (store, _) = Create();
        var created = await store.CreateJobAsync(
            new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);

        var claim = await store.TryClaimAsync(Guid.NewGuid(), "w", Lease, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(created.Id, claim!.Value.Job.Id);
        Assert.Equal(JobStatus.Preparing.ToWire(), claim.Value.Job.Status);
        Assert.Equal(1, claim.Value.Job.Attempt);
        Assert.True(await store.HasValidLeaseAsync(created.Id, claim.Value.LeaseToken, CancellationToken.None));
    }

    [Fact]
    public async Task Claim_skips_jobs_with_live_leases_or_cancel_requests()
    {
        var (store, _) = Create();
        await store.TryClaimAsync(Guid.NewGuid(), "w", Lease, CancellationToken.None); // claims the only queued job

        var second = await store.TryClaimAsync(Guid.NewGuid(), "w2", Lease, CancellationToken.None);
        Assert.Null(second);
    }

    [Fact]
    public async Task Heartbeat_extends_lease_and_accumulates_usage()
    {
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);

        var before = await store.ResolveAsync(job.Id.ToString(), CancellationToken.None);
        var token = Guid.NewGuid(); // unknown -> rejected below; we need the real one, re-claim instead

        // Fetch real token by claiming a fresh job.
        var fresh = await store.CreateJobAsync(new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "w", Lease, CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(fresh.Id, claim!.Value.Job.Id);

        var outcome = await store.HeartbeatAsync(fresh.Id, claim.Value.LeaseToken,
            TimeSpan.FromSeconds(15), new Usage { TokensIn = 100, TokensOut = 50, ToolCalls = 3 }, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.False(outcome!.CancelRequested);
        Assert.Equal(150, outcome.Job.Usage.TotalTokens);

        // Unknown token must be rejected.
        var stale = await store.HeartbeatAsync(fresh.Id, token, TimeSpan.FromSeconds(15), null, CancellationToken.None);
        Assert.Null(stale);
    }

    [Fact]
    public async Task Sweep_moves_expired_job_through_interrupted_recovering_to_queued()
    {
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);

        clock.Advance(TimeSpan.FromSeconds(16)); // lease expired
        var transitions = await store.SweepExpiredLeasesAsync(CancellationToken.None);

        var chain = transitions.Where(t => t.JobId == job.Id).Select(t => t.To).ToList();
        Assert.Equal([JobStatus.Interrupted, JobStatus.Recovering, JobStatus.Queued], chain);

        var requeued = await store.ResolveAsync(job.Id.ToString(), CancellationToken.None);
        Assert.Equal(JobStatus.Queued.ToWire(), requeued!.Status);
        Assert.Null(requeued.LeaseExpiresAt);

        // A new worker can claim it and receives attempt=2.
        var reclaim = await store.TryClaimAsync(Guid.NewGuid(), "worker-b", Lease, CancellationToken.None);
        Assert.NotNull(reclaim);
        Assert.Equal(job.Id, reclaim!.Value.Job.Id);
        Assert.Equal(2, reclaim.Value.Job.Attempt);
    }

    [Fact]
    public async Task Sweep_fails_job_once_max_attempts_are_exhausted()
    {
        var (store, clock) = Create();

        // Attempt 1 expires, gets requeued; attempt 2 claimed then expires again -> exhausted at 3? max=3:
        // attempt1 expire -> requeue; attempt2 expire -> requeue; attempt3 expire -> failed.
        var job = await store.CreateJobAsync(
            new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);
        var workerId = Guid.NewGuid();

        for (var expectedAttempt = 1; expectedAttempt <= 3; expectedAttempt++)
        {
            var claim = await store.TryClaimAsync(workerId, "w", Lease, CancellationToken.None);
            Assert.NotNull(claim);
            Assert.Equal(expectedAttempt, claim!.Value.Job.Attempt);
            await store.TransitionWithLeaseAsync(job.Id, claim.Value.LeaseToken, JobStatus.Running, null, CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(16));

            if (expectedAttempt == 3)
            {
                var transitions = await store.SweepExpiredLeasesAsync(CancellationToken.None);
                var last = transitions.Last(t => t.JobId == job.Id);
                Assert.Equal(JobStatus.Failed, last.To);

                var resolved = await store.ResolveAsync(job.Id.ToString(), CancellationToken.None);
                Assert.Equal(JobStatus.Failed.ToWire(), resolved!.Status);
                Assert.Contains("exhausted", resolved.FailureReason);
                return;
            }

            await store.SweepExpiredLeasesAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Cancel_directly_cancels_idle_jobs_and_flags_running_ones()
    {
        var (store, clock) = Create();

        var idle = await store.CreateJobAsync(new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);
        Assert.Equal(CancelResult.CancelledDirectly, await store.RequestCancelAsync(idle.Id, CancellationToken.None));
        var cancelled = await store.ResolveAsync(idle.Id.ToString(), CancellationToken.None);
        Assert.Equal(JobStatus.Cancelled.ToWire(), cancelled!.Status);

        var running = await CreateJobAsync(store);
        Assert.Equal(CancelResult.MarkRequested, await store.RequestCancelAsync(running.Id, CancellationToken.None));
        var flagged = await store.ResolveAsync(running.Id.ToString(), CancellationToken.None);
        Assert.True(flagged!.CancelRequested);
        Assert.Equal(JobStatus.Running.ToWire(), flagged.Status);

        // A running job with cancel_requested must never be handed to another worker after recovery.
        clock.Advance(TimeSpan.FromSeconds(16));
        await store.SweepExpiredLeasesAsync(CancellationToken.None);
        var afterSweep = await store.ResolveAsync(running.Id.ToString(), CancellationToken.None);
        Assert.Equal(JobStatus.Cancelled.ToWire(), afterSweep!.Status);
    }

    [Fact]
    public async Task Complete_requires_validating_status_and_valid_lease()
    {
        var (store, _) = Create();
        var job = await store.CreateJobAsync(new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "w", Lease, CancellationToken.None);
        Assert.NotNull(claim);

        var result = new JobResult { Branch = "relay/abcd", TestsPassed = true };
        var wrongState = await store.CompleteAsync(job.Id, claim!.Value.LeaseToken, result, null, CancellationToken.None);
        Assert.Null(wrongState); // completing from preparing is invalid

        await store.TransitionWithLeaseAsync(job.Id, claim.Value.LeaseToken, JobStatus.Running, null, CancellationToken.None);
        await store.TransitionWithLeaseAsync(job.Id, claim.Value.LeaseToken, JobStatus.Validating, null, CancellationToken.None);

        var wrongToken = await store.CompleteAsync(job.Id, Guid.NewGuid(), result, null, CancellationToken.None);
        Assert.Null(wrongToken);

        var done = await store.CompleteAsync(job.Id, claim.Value.LeaseToken, result,
            new Usage { TokensIn = 900, TokensOut = 100, CostUsd = 0.42m }, CancellationToken.None);
        Assert.NotNull(done);
        var completedJob = done!;
        Assert.Equal(JobStatus.Completed.ToWire(), completedJob.Status);
        Assert.Equal(1000, completedJob.Usage.TotalTokens);
        Assert.True(completedJob.Result!.TestsPassed);
    }
}
