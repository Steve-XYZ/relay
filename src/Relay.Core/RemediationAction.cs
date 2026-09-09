using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// What Relay knows how to do. Kept to interventions Relay can actually carry out and
/// verify today; a new kind means one enum member plus one branch in the dispatcher, not
/// a plugin host. Relay coordinates external systems — it does not restart your containers
/// or run your deploys itself.
/// </summary>
public enum ActionKind
{
    /// <summary>
    /// Record that a human must intervene, with the incident's evidence attached. Always
    /// succeeds and is never verified: telling somebody is the whole intervention.
    /// </summary>
    Notify,

    /// <summary>
    /// Run a coding agent against a repository through the existing job runtime. The job is
    /// the execution primitive; the action owns the intent and the audit trail.
    /// </summary>
    RunAgentTask,
}

/// <summary>
/// Action lifecycle. Every action passes through APPROVED even when policy grants blanket
/// authority, so the audit log always answers "who allowed this".
///
///   PROPOSED ──▶ APPROVED ──▶ EXECUTING ──▶ SUCCEEDED | FAILED
///        └─────▶ REJECTED
/// </summary>
public enum ActionStatus
{
    Proposed,
    Approved,
    Executing,
    Succeeded,
    Failed,
    Rejected,
}

/// <summary>Whether the intervention demonstrably worked. Set only after verification concludes.</summary>
public enum ActionOutcome
{
    /// <summary>Fresh evidence showed the expectation satisfied after the action.</summary>
    Verified,

    /// <summary>Fresh evidence arrived and the expectation was still violated.</summary>
    VerificationFailed,

    /// <summary>No fresh evidence arrived before the verification deadline.</summary>
    VerificationTimedOut,

    /// <summary>Nothing to verify (see <see cref="ActionKind.Notify"/>).</summary>
    NotApplicable,
}

/// <summary>
/// One auditable intervention against one incident: what Relay decided to do, who allowed
/// it, what executed it, and whether it worked.
/// </summary>
public sealed record RemediationAction
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("project_id")]
    public required Guid ProjectId { get; init; }

    [JsonPropertyName("incident_id")]
    public required Guid IncidentId { get; init; }

    [JsonPropertyName("kind")]
    public required ActionKind Kind { get; init; }

    [JsonPropertyName("status")]
    public required ActionStatus Status { get; init; }

    /// <summary>Which attempt on the incident this action is (1-based).</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("params")]
    public Dictionary<string, string> Params { get; init; } = [];

    /// <summary>Why Relay proposed this: the verdict that justified it.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; init; }

    /// <summary>"policy" when the remediation granted blanket authority, otherwise the approver.</summary>
    [JsonPropertyName("approved_by")]
    public string? ApprovedBy { get; init; }

    /// <summary>
    /// The job that carried this action out, when the executor is the job runtime. The link
    /// is one-directional on purpose: actions own jobs, jobs know nothing about incidents.
    /// </summary>
    [JsonPropertyName("execution_job_id")]
    public Guid? ExecutionJobId { get; init; }

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("outcome")]
    public ActionOutcome? Outcome { get; init; }

    [JsonPropertyName("outcome_detail")]
    public string? OutcomeDetail { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonIgnore]
    public bool IsSettled => Status is ActionStatus.Succeeded or ActionStatus.Failed or ActionStatus.Rejected;
}

/// <summary>Explicit action lifecycle, re-checked by the store under a row lock.</summary>
public static class ActionStateMachine
{
    private static readonly Dictionary<ActionStatus, HashSet<ActionStatus>> Allowed = new()
    {
        [ActionStatus.Proposed] = [ActionStatus.Approved, ActionStatus.Rejected],
        [ActionStatus.Approved] = [ActionStatus.Executing, ActionStatus.Rejected],
        [ActionStatus.Executing] = [ActionStatus.Succeeded, ActionStatus.Failed],
        [ActionStatus.Succeeded] = [],
        [ActionStatus.Failed] = [],
        [ActionStatus.Rejected] = [],
    };

    public static bool CanTransition(ActionStatus from, ActionStatus to) => Allowed[from].Contains(to);

    public static void Validate(ActionStatus from, ActionStatus to)
    {
        if (!CanTransition(from, to))
            throw new DomainException($"invalid action transition {from.ToWire()} -> {to.ToWire()}");
    }

    public static IEnumerable<ActionStatus> Next(ActionStatus from) => Allowed[from];
}

public static class ActionWire
{
    public static string ToWire(this ActionKind kind) => EnumWire.Camel(kind.ToString());
    public static string ToWire(this ActionStatus status) => status.ToString().ToLowerInvariant();
    public static string ToWire(this ActionOutcome outcome) => EnumWire.Camel(outcome.ToString());

    public static ActionKind ParseActionKind(string value) =>
        Enum.TryParse<ActionKind>(value.Replace("_", ""), ignoreCase: true, out var k)
            ? k
            : throw new DomainException($"unknown action kind '{value}'");

    public static ActionStatus ParseActionStatus(string value) =>
        Enum.TryParse<ActionStatus>(value, ignoreCase: true, out var s)
            ? s
            : throw new DomainException($"unknown action status '{value}'");

    public static ActionOutcome ParseActionOutcome(string value) =>
        Enum.TryParse<ActionOutcome>(value.Replace("_", ""), ignoreCase: true, out var o)
            ? o
            : throw new DomainException($"unknown action outcome '{value}'");
}
