using Microsoft.Extensions.Options;
using Relay.Core;
using Relay.Server.Stores;
using Relay.Server.Telemetry;

namespace Relay.Server.Services;

public sealed class ReliabilityLoopOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the loop runs. Each tick advances every active incident by one step.</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>Ceiling on incidents advanced per tick, so one noisy project cannot starve others.</summary>
    public int MaxIncidentsPerTick { get; set; } = 500;
}

/// <summary>
/// Relay's control loop: observe, understand, compare with desired state, act, verify,
/// remember, repeat.
///
///   pass 1  evaluate every (policy, resource) against the latest observations, and open,
///           refresh or resolve incidents accordingly;
///   pass 2  advance every active incident by one step of its state machine.
///
/// The loop owns no state of its own. Everything it decides is read from and written back to
/// the durable store under compare-and-swap, so several server replicas can run it at once and
/// a restart mid-incident loses nothing but time. "Remember" is not a separate pass: it is the
/// incident's own audit log, which is why every decision here is written down before the next
/// one is taken.
/// </summary>
public sealed class ReliabilityLoop : BackgroundService
{
    private readonly IControlPlaneStore _control;
    private readonly IJobStore _jobs;
    private readonly ProjectService _projects;
    private readonly ActionDispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly ReliabilityLoopOptions _options;
    private readonly ILogger<ReliabilityLoop> _logger;

    /// <summary>
    /// Steps one incident may take in a single tick. Settling an action and then entering
    /// verification are two moves; without this an incident would need one tick per move.
    /// </summary>
    private const int MaxStepsPerIncidentPerTick = 4;

    public ReliabilityLoop(
        IControlPlaneStore control,
        IJobStore jobs,
        ProjectService projects,
        ActionDispatcher dispatcher,
        TimeProvider clock,
        IOptions<ReliabilityLoopOptions> options,
        ILogger<ReliabilityLoop> logger)
    {
        _control = control;
        _jobs = jobs;
        _projects = projects;
        _dispatcher = dispatcher;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Reliability loop disabled by configuration");
            return;
        }

        _logger.LogInformation("Reliability loop started (every {Interval}s)", _options.IntervalSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reliability loop tick failed; retrying next tick");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One full pass. Public and side-effect-complete so tests can drive the loop tick by tick
    /// against a controlled clock instead of waiting on a timer.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var activity = RelayMetrics.ActivitySource.StartActivity("relay.loop.tick");

        foreach (var project in await _control.ListProjectsAsync(ct))
            await EvaluateProjectAsync(project, ct);

        // Least-recently-touched first, so the per-tick ceiling cannot starve an old incident
        // behind a burst of new ones.
        var active = await _control.ListIncidentsToAdvanceAsync(_options.MaxIncidentsPerTick, ct);
        foreach (var incident in active)
            await AdvanceIncidentAsync(incident, ct);
    }

    // ---- pass 1: observe, understand, compare ----

    private async Task EvaluateProjectAsync(Project project, CancellationToken ct)
    {
        var resources = await _control.ListResourcesAsync(project.Id, ct);
        if (resources.Count == 0) return;

        var policies = await _control.ListPoliciesAsync(project.Id, enabledOnly: true, ct);
        var now = _clock.GetUtcNow();

        foreach (var policy in policies)
        foreach (var resource in resources.Where(policy.Target.Matches))
        {
            var snapshot = await _control.SnapshotAsync(resource.Id, ct);
            var verdict = Evaluator.Evaluate(policy.Expectation, snapshot, now);
            var active = await _control.FindActiveIncidentAsync(policy.Id, resource.Id, ct);

            // ACTING and VERIFYING belong to pass 2: an action must land before the incident
            // can close, and verification demands evidence that post-dates the action, which is
            // a stricter rule than this pass applies. Leaving those two alone is what stops a
            // stale green observation from closing a real incident — and keeps this pass from
            // narrating its own polling into the audit log.
            if (active is not null && active.Status is IncidentStatus.Acting or IncidentStatus.Verifying)
                continue;

            if (verdict.IsViolated)
            {
                if (active is null)
                    await _projects.OpenIncidentAsync(new NewIncident
                    {
                        ProjectId = project.Id,
                        Policy = policy,
                        Resource = resource,
                        Summary = verdict.Summary,
                    }, verdict, ct);
                else
                    await _projects.RecordVerdictAsync(active, verdict, ct);
                continue;
            }

            if (!verdict.IsSatisfied || active is null) continue;

            var resolution = active.Status == IncidentStatus.Escalated ? "recovered_externally" : "self_healed";
            await _projects.ResolveIncidentAsync(active, resolution, verdict.Summary, ct);
        }
    }

    // ---- pass 2: act, verify, remember ----

