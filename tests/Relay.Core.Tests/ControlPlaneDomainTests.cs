using Relay.Core;
using Xunit;

namespace Relay.Core.Tests;

public class IncidentStateMachineTests
{
    [Fact]
    public void The_happy_path_is_observe_act_verify_resolve()
    {
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Open, IncidentStatus.Acting));
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Acting, IncidentStatus.Verifying));
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Verifying, IncidentStatus.Resolved));
    }

    [Fact]
    public void An_action_in_flight_must_land_before_the_incident_can_close()
    {
        // Resolving out of ACTING would leave an intervention running against a closed
        // incident, which no audit log could explain afterwards.
        Assert.False(IncidentStateMachine.CanTransition(IncidentStatus.Acting, IncidentStatus.Resolved));
    }

    [Fact]
    public void Acting_cannot_be_reached_without_a_decision_or_an_approval()
    {
        Assert.False(IncidentStateMachine.CanTransition(IncidentStatus.Verifying, IncidentStatus.Acting));
        Assert.False(IncidentStateMachine.CanTransition(IncidentStatus.Escalated, IncidentStatus.Acting));

        // Retries route back through OPEN so cool-down, approval and the attempt budget are
        // applied by the single place that owns those rules.
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Verifying, IncidentStatus.Open));
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Acting, IncidentStatus.Open));
        Assert.True(IncidentStateMachine.CanTransition(IncidentStatus.Escalated, IncidentStatus.Open));
    }

    [Fact]
    public void An_incident_can_recover_without_relay_from_any_waiting_state()
    {
        foreach (var from in new[] { IncidentStatus.Open, IncidentStatus.AwaitingApproval, IncidentStatus.Escalated })
            Assert.True(IncidentStateMachine.CanTransition(from, IncidentStatus.Resolved));
    }

    [Fact]
    public void Resolved_is_the_only_terminal_status()
    {
        Assert.Empty(IncidentStateMachine.Next(IncidentStatus.Resolved));
        Assert.True(IncidentStatus.Resolved.IsTerminal());

        // An escalated incident is still Relay's to track — it is just not Relay's to fix.
        Assert.False(IncidentStatus.Escalated.IsTerminal());
        Assert.NotEmpty(IncidentStateMachine.Next(IncidentStatus.Escalated));
    }

    [Fact]
    public void Invalid_transitions_are_rejected_by_name()
    {
        var ex = Assert.Throws<DomainException>(
            () => IncidentStateMachine.Validate(IncidentStatus.Resolved, IncidentStatus.Open));
        Assert.Contains("resolved -> open", ex.Message);
    }
}

public class ActionStateMachineTests
{
    [Fact]
    public void Every_action_passes_through_an_approval()
    {
        // Even blanket policy authority is recorded as an approval, so the audit log always
        // answers "who allowed this".
        Assert.False(ActionStateMachine.CanTransition(ActionStatus.Proposed, ActionStatus.Executing));
        Assert.True(ActionStateMachine.CanTransition(ActionStatus.Proposed, ActionStatus.Approved));
        Assert.True(ActionStateMachine.CanTransition(ActionStatus.Approved, ActionStatus.Executing));
    }

    [Fact]
    public void Execution_can_only_end_in_succeeded_or_failed()
    {
        Assert.Equal(
            new[] { ActionStatus.Succeeded, ActionStatus.Failed },
            ActionStateMachine.Next(ActionStatus.Executing).ToArray());
    }

    [Fact]
    public void Settled_actions_are_immutable()
    {
        foreach (var status in new[] { ActionStatus.Succeeded, ActionStatus.Failed, ActionStatus.Rejected })
            Assert.Empty(ActionStateMachine.Next(status));
    }
}

public class PolicyValidationTests
{
    private static Remediation AgentTask(Dictionary<string, string>? parameters = null) => new()
    {
        Action = ActionKind.RunAgentTask,
        Params = parameters ?? new Dictionary<string, string> { ["prompt"] = "fix it" },
    };

    [Fact]
    public void An_agent_task_without_a_prompt_is_rejected_at_authoring_time()
    {
        // Discovering an unrunnable remediation during an outage is the worst possible moment.
        var ex = Assert.Throws<DomainException>(() => AgentTask([]).Validated());
        Assert.Contains("prompt", ex.Message);
    }

    [Fact]
    public void Notify_needs_no_prompt_and_has_nothing_to_verify()
    {
        var notify = new Remediation { Action = ActionKind.Notify }.Validated();
        Assert.False(notify.IsVerifiable);
        Assert.True(AgentTask().Validated().IsVerifiable);
    }

    [Theory]
    [InlineData(0, 300, 120)]
    [InlineData(1, -1, 120)]
    [InlineData(1, 300, 0)]
    public void Nonsensical_remediation_limits_are_rejected(int maxAttempts, long cooldown, long verifyWithin)
    {
        var remediation = AgentTask() with
        {
            MaxAttempts = maxAttempts,
            CooldownSeconds = cooldown,
            VerifyWithinSeconds = verifyWithin,
        };
        Assert.Throws<DomainException>(() => remediation.Validated());
    }

    [Fact]
    public void A_fresh_expectation_without_a_max_age_asserts_nothing()
    {
        Assert.Throws<DomainException>(() =>
            new Expectation { Kind = ExpectationKind.Fresh }.Validated());
    }

    [Theory]
    [InlineData(ExpectationKind.FactAtMost)]
    [InlineData(ExpectationKind.FactAtLeast)]
    public void Threshold_expectations_need_both_a_fact_and_a_threshold(ExpectationKind kind)
    {
        Assert.Throws<DomainException>(() => new Expectation { Kind = kind, Fact = "depth" }.Validated());
        Assert.Throws<DomainException>(() => new Expectation { Kind = kind, Threshold = 10 }.Validated());
        new Expectation { Kind = kind, Fact = "depth", Threshold = 10 }.Validated();
    }

    [Fact]
    public void A_selector_with_no_key_matches_every_resource_of_its_kind()
    {
        var all = new ResourceSelector { Kind = ResourceKind.Service };
        var one = new ResourceSelector { Kind = ResourceKind.Service, Key = "api" };

        var api = Resource(ResourceKind.Service, "api");
        var worker = Resource(ResourceKind.Service, "worker");
        var repo = Resource(ResourceKind.Repository, "api");

        Assert.True(all.Matches(api));
        Assert.True(all.Matches(worker));
        Assert.False(all.Matches(repo));

        Assert.True(one.Matches(api));
        Assert.False(one.Matches(worker));
    }

    private static Resource Resource(ResourceKind kind, string key) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Kind = kind,
        Key = key,
    };

    [Theory]
    [InlineData("Checkout API", "checkout-api")]
    [InlineData("  billing  ", "billing")]
    public void Project_slugs_are_normalized(string input, string expected) =>
        Assert.Equal(expected, Project.NormalizeSlug(input));

    [Theory]
    [InlineData("")]
    [InlineData("has/slash")]
    [InlineData("has.dot")]
    public void Ambiguous_project_slugs_are_rejected(string input) =>
        Assert.Throws<DomainException>(() => Project.NormalizeSlug(input));
}
