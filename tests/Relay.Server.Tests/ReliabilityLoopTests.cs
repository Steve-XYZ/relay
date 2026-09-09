using Relay.Core;
using Xunit;
using static Relay.Server.Tests.TestFixtures;

namespace Relay.Server.Tests;

/// <summary>
/// The Relay loop end to end over in-memory stores and a frozen clock:
/// observe → understand → compare with desired state → act → verify → remember.
///
/// Every test drives real ticks of <see cref="Relay.Server.Services.ReliabilityLoop"/>, so a
/// change that breaks the loop's ordering or its guarantees fails here rather than in
/// production.
/// </summary>
public class ReliabilityLoopTests
{
    [Fact]
    public async Task A_healthy_project_produces_no_incidents()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();

        Assert.Empty(await cp.Control.ListIncidentsAsync(null, activeOnly: false, 10, default));
    }

    [Fact]
    public async Task A_resource_nobody_has_reported_on_does_not_raise_an_incident()
    {
        // Unknown is not unhealthy. Opening an incident for every freshly declared resource
        // would make onboarding a project an outage.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await cp.TickAsync();

        Assert.Empty(await cp.Control.ListIncidentsAsync(null, activeOnly: false, 10, default));
    }

    [Fact]
    public async Task Divergence_opens_an_incident_and_dispatches_an_agent_task()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.Acting, incident.Status);
        Assert.Equal(policy.Id, incident.PolicyId);
        Assert.Equal(Severity.Critical, incident.Severity);
        Assert.Equal(1, incident.Attempt);
        Assert.StartsWith("I", incident.ShortId);

        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        Assert.Equal(ActionKind.RunAgentTask, action.Kind);
        Assert.Equal(ActionStatus.Executing, action.Status);
        Assert.Equal($"policy:{policy.Name}", action.ApprovedBy);
        Assert.NotNull(action.ExecutionJobId);

        // The job is execution machinery underneath the action, and knows it.
        var job = await cp.Jobs.ResolveAsync(action.ExecutionJobId!.Value.ToString(), default);
        Assert.NotNull(job);
        Assert.Equal(JobOrigin.Policy, job!.Origin);
        Assert.Equal(project.Id, job.ProjectId);
        Assert.Equal(RepoUrl, job.RepoUrl);
        Assert.Contains("restore the service", job.Prompt);
        Assert.Contains(incident.ShortId, job.Prompt);
    }

    [Fact]
    public async Task A_successful_job_does_not_resolve_the_incident_on_its_own()
    {
        // The single most important behaviour in the product. A job exiting zero is a claim
        // about the job; only an observation received afterwards is evidence about the resource.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        await CompleteJobAsync(cp, action.ExecutionJobId!.Value);

        await cp.TickAsync();
        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Verifying, incident.Status);
        Assert.NotNull(incident.VerifyEvidenceAfter);
        Assert.NotNull(incident.VerifyDeadlineAt);

        // Ticks alone change nothing: there is no evidence newer than the action.
        await cp.TickAsync();
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);
        Assert.Null((await cp.Control.GetActionAsync(action.Id, default))!.Outcome);
    }

    [Fact]
    public async Task The_audit_log_records_events_not_polling()
    {
        // The loop runs every few seconds forever. If every tick wrote what it saw, the log
        // would be unreadable and useless as an audit trail.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        await CompleteJobAsync(cp, action.ExecutionJobId!.Value);
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);

        // The last observation still says unavailable, so both passes have something to say on
        // every tick — and neither of them should say it more than once.
        var quiescent = (await cp.Control.GetIncidentEventsAsync(incident.Id, 0, 500, default)).Count;
        for (var i = 0; i < 10; i++)
        {
            cp.Clock.Advance(TimeSpan.FromSeconds(1));
            await cp.TickAsync();
        }

        Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);
        Assert.Equal(quiescent, (await cp.Control.GetIncidentEventsAsync(incident.Id, 0, 500, default)).Count);
    }

    [Fact]
    public async Task Fresh_healthy_evidence_after_the_action_resolves_the_incident_as_verified()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        await CompleteJobAsync(cp, action.ExecutionJobId!.Value);
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);

        cp.Clock.Advance(TimeSpan.FromSeconds(5));
        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal("verified", incident.Resolution);
        Assert.NotNull(incident.ResolvedAt);

        var settled = await cp.Control.GetActionAsync(action.Id, default);
        Assert.Equal(ActionStatus.Succeeded, settled!.Status);
        Assert.Equal(ActionOutcome.Verified, settled.Outcome);

        // The audit log carries the whole story, in order.
        var log = await cp.Control.GetIncidentEventsAsync(incident.Id, 0, 100, default);
        Assert.Contains(log, e => e.Message == "incident_opened");
        Assert.Contains(log, e => e.Kind == EventKind.Verification.ToWire() && e.Message.StartsWith("verified:"));
        Assert.Contains(log, e => e.Message == "incident_resolved");
        Assert.Equal(Enumerable.Range(1, log.Count).Select(i => (long)i), log.Select(e => e.Seq));
    }

    [Fact]
    public async Task Evidence_no_newer_than_the_action_cannot_close_the_incident()
    {
        // Clock frozen at the instant the action finished: an observation received then could
        // have been taken before the fix landed, so it is not evidence about the fix.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        await CompleteJobAsync(cp, action.ExecutionJobId!.Value);
        await cp.TickAsync();

        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);

        cp.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Resolved, (await RefreshAsync(cp, incident)).Status);
    }

    [Fact]
    public async Task Verification_that_times_out_retries_within_budget_then_escalates()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation,
            AgentTask(maxAttempts: 2, verifyWithinSeconds: 60));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            incident = await RefreshAsync(cp, incident);
            Assert.Equal(IncidentStatus.Acting, incident.Status);
            Assert.Equal(attempt, incident.Attempt);

            var action = (await cp.Control.ListActionsAsync(incident.Id, default))[^1];
            await CompleteJobAsync(cp, action.ExecutionJobId!.Value);
            await cp.TickAsync();
            Assert.Equal(IncidentStatus.Verifying, (await RefreshAsync(cp, incident)).Status);

            // Nothing reports back before the deadline.
            cp.Clock.Advance(TimeSpan.FromSeconds(61));
            await cp.TickAsync();

            Assert.Equal(ActionOutcome.VerificationTimedOut,
                (await cp.Control.GetActionAsync(action.Id, default))!.Outcome);
        }

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        Assert.Contains("exhausted", incident.EscalationReason);
        Assert.Equal(2, (await cp.Control.ListActionsAsync(incident.Id, default)).Count);
    }

    [Fact]
    public async Task A_failed_execution_is_a_failed_attempt_not_a_verification_problem()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask(maxAttempts: 1));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));

        await FailJobAsync(cp, action.ExecutionJobId!.Value, "tests failed (exit 1)");
        await cp.TickAsync();

        var settled = await cp.Control.GetActionAsync(action.Id, default);
        Assert.Equal(ActionStatus.Failed, settled!.Status);
        Assert.Contains("tests failed", settled.FailureReason);
        Assert.Null(settled.Outcome); // nothing was verified, so nothing is claimed

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        Assert.Contains("tests failed", incident.EscalationReason);
    }

    [Fact]
    public async Task A_cooldown_paces_retries()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation,
            AgentTask(maxAttempts: 3, cooldownSeconds: 600));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        var first = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));

        await FailJobAsync(cp, first.ExecutionJobId!.Value, "boom");
        await cp.TickAsync();

        // Back to OPEN, but the cool-down has not elapsed, so no second attempt yet.
        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Open, incident.Status);
        await cp.TickAsync();
        Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));

        cp.Clock.Advance(TimeSpan.FromSeconds(601));
        await cp.TickAsync();
        Assert.Equal(2, (await cp.Control.ListActionsAsync(incident.Id, default)).Count);
        Assert.Equal(2, (await RefreshAsync(cp, incident)).Attempt);
    }

    [Fact]
    public async Task Approval_required_means_relay_waits_and_says_what_it_wants_to_do()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask(requiresApproval: true));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        await cp.TickAsync(); // extra ticks must not sneak past the gate

        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.AwaitingApproval, incident.Status);

        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        Assert.Equal(ActionStatus.Proposed, action.Status);
        Assert.Null(action.ExecutionJobId);
        Assert.Contains(action, await cp.Control.ListPendingApprovalsAsync(project.Id, default));

        await cp.Projects.ApproveActionAsync(action.Id, "stive", default);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Acting, incident.Status);
        var dispatched = await cp.Control.GetActionAsync(action.Id, default);
        Assert.Equal("stive", dispatched!.ApprovedBy);
        Assert.NotNull(dispatched.ExecutionJobId);
    }

    [Fact]
    public async Task A_rejected_action_escalates_instead_of_improvising()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation,
            AgentTask(requiresApproval: true, maxAttempts: 3));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));

        await cp.Projects.RejectActionAsync(action.Id, "stive", "we are failing over by hand", default);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        Assert.Contains("by hand", incident.EscalationReason);
        // Rejection is not a failed attempt to retry around.
        Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
    }

    [Fact]
    public async Task An_incident_that_recovers_before_relay_acts_is_recorded_as_self_healed()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask(requiresApproval: true));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.AwaitingApproval, incident.Status);

        cp.Clock.Advance(TimeSpan.FromSeconds(30));
        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal("self_healed", incident.Resolution);
    }

    [Fact]
    public async Task An_escalated_incident_closes_when_a_human_fixes_the_resource()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, new Remediation
        {
            Action = ActionKind.Notify,
            Params = new Dictionary<string, string> { ["message"] = "page the on-call" },
        });

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        Assert.Equal("page the on-call", incident.EscalationReason);

        // Notify is honest about having nothing to verify.
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        Assert.Equal(ActionKind.Notify, action.Kind);
        Assert.Equal(ActionStatus.Succeeded, action.Status);
        Assert.Equal(ActionOutcome.NotApplicable, action.Outcome);
        Assert.Null(action.ExecutionJobId);

        cp.Clock.Advance(TimeSpan.FromMinutes(20));
        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal("recovered_externally", incident.Resolution);
    }

    [Fact]
    public async Task An_undispatchable_action_fails_loudly_instead_of_hanging()
    {
        // No repository resource and no repo_url param: the agent task cannot run at all.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask(maxAttempts: 1));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var incident = await SingleIncidentAsync(cp);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        Assert.Equal(ActionStatus.Failed, action.Status);
        Assert.Contains("repository", action.FailureReason);

        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        var log = await cp.Control.GetIncidentEventsAsync(incident.Id, 0, 100, default);
        Assert.Contains(log, e => e.Kind == EventKind.Error.ToWire());
    }

    [Fact]
    public async Task A_stale_scheduled_process_is_a_violation_and_a_fresh_run_resolves_it()
    {
        var cp = TestServiceFactory.CreateControlPlane(start: DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var project = await ProjectAsync(cp);
        var etl = await ResourceAsync(cp, project, ResourceKind.ScheduledProcess, "nightly-etl");
        await PolicyAsync(cp, project,
            new Expectation { Kind = ExpectationKind.Fresh, Signal = "last_run", MaxAgeSeconds = 86_400 },
            new Remediation { Action = ActionKind.Notify },
            name: "nightly etl must run daily",
            target: new ResourceSelector { Kind = ResourceKind.ScheduledProcess });

        await ReportAsync(cp, project, etl, ObservedState.Healthy, signal: "last_run");
        await cp.TickAsync();
        Assert.Empty(await cp.Control.ListIncidentsAsync(null, activeOnly: false, 10, default));

        cp.Clock.Advance(TimeSpan.FromHours(30));
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.Escalated, incident.Status);
        Assert.Contains("last reported", incident.Summary);

        await ReportAsync(cp, project, etl, ObservedState.Healthy, signal: "last_run");
        await cp.TickAsync();
        Assert.Equal(IncidentStatus.Resolved, (await RefreshAsync(cp, incident)).Status);
    }

    [Fact]
    public async Task Disabling_a_policy_closes_the_work_it_caused()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask(requiresApproval: true));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        Assert.True(incident.IsActive);

        await cp.Control.SetPolicyEnabledAsync(policy.Id, enabled: false, default);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal("policy_disabled", incident.Resolution);
    }

    [Fact]
    public async Task Disabling_a_policy_mid_action_settles_the_intervention_before_closing()
    {
        // The state machine forbids resolving around a live intervention, so an incident that
        // is ACTING when its policy is turned off has to settle the action and route through
        // OPEN. Getting this wrong strands the incident in ACTING forever.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();
        var incident = await SingleIncidentAsync(cp);
        Assert.Equal(IncidentStatus.Acting, incident.Status);
        var action = Assert.Single(await cp.Control.ListActionsAsync(incident.Id, default));
        Assert.Equal(ActionStatus.Executing, action.Status);

        await cp.Control.SetPolicyEnabledAsync(policy.Id, enabled: false, default);
        await cp.TickAsync();

        incident = await RefreshAsync(cp, incident);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal("policy_disabled", incident.Resolution);

        var settled = await cp.Control.GetActionAsync(action.Id, default);
        Assert.Equal(ActionStatus.Failed, settled!.Status);
        Assert.Contains("abandoned", settled.FailureReason);
    }

    [Fact]
    public async Task One_policy_can_watch_every_resource_of_a_kind()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var api = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var worker = await ResourceAsync(cp, project, ResourceKind.Service, "worker");
        await ResourceAsync(cp, project, ResourceKind.Database, "primary");
        await PolicyAsync(cp, project, HealthyExpectation, new Remediation { Action = ActionKind.Notify });

        await ReportAsync(cp, project, api, ObservedState.Unavailable);
        await ReportAsync(cp, project, worker, ObservedState.Degraded);
        await cp.TickAsync();

        var incidents = await cp.Control.ListIncidentsAsync(project.Id, activeOnly: true, 10, default);
        Assert.Equal(2, incidents.Count);
        Assert.Equal(
            new[] { "service/api", "service/worker" },
            incidents.Select(i => i.ResourceLabel).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Project_state_answers_the_whole_question_in_one_read()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        await RepositoryAsync(cp, project);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        await PolicyAsync(cp, project, HealthyExpectation, AgentTask(requiresApproval: true));

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        await cp.TickAsync();

        var state = await cp.Projects.GetStateAsync(project, default);
        Assert.Equal(2, state.Resources.Count);                                 // what exists
        Assert.Single(state.Policies);                                          // what should be true
        Assert.Equal(ResourceHealth.Unavailable,
            state.Resources.Single(r => r.Id == service.Id).Health);            // what is actually happening
        Assert.Single(state.ActiveIncidents);                                   // what needs intervention
        Assert.Single(state.PendingApprovals);                                  // what is waiting on a human
    }
}
