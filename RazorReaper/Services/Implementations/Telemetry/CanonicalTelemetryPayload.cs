using System.Text.Json.Serialization;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Wire-format payload posted to the telemetry endpoint. Field names match the backend
/// schema (rr.session.v2) and must not change without coordinating with the receiver.
/// </summary>
internal sealed record CanonicalTelemetryPayload(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("metrics")] Dictionary<string, object?> Metrics,
    [property: JsonPropertyName("message")] string? Message);
