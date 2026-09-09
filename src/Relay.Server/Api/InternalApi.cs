using Relay.Core;
using Relay.Server.Services;
using Relay.Server.Stores;

namespace Relay.Server.Api;

/// <summary>
/// Execution-plane surface for workers. Workers never touch Postgres; every call is
/// authorized by the lease token issued at claim time and rejected with 409 once that lease
/// is stale. That includes the append-only writes: events and checkpoints carry no status
/// change, but a stale worker able to write them could still poison the checkpoint a
/// recovered worker resumes from.
/// </summary>
public static class InternalApi
{
    public static IEndpointRouteBuilder MapInternalApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/jobs/claim", async (
            ClaimRequest request, IJobStore store, CancellationToken ct) =>
        {
            var claimed = await store.TryClaimAsync(
                request.WorkerId, request.WorkerName, TimeSpan.FromSeconds(request.LeaseSeconds), ct);
            return claimed is null
                ? Results.NoContent()
                : Results.Ok(new ClaimResponse { Job = claimed.Value.Job, LeaseToken = claimed.Value.LeaseToken, Checkpoints = claimed.Value.Item3 });
        });

        app.MapPost("/internal/jobs/{id}/heartbeat", async (
            Guid id, HeartbeatRequest request, IJobStore store, CancellationToken ct) =>
        {
            var outcome = await store.HeartbeatAsync(
                id, request.LeaseToken, TimeSpan.FromSeconds(request.ExtendSeconds), request.UsageDelta, ct);
            return outcome is null ? Results.Conflict(new { code = "stale_lease" }) : Results.Ok(outcome);
        });

        app.MapPost("/internal/jobs/{id}/transition", async (
            Guid id, TransitionRequest request, JobService jobs, CancellationToken ct) =>
        {
            if (!Enum.TryParse<JobStatus>(request.To, ignoreCase: true, out var to))
                return Results.BadRequest(new { error = $"unknown status '{request.To}'" });

            var (ok, job) = await jobs.TransitionWithLeaseAsync(id, request.LeaseToken, to, request.Reason, ct);
            return ok ? Results.Ok(job) : Results.Conflict(new { code = "invalid_transition" });
        });

        app.MapPost("/internal/jobs/{id}/events", async (
            Guid id, AppendEventsRequest request, IJobStore store, JobService jobs, CancellationToken ct) =>
        {
            if (!await store.HasValidLeaseAsync(id, request.LeaseToken, ct))
                return Results.Conflict(new { code = "stale_lease" });

            var persisted = await jobs.AppendEventsAsync(id, request.Events, ct);
            return Results.Ok(new { last_seq = persisted.Count == 0 ? 0 : persisted[^1].Seq });
        });

        app.MapPost("/internal/jobs/{id}/checkpoints", async (
            Guid id, CheckpointRequest request, IJobStore store, CancellationToken ct) =>
        {
            if (!await store.HasValidLeaseAsync(id, request.LeaseToken, ct))
                return Results.Conflict(new { code = "stale_lease" });

            await store.AppendCheckpointAsync(id,
                new Checkpoint { Seq = 0, Label = request.Label, Data = request.Data }, ct);
            return Results.NoContent();
        });

        app.MapGet("/internal/jobs/{id}/checkpoints", async (Guid id, IJobStore store, CancellationToken ct) =>
            Results.Ok(await store.GetCheckpointsAsync(id, ct)));

        app.MapPost("/internal/jobs/{id}/complete", async (
            Guid id, CompleteRequest request, JobService jobs, CancellationToken ct) =>
        {
            var job = await jobs.CompleteAsync(id, request.LeaseToken, request.Result, request.UsageFinal, ct);
            return job is null ? Results.Conflict(new { code = "invalid_transition" }) : Results.Ok(job);
        });

        app.MapPost("/internal/jobs/{id}/fail", async (
            Guid id, FailRequest request, JobService jobs, CancellationToken ct) =>
        {
            var job = await jobs.FailAsync(id, request.LeaseToken, request.Reason, request.UsageFinal, ct);
            return job is null ? Results.Conflict(new { code = "invalid_transition" }) : Results.Ok(job);
        });

        return app;
    }
}
