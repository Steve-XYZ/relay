using Relay.Core;

namespace Relay.Server.Stores;

/// <summary>
/// Non-durable control-plane store for tests and local development without Postgres.
/// Holds exactly the same invariants as <see cref="PostgresControlPlaneStore"/>: one active
/// incident per (policy, resource), compare-and-swap transitions validated against the
/// state machines, immutable observations, and clamped receive times. A single lock stands
/// in for the row locks Postgres uses.
/// </summary>
public sealed class InMemoryControlPlaneStore : IControlPlaneStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    private readonly Dictionary<Guid, Project> _projects = [];
    private readonly Dictionary<Guid, Resource> _resources = [];
    private readonly Dictionary<Guid, Policy> _policies = [];
    private readonly Dictionary<Guid, Incident> _incidents = [];
    private readonly Dictionary<Guid, RemediationAction> _actions = [];
    private readonly List<Observation> _observations = [];
    private readonly Dictionary<Guid, List<IncidentEvent>> _incidentEvents = [];

    private long _nextObservationId = 1;

    public InMemoryControlPlaneStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    // ---- projects ----

    public Task<Project> UpsertProjectAsync(UpsertProjectRequest request, CancellationToken ct)
    {
        var slug = Project.NormalizeSlug(request.Slug);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var existing = _projects.Values.FirstOrDefault(p => p.Slug == slug);
            var project = existing is null
                ? new Project { Id = Guid.NewGuid(), Slug = slug, Name = request.Name ?? slug, CreatedAt = now, UpdatedAt = now }
                : existing with { Name = request.Name ?? existing.Name, UpdatedAt = now };
            _projects[project.Id] = project;
            return Task.FromResult(project);
        }
    }

    public Task<Project?> ResolveProjectAsync(string idOrSlug, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Guid.TryParse(idOrSlug, out var id) && _projects.TryGetValue(id, out var byId))
                return Task.FromResult<Project?>(byId);
            return Task.FromResult(_projects.Values.FirstOrDefault(p =>
                p.Slug.Equals(idOrSlug, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Project>>(_projects.Values.OrderBy(p => p.Slug).ToList());
    }

    // ---- resources ----

    public Task<Resource> UpsertResourceAsync(Guid projectId, UpsertResourceRequest request, CancellationToken ct)
    {
        var key = Resource.NormalizeKey(request.Key);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var existing = _resources.Values.FirstOrDefault(r =>
                r.ProjectId == projectId && r.Kind == request.Kind && r.Key == key);

            Resource resource;
            if (existing is null)
            {
                resource = new Resource
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Kind = request.Kind,
                    Key = key,
                    Name = request.Name ?? key,
                    Attributes = request.Attributes is null ? [] : new Dictionary<string, string>(request.Attributes),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
            }
            else
            {
                var attributes = new Dictionary<string, string>(existing.Attributes);
                if (request.Attributes is not null)
                    foreach (var (k, v) in request.Attributes) attributes[k] = v;
                resource = existing with
                {
                    Name = request.Name ?? existing.Name,
                    Attributes = attributes,
                    UpdatedAt = now,
                };
            }
            _resources[resource.Id] = resource;
            return Task.FromResult(resource);
        }
    }

    public Task<Resource?> FindResourceAsync(Guid projectId, ResourceKind kind, string key, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_resources.Values.FirstOrDefault(r =>
                r.ProjectId == projectId && r.Kind == kind && r.Key == key));
    }

    public Task<Resource?> GetResourceAsync(Guid resourceId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_resources.GetValueOrDefault(resourceId));
    }

    public Task<IReadOnlyList<Resource>> ListResourcesAsync(Guid projectId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Resource>>(_resources.Values
                .Where(r => r.ProjectId == projectId)
                .OrderBy(r => r.Kind).ThenBy(r => r.Key)
                .ToList());
    }

    // ---- observations ----

    public Task<Observation> RecordObservationAsync(Resource resource, ReportObservationRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            var receivedAt = _clock.GetUtcNow();
            // An observation may be late but never from the future: verification trusts this.
            var observedAt = request.ObservedAt is { } reported && reported < receivedAt ? reported : receivedAt;

            var observation = new Observation
            {
                Id = _nextObservationId++,
                ProjectId = resource.ProjectId,
                ResourceId = resource.Id,
                Signal = string.IsNullOrWhiteSpace(request.Signal) ? Observation.DefaultSignal : request.Signal,
                State = request.State,
                Facts = request.Facts is null ? [] : new Dictionary<string, string>(request.Facts),
                Source = string.IsNullOrWhiteSpace(request.Source) ? "unknown" : request.Source,
                Message = request.Message,
                ObservedAt = observedAt,
                ReceivedAt = receivedAt,
            };
            _observations.Add(observation);

            var (health, reason) = DeriveHealthLocked(resource.Id);
            _resources[resource.Id] = _resources[resource.Id] with
            {
                Health = health,
                HealthReason = reason,
                LastObservedAt = observedAt,
                UpdatedAt = receivedAt,
            };
            return Task.FromResult(observation);
        }
    }

    /// <summary>Derived health is the worst state across the latest observation of every signal.</summary>
    private (ResourceHealth Health, string? Reason) DeriveHealthLocked(Guid resourceId)
    {
        var latest = LatestPerSignalLocked(resourceId);
        if (latest.Count == 0) return (ResourceHealth.Unknown, null);

        var worst = latest.MaxBy(o => (int)o.ToHealth())!;
        return (worst.ToHealth(), $"{worst.Signal}: {worst.State.ToWire()} per {worst.Source}");
    }

    private List<Observation> LatestPerSignalLocked(Guid resourceId) =>
        _observations
            .Where(o => o.ResourceId == resourceId)
            .GroupBy(o => o.Signal, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(o => o.ReceivedAt).ThenByDescending(o => o.Id).First())
            .ToList();

    public Task<ObservationSnapshot> SnapshotAsync(Guid resourceId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(new ObservationSnapshot(LatestPerSignalLocked(resourceId)));
    }

    public Task<IReadOnlyList<Observation>> ListObservationsAsync(Guid resourceId, int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Observation>>(_observations
                .Where(o => o.ResourceId == resourceId)
                .OrderByDescending(o => o.Id)
                .Take(limit)
                .ToList());
    }

    // ---- policies ----

    public Task<Policy> CreatePolicyAsync(Guid projectId, CreatePolicyRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var existing = _policies.Values.FirstOrDefault(p =>
                p.ProjectId == projectId && p.Name == request.Name);

            if (existing is not null)
            {
                // Idempotent re-apply: same name in the same project updates in place.
                var updated = existing with
                {
                    Target = request.Target,
                    Expectation = request.Expectation.Validated(),
                    Severity = request.Severity,
                    Remediation = request.Remediation.Validated(),
                    Enabled = request.Enabled,
                    UpdatedAt = now,
                };
                _policies[updated.Id] = updated;
                return Task.FromResult(updated);
            }

            var policy = new Policy
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = request.Name,
                Target = request.Target,
                Expectation = request.Expectation.Validated(),
                Severity = request.Severity,
                Remediation = request.Remediation.Validated(),
                Enabled = request.Enabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _policies[policy.Id] = policy;
            return Task.FromResult(policy);
        }
    }

    public Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_policies.GetValueOrDefault(policyId));
    }

    public Task<IReadOnlyList<Policy>> ListPoliciesAsync(Guid projectId, bool enabledOnly, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Policy>>(_policies.Values
                .Where(p => p.ProjectId == projectId && (!enabledOnly || p.Enabled))
                .OrderBy(p => p.Name)
                .ToList());
    }

    public Task<Policy?> SetPolicyEnabledAsync(Guid policyId, bool enabled, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_policies.TryGetValue(policyId, out var policy)) return Task.FromResult<Policy?>(null);
            policy = policy with { Enabled = enabled, UpdatedAt = _clock.GetUtcNow() };
            _policies[policyId] = policy;
            return Task.FromResult<Policy?>(policy);
        }
    }

    // ---- incidents ----

    public Task<Incident?> OpenIncidentAsync(NewIncident spec, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ActiveIncidentLocked(spec.Policy.Id, spec.Resource.Id) is not null)
                return Task.FromResult<Incident?>(null);

            var now = _clock.GetUtcNow();
            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                ShortId = UniqueIncidentShortIdLocked(),
                ProjectId = spec.ProjectId,
                PolicyId = spec.Policy.Id,
                PolicyName = spec.Policy.Name,
                ResourceId = spec.Resource.Id,
                ResourceLabel = spec.Resource.Describe(),
                Status = IncidentStatus.Open,
                Severity = spec.Policy.Severity,
                Summary = spec.Summary,
                Detail = spec.Summary,
                Attempt = 0,
                MaxAttempts = spec.Policy.Remediation.MaxAttempts,
                OpenedAt = now,
                UpdatedAt = now,
            };
            _incidents[incident.Id] = incident;
            _incidentEvents[incident.Id] = [];
            return Task.FromResult<Incident?>(incident);
        }
    }

    private string UniqueIncidentShortIdLocked()
    {
        for (var i = 0; i < 64; i++)
        {
            var candidate = "I" + ShortId.Generate(4);
            if (_incidents.Values.All(x => x.ShortId != candidate)) return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique incident short id");
    }

    private Incident? ActiveIncidentLocked(Guid policyId, Guid resourceId) =>
        _incidents.Values.FirstOrDefault(i =>
            i.PolicyId == policyId && i.ResourceId == resourceId && i.IsActive);

    public Task<Incident?> FindActiveIncidentAsync(Guid policyId, Guid resourceId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(ActiveIncidentLocked(policyId, resourceId));
    }

    public Task<Incident?> ResolveIncidentRefAsync(string idOrShortId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Guid.TryParse(idOrShortId, out var id) && _incidents.TryGetValue(id, out var byId))
                return Task.FromResult<Incident?>(byId);
            return Task.FromResult(_incidents.Values.FirstOrDefault(i =>
                i.ShortId.Equals(idOrShortId, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Task<IReadOnlyList<Incident>> ListIncidentsAsync(Guid? projectId, bool activeOnly, int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Incident>>(_incidents.Values
                .Where(i => projectId is null || i.ProjectId == projectId)
                .Where(i => !activeOnly || i.IsActive)
                .OrderByDescending(i => i.OpenedAt)
                .Take(limit)
                .ToList());
    }

    public Task<IReadOnlyList<Incident>> ListIncidentsToAdvanceAsync(int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<Incident>>(_incidents.Values
                .Where(i => i.IsActive)
                .OrderBy(i => i.UpdatedAt).ThenBy(i => i.OpenedAt)
                .Take(limit)
                .ToList());
    }

    public Task<Incident?> RecordVerdictAsync(Guid incidentId, string detail, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_incidents.TryGetValue(incidentId, out var incident)) return Task.FromResult<Incident?>(null);
            incident = incident with { Detail = detail, UpdatedAt = _clock.GetUtcNow() };
            _incidents[incidentId] = incident;
            return Task.FromResult<Incident?>(incident);
        }
    }

    /// <summary>Compare-and-swap transition: fails when the incident is no longer in <paramref name="from"/>.</summary>
    private Incident? TransitionLocked(
        Guid incidentId, IncidentStatus from, IncidentStatus to, Func<Incident, Incident> patch)
    {
        if (!_incidents.TryGetValue(incidentId, out var incident)) return null;
        if (incident.Status != from) return null;
        if (!IncidentStateMachine.CanTransition(from, to)) return null;

        var updated = patch(incident with { Status = to, UpdatedAt = _clock.GetUtcNow() });
        _incidents[incidentId] = updated;
        return updated;
    }

    public Task<Incident?> AwaitApprovalAsync(Guid incidentId, IncidentStatus from, Guid actionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, from, IncidentStatus.AwaitingApproval,
                i => i with { CurrentActionId = actionId }));
    }

    public Task<Incident?> BeginActingAsync(Guid incidentId, IncidentStatus from, Guid actionId, int attempt, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, from, IncidentStatus.Acting,
                i => i with
                {
                    CurrentActionId = actionId,
                    Attempt = attempt,
                    LastAttemptAt = _clock.GetUtcNow(),
                    VerifyDeadlineAt = null,
                    VerifyEvidenceAfter = null,
                }));
    }

    public Task<Incident?> BeginVerifyingAsync(
        Guid incidentId, Guid actionId, DateTimeOffset evidenceAfter, DateTimeOffset deadline, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, IncidentStatus.Acting, IncidentStatus.Verifying,
                i => i with
                {
                    CurrentActionId = actionId,
                    VerifyEvidenceAfter = evidenceAfter,
                    VerifyDeadlineAt = deadline,
                }));
    }

    public Task<Incident?> ReopenIncidentAsync(Guid incidentId, IncidentStatus from, string detail, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, from, IncidentStatus.Open,
                i => i with
                {
                    Detail = detail,
                    CurrentActionId = null,
                    VerifyDeadlineAt = null,
                    VerifyEvidenceAfter = null,
                }));
    }

    public Task<Incident?> ResolveIncidentAsync(
        Guid incidentId, IncidentStatus from, string resolution, string detail, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, from, IncidentStatus.Resolved,
                i => i with
                {
                    Detail = detail,
                    Resolution = resolution,
                    ResolvedAt = _clock.GetUtcNow(),
                    VerifyDeadlineAt = null,
                    VerifyEvidenceAfter = null,
                    EscalationReason = null,
                }));
    }

    public Task<Incident?> EscalateIncidentAsync(Guid incidentId, IncidentStatus from, string reason, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionLocked(incidentId, from, IncidentStatus.Escalated,
                i => i with
                {
                    EscalationReason = reason,
                    VerifyDeadlineAt = null,
                    VerifyEvidenceAfter = null,
                }));
    }

    // ---- incident audit log ----

    public Task<IReadOnlyList<IncidentEvent>> AppendIncidentEventsAsync(
        Guid incidentId, IReadOnlyList<IncidentEvent> events, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_incidentEvents.TryGetValue(incidentId, out var log))
                return Task.FromResult<IReadOnlyList<IncidentEvent>>([]);

            var now = _clock.GetUtcNow();
            var persisted = new List<IncidentEvent>(events.Count);
            foreach (var e in events)
            {
                var stored = e with { Seq = log.Count + 1, CreatedAt = now };
                log.Add(stored);
                persisted.Add(stored);
            }
            return Task.FromResult<IReadOnlyList<IncidentEvent>>(persisted);
        }
    }

    public Task<IReadOnlyList<IncidentEvent>> GetIncidentEventsAsync(
        Guid incidentId, long afterSeq, int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<IncidentEvent>>(
                (_incidentEvents.GetValueOrDefault(incidentId) ?? [])
                .Where(e => e.Seq > afterSeq)
                .OrderBy(e => e.Seq)
                .Take(limit)
                .ToList());
    }

    // ---- actions ----

    public Task<RemediationAction> CreateActionAsync(NewAction spec, CancellationToken ct)
    {
        lock (_gate)
        {
            var action = new RemediationAction
            {
                Id = Guid.NewGuid(),
                ProjectId = spec.ProjectId,
                IncidentId = spec.IncidentId,
                Kind = spec.Kind,
                Status = ActionStatus.Proposed,
                Attempt = spec.Attempt,
                Params = new Dictionary<string, string>(spec.Params),
                Reason = spec.Reason,
                RequiresApproval = spec.RequiresApproval,
                CreatedAt = _clock.GetUtcNow(),
            };
            _actions[action.Id] = action;
            return Task.FromResult(action);
        }
    }

    public Task<RemediationAction?> GetActionAsync(Guid actionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_actions.GetValueOrDefault(actionId));
    }

    public Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid incidentId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RemediationAction>>(_actions.Values
                .Where(a => a.IncidentId == incidentId)
                .OrderBy(a => a.Attempt).ThenBy(a => a.CreatedAt)
                .ToList());
    }

    public Task<IReadOnlyList<RemediationAction>> ListPendingApprovalsAsync(Guid projectId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RemediationAction>>(_actions.Values
                .Where(a => a.ProjectId == projectId && a.Status == ActionStatus.Proposed && a.RequiresApproval)
                .OrderBy(a => a.CreatedAt)
                .ToList());
    }

    private RemediationAction? TransitionActionLocked(
        Guid actionId, ActionStatus from, ActionStatus to, Func<RemediationAction, RemediationAction> patch)
    {
        if (!_actions.TryGetValue(actionId, out var action)) return null;
        if (action.Status != from) return null;
        if (!ActionStateMachine.CanTransition(from, to)) return null;

        var updated = patch(action with { Status = to });
        _actions[actionId] = updated;
        return updated;
    }

    public Task<RemediationAction?> ApproveActionAsync(Guid actionId, string approvedBy, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionActionLocked(actionId, ActionStatus.Proposed, ActionStatus.Approved,
                a => a with { ApprovedBy = approvedBy }));
    }

    public Task<RemediationAction?> RejectActionAsync(Guid actionId, string rejectedBy, string? reason, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionActionLocked(actionId, ActionStatus.Proposed, ActionStatus.Rejected,
                a => a with
                {
                    FailureReason = reason ?? $"rejected by {rejectedBy}",
                    FinishedAt = _clock.GetUtcNow(),
                }));
    }

    public Task<RemediationAction?> SupersedeActionAsync(Guid actionId, string reason, CancellationToken ct)
    {
        lock (_gate)
        {
            var patch = (RemediationAction a) => a with { FailureReason = reason, FinishedAt = _clock.GetUtcNow() };
            return Task.FromResult(
                TransitionActionLocked(actionId, ActionStatus.Proposed, ActionStatus.Rejected, patch)
                ?? TransitionActionLocked(actionId, ActionStatus.Approved, ActionStatus.Rejected, patch));
        }
    }

    public Task<RemediationAction?> BeginExecutionAsync(Guid actionId, Guid? executionJobId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionActionLocked(actionId, ActionStatus.Approved, ActionStatus.Executing,
                a => a with { ExecutionJobId = executionJobId, StartedAt = _clock.GetUtcNow() }));
    }

    public Task<RemediationAction?> SettleActionAsync(
        Guid actionId, bool succeeded, string? failureReason, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(TransitionActionLocked(actionId, ActionStatus.Executing,
                succeeded ? ActionStatus.Succeeded : ActionStatus.Failed,
                a => a with { FailureReason = failureReason, FinishedAt = _clock.GetUtcNow() }));
    }

    public Task<RemediationAction?> RecordOutcomeAsync(
        Guid actionId, ActionOutcome outcome, string detail, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_actions.TryGetValue(actionId, out var action)) return Task.FromResult<RemediationAction?>(null);
            action = action with { Outcome = outcome, OutcomeDetail = detail };
            _actions[actionId] = action;
            return Task.FromResult<RemediationAction?>(action);
        }
    }
}
