using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Sends in-app user feedback to the admin panel. Attaches best-effort identity (machine name,
/// HWID, license key, app version, platform, install id) so the admin can act on it, plus the
/// user's optional contact handle. Mirrors LicenseService's HTTP + DTO conventions.
/// </summary>
public class FeedbackService : IFeedbackService
{
    // Matches TelemetryService.Identity's install-id preference key so feedback and telemetry
    // report the same install_id for a given machine.
    private const string InstallIdPreferenceKey = "rr.telemetry.install_id";

    private readonly HttpClient _httpClient;
    private readonly IHwidService _hwidService;
    private readonly ILicenseService _licenseService;
    private readonly IOptions<AppConfiguration> _options;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(
        HttpClient httpClient,
        IHwidService hwidService,
        ILicenseService licenseService,
        IOptions<AppConfiguration> options,
        ILogger<FeedbackService> logger)
    {
        _httpClient = httpClient;
        _hwidService = hwidService;
        _licenseService = licenseService;
        _options = options;
        _logger = logger;
    }

    public async Task<(bool Success, string Message)> SubmitAsync(string message, string? contact, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return (false, "Please enter your feedback before submitting.");
        }

        var settings = _options.Value.AdminPanel;
        var baseUrl = settings.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return (false, "Feedback is not configured.");
        }

        try
        {
            var payload = new
            {
                message = message.Trim(),
                contact = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim(),
                hwid = SafeGetHwid(),
                install_id = SafeGetInstallId(),
                license_key = SafeGetLicenseKey(),
                machine_name = Environment.MachineName,
                app_version = SafeGetAppVersion(),
                platform = SafeGetPlatform()
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)));

            // The feedback endpoint is gated by the same app key telemetry already ships, so random
            // internet POSTs can't spam it. Reuse the configured Telemetry.AppKey as x-app-key.
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/feedback")
            {
                Content = JsonContent.Create(payload)
            };
            var appKey = _options.Value.Telemetry.AppKey;
            if (!string.IsNullOrWhiteSpace(appKey))
            {
                request.Headers.TryAddWithoutValidation("x-app-key", appKey);
            }

            var response = await _httpClient.SendAsync(request, cts.Token);
            var result = await response.Content.ReadFromJsonAsync<FeedbackApiResponse>(cts.Token);

            if (response.IsSuccessStatusCode && result is { Ok: true })
            {
                return (true, result.Message ?? "Thanks for your feedback!");
            }

            return (false, result?.Error ?? "Failed to send feedback. Please try again.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, "Feedback submission was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit feedback.");
            return (false, $"Network error: {ex.Message}");
        }
    }

    private string? SafeGetHwid()
    {
        try { return _hwidService.GetHardwareId(); } catch { return null; }
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

    private static string? SafeGetInstallId()
    {
        try
        {
            var value = Preferences.Get(InstallIdPreferenceKey, string.Empty);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch { return null; }
    }

    private static string? SafeGetAppVersion()
    {
        try
        {
            var ver = AppInfo.Current.Version;
            return ver.Build > 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}";
        }
        catch { return null; }
    }

    private static string? SafeGetPlatform()
    {
        try { return DeviceInfo.Platform.ToString().ToLowerInvariant(); } catch { return null; }
    }

    private sealed class FeedbackApiResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
