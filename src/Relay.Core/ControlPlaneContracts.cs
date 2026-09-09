using System.Text.Json.Serialization;

namespace Relay.Core;

// ---- Project + resource declaration ----

public sealed record UpsertProjectRequest
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed record UpsertResourceRequest
{
    [JsonPropertyName("kind")]
    public required ResourceKind Kind { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Merged into the existing attributes; absent keys are left alone.</summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, string>? Attributes { get; init; }
}

// ---- Observation ingest ----

/// <summary>
/// What a reporter pushes. Relay stamps the receive time itself and clamps
/// <see cref="ObservedAt"/> to it, so no reporter can submit evidence from the future.
/// </summary>
public sealed record ReportObservationRequest
{
    [JsonPropertyName("kind")]
    public required ResourceKind Kind { get; init; }

    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("signal")]
    public string Signal { get; init; } = Observation.DefaultSignal;

    [JsonPropertyName("state")]
    public required ObservedState State { get; init; }

    [JsonPropertyName("facts")]
    public Dictionary<string, string>? Facts { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "unknown";

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("observed_at")]
    public DateTimeOffset? ObservedAt { get; init; }
}

// ---- Policy authoring ----

public sealed record CreatePolicyRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("target")]
    public required ResourceSelector Target { get; init; }

    [JsonPropertyName("expectation")]
    public required Expectation Expectation { get; init; }

    [JsonPropertyName("severity")]
    public Severity Severity { get; init; } = Severity.Warning;

    [JsonPropertyName("remediation")]
    public required Remediation Remediation { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

// ---- Human decisions on proposed actions ----

public sealed record ApproveActionRequest
{
    [JsonPropertyName("approved_by")]
    public required string ApprovedBy { get; init; }
}

public sealed record RejectActionRequest
{
    [JsonPropertyName("rejected_by")]
    public required string RejectedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

// ---- Read models ----

/// <summary>
/// One project's operational picture in a single payload: what exists, what diverges, and
/// what Relay is doing about it. This is the shape that makes the product's core claim
/// checkable in one request.
/// </summary>
public sealed record ProjectState
{
    [JsonPropertyName("project")]
    public required Project Project { get; init; }

    [JsonPropertyName("resources")]
    public required IReadOnlyList<Resource> Resources { get; init; }

    [JsonPropertyName("policies")]
    public required IReadOnlyList<Policy> Policies { get; init; }

    [JsonPropertyName("active_incidents")]
    public required IReadOnlyList<Incident> ActiveIncidents { get; init; }

    [JsonPropertyName("pending_approvals")]
    public required IReadOnlyList<RemediationAction> PendingApprovals { get; init; }

    [JsonPropertyName("evaluated_at")]
    public required DateTimeOffset EvaluatedAt { get; init; }
}

/// <summary>An incident with the audit trail that justifies it.</summary>
public sealed record IncidentDetail
{
    [JsonPropertyName("incident")]
    public required Incident Incident { get; init; }

    [JsonPropertyName("actions")]
    public required IReadOnlyList<RemediationAction> Actions { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<IncidentEvent> Events { get; init; }
}
