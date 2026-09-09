using Relay.Core;
using Relay.Server.Services;
using Relay.Server.Sse;
using Relay.Server.Stores;

namespace Relay.Server.Api;

/// <summary>
/// Control-plane REST surface: declare what exists, state what should be true, push what is
/// actually happening, and read back what requires intervention.
/// </summary>
public static class ProjectsApi
{
    public static IEndpointRouteBuilder MapProjectsApi(this IEndpointRouteBuilder app)
    {
        // ---- projects ----

        app.MapPost("/api/projects", async (
            UpsertProjectRequest request, ProjectService projects, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Slug))
                return Results.BadRequest(new { error = "slug is required" });

            var project = await projects.UpsertProjectAsync(request, ct);
            return Results.Ok(project);
        });

        app.MapGet("/api/projects", async (IControlPlaneStore store, CancellationToken ct) =>
            Results.Ok(await store.ListProjectsAsync(ct)));

        // The one request that answers the product's claim: what should be happening, what is
        // actually happening, what needs intervention, and what is waiting on a human.
        app.MapGet("/api/projects/{project}/state", async (
            string project, IControlPlaneStore store, ProjectService projects, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            return found is null
                ? Results.NotFound()
                : Results.Ok(await projects.GetStateAsync(found, ct));
        });

        // ---- resources ----

        app.MapPost("/api/projects/{project}/resources", async (
            string project, UpsertResourceRequest request, IControlPlaneStore store,
            ProjectService projects, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            if (found is null) return Results.NotFound(new { error = $"unknown project '{project}'" });

            var resource = await projects.UpsertResourceAsync(found.Id, request, ct);
            return Results.Ok(resource);
        });

        app.MapGet("/api/projects/{project}/resources", async (
            string project, IControlPlaneStore store, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            return found is null
                ? Results.NotFound()
                : Results.Ok(await store.ListResourcesAsync(found.Id, ct));
        });

        // ---- observations ----

        app.MapPost("/api/projects/{project}/observations", async (
            string project, ReportObservationRequest request, IControlPlaneStore store,
            ProjectService projects, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            if (found is null) return Results.NotFound(new { error = $"unknown project '{project}'" });

            var recorded = await projects.RecordObservationAsync(found, request, ct);
            return recorded is null
                ? Results.NotFound(new
                {
                    error = $"unknown resource {request.Kind.ToWire()}/{request.Key} in project '{found.Slug}'",
                    hint = "declare the resource first: POST /api/projects/{project}/resources",
                })
                : Results.Ok(new { observation = recorded.Value.Observation, resource = recorded.Value.Resource });
        });

        app.MapGet("/api/resources/{resourceId:guid}/observations", async (
            Guid resourceId, int? limit, IControlPlaneStore store, CancellationToken ct) =>
            Results.Ok(await store.ListObservationsAsync(resourceId, Math.Clamp(limit ?? 50, 1, 500), ct)));

        // ---- policies ----

        app.MapPost("/api/projects/{project}/policies", async (
            string project, CreatePolicyRequest request, IControlPlaneStore store,
            ProjectService projects, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            if (found is null) return Results.NotFound(new { error = $"unknown project '{project}'" });
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required" });

            var policy = await projects.CreatePolicyAsync(found.Id, request, ct);
            return Results.Ok(policy);
        });

        app.MapGet("/api/projects/{project}/policies", async (
            string project, IControlPlaneStore store, CancellationToken ct) =>
        {
            var found = await store.ResolveProjectAsync(project, ct);
            return found is null
                ? Results.NotFound()
                : Results.Ok(await store.ListPoliciesAsync(found.Id, enabledOnly: false, ct));
        });

        app.MapPost("/api/policies/{policyId:guid}/enabled", async (
            Guid policyId, bool value, IControlPlaneStore store, CancellationToken ct) =>
        {
            var policy = await store.SetPolicyEnabledAsync(policyId, value, ct);
            return policy is null ? Results.NotFound() : Results.Ok(policy);
        });

        // ---- incidents ----

        app.MapGet("/api/incidents", async (
            string? project, bool? active, int? limit, IControlPlaneStore store, CancellationToken ct) =>
        {
            Guid? projectId = null;
            if (project is not null)
            {
                var found = await store.ResolveProjectAsync(project, ct);
                if (found is null) return Results.NotFound(new { error = $"unknown project '{project}'" });
                projectId = found.Id;
            }
            return Results.Ok(await store.ListIncidentsAsync(
                projectId, active ?? true, Math.Clamp(limit ?? 50, 1, 200), ct));
        });

        app.MapGet("/api/incidents/{idOrShortId}", async (
            string idOrShortId, IControlPlaneStore store, ProjectService projects, CancellationToken ct) =>
        {
            var incident = await store.ResolveIncidentRefAsync(idOrShortId, ct);
            return incident is null
                ? Results.NotFound()
                : Results.Ok(await projects.GetIncidentDetailAsync(incident, ct));
        });

        // Live incident timeline: replays the audit log, then follows.
        app.MapGet("/api/incidents/{idOrShortId}/events", async (
            string idOrShortId, long? afterSeq, HttpContext http,
            IControlPlaneStore store, SseHub sse, CancellationToken ct) =>
        {
            var incident = await store.ResolveIncidentRefAsync(idOrShortId, ct);
            if (incident is null) return Results.NotFound();

            var cursor = afterSeq ?? SseStream.ParseLastEventId(http.Request) ?? 0;
            var history = await store.GetIncidentEventsAsync(incident.Id, cursor, 10_000, ct);

            await SseStream.RunAsync(http, sse, incident.Id,
                history.Select(SseFormat.Frame).ToList(), ct);
            return Results.Empty;
        });

        // ---- human decisions ----

        app.MapPost("/api/actions/{actionId:guid}/approve", async (
            Guid actionId, ApproveActionRequest request, ProjectService projects, CancellationToken ct) =>
        {
            var action = await projects.ApproveActionAsync(actionId, request.ApprovedBy, ct);
            return action is null
                ? Results.Conflict(new { code = "not_awaiting_approval" })
                : Results.Ok(action);
        });

        app.MapPost("/api/actions/{actionId:guid}/reject", async (
            Guid actionId, RejectActionRequest request, ProjectService projects, CancellationToken ct) =>
        {
            var action = await projects.RejectActionAsync(actionId, request.RejectedBy, request.Reason, ct);
            return action is null
                ? Results.Conflict(new { code = "not_awaiting_approval" })
                : Results.Ok(action);
        });

        return app;
    }
}
