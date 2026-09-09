using System.Globalization;
using System.Text.Json.Serialization;

namespace Relay.Core;

public enum EvaluationStatus
{
    /// <summary>Reality matches desired state.</summary>
    Satisfied,

    /// <summary>Reality diverges from desired state. This is what opens incidents.</summary>
    Violated,

    /// <summary>
    /// Relay cannot tell. Distinct from Satisfied on purpose: "nobody reported" and
    /// "reported healthy" are different facts and must not be conflated.
    /// </summary>
    Unknown,
}

/// <summary>
/// The outcome of comparing one expectation against one resource's observations, with the
/// observation that decided it. Every incident and every verification cites a verdict, so
/// "why did Relay think this" is always answerable from durable state.
/// </summary>
public sealed record Verdict
{
    [JsonPropertyName("status")]
    public required EvaluationStatus Status { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    /// <summary>The observation the verdict was drawn from, if any.</summary>
    [JsonIgnore]
    public Observation? Evidence { get; init; }

    [JsonPropertyName("evidence_observation_id")]
    public long? EvidenceObservationId => Evidence?.Id;

    [JsonPropertyName("evidence_source")]
    public string? EvidenceSource => Evidence?.Source;

    [JsonPropertyName("evidence_observed_at")]
    public DateTimeOffset? EvidenceObservedAt => Evidence?.ObservedAt;

    public bool IsViolated => Status == EvaluationStatus.Violated;
    public bool IsSatisfied => Status == EvaluationStatus.Satisfied;
}

/// <summary>
/// Pure comparison of desired state against observed state. No clock, no I/O, no policy
/// side effects: given the same snapshot and instant it always returns the same verdict,
/// which is what makes the loop's decisions reproducible from the audit log.
/// </summary>
public static class Evaluator
{
    public static Verdict Evaluate(Expectation expectation, ObservationSnapshot snapshot, DateTimeOffset now)
    {
        var latest = snapshot.Latest(expectation.Signal);

        if (expectation.Kind == ExpectationKind.Fresh)
        {
            var maxAge = TimeSpan.FromSeconds(expectation.MaxAgeSeconds ?? 0);
            if (latest is null)
                return Violated($"no '{expectation.Signal}' observation has ever been reported", null);

            var age = now - latest.ObservedAt;
            return age <= maxAge
                ? Satisfied($"'{expectation.Signal}' reported {Format(age)} ago (limit {Format(maxAge)})", latest)
                : Violated($"'{expectation.Signal}' last reported {Format(age)} ago, limit is {Format(maxAge)}", latest);
        }

        if (latest is null)
            return expectation.TreatUnknownAsViolation
                ? Violated($"no '{expectation.Signal}' observation has ever been reported", null)
                : Unknown($"no '{expectation.Signal}' observation has ever been reported");

        switch (expectation.Kind)
        {
            case ExpectationKind.Healthy:
                return latest.State == ObservedState.Healthy
                    ? Satisfied($"'{expectation.Signal}' is healthy per {latest.Source}", latest)
                    : Violated($"'{expectation.Signal}' is {latest.State.ToWire()} per {latest.Source}"
                               + (latest.Message is null ? "" : $": {latest.Message}"), latest);

            case ExpectationKind.FactAtMost:
            case ExpectationKind.FactAtLeast:
            {
                var fact = expectation.Fact!;
                if (!latest.TryNumericFact(fact, out var actual))
                    return expectation.TreatUnknownAsViolation
                        ? Violated($"'{expectation.Signal}' carries no numeric fact '{fact}'", latest)
                        : Unknown($"'{expectation.Signal}' carries no numeric fact '{fact}'");

                var threshold = expectation.Threshold!.Value;
                var ok = expectation.Kind == ExpectationKind.FactAtMost ? actual <= threshold : actual >= threshold;
                var op = expectation.Kind == ExpectationKind.FactAtMost ? "<=" : ">=";
                var text = $"{expectation.Signal}.{fact} = {Num(actual)}, expected {op} {Num(threshold)}";
                return ok ? Satisfied(text, latest) : Violated(text, latest);
            }

            case ExpectationKind.FactEquals:
            {
                var fact = expectation.Fact!;
                if (!latest.TryFact(fact, out var actual))
                    return expectation.TreatUnknownAsViolation
                        ? Violated($"'{expectation.Signal}' carries no fact '{fact}'", latest)
                        : Unknown($"'{expectation.Signal}' carries no fact '{fact}'");

                var text = $"{expectation.Signal}.{fact} = '{actual}', expected '{expectation.Value}'";
                return string.Equals(actual, expectation.Value, StringComparison.Ordinal)
                    ? Satisfied(text, latest)
                    : Violated(text, latest);
            }

            default:
                return Unknown($"unsupported expectation kind '{expectation.Kind.ToWire()}'");
        }
    }

    private static Verdict Satisfied(string summary, Observation? evidence) =>
        new() { Status = EvaluationStatus.Satisfied, Summary = summary, Evidence = evidence };

    private static Verdict Violated(string summary, Observation? evidence) =>
        new() { Status = EvaluationStatus.Violated, Summary = summary, Evidence = evidence };

    private static Verdict Unknown(string summary) =>
        new() { Status = EvaluationStatus.Unknown, Summary = summary };

    private static string Num(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    internal static string Format(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:F0}s"
        : t.TotalMinutes < 90 ? $"{t.TotalMinutes:F0}m"
        : $"{t.TotalHours:F1}h";
}

public static class EvaluationWire
{
    public static string ToWire(this EvaluationStatus status) => status.ToString().ToLowerInvariant();
}
