using System.Text.Json.Serialization;

namespace Relay.Core;

// ---- Job API contracts (clients -> server) ----

public sealed record CreateJobRequest
{
    [JsonPropertyName("repo_url")]
    public required string RepoUrl { get; init; }

    /// <summary>Owning project. Optional: a job can still be a standalone piece of work.</summary>
    [JsonPropertyName("project_id")]
    public Guid? ProjectId { get; init; }

    /// <summary>Set by the action dispatcher for policy-driven work; clients leave it alone.</summary>
    [JsonPropertyName("origin")]
    public JobOrigin Origin { get; init; } = JobOrigin.User;

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("base_ref")]
    public string? BaseRef { get; init; }

    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    [JsonPropertyName("test_command")]
    public string? TestCommand { get; init; }

    [JsonPropertyName("budget")]
    public Budget? Budget { get; init; }
}

public static class Wire
{
    public static string For(JobStatus status) => status.ToWire();

    /// <summary>Static entry point so non-expression call sites can parse without the extension class.</summary>
    public static JobStatus From(string value) => JobStatusExtensions.FromWire(value);
}

// ---- Internal API contracts (workers <-> server control plane) ----

public sealed record ClaimRequest
{
    [JsonPropertyName("worker_id")]
    public required Guid WorkerId { get; init; }

    [JsonPropertyName("worker_name")]
    public string WorkerName { get; init; } = "";

    [JsonPropertyName("lease_seconds")]
    public int LeaseSeconds { get; init; } = 15;
}

public sealed record ClaimResponse
{
    [JsonPropertyName("job")]
    public required Job Job { get; init; }

    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("checkpoints")]
    public IReadOnlyList<Checkpoint> Checkpoints { get; init; } = [];
}

public sealed record HeartbeatRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("usage_delta")]
    public Usage? UsageDelta { get; init; }

    [JsonPropertyName("extend_seconds")]
    public int ExtendSeconds { get; init; } = 15;
}

public sealed record TransitionRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record AppendEventsRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<JobEvent> Events { get; init; }
}

public sealed record CheckpointRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; init; }
}

public sealed record CompleteRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("result")]
    public required JobResult Result { get; init; }

    [JsonPropertyName("usage_final")]
    public Usage? UsageFinal { get; set; }
}

public sealed record HeartbeatOutcome(bool CancelRequested, DateTimeOffset LeaseExpiresAt, Job Job);

public sealed record FailRequest
{
    [JsonPropertyName("lease_token")]
    public required Guid LeaseToken { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("usage_final")]
    public Usage? UsageFinal { get; set; }
}
