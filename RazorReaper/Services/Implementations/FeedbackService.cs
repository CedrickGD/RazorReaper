using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Diagnostics;
using RazorReaper.Services;
using RazorReaper.Services.Diagnostics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Sends in-app user feedback to the admin panel. Attaches best-effort identity (machine name,
/// HWID, license key, app version, platform, install id) so the admin can act on it, plus the
/// user's optional contact handle. Mirrors LicenseService's HTTP + DTO conventions.
/// </summary>
public class FeedbackService : IFeedbackService
{
    // The backend independently caps the optional diagnostics object at 12 KiB. Keep enough
    // headroom for the established 4,000-character message and identity fields so a valid report
    // is never compacted or discarded merely because the message contains multi-byte text.
    private const int MaxRequestBytes = 48 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IClientIdentityService _clientIdentityService;
    private readonly ILicenseService _licenseService;
    private readonly IOptions<AppConfiguration> _options;
    private readonly IDiagnosticSnapshotService _diagnostics;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(
        HttpClient httpClient,
        IClientIdentityService clientIdentityService,
        ILicenseService licenseService,
        IOptions<AppConfiguration> options,
        IDiagnosticSnapshotService diagnostics,
        ILogger<FeedbackService> logger)
    {
        _httpClient = httpClient;
        _clientIdentityService = clientIdentityService;
        _licenseService = licenseService;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> SubmitAsync(string message, string? contact, CancellationToken cancellationToken = default)
    {
        var result = await SubmitCoreAsync(
            message,
            contact,
            sourceRoute: null,
            includeDiagnostics: false,
            requireDiagnostics: false,
            cancellationToken).ConfigureAwait(false);
        return (result.Success, result.Message);
    }

    public Task<FeedbackSubmissionResult> SubmitWithDiagnosticsAsync(
        string message,
        string? contact,
        string? sourceRoute,
        CancellationToken cancellationToken = default)
        => SubmitCoreAsync(message, contact, sourceRoute, includeDiagnostics: true, requireDiagnostics: false, cancellationToken);

    public Task<FeedbackSubmissionResult> SubmitDiagnosticsAsync(
        string message,
        string? contact,
        string? sourceRoute,
        CancellationToken cancellationToken = default)
        => SubmitCoreAsync(message, contact, sourceRoute, includeDiagnostics: true, requireDiagnostics: true, cancellationToken);

    private async Task<FeedbackSubmissionResult> SubmitCoreAsync(
        string message,
        string? contact,
        string? sourceRoute,
        bool includeDiagnostics,
        bool requireDiagnostics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new(false, "Please enter your feedback before submitting.");
        }

        var settings = _options.Value.AdminPanel;
        var baseUrl = settings.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new(false, "Feedback is not configured.");
        }

        try
        {
            var identity = SafeGetIdentity();
            FeedbackDiagnostics? diagnosticSnapshot = null;
            if (includeDiagnostics)
            {
                try
                {
                    diagnosticSnapshot = await _diagnostics
                        .CaptureAsync(sourceRoute, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Diagnostics are explicitly optional. A collector/configuration failure must
                    // never regress the proven feedback path.
                    _logger.LogWarning(ex, "Diagnostic snapshot could not be attached to feedback.");
                }
            }

            if (requireDiagnostics && diagnosticSnapshot is null)
            {
                return new(false, "Diagnostics could not be collected. Nothing was sent—please try again.");
            }

            var payload = new FeedbackPayload
            {
                Message = message.Trim(),
                Contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
                Hwid = identity?.HardwareId,
                InstallId = identity?.InstallId,
                LicenseKey = SafeGetLicenseKey(),
                MachineName = Environment.MachineName,
                AppVersion = SafeGetAppVersion(),
                Platform = SafeGetPlatform(),
                Diagnostics = diagnosticSnapshot,
            };

            if (payload.Diagnostics is not null && SerializedSize(payload) > MaxRequestBytes)
            {
                payload = payload with
                {
                    Diagnostics = DiagnosticSnapshotService.CompactForTransport(payload.Diagnostics),
                };
            }

            if (payload.Diagnostics is not null && SerializedSize(payload) > MaxRequestBytes)
            {
                // A maximum-length/multibyte message can consume the shared body budget by
                // itself. Keep the established feedback fields intact and omit only the optional
                // object rather than changing or rejecting the user's message.
                if (requireDiagnostics)
                {
                    return new(false, "The diagnostic snapshot is too large to send. Nothing was sent—please try again after restarting the app.");
                }

                _logger.LogWarning("Diagnostic snapshot omitted because the feedback body exceeds {MaxBytes} bytes.", MaxRequestBytes);
                payload = payload with { Diagnostics = null };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)));

            // Authenticated per install by SignedRequestHandler (rr.install.v1 signature headers).
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/feedback")
            {
                Content = JsonContent.Create(payload)
            };

            var response = await _httpClient.SendAsync(request, cts.Token);
            var result = await response.Content.ReadFromJsonAsync<FeedbackApiResponse>(cts.Token);

            if (response.IsSuccessStatusCode && result is { Ok: true })
            {
                return new(true, result.Message ?? "Thanks for your feedback!", result.ReportId);
            }

            return new(false, result?.Error ?? "Failed to send feedback. Please try again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, "Feedback submission was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit feedback.");
            return new(false, $"Network error: {ex.Message}");
        }
    }

    private ClientIdentity? SafeGetIdentity()
    {
        try { return _clientIdentityService.GetIdentity(); } catch { return null; }
    }

    private string? SafeGetLicenseKey()
    {
        try
        {
            var key = _licenseService.CurrentLicenseKey;
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch { return null; }
    }

    private static string? SafeGetAppVersion()
        => AppVersionInfo.VersionString;

    private static string? SafeGetPlatform()
    {
        try { return DeviceInfo.Platform.ToString().ToLowerInvariant(); } catch { return null; }
    }

    private static int SerializedSize(FeedbackPayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload).Length;

    private sealed class FeedbackApiResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("report_id")]
        public string? ReportId { get; set; }
    }

    private sealed record FeedbackPayload
    {
        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("contact")]
        public string? Contact { get; init; }

        [JsonPropertyName("hwid")]
        public string? Hwid { get; init; }

        [JsonPropertyName("install_id")]
        public string? InstallId { get; init; }

        [JsonPropertyName("license_key")]
        public string? LicenseKey { get; init; }

        [JsonPropertyName("machine_name")]
        public string? MachineName { get; init; }

        [JsonPropertyName("app_version")]
        public string? AppVersion { get; init; }

        [JsonPropertyName("platform")]
        public string? Platform { get; init; }

        [JsonPropertyName("diagnostics")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public FeedbackDiagnostics? Diagnostics { get; init; }
    }
}
