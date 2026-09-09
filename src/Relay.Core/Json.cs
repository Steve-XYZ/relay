using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relay.Core;

/// <summary>
/// The one JSON contract, shared by server, worker and CLI. Domain enums cross the wire as
/// snake_case strings — the same spelling the stores and the <c>ToWire()</c> helpers use — so
/// a payload reads the same in an HTTP body, a database column and a log line.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Default = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }

    /// <summary>
    /// Applies the contract to an existing options object. ASP.NET builds its own instance for
    /// minimal-API binding, so it has to be configured through this rather than handed
    /// <see cref="Default"/>; anything else lets the two drift apart silently.
    /// </summary>
    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Default);

    public static T? Deserialize<T>(string json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Default);
}
