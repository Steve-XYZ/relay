using Relay.Core;
using Relay.Server.Sse;
using Relay.Server.Stores;
using Relay.Server.Telemetry;

namespace Relay.Server.Services;

/// <summary>
/// Orchestrates persisted state changes and live fan-out. Every status change goes through
/// here so the state event is appended to the durable log and pushed to subscribers together.
/// </summary>
public sealed class JobService
{
    private readonly IJobStore _store;
    private readonly SseHub _sse;
    private readonly ILogger<JobService> _logger;

    public JobService(IJobStore store, SseHub sse, ILogger<JobService> logger)
    {
        _store = store;
        _sse = sse;
        _logger = logger;
    }

    private void Broadcast(Guid jobId, IReadOnlyList<JobEvent> events)
    {
        foreach (var e in events)
            _sse.Publish(jobId, SseFormat.Frame(e));
    }

    public async Task<(Job Job, IReadOnlyList<JobEvent> Events)> CreateAsync(CreateJobRequest request, CancellationToken ct)
    {
        var job = await _store.CreateJobAsync(request, ct);
        var events = await _store.AppendEventsAsync(job.Id,
            [JobEvent.Milestone("job_created", job.ShortId)], ct);
        Broadcast(job.Id, events);
        RelayMetrics.JobsCreated.Add(1);
        return (job, events);
    }

    public async Task<IReadOnlyList<JobEvent>> AppendEventsAsync(Guid jobId, IReadOnlyList<JobEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return events;
        var persisted = await _store.AppendEventsAsync(jobId, events, ct);
        Broadcast(jobId, persisted);
        return persisted;
    }

    /// <summary>Worker-held transition. Returns false when the lease is stale or the move is invalid.</summary>
    public async Task<(bool Ok, Job? Job)> TransitionWithLeaseAsync(Guid jobId, Guid leaseToken, JobStatus to, string? reason, CancellationToken ct)
    {
        var outcome = await _store.TransitionWithLeaseAsync(jobId, leaseToken, to, reason, ct);
        if (outcome is null) return (false, null);

        var events = await AppendEventsAsync(jobId,
        [
            JobEvent.State(outcome.Value.From, to, reason),
            ..to.IsTerminal() ? [JobEvent.Milestone($"job_{to.ToWire()}")] : Array.Empty<JobEvent>(),
        ], ct);
        if (to.IsTerminal()) RelayMetrics.JobsFinished.Add(1, KeyValuePair.Create<string, object?>("status", to.ToWire()));
        return (true, outcome.Value.Job);
    }

    public async Task HandleSweepTransitionsAsync(IReadOnlyList<SweepTransition> transitions, CancellationToken ct)
    {
        foreach (var t in transitions)
        {
            _logger.LogWarning("Job {ShortId}: {From} -> {To} ({Reason})", t.ShortId, t.From.ToWire(), t.To.ToWire(), t.Reason);
            await AppendEventsAsync(t.JobId,
                [JobEvent.State(t.From, t.To, t.Reason), ..TerminalEvent(t.To)],
                ct);
            if (t.To.IsTerminal()) RelayMetrics.JobsFinished.Add(1, KeyValuePair.Create<string, object?>("status", t.To.ToWire()));
        }
    }

    private static JobEvent[] TerminalEvent(JobStatus to) =>
        to.IsTerminal() ? [JobEvent.Milestone($"job_{to.ToWire()}")] : [];

    /// <summary>
    /// Completion is a state change like any other, so it appends the same
    /// <c>validating -> completed</c> state event. Without it the durable log would disagree
    /// with the row, and replaying the log — which is how the CLI, the web timeline and any
    /// future consumer reconstruct a job — would never show the job finishing.
    /// </summary>
    public async Task<Job?> CompleteAsync(Guid jobId, Guid leaseToken, JobResult result, Usage? usageFinal, CancellationToken ct)
    {
        var job = await _store.CompleteAsync(jobId, leaseToken, result, usageFinal, ct);
        if (job is null) return null;
        await AppendEventsAsync(jobId,
        [
            JobEvent.State(JobStatus.Validating, JobStatus.Completed),
            JobEvent.Milestone("result_ready", job.Result?.Branch ?? ""),
            JobEvent.Milestone("job_completed"),
        ], ct);
        RelayMetrics.JobsFinished.Add(1, KeyValuePair.Create<string, object?>("status", "completed"));
        return job;
    }

    public async Task<Job?> FailAsync(Guid jobId, Guid leaseToken, string reason, Usage? usageFinal, CancellationToken ct)
    {
        var outcome = await _store.FailAsync(jobId, leaseToken, reason, usageFinal, ct);
        if (outcome is null) return null;
        await AppendEventsAsync(jobId,
        [
            JobEvent.Error(reason),
            JobEvent.State(outcome.Value.From, JobStatus.Failed, reason),
            JobEvent.Milestone("job_failed"),
        ], ct);
        RelayMetrics.JobsFinished.Add(1, KeyValuePair.Create<string, object?>("status", "failed"));
        return outcome.Value.Job;
    }
}
