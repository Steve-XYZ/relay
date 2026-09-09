using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// Where an incident is in the Relay loop. The statuses are the loop's own steps, so
/// "what is Relay doing about this" is answerable by reading one column.
///
///                    ┌──────────────────────────────┐
///                    ▼                              │ retry within budget
///   OPEN ──▶ AWAITING_APPROVAL ──▶ ACTING ──▶ VERIFYING ──▶ RESOLVED
///     │                    ▲          │              │
///     └────────────────────┘          └──────────────┴──▶ ESCALATED ──▶ OPEN
///
/// Every retry routes back through OPEN, so approval, cool-down and the attempt budget are
/// decided in exactly one place. RESOLVED is the only terminal status: an escalated incident
/// is still Relay's problem to track, it is just not Relay's to fix.
/// </summary>
public enum IncidentStatus
{
    /// <summary>Divergence detected and recorded; no intervention decided yet.</summary>
    Open,

    /// <summary>An action is proposed but policy requires a human to authorize it.</summary>
    AwaitingApproval,

    /// <summary>An authorized action is executing.</summary>
    Acting,

    /// <summary>The action finished; Relay is waiting for fresh evidence that it worked.</summary>
    Verifying,

    /// <summary>The expectation is satisfied again, backed by evidence newer than the action.</summary>
    Resolved,

    /// <summary>Out of attempts, out of authority, or nothing to try. A human owns it now.</summary>
    Escalated,
}

/// <summary>
/// A durable record that reality diverged from desired state, and everything Relay did
/// about it. At most one incident per (policy, resource) may be non-terminal at a time;
/// that invariant is what stops a flapping resource from producing an incident storm.
/// </summary>
public sealed record Incident
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("short_id")]
    public required string ShortId { get; init; }

    [JsonPropertyName("project_id")]
    public required Guid ProjectId { get; init; }

    [JsonPropertyName("policy_id")]
    public required Guid PolicyId { get; init; }

    [JsonPropertyName("policy_name")]
    public string PolicyName { get; init; } = "";

    [JsonPropertyName("resource_id")]
    public required Guid ResourceId { get; init; }

    [JsonPropertyName("resource_label")]
    public string ResourceLabel { get; init; } = "";

    [JsonPropertyName("status")]
    public required IncidentStatus Status { get; init; }

    [JsonPropertyName("severity")]
    public Severity Severity { get; init; } = Severity.Warning;

    /// <summary>The verdict summary that opened the incident.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    /// <summary>Most recent verdict summary, refreshed on every loop pass.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>Number of remediation attempts Relay has spent on this incident.</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    /// <summary>
    /// The attempt budget the policy granted when this incident opened, kept for display and
    /// for reading the audit log later. The loop decides against the policy's current value,
    /// so editing a policy takes effect on incidents already open.
    /// </summary>
    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; } = 1;

    /// <summary>The action currently being executed or verified, if any.</summary>
    [JsonPropertyName("current_action_id")]
    public Guid? CurrentActionId { get; init; }

    /// <summary>When the last attempt finished; the cool-down is measured from here.</summary>
    [JsonPropertyName("last_attempt_at")]
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>
    /// Point after which verification has run out of patience. Set when entering VERIFYING,
    /// cleared on leaving it, so a stuck verification cannot hold an incident open forever.
    /// </summary>
    [JsonPropertyName("verify_deadline_at")]
    public DateTimeOffset? VerifyDeadlineAt { get; init; }

    /// <summary>
    /// Only evidence received after this instant may resolve the incident. Written when an
    /// action completes; this single field is what makes "did the fix actually work" a fact
    /// rather than an assumption.
    /// </summary>
    [JsonPropertyName("verify_evidence_after")]
    public DateTimeOffset? VerifyEvidenceAfter { get; init; }

    [JsonPropertyName("escalation_reason")]
    public string? EscalationReason { get; init; }

    [JsonPropertyName("opened_at")]
    public DateTimeOffset OpenedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("resolved_at")]
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>How the incident ended: "self_healed", "verified", "manual".</summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [JsonIgnore]
    public bool IsActive => !Status.IsTerminal();
}