    private async Task AdvanceIncidentAsync(Incident incident, CancellationToken ct)
    {
        var current = incident;
        for (var step = 0; step < MaxStepsPerIncidentPerTick; step++)
        {
            if (!await StepAsync(current, ct)) return;

            var refreshed = await _control.ResolveIncidentRefAsync(current.Id.ToString(), ct);
            if (refreshed is null || !refreshed.IsActive) return;
            current = refreshed;
        }
    }

    /// <summary>Advances one incident by a single move. Returns false when there is nothing to do.</summary>
    private async Task<bool> StepAsync(Incident incident, CancellationToken ct)
    {
        // Re-read the policy and resource on every step, so an operator disabling a policy is
        // honored on the spot rather than after the incident finishes what it started.
        var policy = await _control.GetPolicyAsync(incident.PolicyId, ct);
        var resource = policy is null ? null : await _control.GetResourceAsync(incident.ResourceId, ct);

        if (policy is null)
            return await CloseAdministrativelyAsync(incident, "policy_deleted",
                "the policy that opened this incident no longer exists", ct);
        if (!policy.Enabled)
            // Turning a policy off must actually stop the work it caused, otherwise a noisy
            // policy cannot be silenced without deleting its history.
            return await CloseAdministrativelyAsync(incident, "policy_disabled",
                $"policy '{policy.Name}' was disabled", ct);
        // No API deletes a resource today: doing that correctly means deciding what happens
        // to its observation history and the policies targeting it, which is its own change.
        // This branch is a guard so a store-level inconsistency degrades to a closed incident
        // rather than a null dereference inside a background loop.
        if (resource is null)
            return await CloseAdministrativelyAsync(incident, "resource_removed",
                "the resource this incident was about no longer exists", ct);

        return incident.Status switch
        {
            IncidentStatus.Open => await DecideAsync(incident, policy, resource, ct),
            IncidentStatus.AwaitingApproval => await CheckApprovalAsync(incident, policy, resource, ct),
            IncidentStatus.Acting => await CheckExecutionAsync(incident, policy, resource, ct),
            IncidentStatus.Verifying => await VerifyAsync(incident, policy, ct),
            // Escalated incidents wait for reality to change (pass 1 resolves them) or for a
            // human to re-arm them. Relay has said what it knows and what it cannot do.
            _ => false,
        };
    }

    /// <summary>
    /// Closes an incident for a reason that has nothing to do with the resource: its policy or
    /// the resource itself went away. Any unsettled intervention is closed out first, and an
    /// incident in ACTING routes through OPEN rather than resolving directly, so the state
    /// machine's rule against closing around a live intervention still holds.
    /// </summary>
    private async Task<bool> CloseAdministrativelyAsync(
        Incident incident, string resolution, string detail, CancellationToken ct)
    {
        if (await CurrentActionAsync(incident, ct) is { IsSettled: false } action)
            await AbandonActionAsync(action, detail, ct);

        if (incident.Status == IncidentStatus.Acting)
            return await _projects.ReopenIncidentAsync(incident, detail, ct) is not null;

        return await _projects.ResolveIncidentAsync(incident, resolution, detail, ct) is not null;
    }

    private Task AbandonActionAsync(RemediationAction action, string reason, CancellationToken ct) =>
        action.Status == ActionStatus.Executing
            ? _control.SettleActionAsync(action.Id, succeeded: false, $"abandoned: {reason}", ct)
            : _control.SupersedeActionAsync(action.Id, $"abandoned: {reason}", ct);

    // ---- decide ----

    private async Task<bool> DecideAsync(Incident incident, Policy policy, Resource resource, CancellationToken ct)
    {
        var remediation = policy.Remediation;

        if (incident.Attempt >= remediation.MaxAttempts)
            return await _projects.EscalateIncidentAsync(incident,
                $"remediation attempts exhausted ({remediation.MaxAttempts})", ct) is not null;

        var now = _clock.GetUtcNow();
        if (incident.LastAttemptAt is { } last &&
            now - last < TimeSpan.FromSeconds(remediation.CooldownSeconds))
            return false;

        var action = await _control.CreateActionAsync(new NewAction
        {
            ProjectId = incident.ProjectId,
            IncidentId = incident.Id,
            Kind = remediation.Action,
            Attempt = incident.Attempt + 1,
            Reason = incident.Detail ?? incident.Summary,
            RequiresApproval = remediation.RequiresApproval,
            Params = new Dictionary<string, string>(remediation.Params),
        }, ct);

        if (remediation.RequiresApproval)
        {
            if (await _projects.AwaitApprovalAsync(incident, action, ct) is not null) return true;
            await SupersedeAsync(action, "the incident advanced concurrently", ct);
            return false;
        }

        // Even blanket authority is recorded as an approval, so every action in the audit log
        // answers "who allowed this".
        var approved = await _control.ApproveActionAsync(action.Id, $"policy:{policy.Name}", ct);
        if (approved is null) return false;

        if (await _projects.BeginActingAsync(incident, approved, ct) is null)
        {
            await SupersedeAsync(approved, "the incident advanced concurrently", ct);
            return false;
        }

        await DispatchAsync(incident, policy, resource, approved, ct);
        return true;
    }

