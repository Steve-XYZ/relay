using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>What a reporter says it saw. Deliberately coarse: Relay is not a metrics store.</summary>
public enum ObservedState
{
    Healthy,
    Degraded,
    Unavailable,
}

/// <summary>
/// One immutable, timestamped fact reported about a resource. Observations are the only
/// input to Relay's picture of reality: the loop never invents them and never mutates them.
///
/// Relay does not scrape. Observations arrive by push from whatever already knows — a
/// health checker, CI, a cron wrapper, a worker heartbeat — which is what keeps Relay a
/// control plane instead of a monitoring system.
/// </summary>
public sealed record Observation
{
    /// <summary>Signal used when a reporter does not name one; the resource's overall health.</summary>
    public const string DefaultSignal = "health";

    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("project_id")]
    public required Guid ProjectId { get; init; }

    [JsonPropertyName("resource_id")]
    public required Guid ResourceId { get; init; }

    /// <summary>
    /// Which aspect of the resource this is about: "health", "last_run", "queue_depth".
    /// Policies name a signal, so one resource can be watched from several angles.
    /// </summary>
    [JsonPropertyName("signal")]
    public string Signal { get; init; } = DefaultSignal;

    [JsonPropertyName("state")]
    public required ObservedState State { get; init; }

    /// <summary>Reporter-supplied detail the expectations can assert on, e.g. {"lag_seconds":"31"}.</summary>
    [JsonPropertyName("facts")]
    public Dictionary<string, string> Facts { get; init; } = [];

    /// <summary>Who reported this. Kept for audit: an incident should never cite anonymous evidence.</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = "unknown";

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>When the reporter says it saw this. Clamped at ingest so it can never be in the future.</summary>
    [JsonPropertyName("observed_at")]
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>
    /// When Relay durably accepted it, on Relay's own clock. Verification uses this rather
    /// than <see cref="ObservedAt"/>: evidence that an intervention worked must not be
    /// forgeable by a reporter with a fast clock.
    /// </summary>
    [JsonPropertyName("received_at")]
    public required DateTimeOffset ReceivedAt { get; init; }

    public bool TryFact(string name, out string value) => Facts.TryGetValue(name, out value!);

    public bool TryNumericFact(string name, out decimal value)
    {
        value = 0;
        return Facts.TryGetValue(name, out var raw)
            && decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public ResourceHealth ToHealth() => State switch
    {
        ObservedState.Healthy => ResourceHealth.Healthy,
        ObservedState.Degraded => ResourceHealth.Degraded,
        _ => ResourceHealth.Unavailable,
    };
}

/// <summary>
/// The latest observation per signal for one resource: everything the evaluator is allowed
/// to look at. Passing a snapshot instead of a query handle keeps evaluation pure and makes
/// "what did Relay know when it decided this" answerable from the audit log.
/// </summary>
public sealed class ObservationSnapshot
{
    private readonly Dictionary<string, Observation> _bySignal;

    public ObservationSnapshot(IEnumerable<Observation> latestPerSignal) =>
        _bySignal = latestPerSignal.ToDictionary(o => o.Signal, StringComparer.Ordinal);

    public static ObservationSnapshot Empty { get; } = new([]);

    public Observation? Latest(string signal) =>
        _bySignal.TryGetValue(signal, out var o) ? o : null;

    public IReadOnlyCollection<Observation> All => _bySignal.Values;
}

public static class ObservedStateWire
{
    public static string ToWire(this ObservedState state) => state.ToString().ToLowerInvariant();

    public static ObservedState Parse(string value) =>
        Enum.TryParse<ObservedState>(value, ignoreCase: true, out var s)
            ? s
            : throw new DomainException($"unknown observed state '{value}'");
}
