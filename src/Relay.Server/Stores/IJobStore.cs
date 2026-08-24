using Relay.Core;
using Relay.Server.Data;

namespace Relay.Server.Stores;

public enum CancelResult
{
    NotFound,
    AlreadyTerminal,
    CancelledDirectly,
    MarkRequested
}

/// <summary>
/// Single writer over durable job state. The server owns every persisted transition;
/// workers act only through leased internal calls.
/// </summary>
public interface IJobStore
{
    Task<Job> CreateJobAsync(CreateJobRequest request, CancellationToken ct);
    Task<Job?> ResolveAsync(string idOrShortId, CancellationToken ct);
    Task<IReadOnlyList<Job>> ListAsync(JobStatus? status, int limit, CancellationToken ct);

    Task<(Job Job, Guid LeaseToken, IReadOnlyList<Checkpoint>)?> TryClaimAsync(
        Guid workerId, string workerName, TimeSpan leaseTtl, CancellationToken ct);

    Task<HeartbeatOutcome?> HeartbeatAsync(
        Guid jobId, Guid leaseToken, TimeSpan extendBy, Usage? usageDelta, CancellationToken ct);

    /// <summary>Validated transition performed while holding a live lease.</summary>
    Task<(Job Job, JobStatus From)?> TransitionWithLeaseAsync(Guid jobId, Guid leaseToken, JobStatus to, string? reason, CancellationToken ct);

    /// <summary>Server-initiated validated transition (sweeper, cancellation).</summary>
    Task<(Job Job, JobStatus From)?> TransitionAsync(Guid jobId, JobStatus to, string? reason, CancellationToken ct);

    /// <summary>Persists events, assigning per-job sequence numbers. Returns events with assigned seq/timestamp.</summary>
    Task<IReadOnlyList<JobEvent>> AppendEventsAsync(Guid jobId, IReadOnlyList<JobEvent> events, CancellationToken ct);

    Task AppendCheckpointAsync(Guid jobId, Checkpoint checkpoint, CancellationToken ct);
    Task<IReadOnlyList<Checkpoint>> GetCheckpointsAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, long afterSeq, int limit, CancellationToken ct);

    Task<Job?> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, Usage? usageFinal, CancellationToken ct);
    Task<(Job Job, JobStatus From)?> FailAsync(Guid jobId, Guid leaseToken, string reason, Usage? usageFinal, CancellationToken ct);

    Task<CancelResult> RequestCancelAsync(Guid jobId, CancellationToken ct);

    /// <summary>
    /// Detects leases that expired while a job sat in preparing/running/validating and runs the
    /// recovery chain per job: -> INTERRUPTED -> RECOVERING -> QUEUED (or FAILED once max attempts
    /// are exhausted, or CANCELLED if cancellation had been requested).
    /// </summary>
    Task<IReadOnlyList<SweepTransition>> SweepExpiredLeasesAsync(CancellationToken ct);
}

public sealed record SweepTransition(
    Guid JobId, string ShortId, JobStatus From, JobStatus To, string? Reason);
