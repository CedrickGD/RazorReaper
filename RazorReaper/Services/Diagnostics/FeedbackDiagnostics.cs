using System.Text.Json.Serialization;

namespace RazorReaper.Services.Diagnostics;

/// <summary>
/// Versioned, privacy-bounded diagnostics attached only when the user explicitly sends an
/// in-app diagnostic report. This object is additive to the long-standing feedback payload.
/// </summary>
public sealed record FeedbackDiagnostics
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("consent")]
    public bool Consent { get; init; } = true;

    [JsonPropertyName("providers")]
    public IReadOnlyList<DiagnosticProviderReport> Providers { get; init; } = [];
}

public sealed record DiagnosticProviderReport
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; init; }

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonPropertyName("checks")]
    public IReadOnlyList<DiagnosticCheck> Checks { get; init; } = [];
}

public sealed record DiagnosticCheck
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; init; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
}

/// <summary>Context known by the UI at the instant the report is sent.</summary>
public sealed record DiagnosticCaptureContext(string SourceRoute);

/// <summary>Provider-owned data; the orchestrator supplies identity, timing, and isolation.</summary>
public sealed record DiagnosticProviderData(
    string Status,
    IReadOnlyList<DiagnosticCheck> Checks,
    string? Summary = null,
    string? Version = "1");

public interface IDiagnosticProvider
{
    string ProviderId { get; }

    Task<DiagnosticProviderData> CaptureAsync(
        DiagnosticCaptureContext context,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticSnapshotService
{
    Task<FeedbackDiagnostics> CaptureAsync(
        string? sourceRoute,
        CancellationToken cancellationToken = default);
}