/// <summary>
/// Explicit incident lifecycle. Nothing but this table may decide whether a move is legal,
/// and the store re-checks it under a row lock, so two server replicas racing the same
/// incident cannot both advance it.
/// </summary>
public static class IncidentStateMachine
{
    private static readonly Dictionary<IncidentStatus, HashSet<IncidentStatus>> Allowed = new()
    {
        [IncidentStatus.Open] =
            [IncidentStatus.AwaitingApproval, IncidentStatus.Acting, IncidentStatus.Resolved, IncidentStatus.Escalated],
        [IncidentStatus.AwaitingApproval] =
            [IncidentStatus.Acting, IncidentStatus.Resolved, IncidentStatus.Escalated],
        // No ACTING -> RESOLVED: an action in flight must land first, otherwise the audit log
        // would show an incident closing with an orphaned intervention still running.
        [IncidentStatus.Acting] =
            [IncidentStatus.Verifying, IncidentStatus.Open, IncidentStatus.Escalated],
        [IncidentStatus.Verifying] =
            [IncidentStatus.Resolved, IncidentStatus.Open, IncidentStatus.Escalated],
        // Re-arming an escalated incident goes back to the decision state, never straight to
        // acting, so a human retry is still subject to policy.
        [IncidentStatus.Escalated] =
            [IncidentStatus.Open, IncidentStatus.Resolved],
        [IncidentStatus.Resolved] = [],
    };

    public static bool CanTransition(IncidentStatus from, IncidentStatus to) => Allowed[from].Contains(to);

    public static void Validate(IncidentStatus from, IncidentStatus to)
    {
        if (!CanTransition(from, to))
            throw new DomainException($"invalid incident transition {from.ToWire()} -> {to.ToWire()}");
    }

    public static IEnumerable<IncidentStatus> Next(IncidentStatus from) => Allowed[from];
}

public static class IncidentWire
{
    public static string ToWire(this IncidentStatus status) => EnumWire.Camel(status.ToString());

    public static bool IsTerminal(this IncidentStatus status) => status == IncidentStatus.Resolved;

    public static IncidentStatus ParseIncidentStatus(string value) =>
        Enum.TryParse<IncidentStatus>(value.Replace("_", ""), ignoreCase: true, out var s)
            ? s
            : throw new DomainException($"unknown incident status '{value}'");

    /// <summary>Statuses that count against the one-active-incident-per-(policy,resource) rule.</summary>
    public static readonly IReadOnlyList<IncidentStatus> Active =
    [
        IncidentStatus.Open, IncidentStatus.AwaitingApproval, IncidentStatus.Acting,
        IncidentStatus.Verifying, IncidentStatus.Escalated,
    ];
}

/// <summary>
/// One entry in an incident's audit log. Same shape and guarantees as <see cref="JobEvent"/>:
/// append-only, densely sequenced per incident, replayable over SSE.
/// </summary>
public sealed record IncidentEvent
{
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    public static IncidentEvent Of(EventKind kind, string message, Dictionary<string, string>? data = null) =>
        new() { Seq = 0, Kind = kind.ToWire(), Message = message, Data = data };

    public static IncidentEvent State(IncidentStatus from, IncidentStatus to, string? reason)
    {
        var data = new Dictionary<string, string> { ["from"] = from.ToWire(), ["to"] = to.ToWire() };
        if (reason is not null) data["reason"] = reason;
        return Of(EventKind.State, $"{from.ToWire()} -> {to.ToWire()}", data);
    }

    public static IncidentEvent Observed(Verdict verdict) =>
        Of(EventKind.Observation, verdict.Summary, new Dictionary<string, string>
        {
            ["status"] = verdict.Status.ToWire(),
            ["evidence_source"] = verdict.EvidenceSource ?? "none",
        });

    public static IncidentEvent Decision(string message, Dictionary<string, string>? data = null) =>
        Of(EventKind.Decision, message, data);

    public static IncidentEvent Verification(string message, Dictionary<string, string>? data = null) =>
        Of(EventKind.Verification, message, data);
}
