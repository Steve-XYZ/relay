using Relay.Core;
using Relay.Server.Services;
using Relay.Server.Sse;
using Relay.Server.Stores;

namespace Relay.Server.Api;

/// <summary>Public REST + SSE surface consumed by the CLI and web UI.</summary>
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
                    var events = await jobs.AppendEventsAsync(job.Id,
                        [JobEvent.State(JobStatus.Queued, JobStatus.Cancelled, "cancelled by user"), JobEvent.Milestone("job_cancelled")], ct);
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

            long cursor = afterSeq ?? ParseLastEventId(http.Request) ?? 0;

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var history = await store.GetEventsAsync(job.Id, cursor, 10_000, ct);
            foreach (var e in history)
            {
                await http.Response.WriteAsync(SseFormat.Frame(e), ct);
                cursor = e.Seq;
            }
            await http.Response.Body.FlushAsync(ct);

            using var subscription = sse.Subscribe(job.Id, out var reader);
            var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                while (true)
                {
                    var readTask = reader.WaitToReadAsync(heartbeatCts.Token).AsTask();
                    var idleTask = Task.Delay(TimeSpan.FromSeconds(15), heartbeatCts.Token);

                    if (await Task.WhenAny(readTask, idleTask) == readTask)
                    {
                        if (!await readTask) break;
                        while (reader.TryRead(out var frame))
                            await http.Response.WriteAsync(frame, ct);
                    }
                    else
                    {
                        // Comment line keeps proxies from closing an idle stream.
                        await http.Response.WriteAsync(": ping\n\n", ct);
                    }
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || !http.RequestAborted.IsCancellationRequested)
            {
                // client disconnected or server shutting down
            }
            finally
            {
                heartbeatCts.Dispose();
            }
            return Results.Empty;
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        return app;
    }

    private static long? ParseLastEventId(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Last-Event-ID", out var values)) return null;
        return long.TryParse(values.LastOrDefault(), out var id) ? id : null;
    }
}
