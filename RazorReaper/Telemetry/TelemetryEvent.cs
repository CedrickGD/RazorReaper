using System.Text.Json.Serialization;

namespace RazorReaper.Telemetry;

public sealed class TelemetryEvent
{
    [JsonPropertyName("install_id")]
    public string InstallId { get; init; } = string.Empty;

    [JsonPropertyName("event_name")]
    public string EventName { get; init; } = string.Empty;

    [JsonPropertyName("app_version")]
    public string AppVersion { get; init; } = string.Empty;

    [JsonPropertyName("timestamp_utc")]
    public DateTimeOffset TimestampUtc { get; init; }

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "windows";

    [JsonPropertyName("properties")]
    public Dictionary<string, string>? Properties { get; init; }
}
