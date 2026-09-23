using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Relay.Core;
using Relay.Server.Services;
using Relay.Server.Sse;
using Relay.Server.Stores;
using Xunit;

namespace Relay.Server.Tests;

/// <summary>
/// Wires the real server components over in-memory stores and a controllable clock, so tests
/// exercise the actual loop, dispatcher and service code rather than a stand-in.
/// </summary>
public sealed class TestControlPlane
{
    public required MutableTimeProvider Clock { get; init; }
    public required InMemoryControlPlaneStore Control { get; init; }
    public required InMemoryJobStore Jobs { get; init; }
    public required ProjectService Projects { get; init; }
    public required ReliabilityLoop Loop { get; init; }

    /// <summary>Runs one full loop pass: evaluate every policy, then advance every incident.</summary>
    public Task TickAsync() => Loop.RunOnceAsync(CancellationToken.None);
}

public static class TestServiceFactory
{
    public static JobService CreateJobService(InMemoryJobStore store) =>
        new(store, new SseHub(), NullLogger<JobService>.Instance);

    public static TestControlPlane CreateControlPlane(
        DateTimeOffset? start = null, ReliabilityLoopOptions? options = null)
    {
        var clock = new MutableTimeProvider(start ?? DateTimeOffset.Parse("2026-09-09T12:00:00Z"));
        var control = new InMemoryControlPlaneStore(clock);
        var jobs = new InMemoryJobStore(clock);
        var sse = new SseHub();
        var projects = new ProjectService(control, sse, clock, NullLogger<ProjectService>.Instance);
        var dispatcher = new ActionDispatcher(jobs, control, NullLogger<ActionDispatcher>.Instance);
        var loop = new ReliabilityLoop(control, jobs, projects, dispatcher, clock,
            Options.Create(options ?? new ReliabilityLoopOptions()), NullLogger<ReliabilityLoop>.Instance);

        return new TestControlPlane
        {
            Clock = clock,
            Control = control,
            Jobs = jobs,
            Projects = projects,
            Loop = loop,
        };
    }
}

/// <summary>Shorthand for the fixtures every control-plane test needs.</summary>
public static class TestFixtures
{
    public const string RepoUrl = "https://github.com/acme/widget";

    public static Task<Project> ProjectAsync(TestControlPlane cp, string slug = "widget") =>
        cp.Control.UpsertProjectAsync(new UpsertProjectRequest { Slug = slug }, CancellationToken.None);

    public static Task<Resource> ResourceAsync(
        TestControlPlane cp, Project project, ResourceKind kind, string key,
        Dictionary<string, string>? attributes = null) =>
        cp.Control.UpsertResourceAsync(project.Id, new UpsertResourceRequest
        {
            Kind = kind,
            Key = key,
            Attributes = attributes,
        }, CancellationToken.None);

    public static Task<Resource> RepositoryAsync(TestControlPlane cp, Project project, string key = "widget") =>
        ResourceAsync(cp, project, ResourceKind.Repository, key,
            new Dictionary<string, string> { ["repo_url"] = RepoUrl });

    public static Task<Policy> PolicyAsync(
        TestControlPlane cp, Project project, Expectation expectation, Remediation remediation,
        string name = "service must be healthy", ResourceSelector? target = null) =>
        cp.Control.CreatePolicyAsync(project.Id, new CreatePolicyRequest
        {
            Name = name,
            Target = target ?? new ResourceSelector { Kind = ResourceKind.Service },
            Expectation = expectation,
            Remediation = remediation,
            Severity = Severity.Critical,
        }, CancellationToken.None);


    public static Expectation HealthyExpectation => new() { Kind = ExpectationKind.Healthy };

    public static Remediation AgentTask(
        bool requiresApproval = false, int maxAttempts = 1, long cooldownSeconds = 0,
        long verifyWithinSeconds = 120) => new()
    {
        Action = ActionKind.RunAgentTask,
        Params = new Dictionary<string, string> { ["prompt"] = "restore the service" },
        RequiresApproval = requiresApproval,
        MaxAttempts = maxAttempts,
        CooldownSeconds = cooldownSeconds,
        VerifyWithinSeconds = verifyWithinSeconds,
    };

    public static Remediation RunCommand(
        string command = "true", bool requiresApproval = false, int maxAttempts = 1,
        long verifyWithinSeconds = 120) => new()
    {
        Action = ActionKind.RunCommand,
        Params = new Dictionary<string, string> { ["command"] = command },
        RequiresApproval = requiresApproval,
        MaxAttempts = maxAttempts,
        VerifyWithinSeconds = verifyWithinSeconds,
    };

    public static Task<Observation?> ReportAsync(
        TestControlPlane cp, Project project, Resource resource, ObservedState state,
        string signal = Observation.DefaultSignal, Dictionary<string, string>? facts = null,
        DateTimeOffset? observedAt = null) =>
        ReportCoreAsync(cp, project, resource, state, signal, facts, observedAt);

    private static async Task<Observation?> ReportCoreAsync(
        TestControlPlane cp, Project project, Resource resource, ObservedState state,
        string signal, Dictionary<string, string>? facts, DateTimeOffset? observedAt)
    {
        var recorded = await cp.Projects.RecordObservationAsync(project, new ReportObservationRequest
        {
            Kind = resource.Kind,
            Key = resource.Key,
            Signal = signal,
            State = state,
            Facts = facts,
            Source = "health-checker",
            ObservedAt = observedAt,
        }, CancellationToken.None);
        return recorded?.Observation;
    }

    /// <summary>Drives a job through the real execution state machine to COMPLETED.</summary>
    public static Task CompleteJobAsync(TestControlPlane cp, Guid jobId) =>
        FinishJobAsync(cp, jobId, succeed: true, reason: null);

    /// <summary>Drives a job to FAILED, the way a worker reports a broken run.</summary>
    public static Task FailJobAsync(TestControlPlane cp, Guid jobId, string reason) =>
        FinishJobAsync(cp, jobId, succeed: false, reason);

    private static async Task FinishJobAsync(TestControlPlane cp, Guid jobId, bool succeed, string? reason)
    {
        var ct = CancellationToken.None;
        var claim = await cp.Jobs.TryClaimAsync(Guid.NewGuid(), "test-worker", TimeSpan.FromMinutes(10), ct);
        if (claim is null || claim.Value.Job.Id != jobId)
            throw new InvalidOperationException($"expected to claim job {jobId}");

        var token = claim.Value.LeaseToken;
        await cp.Jobs.TransitionWithLeaseAsync(jobId, token, JobStatus.Running, null, ct);

        if (!succeed)
        {
            await cp.Jobs.FailAsync(jobId, token, reason ?? "failed", null, ct);
            return;
        }

        await cp.Jobs.TransitionWithLeaseAsync(jobId, token, JobStatus.Validating, null, ct);
        await cp.Jobs.CompleteAsync(jobId, token, new JobResult { TestsPassed = true }, null, ct);
    }

    public static async Task<Incident> SingleIncidentAsync(TestControlPlane cp)
    {
        var incidents = await cp.Control.ListIncidentsAsync(null, activeOnly: false, 100, CancellationToken.None);
        return Assert.Single(incidents);
    }

    public static async Task<Incident> RefreshAsync(TestControlPlane cp, Incident incident) =>
        await cp.Control.ResolveIncidentRefAsync(incident.Id.ToString(), CancellationToken.None)
        ?? throw new InvalidOperationException("incident vanished");
}
