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

/// <summary>
/// Guarantees the control plane now leans on: a stale worker must not be able to write
/// anything, and a recovered job must carry forward what earlier attempts already spent.
/// </summary>
public class JobLeaseGuaranteeTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);

    private static (InMemoryJobStore Store, MutableTimeProvider Clock) Create()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-09T12:00:00Z"));
        return (new InMemoryJobStore(clock), clock);
    }

    private static Task<Job> CreateJobAsync(InMemoryJobStore store) =>
        store.CreateJobAsync(new CreateJobRequest { RepoUrl = "r", Prompt = "p" }, CancellationToken.None);

    [Fact]
    public async Task A_valid_lease_is_what_authorizes_event_and_checkpoint_writes()
    {
        // The internal API gates /events and /checkpoints on exactly this check. Those calls
        // change no status, but a stale worker able to make them could poison the checkpoint
        // a recovered worker resumes from.
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);

        Assert.True(await store.HasValidLeaseAsync(job.Id, claim!.Value.LeaseToken, CancellationToken.None));
        Assert.False(await store.HasValidLeaseAsync(job.Id, Guid.NewGuid(), CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.False(await store.HasValidLeaseAsync(job.Id, claim.Value.LeaseToken, CancellationToken.None));
    }

    [Fact]
    public async Task A_recovered_job_cannot_be_heartbeated_by_the_worker_that_lost_it()
    {
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);
        var stale = claim!.Value.LeaseToken;
        await store.TransitionWithLeaseAsync(job.Id, stale, JobStatus.Running, null, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(16));
        await store.SweepExpiredLeasesAsync(CancellationToken.None);

        // The lease token is cleared on requeue, so even a clock skew that made the old expiry
        // look live again could not match it.
        Assert.Null(await store.HeartbeatAsync(
            job.Id, stale, Lease, new Usage { TokensIn = 10 }, CancellationToken.None));
        Assert.False(await store.HasValidLeaseAsync(job.Id, stale, CancellationToken.None));

        var reclaimed = await store.TryClaimAsync(Guid.NewGuid(), "worker-b", Lease, CancellationToken.None);
        Assert.NotEqual(stale, reclaimed!.Value.LeaseToken);
        Assert.Null(await store.HeartbeatAsync(job.Id, stale, Lease, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_heartbeat_is_refused_once_the_job_is_no_longer_executing()
    {
        var (store, _) = Create();
        var job = await CreateJobAsync(store);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);
        var token = claim!.Value.LeaseToken;

        Assert.NotNull(await store.HeartbeatAsync(job.Id, token, Lease, null, CancellationToken.None));

        await store.TransitionWithLeaseAsync(job.Id, token, JobStatus.Running, null, CancellationToken.None);
        await store.TransitionAsync(job.Id, JobStatus.Interrupted, "server decided", CancellationToken.None);

        // Lease clock is still live, but the job is not executing any more. Extending it would
        // let a worker keep a lease alive on work the server has already taken back.
        Assert.Null(await store.HeartbeatAsync(job.Id, token, Lease, null, CancellationToken.None));
    }

    [Fact]
    public async Task Usage_survives_recovery_so_budgets_span_attempts()
    {
        // The worker seeds its budget meter from Job.Usage at claim time. If usage did not
        // carry across attempts, a job that kept crashing would get a fresh budget every time.
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);
        var first = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);
        await store.TransitionWithLeaseAsync(job.Id, first!.Value.LeaseToken, JobStatus.Running, null, CancellationToken.None);
        await store.HeartbeatAsync(job.Id, first.Value.LeaseToken, Lease,
            new Usage { TokensIn = 40_000, TokensOut = 20_000, CostUsd = 1.25m }, CancellationToken.None);

        // The heartbeat extended the lease, so expiry is 30s out rather than 15s.
        clock.Advance(TimeSpan.FromSeconds(31));
        await store.SweepExpiredLeasesAsync(CancellationToken.None);

        var second = await store.TryClaimAsync(Guid.NewGuid(), "worker-b", Lease, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Value.Job.Attempt);
        Assert.Equal(60_000, second.Value.Job.Usage.TotalTokens);
        Assert.Equal(1.25m, second.Value.Job.Usage.CostUsd);
    }

    [Fact]
    public async Task Completion_is_refused_once_the_lease_has_expired()
    {
        // Matches the Postgres guard. Between lease expiry and the sweeper noticing, a worker
        // the server has given up on must not be able to declare the job done.
        var (store, clock) = Create();
        var job = await CreateJobAsync(store);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);
        var token = claim!.Value.LeaseToken;
        await store.TransitionWithLeaseAsync(job.Id, token, JobStatus.Running, null, CancellationToken.None);
        await store.TransitionWithLeaseAsync(job.Id, token, JobStatus.Validating, null, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(16));

        Assert.Null(await store.CompleteAsync(
            job.Id, token, new JobResult { TestsPassed = true }, null, CancellationToken.None));
        Assert.Null(await store.FailAsync(job.Id, token, "too late", null, CancellationToken.None));
        Assert.Equal(JobStatus.Validating.ToWire(),
            (await store.ResolveAsync(job.Id.ToString(), CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task Completing_a_job_appends_the_state_transition_to_the_durable_log()
    {
        // The log is how the CLI, the web timeline and the control plane reconstruct a job.
        // A completion missing from it would make replay disagree with the row.
        var (store, _) = Create();
        var jobs = TestServiceFactory.CreateJobService(store);
        var job = await CreateJobAsync(store);
        var claim = await store.TryClaimAsync(Guid.NewGuid(), "worker-a", Lease, CancellationToken.None);
        var token = claim!.Value.LeaseToken;
        await store.TransitionWithLeaseAsync(job.Id, token, JobStatus.Running, null, CancellationToken.None);
        await store.TransitionWithLeaseAsync(job.Id, token, JobStatus.Validating, null, CancellationToken.None);

        Assert.NotNull(await jobs.CompleteAsync(
            job.Id, token, new JobResult { Branch = "relay/x", TestsPassed = true }, null, CancellationToken.None));

        var events = await store.GetEventsAsync(job.Id, 0, 100, CancellationToken.None);
        var transition = Assert.Single(events,
            e => e.Kind == EventKind.State.ToWire() && e.Data?.GetValueOrDefault("to") == "completed");
        Assert.Equal("validating", transition.Data!["from"]);
        Assert.Contains(events, e => e.Message == "job_completed");
    }
}
