using Relay.Core;
using Relay.Server.Stores;
using Xunit;
using static Relay.Server.Tests.TestFixtures;

namespace Relay.Server.Tests;

/// <summary>
/// Durable invariants of the control-plane store. Every one of these is enforced by the
/// Postgres store too — by a partial unique index or a guarded UPDATE rather than by a read
/// followed by a write — so the in-memory store is a faithful stand-in for tests.
/// </summary>
public class ControlPlaneStoreTests
{
    [Fact]
    public async Task A_project_slug_is_idempotent()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var first = await cp.Control.UpsertProjectAsync(new UpsertProjectRequest { Slug = "Widget API" }, default);
        var again = await cp.Control.UpsertProjectAsync(
            new UpsertProjectRequest { Slug = "widget-api", Name = "Widget API" }, default);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("widget-api", again.Slug);
        Assert.Single(await cp.Control.ListProjectsAsync(default));
    }

    [Fact]
    public async Task Resource_attributes_merge_rather_than_replace()
    {
        // Two reporters each know part of the configuration; neither may erase the other's.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);

        await ResourceAsync(cp, project, ResourceKind.Service, "api",
            new Dictionary<string, string> { ["url"] = "https://api.internal", ["team"] = "platform" });
        var merged = await ResourceAsync(cp, project, ResourceKind.Service, "api",
            new Dictionary<string, string> { ["team"] = "payments", ["repo_url"] = RepoUrl });

        Assert.Equal("https://api.internal", merged.Attributes["url"]);
        Assert.Equal("payments", merged.Attributes["team"]);
        Assert.Equal(RepoUrl, merged.Attributes["repo_url"]);
    }

    [Fact]
    public async Task An_observation_can_be_late_but_never_from_the_future()
    {
        // received_at is Relay's clock and is what verification trusts, so a reporter with a
        // fast clock must not be able to stamp evidence ahead of it.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var now = cp.Clock.GetUtcNow();

        var late = await ReportAsync(cp, project, service, ObservedState.Healthy,
            observedAt: now.AddMinutes(-5));
        Assert.Equal(now.AddMinutes(-5), late!.ObservedAt);
        Assert.Equal(now, late.ReceivedAt);

        var forged = await ReportAsync(cp, project, service, ObservedState.Healthy,
            observedAt: now.AddHours(1));
        Assert.Equal(now, forged!.ObservedAt);
        Assert.Equal(now, forged.ReceivedAt);
    }

    [Fact]
    public async Task Observations_about_an_undeclared_resource_are_refused()
    {
        // Relay will not invent resources from a reporter's typo: a resource nobody declared
        // is one no policy watches and no human owns.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);

        var recorded = await cp.Projects.RecordObservationAsync(project, new ReportObservationRequest
        {
            Kind = ResourceKind.Service,
            Key = "typo",
            State = ObservedState.Unavailable,
        }, default);

        Assert.Null(recorded);
    }

    [Fact]
    public async Task Derived_health_is_the_worst_of_the_latest_signals()
    {
        // A green health check must not mask a red queue-depth signal.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");

        await ReportAsync(cp, project, service, ObservedState.Healthy);
        Assert.Equal(ResourceHealth.Healthy, (await Refresh(cp, service)).Health);

        await ReportAsync(cp, project, service, ObservedState.Degraded, signal: "queue");
        var degraded = await Refresh(cp, service);
        Assert.Equal(ResourceHealth.Degraded, degraded.Health);
        Assert.Contains("queue", degraded.HealthReason);

        cp.Clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(cp, project, service, ObservedState.Healthy, signal: "queue");
        Assert.Equal(ResourceHealth.Healthy, (await Refresh(cp, service)).Health);
    }

    [Fact]
    public async Task A_snapshot_holds_only_the_latest_observation_per_signal()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");

        await ReportAsync(cp, project, service, ObservedState.Unavailable);
        cp.Clock.Advance(TimeSpan.FromSeconds(1));
        await ReportAsync(cp, project, service, ObservedState.Healthy);
        await ReportAsync(cp, project, service, ObservedState.Degraded, signal: "queue");

        var snapshot = await cp.Control.SnapshotAsync(service.Id, default);
        Assert.Equal(2, snapshot.All.Count);
        Assert.Equal(ObservedState.Healthy, snapshot.Latest("health")!.State);
        Assert.Equal(ObservedState.Degraded, snapshot.Latest("queue")!.State);

        // The history is still append-only underneath.
        Assert.Equal(3, (await cp.Control.ListObservationsAsync(service.Id, 10, default)).Count);
    }

    [Fact]
    public async Task Only_one_incident_per_policy_and_resource_can_be_active()
    {
        // The invariant that keeps a flapping resource from producing an incident storm.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation,
            new Remediation { Action = ActionKind.Notify });

        var spec = new NewIncident
        {
            ProjectId = project.Id,
            Policy = policy,
            Resource = service,
            Summary = "service is unavailable",
        };

        var first = await cp.Control.OpenIncidentAsync(spec, default);
        Assert.NotNull(first);
        Assert.Null(await cp.Control.OpenIncidentAsync(spec, default));

        // Escalated still counts as active: it is unresolved, just not Relay's to fix.
        await cp.Control.EscalateIncidentAsync(first!.Id, IncidentStatus.Open, "needs a human", default);
        Assert.Null(await cp.Control.OpenIncidentAsync(spec, default));

        await cp.Control.ResolveIncidentAsync(
            first.Id, IncidentStatus.Escalated, "recovered_externally", "healthy again", default);
        var second = await cp.Control.OpenIncidentAsync(spec, default);
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second!.Id);
    }

    [Fact]
    public async Task Incident_transitions_only_apply_from_the_status_the_caller_expected()
    {
        // The compare-and-swap that lets several server replicas run the loop at once.
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask());

        var incident = (await cp.Control.OpenIncidentAsync(new NewIncident
        {
            ProjectId = project.Id,
            Policy = policy,
            Resource = service,
            Summary = "down",
        }, default))!;

        var action = await cp.Control.CreateActionAsync(new NewAction
        {
            ProjectId = project.Id,
            IncidentId = incident.Id,
            Kind = ActionKind.RunAgentTask,
            Attempt = 1,
            Reason = "down",
            RequiresApproval = false,
        }, default);

        // Two replicas race; exactly one wins.
        Assert.NotNull(await cp.Control.BeginActingAsync(incident.Id, IncidentStatus.Open, action.Id, 1, default));
        Assert.Null(await cp.Control.BeginActingAsync(incident.Id, IncidentStatus.Open, action.Id, 1, default));

        // And an illegal move is refused even with the right expectation.
        Assert.Null(await cp.Control.ResolveIncidentAsync(
            incident.Id, IncidentStatus.Acting, "verified", "looks fine", default));
    }

    [Fact]
    public async Task Action_transitions_are_guarded_the_same_way()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask());
        var incident = (await cp.Control.OpenIncidentAsync(new NewIncident
        {
            ProjectId = project.Id, Policy = policy, Resource = service, Summary = "down",
        }, default))!;

        var action = await cp.Control.CreateActionAsync(new NewAction
        {
            ProjectId = project.Id,
            IncidentId = incident.Id,
            Kind = ActionKind.RunAgentTask,
            Attempt = 1,
            Reason = "down",
            RequiresApproval = true,
        }, default);

        // Execution requires an approval on the record.
        Assert.Null(await cp.Control.BeginExecutionAsync(action.Id, null, default));

        Assert.NotNull(await cp.Control.ApproveActionAsync(action.Id, "stive", default));
        Assert.Null(await cp.Control.ApproveActionAsync(action.Id, "someone-else", default));
        Assert.Null(await cp.Control.RejectActionAsync(action.Id, "someone-else", null, default));

        Assert.NotNull(await cp.Control.BeginExecutionAsync(action.Id, null, default));
        var settled = await cp.Control.SettleActionAsync(action.Id, succeeded: true, null, default);
        Assert.Equal(ActionStatus.Succeeded, settled!.Status);
        Assert.Null(await cp.Control.SettleActionAsync(action.Id, succeeded: false, "too late", default));
    }

    [Fact]
    public async Task The_incident_log_is_append_only_and_densely_sequenced()
    {
        var cp = TestServiceFactory.CreateControlPlane();
        var project = await ProjectAsync(cp);
        var service = await ResourceAsync(cp, project, ResourceKind.Service, "api");
        var policy = await PolicyAsync(cp, project, HealthyExpectation, AgentTask());
        var incident = (await cp.Control.OpenIncidentAsync(new NewIncident
        {
            ProjectId = project.Id, Policy = policy, Resource = service, Summary = "down",
        }, default))!;

        await cp.Projects.LogAsync(incident.Id, [IncidentEvent.Of(EventKind.Log, "one")], default);
        await cp.Projects.LogAsync(incident.Id,
            [IncidentEvent.Of(EventKind.Log, "two"), IncidentEvent.Of(EventKind.Log, "three")], default);

        var all = await cp.Control.GetIncidentEventsAsync(incident.Id, 0, 100, default);
        Assert.Equal([1, 2, 3], all.Select(e => e.Seq));
        Assert.Equal(["one", "two", "three"], all.Select(e => e.Message));

        // Replay from a cursor, the way an SSE client reconnects.
        var tail = await cp.Control.GetIncidentEventsAsync(incident.Id, 1, 100, default);
        Assert.Equal(["two", "three"], tail.Select(e => e.Message));
    }

    private static async Task<Resource> Refresh(TestControlPlane cp, Resource resource) =>
        await cp.Control.GetResourceAsync(resource.Id, default)
        ?? throw new InvalidOperationException("resource vanished");
}