    /// <summary>
    /// Closes out an action whose incident moved on underneath it, so a lost race leaves an
    /// explained row rather than a mystery one.
    /// </summary>
    private Task SupersedeAsync(RemediationAction action, string reason, CancellationToken ct) =>
        _control.SupersedeActionAsync(action.Id, $"superseded: {reason}", ct);

    // ---- approval gate ----

    private async Task<bool> CheckApprovalAsync(
        Incident incident, Policy policy, Resource resource, CancellationToken ct)
    {
        var action = await CurrentActionAsync(incident, ct);
        if (action is null)
            return await _projects.ReopenIncidentAsync(incident,
                "proposed action is missing; re-deciding", ct) is not null;

        switch (action.Status)
        {
            case ActionStatus.Proposed:
                return false; // still waiting on a human

            case ActionStatus.Approved:
                if (await _projects.BeginActingAsync(incident, action, ct) is null) return false;
                await DispatchAsync(incident, policy, resource, action, ct);
                return true;

            case ActionStatus.Rejected:
                return await _projects.EscalateIncidentAsync(incident,
                    action.FailureReason ?? "remediation was rejected", ct) is not null;

            default:
                await SupersedeAsync(action, $"unexpectedly {action.Status.ToWire()} while awaiting approval", ct);
                return await _projects.ReopenIncidentAsync(incident,
                    $"proposed action is unexpectedly {action.Status.ToWire()}; re-deciding", ct) is not null;
        }
    }

    // ---- execution ----

    private async Task DispatchAsync(
        Incident incident, Policy policy, Resource resource, RemediationAction action, CancellationToken ct)
    {
        DispatchOutcome outcome;
        try
        {
            outcome = await _dispatcher.DispatchAsync(policy, incident, resource, action, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "incident {Incident}: dispatching {Kind} threw",
                incident.ShortId, action.Kind.ToWire());
            outcome = DispatchOutcome.Undispatchable(ex.Message);
        }

        RelayMetrics.ActionsDispatched.Add(1,
            RelayMetrics.Tag("kind", action.Kind.ToWire()),
            RelayMetrics.Tag("started", outcome.Started ? "true" : "false"));

        // The action enters EXECUTING either way: a dispatch that could not even start is a
        // failed attempt and must be visible as one, not silently dropped.
        var executing = await _control.BeginExecutionAsync(action.Id, outcome.ExecutionJobId, ct);
        if (executing is null) return;

        if (!outcome.Started)
        {
            await _control.SettleActionAsync(action.Id, succeeded: false, outcome.FailureReason, ct);
            await _projects.LogAsync(incident.Id,
                [IncidentEvent.Of(EventKind.Error,
                    $"could not dispatch {action.Kind.ToWire()}: {outcome.FailureReason}")], ct);
            return;
        }

        await _projects.LogAsync(incident.Id,
            [IncidentEvent.Of(EventKind.Milestone, $"{action.Kind.ToWire()}_started",
                outcome.ExecutionJobId is { } jobId
                    ? new Dictionary<string, string> { ["execution_job_id"] = jobId.ToString() }
                    : null)], ct);
    }

    private async Task<bool> CheckExecutionAsync(
        Incident incident, Policy policy, Resource resource, CancellationToken ct)
    {
        var action = await CurrentActionAsync(incident, ct);
        if (action is null)
            return await _projects.EscalateIncidentAsync(incident,
                "execution record lost; cannot tell whether the intervention ran", ct) is not null;

        switch (action.Status)
        {
            case ActionStatus.Proposed:
                // The incident says acting but the action was never authorized: a process died
                // in the gap. Close the orphan out and re-decide from scratch.
                await SupersedeAsync(action, "never authorized", ct);
                return await _projects.ReopenIncidentAsync(incident,
                    "action never left the proposed state; re-deciding", ct) is not null;

            case ActionStatus.Approved:
                // Approved but never dispatched — a restart landed in the gap. Dispatch now.
                await DispatchAsync(incident, policy, resource, action, ct);
                return true;

            case ActionStatus.Executing:
                return await PollExecutionAsync(action, ct);

            case ActionStatus.Succeeded:
                return await AfterActionSucceededAsync(incident, policy, action, ct);

            case ActionStatus.Failed:
            case ActionStatus.Rejected:
                return await RetryOrEscalateAsync(incident, policy,
                    $"{action.Kind.ToWire()} failed: {action.FailureReason ?? "no reason recorded"}", ct);

            default:
                return false;
        }
    }

