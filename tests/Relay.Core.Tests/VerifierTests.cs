using Relay.Core;
using Xunit;

namespace Relay.Core.Tests;

/// <summary>
/// Whether an intervention worked. The rule under test is the one that separates Relay from a
/// script that runs a fix and declares victory: only evidence Relay received after the action
/// finished may resolve an incident.
/// </summary>
public class VerifierTests
{
    private static readonly DateTimeOffset ActionFinished = DateTimeOffset.Parse("2026-09-09T12:00:00Z");
    private static readonly DateTimeOffset Deadline = ActionFinished.AddMinutes(2);

    private static readonly Expectation Healthy = new() { Kind = ExpectationKind.Healthy };

    private static ObservationSnapshot Snapshot(
        ObservedState state, DateTimeOffset receivedAt, DateTimeOffset? observedAt = null) =>
        new([new Observation
        {
            Id = 1,
            ProjectId = Guid.NewGuid(),
            ResourceId = Guid.NewGuid(),
            State = state,
            Source = "health-checker",
            ObservedAt = observedAt ?? receivedAt,
            ReceivedAt = receivedAt,
        }]);

    [Fact]
    public void Fresh_healthy_evidence_passes_verification()
    {
        var result = Verifier.Verify(Healthy,
            Snapshot(ObservedState.Healthy, ActionFinished.AddSeconds(10)),
            ActionFinished, Deadline, ActionFinished.AddSeconds(10));

        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.Equal(ActionOutcome.Verified, result.ToOutcome());
    }

    [Fact]
    public void Evidence_from_before_the_action_cannot_pass_verification()
    {
        // The core invariant. A health check that went green a minute before the fix ran says
        // nothing about whether the fix worked, so it must not close the incident.
        var result = Verifier.Verify(Healthy,
            Snapshot(ObservedState.Healthy, ActionFinished.AddSeconds(-30)),
            ActionFinished, Deadline, ActionFinished.AddSeconds(5));

        Assert.Equal(VerificationStatus.Pending, result.Status);
        Assert.Null(result.ToOutcome());
    }

    [Fact]
    public void No_evidence_at_all_stays_pending_until_the_deadline_then_times_out()
    {
        var pending = Verifier.Verify(Healthy, ObservationSnapshot.Empty,
            ActionFinished, Deadline, ActionFinished.AddSeconds(30));
        Assert.Equal(VerificationStatus.Pending, pending.Status);

        var timedOut = Verifier.Verify(Healthy, ObservationSnapshot.Empty,
            ActionFinished, Deadline, Deadline);
        Assert.Equal(VerificationStatus.TimedOut, timedOut.Status);
        Assert.Equal(ActionOutcome.VerificationTimedOut, timedOut.ToOutcome());
    }

    [Fact]
    public void Fresh_evidence_that_is_still_bad_fails_verification_immediately()
    {
        // No point waiting out the deadline: the resource has spoken since the fix ran.
        var result = Verifier.Verify(Healthy,
            Snapshot(ObservedState.Unavailable, ActionFinished.AddSeconds(3)),
            ActionFinished, Deadline, ActionFinished.AddSeconds(3));

        Assert.Equal(VerificationStatus.Failed, result.Status);
        Assert.Equal(ActionOutcome.VerificationFailed, result.ToOutcome());
        Assert.Contains("still violated", result.Summary);
    }

    [Fact]
    public void A_reporter_with_a_fast_clock_cannot_forge_fresh_evidence()
    {
        // observed_at claims the future; received_at is Relay's own clock and says the
        // observation predates the action. Verification trusts received_at.
        var result = Verifier.Verify(Healthy,
            Snapshot(ObservedState.Healthy,
                receivedAt: ActionFinished.AddSeconds(-1),
                observedAt: ActionFinished.AddHours(1)),
            ActionFinished, Deadline, ActionFinished.AddSeconds(5));

        Assert.Equal(VerificationStatus.Pending, result.Status);
    }

    [Fact]
    public void Fresh_but_inconclusive_evidence_waits_rather_than_guessing()
    {
        var expectation = new Expectation
        {
            Kind = ExpectationKind.FactAtMost,
            Fact = "lag_seconds",
            Threshold = 10,
        };

        var pending = Verifier.Verify(expectation,
            Snapshot(ObservedState.Healthy, ActionFinished.AddSeconds(1)),
            ActionFinished, Deadline, ActionFinished.AddSeconds(1));
        Assert.Equal(VerificationStatus.Pending, pending.Status);

        var timedOut = Verifier.Verify(expectation,
            Snapshot(ObservedState.Healthy, ActionFinished.AddSeconds(1)),
            ActionFinished, Deadline, Deadline);
        Assert.Equal(VerificationStatus.TimedOut, timedOut.Status);
        Assert.Contains("conclusive", timedOut.Summary);
    }
}
