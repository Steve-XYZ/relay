using Relay.Core;
using Relay.Server.Sse;
using Relay.Server.Stores;
using Relay.Server.Telemetry;

namespace Relay.Server.Services;

/// <summary>
/// Orchestrates durable control-plane changes and their audit trail. Every incident move
/// goes through here so the state event lands in the incident log and reaches live
/// subscribers together with the row change — the same contract <see cref="JobService"/>
/// holds for jobs.
///
/// Methods that move an incident take the status the caller believes it is in and return
/// null when that belief was wrong, so a caller that lost a race simply does nothing.
/// </summary>
public sealed class ProjectService
{
    private readonly IControlPlaneStore _store;
    private readonly SseHub _sse;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        IControlPlaneStore store, SseHub sse, TimeProvider clock, ILogger<ProjectService> logger)
    {
        _store = store;
        _sse = sse;
        _clock = clock;
        _logger = logger;
    }

    // ---- declaration ----

    public Task<Project> UpsertProjectAsync(UpsertProjectRequest request, CancellationToken ct) =>
        _store.UpsertProjectAsync(request, ct);

    public Task<Resource> UpsertResourceAsync(Guid projectId, UpsertResourceRequest request, CancellationToken ct) =>
        _store.UpsertResourceAsync(projectId, request, ct);

    public Task<Policy> CreatePolicyAsync(Guid projectId, CreatePolicyRequest request, CancellationToken ct) =>
        _store.CreatePolicyAsync(projectId, request, ct);

    // ---- observation ingest ----

    /// <summary>
    /// Records evidence about an already-declared resource. Returns null when the resource is
    /// unknown: Relay will not invent resources from a reporter's typo, because a resource
    /// nobody declared is a resource no policy watches and no human owns.
    /// </summary>
    public async Task<(Observation Observation, Resource Resource)?> RecordObservationAsync(
        Project project, ReportObservationRequest request, CancellationToken ct)
    {
        var resource = await _store.FindResourceAsync(
            project.Id, request.Kind, Resource.NormalizeKey(request.Key), ct);
        if (resource is null) return null;

        var observation = await _store.RecordObservationAsync(resource, request, ct);
        RelayMetrics.ObservationsRecorded.Add(1,
            RelayMetrics.Tag("state", observation.State.ToWire()),
            RelayMetrics.Tag("signal", observation.Signal));

        var refreshed = await _store.FindResourceAsync(project.Id, resource.Kind, resource.Key, ct) ?? resource;
        return (observation, refreshed);
    }

    // ---- audit log ----

    public async Task<IReadOnlyList<IncidentEvent>> LogAsync(
        Guid incidentId, IReadOnlyList<IncidentEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return events;
        var persisted = await _store.AppendIncidentEventsAsync(incidentId, events, ct);
        foreach (var e in persisted) _sse.Publish(incidentId, SseFormat.Frame(e));
        return persisted;
    }

    // ---- incident lifecycle ----

    public async Task<Incident?> OpenIncidentAsync(NewIncident spec, Verdict verdict, CancellationToken ct)
    {
        var incident = await _store.OpenIncidentAsync(spec, ct);
        if (incident is null) return null;

        _logger.LogWarning("incident {ShortId} opened: {Policy} on {Resource} — {Summary}",
            incident.ShortId, incident.PolicyName, incident.ResourceLabel, incident.Summary);
        RelayMetrics.IncidentsOpened.Add(1, RelayMetrics.Tag("severity", incident.Severity.ToWire()));

        await LogAsync(incident.Id,
        [
            IncidentEvent.Of(EventKind.Milestone, "incident_opened", new Dictionary<string, string>
            {
                ["policy"] = incident.PolicyName,
                ["resource"] = incident.ResourceLabel,
                ["severity"] = incident.Severity.ToWire(),
            }),
            IncidentEvent.Observed(verdict),
        ], ct);
        return incident;
    }

    public async Task<Incident?> RecordVerdictAsync(Incident incident, Verdict verdict, CancellationToken ct)
    {
        // Only log a fresh observation event when the picture actually changed; the loop runs
        // every few seconds and an unchanged verdict is not news.
        var changed = incident.Detail != verdict.Summary;
        var updated = await _store.RecordVerdictAsync(incident.Id, verdict.Summary, ct);
        if (updated is not null && changed) await LogAsync(incident.Id, [IncidentEvent.Observed(verdict)], ct);
        return updated;
    }

    public async Task<Incident?> AwaitApprovalAsync(
        Incident incident, RemediationAction action, CancellationToken ct)
    {
        var updated = await _store.AwaitApprovalAsync(incident.Id, incident.Status, action.Id, ct);
        if (updated is null) return null;

        await LogAsync(incident.Id,
        [
            IncidentEvent.State(incident.Status, IncidentStatus.AwaitingApproval, "policy requires approval"),
            IncidentEvent.Decision($"proposed {action.Kind.ToWire()} (attempt {action.Attempt}); awaiting approval",
                new Dictionary<string, string> { ["action_id"] = action.Id.ToString() }),
        ], ct);
        return updated;
    }

    public async Task<Incident?> BeginActingAsync(
        Incident incident, RemediationAction action, CancellationToken ct)
    {
        var updated = await _store.BeginActingAsync(incident.Id, incident.Status, action.Id, action.Attempt, ct);
        if (updated is null) return null;

        await LogAsync(incident.Id,
        [
            IncidentEvent.State(incident.Status, IncidentStatus.Acting, $"{action.Kind.ToWire()} authorized"),
            IncidentEvent.Decision(
                $"acting: {action.Kind.ToWire()} attempt {action.Attempt}/{incident.MaxAttempts}, " +
                $"authorized by {action.ApprovedBy ?? "policy"}",
                new Dictionary<string, string>
                {
                    ["action_id"] = action.Id.ToString(),
                    ["approved_by"] = action.ApprovedBy ?? "policy",
                }),
        ], ct);
        return updated;
    }

    public async Task<Incident?> BeginVerifyingAsync(
        Incident incident, RemediationAction action, DateTimeOffset evidenceAfter, DateTimeOffset deadline,
        CancellationToken ct)
    {
        var updated = await _store.BeginVerifyingAsync(incident.Id, action.Id, evidenceAfter, deadline, ct);
        if (updated is null) return null;

        await LogAsync(incident.Id,
        [
            IncidentEvent.State(incident.Status, IncidentStatus.Verifying, "action finished"),
            IncidentEvent.Verification(
                $"awaiting evidence received after {evidenceAfter:O} (deadline {deadline:O})",
                new Dictionary<string, string> { ["action_id"] = action.Id.ToString() }),
        ], ct);
        return updated;
    }

    public async Task<Incident?> ReopenIncidentAsync(Incident incident, string detail, CancellationToken ct)
    {
        var updated = await _store.ReopenIncidentAsync(incident.Id, incident.Status, detail, ct);
        if (updated is null) return null;

        await LogAsync(incident.Id,
            [IncidentEvent.State(incident.Status, IncidentStatus.Open, detail)], ct);
        return updated;
    }

    public async Task<Incident?> ResolveIncidentAsync(
        Incident incident, string resolution, string detail, CancellationToken ct)
    {
        var updated = await _store.ResolveIncidentAsync(incident.Id, incident.Status, resolution, detail, ct);
        if (updated is null) return null;

        _logger.LogInformation("incident {ShortId} resolved ({Resolution}): {Detail}",
            incident.ShortId, resolution, detail);
        RelayMetrics.IncidentsResolved.Add(1, RelayMetrics.Tag("resolution", resolution));

        await LogAsync(incident.Id,
        [
            IncidentEvent.State(incident.Status, IncidentStatus.Resolved, resolution),
            IncidentEvent.Of(EventKind.Milestone, "incident_resolved",
                new Dictionary<string, string> { ["resolution"] = resolution, ["detail"] = detail }),
        ], ct);
        return updated;
    }

    public async Task<Incident?> EscalateIncidentAsync(Incident incident, string reason, CancellationToken ct)
    {
        var updated = await _store.EscalateIncidentAsync(incident.Id, incident.Status, reason, ct);
        if (updated is null) return null;

        _logger.LogWarning("incident {ShortId} escalated: {Reason}", incident.ShortId, reason);
        RelayMetrics.IncidentsEscalated.Add(1, RelayMetrics.Tag("severity", incident.Severity.ToWire()));

        await LogAsync(incident.Id,
        [
            IncidentEvent.State(incident.Status, IncidentStatus.Escalated, reason),
            IncidentEvent.Of(EventKind.Warning, $"needs human intervention: {reason}"),
        ], ct);
        return updated;
    }

    // ---- human decisions on proposed actions ----

    public async Task<RemediationAction?> ApproveActionAsync(Guid actionId, string approvedBy, CancellationToken ct)
    {
        var action = await _store.ApproveActionAsync(actionId, approvedBy, ct);
        if (action is null) return null;

        await LogAsync(action.IncidentId,
            [IncidentEvent.Decision($"{action.Kind.ToWire()} approved by {approvedBy}",
                new Dictionary<string, string> { ["action_id"] = action.Id.ToString() })], ct);
        return action;
    }

    public async Task<RemediationAction?> RejectActionAsync(
        Guid actionId, string rejectedBy, string? reason, CancellationToken ct)
    {
        var action = await _store.RejectActionAsync(actionId, rejectedBy, reason, ct);
        if (action is null) return null;

        await LogAsync(action.IncidentId,
            [IncidentEvent.Decision($"{action.Kind.ToWire()} rejected by {rejectedBy}"
                + (reason is null ? "" : $": {reason}"))], ct);
        return action;
    }

    // ---- read models ----

    public async Task<ProjectState> GetStateAsync(Project project, CancellationToken ct) => new()
    {
        Project = project,
        Resources = await _store.ListResourcesAsync(project.Id, ct),
        Policies = await _store.ListPoliciesAsync(project.Id, enabledOnly: false, ct),
        ActiveIncidents = await _store.ListIncidentsAsync(project.Id, activeOnly: true, limit: 200, ct),
        PendingApprovals = await _store.ListPendingApprovalsAsync(project.Id, ct),
        EvaluatedAt = _clock.GetUtcNow(),
    };

    public async Task<IncidentDetail> GetIncidentDetailAsync(Incident incident, CancellationToken ct) => new()
    {
        Incident = incident,
        Actions = await _store.ListActionsAsync(incident.Id, ct),
        Events = await _store.GetIncidentEventsAsync(incident.Id, afterSeq: 0, limit: 10_000, ct),
    };
}