    /// <summary>
    /// Settles an executing action once its execution reaches a terminal state. Jobs are
    /// guaranteed to terminate (leases expire, recovery requeues, attempts run out), so an
    /// incident can never be stuck in ACTING waiting on a job that will never answer.
    /// </summary>
    private async Task<bool> PollExecutionAsync(RemediationAction action, CancellationToken ct)
    {
        if (action.ExecutionJobId is null)
        {
            // Nothing external to wait for (a notification). It ran the moment it started.
            await _control.SettleActionAsync(action.Id, succeeded: true, null, ct);
            return true;
        }

        var job = await _jobs.ResolveAsync(action.ExecutionJobId.Value.ToString(), ct);
        if (job is null)
        {
            await _control.SettleActionAsync(action.Id, succeeded: false, "execution job no longer exists", ct);
            return true;
        }

        var status = Wire.From(job.Status);
        if (!status.IsTerminal()) return false;

        var succeeded = status == JobStatus.Completed;
        await _control.SettleActionAsync(action.Id, succeeded,
            succeeded ? null : job.FailureReason ?? $"execution job {status.ToWire()}", ct);
        return true;
    }

    private async Task<bool> AfterActionSucceededAsync(
        Incident incident, Policy policy, RemediationAction action, CancellationToken ct)
    {
        if (!policy.Remediation.IsVerifiable)
        {
            // A notification has no observable effect on the resource, so there is nothing
            // honest to verify. Say so and hand it to a human.
            await _control.RecordOutcomeAsync(action.Id, ActionOutcome.NotApplicable,
                "notification only; nothing to verify", ct);
            var reason = policy.Remediation.Params.GetValueOrDefault("message")
                ?? $"{policy.Name} needs human intervention";
            return await _projects.EscalateIncidentAsync(incident, reason, ct) is not null;
        }

        var finishedAt = action.FinishedAt ?? _clock.GetUtcNow();
        var deadline = finishedAt + TimeSpan.FromSeconds(policy.Remediation.VerifyWithinSeconds);
        return await _projects.BeginVerifyingAsync(incident, action, finishedAt, deadline, ct) is not null;
    }

    // ---- verify ----

    private async Task<bool> VerifyAsync(Incident incident, Policy policy, CancellationToken ct)
    {
        var action = await CurrentActionAsync(incident, ct);
        if (action is null || incident.VerifyEvidenceAfter is null || incident.VerifyDeadlineAt is null)
            return await _projects.ReopenIncidentAsync(incident,
                "verification state incomplete; re-deciding", ct) is not null;

        var snapshot = await _control.SnapshotAsync(incident.ResourceId, ct);
        var result = Verifier.Verify(
            policy.Expectation, snapshot,
            incident.VerifyEvidenceAfter.Value, incident.VerifyDeadlineAt.Value, _clock.GetUtcNow());

        // Pending is the absence of news. The VERIFYING status and verify_deadline_at already
        // say what Relay is waiting for; writing a countdown to the audit log on every tick
        // would bury the events that matter.
        if (result.Status == VerificationStatus.Pending) return false;

        RelayMetrics.Verifications.Add(1,
            RelayMetrics.Tag("status", result.Status.ToWire()),
            RelayMetrics.Tag("kind", action.Kind.ToWire()));

        await _control.RecordOutcomeAsync(action.Id, result.ToOutcome()!.Value, result.Summary, ct);
        await _projects.LogAsync(incident.Id,
            [IncidentEvent.Verification(result.Summary, new Dictionary<string, string>
            {
                ["status"] = result.Status.ToWire(),
                ["action_id"] = action.Id.ToString(),
                ["evidence_source"] = result.Evidence?.Source ?? "none",
            })], ct);

        if (result.Status == VerificationStatus.Passed)
            return await _projects.ResolveIncidentAsync(incident, "verified", result.Summary, ct) is not null;

        return await RetryOrEscalateAsync(incident, policy, result.Summary, ct);
    }

    // ---- shared ----

    private async Task<bool> RetryOrEscalateAsync(
        Incident incident, Policy policy, string reason, CancellationToken ct)
    {
        if (incident.Attempt >= policy.Remediation.MaxAttempts)
            return await _projects.EscalateIncidentAsync(incident,
                $"{reason}; remediation attempts exhausted ({policy.Remediation.MaxAttempts})", ct) is not null;

        // Retries route back through OPEN so cool-down, approval and the attempt budget are
        // re-applied by the one place that owns those rules.
        return await _projects.ReopenIncidentAsync(incident, reason, ct) is not null;
    }

    private async Task<RemediationAction?> CurrentActionAsync(Incident incident, CancellationToken ct) =>
        incident.CurrentActionId is { } id ? await _control.GetActionAsync(id, ct) : null;
}
