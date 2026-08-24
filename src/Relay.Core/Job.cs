using System.Globalization;
using System.Text.Json.Serialization;

namespace Relay.Core;

public enum JobStatus
{
    Queued,
    Preparing,
    Running,
    Validating,
    Completed,
    Interrupted,
    Recovering,
    Failed,
    Cancelled
}

public static class JobStatusExtensions
{
    public static string ToWire(this JobStatus status) => status.ToString().ToLowerInvariant();

    public static JobStatus FromWire(string value) =>
        Enum.TryParse<JobStatus>(value, ignoreCase: true, out var s)
            ? s
            : throw new FormatException($"Unknown job status '{value}'");

    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
}

/// <summary>Resource limits enforced by Relay during execution.</summary>
public sealed record Budget
{
    [JsonPropertyName("max_tokens")]
    public long? MaxTokens { get; init; }

    [JsonPropertyName("max_cost_usd")]
    public decimal? MaxCostUsd { get; init; }

    [JsonPropertyName("max_runtime_seconds")]
    public long? MaxRuntimeSeconds { get; init; }
}

/// <summary>Accumulated consumption for a job across all attempts.</summary>
public sealed record Usage
{
    [JsonPropertyName("tokens_in")]
    public long TokensIn { get; init; }

    [JsonPropertyName("tokens_out")]
    public long TokensOut { get; init; }

    [JsonPropertyName("cost_usd")]
    public decimal CostUsd { get; init; }

    [JsonPropertyName("tool_calls")]
    public long ToolCalls { get; init; }

    [JsonPropertyName("retries")]
    public long Retries { get; init; }

    [JsonIgnore]
    public long TotalTokens => TokensIn + TokensOut;

    public static Usage operator +(Usage a, Usage b) => new()
    {
        TokensIn = a.TokensIn + b.TokensIn,
        TokensOut = a.TokensOut + b.TokensOut,
        CostUsd = a.CostUsd + b.CostUsd,
        ToolCalls = a.ToolCalls + b.ToolCalls,
        Retries = a.Retries + b.Retries,
    };
}

/// <summary>Outcome artifacts produced by the worker after VALIDATING.</summary>
public sealed record JobResult
{
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("diff_stat")]
    public string? DiffStat { get; init; }

    [JsonPropertyName("changed_files")]
    public IReadOnlyList<string>? ChangedFiles { get; init; }

    [JsonPropertyName("commit_sha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("tests_passed")]
    public bool TestsPassed { get; init; }

    [JsonPropertyName("tests_output_tail")]
    public string? TestsOutputTail { get; init; }
}

public sealed record Job
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Human-friendly id used in URLs and CLI output, e.g. "7F2A".</summary>
    [JsonPropertyName("short_id")]
    public required string ShortId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("repo_url")]
    public required string RepoUrl { get; init; }

    [JsonPropertyName("base_ref")]
    public string BaseRef { get; init; } = "HEAD";

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    /// <summary>Agent adapter to run inside the sandbox: "mock" or a shell command template.</summary>
    [JsonPropertyName("agent")]
    public string Agent { get; init; } = "mock";

    [JsonPropertyName("test_command")]
    public string? TestCommand { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("budget")]
    public Budget? Budget { get; init; }

    [JsonPropertyName("usage")]
    public Usage Usage { get; init; } = new();

    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; } = 3;

    [JsonPropertyName("failure_reason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("cancel_requested")]
    public bool CancelRequested { get; init; }

    [JsonPropertyName("result")]
    public JobResult? Result { get; init; }

    [JsonPropertyName("resume_from_checkpoint")]
    public long ResumeFromCheckpoint { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; init; }

    [JsonPropertyName("lease_expires_at")]
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public static string GenerateShortId(Random random)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<char> chars = stackalloc char[4];
        lock (random)
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = alphabet[random.Next(alphabet.Length)];
        }
        return new string(chars);
    }
}
