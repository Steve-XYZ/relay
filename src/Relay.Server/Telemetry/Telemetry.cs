using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Relay.Server.Telemetry;

public static class RelayMetrics
{
    public const string SourceName = "Relay.Server";
    public const string MeterName = "Relay.Server";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(MeterName);

    // ---- execution plane ----

    public static readonly Counter<long> JobsCreated = Meter.CreateCounter<long>("relay.jobs.created");
    public static readonly Counter<long> JobsFinished = Meter.CreateCounter<long>("relay.jobs.finished");

    // ---- control plane ----

    public static readonly Counter<long> ObservationsRecorded =
        Meter.CreateCounter<long>("relay.observations.recorded");

    public static readonly Counter<long> IncidentsOpened = Meter.CreateCounter<long>("relay.incidents.opened");

    /// <summary>Tagged with the resolution ("verified", "self_healed", …) so self-healing is visible.</summary>
    public static readonly Counter<long> IncidentsResolved = Meter.CreateCounter<long>("relay.incidents.resolved");

    public static readonly Counter<long> IncidentsEscalated = Meter.CreateCounter<long>("relay.incidents.escalated");

    public static readonly Counter<long> ActionsDispatched = Meter.CreateCounter<long>("relay.actions.dispatched");

    /// <summary>
    /// Tagged with the verification status. The ratio of passed to failed/timed-out is the
    /// honest measure of whether Relay's interventions work.
    /// </summary>
    public static readonly Counter<long> Verifications = Meter.CreateCounter<long>("relay.verifications");

    public static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);
}
