namespace Relay.Core;

/// <summary>
/// Explicit, persisted job lifecycle. Every transition is validated here and persisted
/// by the server; no other component may move a job between states.
///
/// This is the execution layer. Incidents and actions have their own state machines
/// (<see cref="IncidentStateMachine"/>, <see cref="ActionStateMachine"/>) that sit above it:
/// a job is how an action gets carried out, not a reason for one.
///
///   QUEUED -> PREPARING -> RUNNING -> VALIDATING -> COMPLETED
///   RUNNING/PREPARING/VALIDATING -> INTERRUPTED -> RECOVERING -> (QUEUED | PREPARING)
///   any non-terminal -> FAILED | CANCELLED
/// </summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobStatus, HashSet<JobStatus>> Allowed = new()
    {
        [JobStatus.Queued] = [JobStatus.Preparing, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Preparing] = [JobStatus.Running, JobStatus.Interrupted, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Running] = [JobStatus.Validating, JobStatus.Interrupted, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Validating] = [JobStatus.Completed, JobStatus.Interrupted, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Interrupted] = [JobStatus.Recovering, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Recovering] = [JobStatus.Queued, JobStatus.Preparing, JobStatus.Failed, JobStatus.Cancelled],
        [JobStatus.Completed] = [],
        [JobStatus.Failed] = [],
        [JobStatus.Cancelled] = [],
    };

    public static bool CanTransition(JobStatus from, JobStatus to) =>
        Allowed[from].Contains(to);

    public static void Validate(JobStatus from, JobStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidTransitionException(from, to);
    }

    public static IEnumerable<JobStatus> Next(JobStatus from) => Allowed[from];
}

public sealed class InvalidTransitionException : Exception
{
    public JobStatus From { get; }
    public JobStatus To { get; }

    public InvalidTransitionException(JobStatus from, JobStatus to)
        : base($"Invalid transition {from.ToWire()} -> {to.ToWire()}")
    {
        From = from;
        To = to;
    }
}
