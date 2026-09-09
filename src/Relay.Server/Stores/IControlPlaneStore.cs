using Relay.Core;

namespace Relay.Server.Stores;

/// <summary>Everything needed to open an incident, captured at the moment of divergence.</summary>
public sealed record NewIncident
{
    public required Guid ProjectId { get; init; }
    public required Policy Policy { get; init; }
    public required Resource Resource { get; init; }
    public required string Summary { get; init; }
}

/// <summary>Everything needed to propose one intervention.</summary>
public sealed record NewAction
{
    public required Guid ProjectId { get; init; }
    public required Guid IncidentId { get; init; }
    public required ActionKind Kind { get; init; }
    public required int Attempt { get; init; }
    public required string Reason { get; init; }
    public required bool RequiresApproval { get; init; }
    public Dictionary<string, string> Params { get; init; } = [];
}

/// <summary>
/// Single writer over durable control-plane state: projects, resources, observations,
/// policies, incidents and actions.
///
/// Every state-changing method takes the status the caller believes the record is in and
/// returns null when that belief was wrong. That compare-and-swap discipline — the same one
/// the job store uses for leases — is what lets several server replicas run the reliability
/// loop concurrently without double-acting on an incident.
/// </summary>
public interface IControlPlaneStore
{
    // ---- projects ----

