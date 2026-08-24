using System.Diagnostics;

namespace Relay.Server.Telemetry;

public static class RelayMetrics
{
    public const string SourceName = "Relay.Server";
    public const string MeterName = "Relay.Server";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    public static readonly System.Diagnostics.Metrics.Counter<long> JobsCreated =
        Meter.CreateCounter<long>("relay.jobs.created");

    public static readonly System.Diagnostics.Metrics.Counter<long> JobsFinished =
        Meter.CreateCounter<long>("relay.jobs.finished");
}
