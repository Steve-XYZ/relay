using Relay.Core;
using Xunit;

namespace Relay.Core.Tests;

public class JobStateMachineTests
{
    [Fact]
    public void Happy_path_is_allowed()
    {
        Assert.True(JobStateMachine.CanTransition(JobStatus.Queued, JobStatus.Preparing));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Preparing, JobStatus.Running));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Running, JobStatus.Validating));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Validating, JobStatus.Completed));
    }

    [Theory]
    [InlineData(JobStatus.Preparing, JobStatus.Interrupted)]
    [InlineData(JobStatus.Running, JobStatus.Interrupted)]
    [InlineData(JobStatus.Validating, JobStatus.Interrupted)]
    public void Crash_paths_lead_to_interrupted(JobStatus from, JobStatus to)
        => Assert.True(JobStateMachine.CanTransition(from, to));

    [Fact]
    public void Interrupted_recovers_through_recovering()
    {
        Assert.True(JobStateMachine.CanTransition(JobStatus.Interrupted, JobStatus.Recovering));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Recovering, JobStatus.Queued));
        Assert.True(JobStateMachine.CanTransition(JobStatus.Recovering, JobStatus.Preparing));
    }

    [Fact]
    public void Cannot_skip_states_or_resurrect_terminal_jobs()
    {
        Assert.False(JobStateMachine.CanTransition(JobStatus.Queued, JobStatus.Completed));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Queued, JobStatus.Running));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Running, JobStatus.Queued));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Completed, JobStatus.Running));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Failed, JobStatus.Queued));
        Assert.False(JobStateMachine.CanTransition(JobStatus.Cancelled, JobStatus.Queued));
    }

    [Fact]
    public void Validate_throws_on_invalid_transition()
    {
        var ex = Assert.Throws<InvalidTransitionException>(
            () => JobStateMachine.Validate(JobStatus.Queued, JobStatus.Completed));
        Assert.Equal(JobStatus.Queued, ex.From);
        Assert.Equal(JobStatus.Completed, ex.To);
    }

    [Fact]
    public void Terminal_statuses_have_no_outgoing_transitions()
    {
        foreach (var status in new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled })
        {
            Assert.Empty(JobStateMachine.Next(status));
            Assert.True(status.IsTerminal());
        }
    }
}

public class BudgetMeterTests
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    [Fact]
    public void No_budget_never_exceeds()
    {
        var usage = new Usage { TokensIn = 999_999, TokensOut = 999_999, CostUsd = 100m };
        var verdict = BudgetMeter.Evaluate(usage, budget: null, elapsed: TimeSpan.FromHours(5));
        Assert.False(verdict.Exceeded);
    }

    [Fact]
    public void Token_budget_trips_when_total_tokens_surpass_limit()
    {
        var budget = new Budget { MaxTokens = 120_000 };
        var under = new Usage { TokensIn = 50_000, TokensOut = 60_000 };
        var over = new Usage { TokensIn = 100_000, TokensOut = 20_001 };

        Assert.False(BudgetMeter.Evaluate(under, budget, Zero).Exceeded);
        var verdict = BudgetMeter.Evaluate(over, budget, Zero);
        Assert.True(verdict.Exceeded);
        Assert.Contains("token", verdict.Reason);
    }

    [Fact]
    public void Runtime_budget_trips_at_limit()
    {
        var budget = new Budget { MaxRuntimeSeconds = (long)TimeSpan.FromMinutes(30).TotalSeconds };
        Assert.False(BudgetMeter.Evaluate(new Usage(), budget, TimeSpan.FromMinutes(29)).Exceeded);
        var verdict = BudgetMeter.Evaluate(new Usage(), budget, TimeSpan.FromMinutes(30));
        Assert.True(verdict.Exceeded);
        Assert.Contains("runtime", verdict.Reason);
    }

    [Fact]
    public void Cost_budget_trips_when_cost_surpasses_limit()
    {
        var budget = new Budget { MaxCostUsd = 2.00m };
        Assert.False(BudgetMeter.Evaluate(new Usage { CostUsd = 1.99m }, budget, Zero).Exceeded);
        var verdict = BudgetMeter.Evaluate(new Usage { CostUsd = 2.01m }, budget, Zero);
        Assert.True(verdict.Exceeded);
        Assert.Contains("cost", verdict.Reason);
    }

    [Fact]
    public void First_violation_wins_deterministically()
    {
        var budget = new Budget
        {
            MaxTokens = 100,
            MaxCostUsd = 0.10m,
            MaxRuntimeSeconds = 60,
        };
        var usage = new Usage { TokensIn = 200, CostUsd = 5.00m };
        var verdict = BudgetMeter.Evaluate(usage, budget, TimeSpan.FromMinutes(10));
        Assert.True(verdict.Exceeded);
        Assert.Contains("runtime", verdict.Reason); // runtime is checked first by design
    }
}
