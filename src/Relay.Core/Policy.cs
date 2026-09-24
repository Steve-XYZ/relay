using System.Globalization;
using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// The five things Relay can be asked to expect. A closed set on purpose: an expression
/// language would make policies unreviewable and evaluation untestable, and every real
/// expectation so far reduces to one of these.
/// </summary>
public enum ExpectationKind
{
    /// <summary>The latest observation for the signal reports Healthy.</summary>
    Healthy,

    /// <summary>An observation for the signal exists and is younger than MaxAge. Liveness.</summary>
    Fresh,

    /// <summary>A numeric fact on the latest observation is at or below Threshold.</summary>
    FactAtMost,

    /// <summary>A numeric fact on the latest observation is at or above Threshold.</summary>
    FactAtLeast,

    /// <summary>A fact on the latest observation equals Value exactly.</summary>
    FactEquals,
}

/// <summary>One assertion about a resource's observations. Immutable and self-describing.</summary>
public sealed record Expectation
{
    [JsonPropertyName("kind")]
    public required ExpectationKind Kind { get; init; }

    [JsonPropertyName("signal")]
    public string Signal { get; init; } = Observation.DefaultSignal;

    [JsonPropertyName("fact")]
    public string? Fact { get; init; }

    [JsonPropertyName("threshold")]
    public decimal? Threshold { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("max_age_seconds")]
    public long? MaxAgeSeconds { get; init; }

    /// <summary>
    /// Whether "nobody has reported anything" counts as a violation. Off by default so a
    /// newly declared resource does not immediately raise an incident. <see cref="ExpectationKind.Fresh"/>
    /// ignores this: absence of a heartbeat is exactly what it is asserting against.
    /// </summary>
    [JsonPropertyName("treat_unknown_as_violation")]
    public bool TreatUnknownAsViolation { get; init; }

    /// <summary>Rejects half-formed expectations at write time so the loop never meets one.</summary>
    public Expectation Validated()
    {
        if (string.IsNullOrWhiteSpace(Signal))
            throw new DomainException("expectation signal must not be empty");

        switch (Kind)
        {
            case ExpectationKind.Fresh when MaxAgeSeconds is null or <= 0:
                throw new DomainException("fresh expectation requires a positive max_age_seconds");
            case ExpectationKind.FactAtMost or ExpectationKind.FactAtLeast
                when string.IsNullOrWhiteSpace(Fact) || Threshold is null:
                throw new DomainException($"{Kind.ToWire()} expectation requires 'fact' and 'threshold'");
            case ExpectationKind.FactEquals when string.IsNullOrWhiteSpace(Fact) || Value is null:
                throw new DomainException("fact_equals expectation requires 'fact' and 'value'");
        }
        return this;
    }

    public string Describe() => Kind switch
    {
        ExpectationKind.Healthy => $"{Signal} is healthy",
        ExpectationKind.Fresh => $"{Signal} reported within {MaxAgeSeconds}s",
        ExpectationKind.FactAtMost => $"{Signal}.{Fact} <= {Fmt(Threshold)}",
        ExpectationKind.FactAtLeast => $"{Signal}.{Fact} >= {Fmt(Threshold)}",
        ExpectationKind.FactEquals => $"{Signal}.{Fact} == {Value}",
        _ => Kind.ToWire(),
    };

    private static string Fmt(decimal? d) => d?.ToString("0.####", CultureInfo.InvariantCulture) ?? "?";
}

/// <summary>Which resources a policy applies to. Kind is required; a null key means every one of that kind.</summary>
public sealed record ResourceSelector
{
    [JsonPropertyName("kind")]
    public required ResourceKind Kind { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    public bool Matches(Resource resource) =>
        resource.Kind == Kind &&
        (Key is null || string.Equals(resource.Key, Key, StringComparison.Ordinal));

    public string Describe() => Key is null ? $"all {Kind.ToWire()}" : $"{Kind.ToWire()}/{Key}";
}

public enum Severity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// What Relay is allowed to do when this policy is violated, and how hard it may try.
/// This record is the entire authorization surface for autonomous action: if it does not
/// grant something, the loop escalates to a human instead of improvising.
/// </summary>
public sealed record Remediation
{
    [JsonPropertyName("action")]
    public required ActionKind Action { get; init; }

    /// <summary>Action-specific inputs, e.g. {"prompt":"…"} for an agent task.</summary>
    [JsonPropertyName("params")]
    public Dictionary<string, string> Params { get; init; } = [];

    /// <summary>When set, Relay proposes the action and waits for a human to approve it.</summary>
    [JsonPropertyName("requires_approval")]
    public bool RequiresApproval { get; init; }

    /// <summary>How many times Relay may act on one incident before escalating.</summary>
    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Minimum gap between attempts on the same incident.</summary>
    [JsonPropertyName("cooldown_seconds")]
    public long CooldownSeconds { get; init; } = 300;

    /// <summary>
    /// How long Relay waits for fresh evidence that the action worked before calling the
    /// verification failed. An action reporting success is not evidence.
    /// </summary>
    [JsonPropertyName("verify_within_seconds")]
    public long VerifyWithinSeconds { get; init; } = 120;

    public const string PromptParam = "prompt";
    public const string RepoUrlParam = "repo_url";
    public const string CommandParam = "command";
    public const string TimeoutSecondsParam = "timeout_seconds";

    public Remediation Validated()
    {
        if (MaxAttempts < 1) throw new DomainException("remediation max_attempts must be >= 1");
        if (CooldownSeconds < 0) throw new DomainException("remediation cooldown_seconds must be >= 0");
        if (VerifyWithinSeconds < 1) throw new DomainException("remediation verify_within_seconds must be >= 1");

        // An agent task with no prompt is an action that cannot be carried out. Reject it at
        // authoring time rather than discovering it during an outage.
        if (Action == ActionKind.RunAgentTask &&
            (!Params.TryGetValue(PromptParam, out var prompt) || string.IsNullOrWhiteSpace(prompt)))
            throw new DomainException($"run_agent_task remediation requires a '{PromptParam}' param");

        if (Action == ActionKind.RunCommand &&
            (!Params.TryGetValue(CommandParam, out var command) || string.IsNullOrWhiteSpace(command)))
            throw new DomainException($"run_command remediation requires a '{CommandParam}' param");

        return this;
    }

    /// <summary>Notify has nothing to verify: telling a human is the whole intervention.</summary>
    [JsonIgnore]
    public bool IsVerifiable => Action != ActionKind.Notify;
}

/// <summary>
/// A durable statement of desired state plus the authority to restore it. Policies are the
/// only reason Relay ever acts on its own.
/// </summary>
public sealed record Policy
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("project_id")]
    public required Guid ProjectId { get; init; }

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

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

public static class PolicyWire
{
    public static string ToWire(this ExpectationKind kind) => EnumWire.Camel(kind.ToString());
    public static string ToWire(this Severity severity) => severity.ToString().ToLowerInvariant();

    public static ExpectationKind ParseExpectationKind(string value) =>
        Enum.TryParse<ExpectationKind>(value.Replace("_", ""), ignoreCase: true, out var k)
            ? k
            : throw new DomainException($"unknown expectation kind '{value}'");

    public static Severity ParseSeverity(string value) =>
        Enum.TryParse<Severity>(value, ignoreCase: true, out var s)
            ? s
            : throw new DomainException($"unknown severity '{value}'");
}
