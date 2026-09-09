using Relay.Core;
using Xunit;

namespace Relay.Core.Tests;

/// <summary>
/// Comparing desired state against observed state. These cases are the contract the whole
/// loop rests on: everything downstream — incidents, actions, verification — is a consequence
/// of a verdict produced here.
/// </summary>
public class EvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T12:00:00Z");

    private static Observation Obs(
        ObservedState state,
        string signal = Observation.DefaultSignal,
        Dictionary<string, string>? facts = null,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? receivedAt = null) => new()
    {
        Id = 1,
        ProjectId = Guid.NewGuid(),
        ResourceId = Guid.NewGuid(),
        Signal = signal,
        State = state,
        Facts = facts ?? [],
        Source = "test-reporter",
        ObservedAt = observedAt ?? Now,
        ReceivedAt = receivedAt ?? observedAt ?? Now,
    };

    private static ObservationSnapshot Snapshot(params Observation[] observations) => new(observations);

    [Fact]
    public void Healthy_expectation_is_satisfied_by_a_healthy_observation()
    {
        var verdict = Evaluator.Evaluate(
            new Expectation { Kind = ExpectationKind.Healthy },
            Snapshot(Obs(ObservedState.Healthy)), Now);

        Assert.Equal(EvaluationStatus.Satisfied, verdict.Status);
        Assert.Equal("test-reporter", verdict.EvidenceSource);
    }

    [Theory]
    [InlineData(ObservedState.Degraded)]
    [InlineData(ObservedState.Unavailable)]
    public void Healthy_expectation_is_violated_by_anything_else(ObservedState state)
    {
        var verdict = Evaluator.Evaluate(
            new Expectation { Kind = ExpectationKind.Healthy },
            Snapshot(Obs(state)), Now);

        Assert.True(verdict.IsViolated);
        Assert.Contains(state.ToWire(), verdict.Summary);
    }

    [Fact]
    public void Missing_evidence_is_unknown_not_satisfied()
    {
        // "Nobody has reported" and "reported healthy" are different facts. Conflating them is
        // how monitoring systems go quiet at exactly the wrong moment.
        var verdict = Evaluator.Evaluate(
            new Expectation { Kind = ExpectationKind.Healthy },
            ObservationSnapshot.Empty, Now);

        Assert.Equal(EvaluationStatus.Unknown, verdict.Status);
        Assert.False(verdict.IsViolated);
        Assert.Null(verdict.Evidence);
    }

    [Fact]
    public void Missing_evidence_violates_when_the_policy_says_so()
    {
        var verdict = Evaluator.Evaluate(
            new Expectation { Kind = ExpectationKind.Healthy, TreatUnknownAsViolation = true },
            ObservationSnapshot.Empty, Now);

        Assert.True(verdict.IsViolated);
    }

    [Fact]
    public void Fresh_expectation_treats_silence_as_a_violation()
    {
        // A liveness check exists precisely to notice absence, so it ignores the opt-in.
        var expectation = new Expectation
        {
            Kind = ExpectationKind.Fresh,
            Signal = "last_run",
            MaxAgeSeconds = 3600,
        };

        Assert.True(Evaluator.Evaluate(expectation, ObservationSnapshot.Empty, Now).IsViolated);
    }

    [Fact]
    public void Fresh_expectation_compares_against_the_reporters_observed_time()
    {
        var expectation = new Expectation
        {
            Kind = ExpectationKind.Fresh,
            Signal = "last_run",
            MaxAgeSeconds = 3600,
        };

        var recent = Obs(ObservedState.Healthy, "last_run", observedAt: Now.AddMinutes(-30));
        var stale = Obs(ObservedState.Healthy, "last_run", observedAt: Now.AddHours(-4));

        Assert.True(Evaluator.Evaluate(expectation, Snapshot(recent), Now).IsSatisfied);
        var verdict = Evaluator.Evaluate(expectation, Snapshot(stale), Now);
        Assert.True(verdict.IsViolated);
        Assert.Contains("limit", verdict.Summary);
    }

    [Fact]
    public void A_healthy_signal_cannot_satisfy_an_expectation_about_another_signal()
    {
        var expectation = new Expectation { Kind = ExpectationKind.Healthy, Signal = "queue" };
        var verdict = Evaluator.Evaluate(expectation, Snapshot(Obs(ObservedState.Healthy, "health")), Now);

        Assert.Equal(EvaluationStatus.Unknown, verdict.Status);
    }

    [Fact]
    public void Fact_thresholds_compare_numerically()
    {
        var atMost = new Expectation
        {
            Kind = ExpectationKind.FactAtMost,
            Signal = "queue",
            Fact = "depth",
            Threshold = 100,
        };

        var under = Snapshot(Obs(ObservedState.Healthy, "queue", new() { ["depth"] = "42" }));
        var over = Snapshot(Obs(ObservedState.Healthy, "queue", new() { ["depth"] = "1000" }));

        Assert.True(Evaluator.Evaluate(atMost, under, Now).IsSatisfied);
        Assert.True(Evaluator.Evaluate(atMost, over, Now).IsViolated);

        var atLeast = atMost with { Kind = ExpectationKind.FactAtLeast };
        Assert.True(Evaluator.Evaluate(atLeast, under, Now).IsViolated);
        Assert.True(Evaluator.Evaluate(atLeast, over, Now).IsSatisfied);
    }

    [Fact]
    public void A_missing_or_unparseable_fact_is_unknown_rather_than_a_violation()
    {
        var expectation = new Expectation
        {
            Kind = ExpectationKind.FactAtMost,
            Signal = "queue",
            Fact = "depth",
            Threshold = 100,
        };

        var missing = Snapshot(Obs(ObservedState.Healthy, "queue"));
        var garbage = Snapshot(Obs(ObservedState.Healthy, "queue", new() { ["depth"] = "lots" }));

        Assert.Equal(EvaluationStatus.Unknown, Evaluator.Evaluate(expectation, missing, Now).Status);
        Assert.Equal(EvaluationStatus.Unknown, Evaluator.Evaluate(expectation, garbage, Now).Status);
    }

    [Fact]
    public void Fact_equals_compares_exactly()
    {
        var expectation = new Expectation
        {
            Kind = ExpectationKind.FactEquals,
            Signal = "deploy",
            Fact = "version",
            Value = "1.4.2",
        };

        var match = Snapshot(Obs(ObservedState.Healthy, "deploy", new() { ["version"] = "1.4.2" }));
        var drift = Snapshot(Obs(ObservedState.Healthy, "deploy", new() { ["version"] = "1.4.1" }));

        Assert.True(Evaluator.Evaluate(expectation, match, Now).IsSatisfied);
        Assert.True(Evaluator.Evaluate(expectation, drift, Now).IsViolated);
    }

    [Fact]
    public void Every_verdict_cites_the_observation_it_came_from()
    {
        var observation = Obs(ObservedState.Unavailable);
        var verdict = Evaluator.Evaluate(new Expectation { Kind = ExpectationKind.Healthy },
            Snapshot(observation), Now);

        Assert.Equal(observation.Id, verdict.EvidenceObservationId);
        Assert.Equal(observation.ObservedAt, verdict.EvidenceObservedAt);
    }
}
