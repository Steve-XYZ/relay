using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// The unit of operational ownership. A project is the scope in which Relay knows what
/// should be happening, what is actually happening, and what it may do about the gap.
/// Everything else in the control plane hangs off a project.
/// </summary>
public sealed record Project
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Stable, url-safe handle, e.g. "checkout-api". Unique across the server.</summary>
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Slugs are lowercase kebab; reject anything else so URLs stay unambiguous.</summary>
    public static string NormalizeSlug(string value)
    {
        var slug = value.Trim().ToLowerInvariant().Replace(' ', '-');
        if (slug.Length == 0 || slug.Length > 64)
            throw new DomainException("project slug must be 1-64 characters");
        if (!slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_'))
            throw new DomainException($"project slug '{value}' may only contain a-z, 0-9, '-' and '_'");
        return slug;
    }
}

/// <summary>
/// What a resource is. Relay reasons about all of these the same way — something that has
/// an operational identity, reports observations, and can be the target of a policy.
/// New kinds are added here rather than through a generic type registry: the loop treats
/// every kind identically, so a new kind costs one enum member plus documentation.
/// </summary>
public enum ResourceKind
{
    /// <summary>A git repository Relay can run agent tasks against.</summary>
    Repository,

    /// <summary>A long-running deployed process reachable by whatever reports its health.</summary>
    Service,

    /// <summary>A Relay worker: execution capacity, itself worth watching.</summary>
    Worker,

    /// <summary>A released artifact/version in an environment.</summary>
    Deployment,

    /// <summary>A datastore whose availability or lag matters.</summary>
    Database,

    /// <summary>Cron, queue consumer, nightly ETL — anything expected to run on a cadence.</summary>
    ScheduledProcess,

    /// <summary>A third party Relay depends on but does not control.</summary>
    ExternalDependency,
}

/// <summary>Latest known operational verdict for a resource, derived from observations only.</summary>
public enum ResourceHealth
{
    /// <summary>Nothing has reported on this resource yet. Not the same as healthy.</summary>
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
}

/// <summary>
/// A thing in a project that has an operational identity. Resources are declared (by an
/// operator or an integration) and never invented by the loop; their health is derived
/// from observations and is therefore always evidence-backed.
/// </summary>
public sealed record Resource
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("project_id")]
    public required Guid ProjectId { get; init; }

    [JsonPropertyName("kind")]
    public required ResourceKind Kind { get; init; }

    /// <summary>Caller-owned identity, unique per (project, kind): "api-gateway", "nightly-etl".</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// Kind-specific configuration Relay needs in order to act, e.g. "repo_url" on a
    /// Repository or "url" on a Service. Deliberately a flat string map: the moment this
    /// wants a schema, that kind deserves its own explicit fields.
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, string> Attributes { get; init; } = [];

    [JsonPropertyName("health")]
    public ResourceHealth Health { get; init; } = ResourceHealth.Unknown;

    [JsonPropertyName("health_reason")]
    public string? HealthReason { get; init; }

    [JsonPropertyName("last_observed_at")]
    public DateTimeOffset? LastObservedAt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }

    public string Describe() => $"{Kind.ToWire()}/{Key}";

    public static string NormalizeKey(string value)
    {
        var key = value.Trim();
        if (key.Length == 0 || key.Length > 128)
            throw new DomainException("resource key must be 1-128 characters");
        return key;
    }
}

/// <summary>Raised when a caller tries to persist something the domain forbids.</summary>
public sealed class DomainException(string message) : Exception(message);

public static class EnumWire
{
    public static string ToWire(this ResourceKind kind) => Camel(kind.ToString());
    public static string ToWire(this ResourceHealth health) => Camel(health.ToString());

    public static ResourceKind ParseResourceKind(string value) =>
        Enum.TryParse<ResourceKind>(value.Replace("_", ""), ignoreCase: true, out var k)
            ? k
            : throw new DomainException($"unknown resource kind '{value}'");

    public static ResourceHealth ParseResourceHealth(string value) =>
        Enum.TryParse<ResourceHealth>(value, ignoreCase: true, out var h)
            ? h
            : throw new DomainException($"unknown resource health '{value}'");

    /// <summary>PascalCase -> snake_case so the wire format matches the rest of the API.</summary>
    internal static string Camel(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(pascal[i]));
        }
        return sb.ToString();
    }
}
