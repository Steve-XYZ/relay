using Relay.Core;

namespace Relay.Server.Tests;

/// <summary>Mutable clock so tests can fast-forward past lease expiry.</summary>
public sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
