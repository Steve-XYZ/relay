using System.Diagnostics;

namespace Relay.Worker;

public sealed class WorkerTelemetry
{
    public const string SourceName = "Relay.Worker";

    public static readonly ActivitySource ActivitySource = new(SourceName);
}
