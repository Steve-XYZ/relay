using Relay.Core;

namespace Relay.Server.Stores;

/// <summary>
/// Non-durable store used for tests and local development without Postgres.
/// Implements exactly the same transition, lease and recovery semantics as PostgresJobStore.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly object _gate = new();

    private sealed class Record
    {
        public required Job Job;
        public Guid? LeaseToken;
        public List<JobEvent> Events { get; } = [];
        public List<Checkpoint> Checkpoints { get; } = [];
        public long NextEventSeq = 1;
        public long NextCheckpointSeq = 1;
    }

    private readonly Dictionary<Guid, Record> _jobs = [];
    private readonly Dictionary<Guid, (string Name, DateTimeOffset LastSeen)> _workers = [];
    private readonly TimeProvider _clock;

    public InMemoryJobStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task<Job> CreateJobAsync(CreateJobRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var prompt = request.Prompt;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ShortId = GenerateUniqueShortId(),
            ProjectId = request.ProjectId,
            Origin = request.Origin,
            Title = request.Title ?? (prompt.Length > 63 ? prompt[..60] + "..." : prompt),
            RepoUrl = request.RepoUrl,
            BaseRef = string.IsNullOrWhiteSpace(request.BaseRef) ? "HEAD" : request.BaseRef!,
            Prompt = request.Prompt,
            Agent = string.IsNullOrWhiteSpace(request.Agent) ? "mock" : request.Agent!,
            TestCommand = request.TestCommand,
            Status = JobStatus.Queued.ToWire(),
            Budget = request.Budget,
            Usage = new Usage(),
            Attempt = 0,
            CreatedAt = now,
        };
        lock (_gate) _jobs[job.Id] = new Record { Job = job };
        return Task.FromResult(job);
    }

    private string GenerateUniqueShortId()
    {
        lock (_gate)
        {
            for (var i = 0; i < 64; i++)
            {
                var candidate = ShortId.Generate();
                if (_jobs.Values.All(r => r.Job.ShortId != candidate))
                    return candidate;
            }
            throw new InvalidOperationException("Could not generate a unique short id");
        }
    }

    public Task<Job?> ResolveAsync(string idOrShortId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Guid.TryParse(idOrShortId, out var id) && _jobs.TryGetValue(id, out var byId))
                return Task.FromResult<Job?>(byId.Job);
            var byShort = _jobs.Values.FirstOrDefault(r =>
                r.Job.ShortId.Equals(idOrShortId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(byShort is null ? null : byShort.Job);
        }
    }

    public Task<IReadOnlyList<Job>> ListAsync(JobStatus? status, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IEnumerable<Record> query = _jobs.Values.OrderByDescending(r => r.Job.CreatedAt);
            if (status is not null)
                query = query.Where(r => r.Job.Status == status.Value.ToWire());
            IReadOnlyList<Job> result = query.Take(limit).Select(r => r.Job).ToList();
            return Task.FromResult(result);
        }
    }

    public async Task<(Job Job, Guid LeaseToken, IReadOnlyList<Checkpoint>)?> TryClaimAsync(
        Guid workerId, string workerName, TimeSpan leaseTtl, CancellationToken ct)
    {
        await EnsureWorkerAsync(workerId, workerName, ct);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var candidate = _jobs.Values
                .Where(r => r.Job.Status is "queued" or "recovering")
                .Where(r => !r.Job.CancelRequested && !IsLeaseActive(r, now))
                .OrderBy(r => r.Job.CreatedAt)
                .FirstOrDefault();
            if (candidate is null) return null;

            var token = Guid.NewGuid();
            var job = candidate.Job with
            {
                Status = JobStatus.Preparing.ToWire(),
                Attempt = candidate.Job.Attempt + 1,
                StartedAt = candidate.Job.StartedAt ?? now,
                LeaseExpiresAt = now + leaseTtl,
            };
            candidate.Job = job;
            candidate.LeaseToken = token;
            return (job, token, candidate.Checkpoints.Select(c => c).ToList());
        }
    }

    private static bool IsLeaseActive(Record r, DateTimeOffset now) =>
        r.LeaseToken is not null && r.Job.LeaseExpiresAt is { } exp && exp > now;

    public Task EnsureWorkerAsync(Guid workerId, string name, CancellationToken ct)
    {
        lock (_gate) _workers[workerId] = (name, _clock.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task<bool> HasValidLeaseAsync(Guid jobId, Guid leaseToken, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var r)) return Task.FromResult(false);
            return Task.FromResult(IsLeaseActive(r, _clock.GetUtcNow()) && r.LeaseToken == leaseToken);
        }
    }

    public Task<HeartbeatOutcome?> HeartbeatAsync(
        Guid jobId, Guid leaseToken, TimeSpan extendBy, Usage? usageDelta, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_jobs.TryGetValue(jobId, out var r)) return Task.FromResult<HeartbeatOutcome?>(null);
            if (r.LeaseToken != leaseToken || !IsLeaseActive(r, now)) return Task.FromResult<HeartbeatOutcome?>(null);
            // Same guard as Postgres: a job the sweeper already moved out of execution must
            // not be extendable, or a zombie could keep a recovered job's lease alive.
            if (r.Job.Status is not ("preparing" or "running" or "validating"))
                return Task.FromResult<HeartbeatOutcome?>(null);

            var usage = r.Job.Usage;
            if (usageDelta is not null) usage = usage + usageDelta;
            var expires = (r.Job.LeaseExpiresAt ?? now) + extendBy;
            r.Job = r.Job with { Usage = usage, LeaseExpiresAt = expires };
            return Task.FromResult<HeartbeatOutcome?>(new HeartbeatOutcome(r.Job.CancelRequested, expires, r.Job));
        }
    }

    private Job? TransitionLocked(Record r, JobStatus to, string? reason, bool requireLease, Guid? leaseToken)
    {
        var current = Wire.From(r.Job.Status);
        if (!JobStateMachine.CanTransition(current, to)) return null;
        if (requireLease && (r.LeaseToken != leaseToken || !IsLeaseActive(r, _clock.GetUtcNow()))) return null;

        var now = _clock.GetUtcNow();
        var job = r.Job with
        {
            Status = to.ToWire(),
            FailureReason = to == JobStatus.Failed ? reason : r.Job.FailureReason,
            FinishedAt = to.IsTerminal() ? now : r.Job.FinishedAt,
        };
        if (to.IsTerminal()) { r.LeaseToken = null; job = job with { LeaseExpiresAt = null }; }
        r.Job = job;
        return job;
    }

    public Task<(Job Job, JobStatus From)?> TransitionWithLeaseAsync(Guid jobId, Guid leaseToken, JobStatus to, string? reason, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_jobs.TryGetValue(jobId, out var r)
                ? Wrap(r, () => TransitionLocked(r, to, reason, requireLease: true, leaseToken))
                : null);
    }

    public Task<(Job Job, JobStatus From)?> TransitionAsync(Guid jobId, JobStatus to, string? reason, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_jobs.TryGetValue(jobId, out var r)
                ? Wrap(r, () => TransitionLocked(r, to, reason, requireLease: false, leaseToken: null))
                : null);
    }

    private static (Job, JobStatus)? Wrap(Record r, Func<Job?> transition)
    {
        var from = Wire.From(r.Job.Status);
        var job = transition();
        return job is null ? null : (job, from);
    }

    public Task<IReadOnlyList<JobEvent>> AppendEventsAsync(Guid jobId, IReadOnlyList<JobEvent> events, CancellationToken ct)
    {
        lock (_gate)
        {
            var r = _jobs[jobId];
            var persisted = new List<JobEvent>(events.Count);
            foreach (var e in events)
            {
                var stored = e with { Seq = r.NextEventSeq++, CreatedAt = _clock.GetUtcNow() };
                r.Events.Add(stored);
                persisted.Add(stored);
            }
            return Task.FromResult<IReadOnlyList<JobEvent>>(persisted);
        }
    }

    public Task AppendCheckpointAsync(Guid jobId, Checkpoint checkpoint, CancellationToken ct)
    {
        lock (_gate)
        {
            var r = _jobs[jobId];
            r.Checkpoints.Add(checkpoint with { Seq = r.NextCheckpointSeq++, CreatedAt = _clock.GetUtcNow() });
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Checkpoint>> GetCheckpointsAsync(Guid jobId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Checkpoint>>(_jobs[jobId].Checkpoints.ToList());
    }

    public Task<IReadOnlyList<JobEvent>> GetEventsAsync(Guid jobId, long afterSeq, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<JobEvent> result = _jobs[jobId].Events
                .Where(e => e.Seq > afterSeq)
                .OrderBy(e => e.Seq)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Job?> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, Usage? usageFinal, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var r)) return Task.FromResult<Job?>(null);
            var job = TransitionLocked(r, JobStatus.Completed, reason: null, requireLease: true, leaseToken);
            if (job is null) return Task.FromResult<Job?>(null);
            job = job with { Result = result, Usage = usageFinal ?? r.Job.Usage };
            r.Job = job;
            return Task.FromResult<Job?>(job);
        }
    }

    public Task<(Job Job, JobStatus From)?> FailAsync(Guid jobId, Guid leaseToken, string reason, Usage? usageFinal, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var r)) return Task.FromResult<(Job, JobStatus)?>(null);
            var from = Wire.From(r.Job.Status);
            var job = TransitionLocked(r, JobStatus.Failed, reason, requireLease: true, leaseToken);
            if (job is null) return Task.FromResult<(Job, JobStatus)?>(null);
            if (usageFinal is not null) { job = job with { Usage = usageFinal }; r.Job = job; }
            return Task.FromResult<(Job, JobStatus)?>(new(job, from));
        }
    }

    public Task<CancelResult> RequestCancelAsync(Guid jobId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var r)) return Task.FromResult(CancelResult.NotFound);
            var status = Wire.From(r.Job.Status);
            if (status.IsTerminal()) return Task.FromResult(CancelResult.AlreadyTerminal);

            // Jobs that hold no live work can be cancelled in place.
            if (status is JobStatus.Queued or JobStatus.Interrupted or JobStatus.Recovering &&
                !(r.LeaseToken is not null && IsLeaseActive(r, _clock.GetUtcNow())))
            {
                TransitionLocked(r, JobStatus.Cancelled, "cancelled by user", requireLease: false, null);
                return Task.FromResult(CancelResult.CancelledDirectly);
            }

            r.Job = r.Job with { CancelRequested = true };
            return Task.FromResult(CancelResult.MarkRequested);
        }
    }

    public Task<IReadOnlyList<SweepTransition>> SweepExpiredLeasesAsync(CancellationToken ct)
    {
        var transitions = new List<SweepTransition>();
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var expired = _jobs.Values
                .Where(r => r.Job.Status is "preparing" or "running" or "validating")
                .Where(r => r.Job.LeaseExpiresAt is { } exp && exp <= now)
                .ToList();

            foreach (var r in expired)
            {
                var from = Wire.From(r.Job.Status);

                var interrupted = TransitionLocked(r, JobStatus.Interrupted, "worker lost: lease expired", false, null);
                if (interrupted is null) continue;
                transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, from, JobStatus.Interrupted, "worker lost: lease expired"));

                if (r.Job.CancelRequested)
                {
                    TransitionLocked(r, JobStatus.Recovering, "recovery started", false, null);
                    transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, JobStatus.Interrupted, JobStatus.Recovering, "recovery started"));
                    TransitionLocked(r, JobStatus.Cancelled, "cancelled while worker lost", false, null);
                    transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, JobStatus.Recovering, JobStatus.Cancelled, "cancelled while worker lost"));
                    continue;
                }

                if (r.Job.Attempt >= r.Job.MaxAttempts)
                {
                    var failed = TransitionLocked(r, JobStatus.Failed, $"recovery attempts exhausted ({r.Job.MaxAttempts})", false, null)!;
                    transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, JobStatus.Interrupted, JobStatus.Failed, failed.FailureReason));
                    continue;
                }

                TransitionLocked(r, JobStatus.Recovering, "recovery started", false, null);
                transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, JobStatus.Interrupted, JobStatus.Recovering, "recovery started"));

                var lastCp = r.Checkpoints.Count == 0 ? 0 : r.Checkpoints.Max(c => c.Seq);
                var requeued = TransitionLocked(r, JobStatus.Queued, "requeued for recovery", false, null)!;
                requeued = requeued with { ResumeFromCheckpoint = lastCp, LeaseExpiresAt = null };
                r.Job = requeued;
                r.LeaseToken = null;
                transitions.Add(new SweepTransition(r.Job.Id, r.Job.ShortId, JobStatus.Recovering, JobStatus.Queued, "requeued for recovery"));
            }
        }
        return Task.FromResult<IReadOnlyList<SweepTransition>>(transitions);
    }
}