    Task<Project> UpsertProjectAsync(UpsertProjectRequest request, CancellationToken ct);
    Task<Project?> ResolveProjectAsync(string idOrSlug, CancellationToken ct);
    Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken ct);

    // ---- resources ----

    Task<Resource> UpsertResourceAsync(Guid projectId, UpsertResourceRequest request, CancellationToken ct);
    Task<Resource?> FindResourceAsync(Guid projectId, ResourceKind kind, string key, CancellationToken ct);
    Task<Resource?> GetResourceAsync(Guid resourceId, CancellationToken ct);
    Task<IReadOnlyList<Resource>> ListResourcesAsync(Guid projectId, CancellationToken ct);

    // ---- observations ----

    /// <summary>
    /// Appends an immutable observation and recomputes the resource's derived health.
    /// The store stamps <see cref="Observation.ReceivedAt"/> from its own clock and clamps
    /// <see cref="Observation.ObservedAt"/> to it.
    /// </summary>
    Task<Observation> RecordObservationAsync(Resource resource, ReportObservationRequest request, CancellationToken ct);

    /// <summary>Latest observation per signal: the only input the evaluator is given.</summary>
    Task<ObservationSnapshot> SnapshotAsync(Guid resourceId, CancellationToken ct);

    Task<IReadOnlyList<Observation>> ListObservationsAsync(Guid resourceId, int limit, CancellationToken ct);

    // ---- policies ----

    Task<Policy> CreatePolicyAsync(Guid projectId, CreatePolicyRequest request, CancellationToken ct);
    Task<Policy?> GetPolicyAsync(Guid policyId, CancellationToken ct);
    Task<IReadOnlyList<Policy>> ListPoliciesAsync(Guid projectId, bool enabledOnly, CancellationToken ct);
    Task<Policy?> SetPolicyEnabledAsync(Guid policyId, bool enabled, CancellationToken ct);

    // ---- incidents ----

    /// <summary>
    /// Opens an incident, or returns null when one is already active for this
    /// (policy, resource). That uniqueness is enforced durably, not by a prior read, so a
    /// flapping resource produces one incident rather than a storm.
    /// </summary>
    Task<Incident?> OpenIncidentAsync(NewIncident spec, CancellationToken ct);

    Task<Incident?> FindActiveIncidentAsync(Guid policyId, Guid resourceId, CancellationToken ct);
    Task<Incident?> ResolveIncidentRefAsync(string idOrShortId, CancellationToken ct);
    /// <summary>Newest first: the ordering a human wants when reading a list.</summary>
    Task<IReadOnlyList<Incident>> ListIncidentsAsync(Guid? projectId, bool activeOnly, int limit, CancellationToken ct);

    /// <summary>
    /// Active incidents least-recently-touched first, for the loop to advance. The ordering is
    /// the fairness property: with a per-tick ceiling, newest-first would let a burst of new
    /// incidents starve older ones indefinitely.
    /// </summary>
    Task<IReadOnlyList<Incident>> ListIncidentsToAdvanceAsync(int limit, CancellationToken ct);

    /// <summary>Records the latest verdict without moving the incident. Keeps "what Relay knows now" current.</summary>
    Task<Incident?> RecordVerdictAsync(Guid incidentId, string detail, CancellationToken ct);

    Task<Incident?> AwaitApprovalAsync(Guid incidentId, IncidentStatus from, Guid actionId, CancellationToken ct);
    Task<Incident?> BeginActingAsync(Guid incidentId, IncidentStatus from, Guid actionId, int attempt, CancellationToken ct);

    /// <summary>
    /// Enters VERIFYING and records the two facts verification depends on: the instant after
    /// which evidence counts, and the deadline for producing it.
    /// </summary>
    Task<Incident?> BeginVerifyingAsync(
        Guid incidentId, Guid actionId, DateTimeOffset evidenceAfter, DateTimeOffset deadline, CancellationToken ct);

    /// <summary>
    /// Returns the incident to the decision state after a failed attempt, so the next step is
    /// re-decided under the current policy rather than retried blindly.
    /// </summary>
    Task<Incident?> ReopenIncidentAsync(Guid incidentId, IncidentStatus from, string detail, CancellationToken ct);

    Task<Incident?> ResolveIncidentAsync(
        Guid incidentId, IncidentStatus from, string resolution, string detail, CancellationToken ct);

    Task<Incident?> EscalateIncidentAsync(Guid incidentId, IncidentStatus from, string reason, CancellationToken ct);

    // ---- incident audit log ----

    Task<IReadOnlyList<IncidentEvent>> AppendIncidentEventsAsync(
        Guid incidentId, IReadOnlyList<IncidentEvent> events, CancellationToken ct);

    Task<IReadOnlyList<IncidentEvent>> GetIncidentEventsAsync(
        Guid incidentId, long afterSeq, int limit, CancellationToken ct);

    // ---- actions ----

    Task<RemediationAction> CreateActionAsync(NewAction spec, CancellationToken ct);
    Task<RemediationAction?> GetActionAsync(Guid actionId, CancellationToken ct);
    Task<IReadOnlyList<RemediationAction>> ListActionsAsync(Guid incidentId, CancellationToken ct);
    Task<IReadOnlyList<RemediationAction>> ListPendingApprovalsAsync(Guid projectId, CancellationToken ct);

    Task<RemediationAction?> ApproveActionAsync(Guid actionId, string approvedBy, CancellationToken ct);
    Task<RemediationAction?> RejectActionAsync(Guid actionId, string rejectedBy, string? reason, CancellationToken ct);

    /// <summary>
    /// Closes out an action whose incident advanced underneath it. Accepts either
    /// pre-execution status, so a lost race can never leave an unexplained row in the audit
    /// log — the alternative is an action stuck at "approved" that nothing will ever run.
    /// </summary>
    Task<RemediationAction?> SupersedeActionAsync(Guid actionId, string reason, CancellationToken ct);

    /// <summary>Approved -> Executing, recording which job (if any) is carrying the action out.</summary>
    Task<RemediationAction?> BeginExecutionAsync(Guid actionId, Guid? executionJobId, CancellationToken ct);

    /// <summary>Executing -> Succeeded | Failed. "Succeeded" means it ran, not that it worked.</summary>
    Task<RemediationAction?> SettleActionAsync(
        Guid actionId, bool succeeded, string? failureReason, CancellationToken ct);

    /// <summary>Records whether the intervention demonstrably worked. No status change.</summary>
    Task<RemediationAction?> RecordOutcomeAsync(
        Guid actionId, ActionOutcome outcome, string detail, CancellationToken ct);
}
