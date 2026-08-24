using System.Globalization;
using System.Text.Json.Serialization;

namespace Relay.Core;

public enum EventKind
{
    State,
    Log,
    Progress,
    Checkpoint,
    Warning,
    Milestone,
    Usage,
    Error
}

public static class EventKindExtensions
{
    public static string ToWire(this EventKind kind) => kind.ToString().ToLowerInvariant();

    public static EventKind FromWire(string value) =>
        Enum.TryParse<EventKind>(value, ignoreCase: true, out var k)
            ? k
            : EventKind.Log;
}

public sealed record JobEvent
{
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    public static JobEvent Log(string message, Dictionary<string, string>? data = null) =>
        new() { Seq = 0, Kind = EventKind.Log.ToWire(), Message = message, Data = data };

    public static JobEvent Progress(string message, Dictionary<string, string>? data = null) =>
        new() { Seq = 0, Kind = EventKind.Progress.ToWire(), Message = message, Data = data };

    public static JobEvent State(JobStatus from, JobStatus to, string? reason = null)
    {
        var data = new Dictionary<string, string> { ["from"] = from.ToWire(), ["to"] = to.ToWire() };
        if (reason is not null) data["reason"] = reason;
        return new JobEvent { Seq = 0, Kind = EventKind.State.ToWire(), Message = $"{from.ToWire()} -> {to.ToWire()}", Data = data };
    }

    public static JobEvent Milestone(string name, string detail = "") =>
        new() { Seq = 0, Kind = EventKind.Milestone.ToWire(), Message = name, Data = string.IsNullOrEmpty(detail) ? null : new Dictionary<string, string> { ["detail"] = detail } };

    public static JobEvent Error(string message, Dictionary<string, string>? data = null) =>
        new() { Seq = 0, Kind = EventKind.Error.ToWire(), Message = message, Data = data };
}

public sealed record Checkpoint
{
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Pure budget enforcement. Given accumulated usage and a budget, decide whether execution must stop.</summary>
public static class BudgetMeter
{
    public sealed record Verdict(bool Exceeded, string? Reason);

    public static Verdict Evaluate(Usage usage, Budget? budget, TimeSpan elapsed)
    {
        if (budget is null) return new Verdict(false, null);

        if (budget.MaxRuntimeSeconds is { } maxSeconds && elapsed.TotalSeconds >= maxSeconds)
            return new Verdict(true, $"runtime budget exceeded: {FormatDuration(elapsed)} >= {FormatDuration(TimeSpan.FromSeconds(maxSeconds))}");

        if (budget.MaxTokens is { } maxTokens && usage.TotalTokens > maxTokens)
            return new Verdict(true, $"token budget exceeded: {usage.TotalTokens:N0} / {maxTokens:N0}");

        if (budget.MaxCostUsd is { } maxCost && usage.CostUsd > maxCost)
            return new Verdict(true, $"cost budget exceeded: ${usage.CostUsd.ToString("0.00", CultureInfo.InvariantCulture)} / ${maxCost.ToString("0.00", CultureInfo.InvariantCulture)}");

        return new Verdict(false, null);
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.TotalSeconds:F0}s";
    }

    /// <summary>Rough token estimate when an adapter does not report usage: ~4 chars per token.</summary>
    public static long EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
