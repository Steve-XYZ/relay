using Relay.Core;
using Relay.Server.Services;
using Relay.Server.Sse;
using Relay.Server.Stores;

namespace Relay.Server.Api;

/// <summary>Public REST + SSE surface for jobs, consumed by the CLI and web UI.</summary>
public static class PublicApi
{
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs", async (CreateJobRequest request, JobService jobs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.RepoUrl))
                return Results.BadRequest(new { error = "repo_url is required" });
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "prompt is required" });

            var (job, _) = await jobs.CreateAsync(request, ct);
            return Results.Accepted($"/api/jobs/{job.ShortId}", job);
        });

        app.MapGet("/api/jobs", async (
            string? status, int? limit, IJobStore store, CancellationToken ct) =>
        {
            JobStatus? parsed = null;
            if (status is not null) parsed = Wire.From(status);
            var jobs = await store.ListAsync(parsed, Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(jobs);
        });

        app.MapGet("/api/jobs/{idOrShortId}", async (string idOrShortId, IJobStore store, CancellationToken ct) =>
        {
            var job = await store.ResolveAsync(idOrShortId, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        app.MapPost("/api/jobs/{idOrShortId}/cancel", async (string idOrShortId, IJobStore store, JobService jobs, CancellationToken ct) =>
        {
            var job = await store.ResolveAsync(idOrShortId, ct);
            if (job is null) return Results.NotFound();

            var result = await store.RequestCancelAsync(job.Id, ct);
            switch (result)
            {
                case CancelResult.CancelledDirectly:
                    // The from-state is whatever the job was actually in (queued, interrupted
                    // or recovering). Hardcoding one would make the durable log disagree with
                    // the transitions it is supposed to be a record of.
                    await jobs.AppendEventsAsync(job.Id,
                        [JobEvent.State(Wire.From(job.Status), JobStatus.Cancelled, "cancelled by user"),
                         JobEvent.Milestone("job_cancelled")], ct);
                    return Results.Ok(await store.ResolveAsync(idOrShortId, ct));
                case CancelResult.MarkRequested:
                    await jobs.AppendEventsAsync(job.Id,
                        [new JobEvent { Seq = 0, Kind = EventKind.Warning.ToWire(), Message = "cancellation requested; worker will abort at the next checkpoint" }], ct);
                    return Results.Accepted($"/api/jobs/{job.ShortId}", await store.ResolveAsync(idOrShortId, ct));
                case CancelResult.AlreadyTerminal:
                    return Results.Conflict(new { error = $"job already {job.Status}" });
                default:
                    return Results.NotFound();
            }
        });

        // Live event stream. Replays history after ?afterSeq / Last-Event-ID, then follows live.
        app.MapGet("/api/jobs/{idOrShortId}/events", async (
            string idOrShortId, long? afterSeq, HttpContext http, IJobStore store, SseHub sse, CancellationToken ct) =>
        {
            var job = await store.ResolveAsync(idOrShortId, ct);
            if (job is null) return Results.NotFound();

            var cursor = afterSeq ?? SseStream.ParseLastEventId(http.Request) ?? 0;
            var history = await store.GetEventsAsync(job.Id, cursor, 10_000, ct);

            await SseStream.RunAsync(http, sse, job.Id, history.Select(SseFormat.Frame).ToList(), ct);
            return Results.Empty;
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        return app;
    }
}
