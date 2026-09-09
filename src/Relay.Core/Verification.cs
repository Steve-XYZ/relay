using System.Text.Json.Serialization;

namespace Relay.Core;

public enum VerificationStatus
{
    /// <summary>Fresh evidence shows the expectation satisfied. The intervention worked.</summary>
    Passed,

    /// <summary>No conclusive evidence newer than the action yet, and the deadline has not passed.</summary>
    Pending,

    /// <summary>Fresh evidence shows the expectation still violated. The intervention did not work.</summary>
    Failed,

    /// <summary>The deadline passed without conclusive fresh evidence. Relay does not know.</summary>
    TimedOut,
}

public sealed record VerificationResult
{
    [JsonPropertyName("status")]
    public required VerificationStatus Status { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonIgnore]
    public Observation? Evidence { get; init; }

    public ActionOutcome? ToOutcome() => Status switch
    {
        VerificationStatus.Passed => ActionOutcome.Verified,
        VerificationStatus.Failed => ActionOutcome.VerificationFailed,
        VerificationStatus.TimedOut => ActionOutcome.VerificationTimedOut,
        _ => null,
    };
}

/// <summary>
/// Decides whether an intervention worked. The one rule that matters: only evidence Relay
/// received after the action finished may resolve an incident. An action reporting success
/// is a claim about itself, not a fact about the resource — a job can exit zero and change
/// nothing, and a stale health check can look green for minutes after a real outage.
///
/// Freshness is measured on <see cref="Observation.ReceivedAt"/> (Relay's own clock) rather
/// than <see cref="Observation.ObservedAt"/> (the reporter's), so a reporter with a fast
/// clock cannot close an incident that is still broken.
/// </summary>
public static class Verifier
{
    public static VerificationResult Verify(
        Expectation expectation,
        ObservationSnapshot snapshot,
        DateTimeOffset evidenceAfter,
        DateTimeOffset deadline,
        DateTimeOffset now)
    {
        var latest = snapshot.Latest(expectation.Signal);
        var isFresh = latest is not null && latest.ReceivedAt > evidenceAfter;

        if (isFresh)
        {
            var verdict = Evaluator.Evaluate(expectation, snapshot, now);
            switch (verdict.Status)
            {
                case EvaluationStatus.Satisfied:
                    return new VerificationResult
                    {
                        Status = VerificationStatus.Passed,
                        Summary = $"verified: {verdict.Summary}",
                        Evidence = latest,
                    };
                case EvaluationStatus.Violated:
                    return new VerificationResult
                    {
                        Status = VerificationStatus.Failed,
                        Summary = $"still violated after remediation: {verdict.Summary}",
                        Evidence = latest,
                    };
                // Fresh but inconclusive (e.g. the expected fact is missing): keep waiting
                // rather than guess, and let the deadline decide.
            }
        }

        if (now >= deadline)
            return new VerificationResult
            {
                Status = VerificationStatus.TimedOut,
                Summary = isFresh
                    ? $"no conclusive '{expectation.Signal}' evidence before the verification deadline"
                    : $"no '{expectation.Signal}' observation received after the action finished",
                Evidence = latest,
            };

        return new VerificationResult
        {
            Status = VerificationStatus.Pending,
            Summary = isFresh
                ? $"awaiting conclusive '{expectation.Signal}' evidence ({Evaluator.Format(deadline - now)} left)"
                : $"awaiting fresh '{expectation.Signal}' evidence ({Evaluator.Format(deadline - now)} left)",
            Evidence = latest,
        };
    }
}

public static class VerificationWire
{
    public static string ToWire(this VerificationStatus status) => EnumWire.Camel(status.ToString());
}
