using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

public sealed class TelemetryService : ITelemetryService
{
    private const string InstallIdPreferenceKey = "rr.telemetry.install_id";
    private const string SessionStartEventName = "session_start";
    private const string SessionActiveEventName = "session_active";
    private const string SessionEndEventName = "session_end";
    private const string AppErrorEventName = "app_error";
    private const int MinSessionActivitySeconds = 120;
    private const int MaxSessionActivitySeconds = 3600;
    private const int MinTimeoutSeconds = 3;
    private const int MaxTimeoutSeconds = 60;
    private static readonly Regex InvalidIdentifierChars = new("[^a-zA-Z0-9._:-]", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SessionStartEventName,
        SessionActiveEventName,
        SessionEndEventName,
        AppErrorEventName
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<AppConfiguration> options;
    private readonly ILogger<TelemetryService> logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private CancellationTokenSource? sessionActivityCts;
    private Task? sessionActivityTask;
    private bool isStarted;
    private bool configurationWarningLogged;
    private string? installId;
    private string? sessionId;
    private DateTimeOffset sessionStartedAtUtc;

    public TelemetryService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppConfiguration> options,
        ILogger<TelemetryService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Telemetry;
        if (!settings.Enabled)
        {
            return;
        }

        if (!HasValidConfiguration(settings, out var configurationError))
        {
            LogConfigurationWarning(configurationError);
            return;
        }

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (isStarted)
            {
                return;
            }

            isStarted = true;
            sessionId = Guid.NewGuid().ToString("D");
            sessionStartedAtUtc = DateTimeOffset.UtcNow;
            sessionActivityCts = new CancellationTokenSource();
            sessionActivityTask = Task.Run(() => SessionActivityLoopAsync(sessionActivityCts.Token), CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }

        await TrackEventAsync(
            SessionStartEventName,
            TelemetryEventStatus.Ok,
            "Session started.",
            new Dictionary<string, object?>
            {
                ["session_open"] = true
            },
            cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? ctsToCancel;
        Task? taskToWait;
        bool shouldTrackStop;

        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!isStarted)
            {
                return;
            }

            isStarted = false;
            shouldTrackStop = true;
            ctsToCancel = sessionActivityCts;
            taskToWait = sessionActivityTask;
            sessionActivityCts = null;
            sessionActivityTask = null;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (ctsToCancel is not null)
        {
            try
            {
                await ctsToCancel.CancelAsync();
                if (taskToWait is not null)
                {
                    await taskToWait;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down the activity loop.
            }
            finally
            {
                ctsToCancel.Dispose();
            }
        }

        if (!shouldTrackStop)
        {
            return;
        }

        await TrackEventAsync(
            SessionEndEventName,
            TelemetryEventStatus.Ok,
            "Session ended.",
            new Dictionary<string, object?>
            {
                ["session_open"] = false,
                ["session_duration_seconds"] = GetSessionDurationSeconds()
            },
            cancellationToken);

        sessionId = null;
        sessionStartedAtUtc = default;
    }

    public async Task TrackEventAsync(
        string eventName,
        TelemetryEventStatus status = TelemetryEventStatus.Ok,
        string? message = null,
        IReadOnlyDictionary<string, object?>? metrics = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        var settings = options.Value.Telemetry;
        if (!settings.Enabled)
        {
            return;
        }

        if (!HasValidConfiguration(settings, out var configurationError))
        {
            LogConfigurationWarning(configurationError);
            return;
        }

        var normalizedEventName = SanitizeIdentifier(eventName, "event").ToLowerInvariant();
        if (!AllowedEventNames.Contains(normalizedEventName))
        {
            return;
        }

        var source = BuildSource(settings);
        var metricPayload = BuildBaseMetrics(source);
        metricPayload["telemetry_schema"] = "rr.session.v1";

        if (normalizedEventName is SessionActiveEventName or SessionEndEventName)
        {
            metricPayload["session_duration_seconds"] = GetSessionDurationSeconds();
        }

        if (metrics is not null)
        {
            foreach (var item in metrics)
            {
                var key = item.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                metricPayload[key] = item.Value;
            }
        }

        var payload = new CanonicalTelemetryPayload(
            source,
            normalizedEventName,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ToStatusText(status),
            metricPayload,
            NormalizeMessage(message));

        var requestBody = JsonSerializer.Serialize(payload, SerializerOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        var credential = settings.AppKey.Trim();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.TryAddWithoutValidation("x-app-key", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var client = httpClientFactory.CreateClient("RazorReaperTelemetry");
        client.Timeout = TimeSpan.FromSeconds(Clamp(settings.RequestTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await SafeReadResponseAsync(response, cancellationToken);
                var statusCode = (int)response.StatusCode;

                if (statusCode >= 300 && statusCode < 400)
                {
                    var location = response.Headers.Location?.ToString() ?? "n/a";
                    logger.LogWarning(
                        "Telemetry push redirected ({StatusCode}) for service {Service}. Endpoint may be Access-protected. Location: {Location}. Response: {Response}",
                        statusCode,
                        normalizedEventName,
                        location,
                        Truncate(responseText, 200));
                    return;
                }

                if (statusCode is 401 or 403)
                {
                    logger.LogWarning(
                        "Telemetry push unauthorized ({StatusCode}) for service {Service}. Verify telemetry token matches backend ingest secret. Response: {Response}",
                        statusCode,
                        normalizedEventName,
                        Truncate(responseText, 200));
                    return;
                }

                logger.LogWarning(
                    "Telemetry push failed ({StatusCode}) for service {Service}. Response: {Response}",
                    statusCode,
                    normalizedEventName,
                    Truncate(responseText, 200));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller canceled request.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry push failed for service {Service}.", normalizedEventName);
        }
    }

    private async Task SessionActivityLoopAsync(CancellationToken cancellationToken)
    {
        var intervalSeconds = Clamp(
            options.Value.Telemetry.SessionActivityIntervalSeconds,
            MinSessionActivitySeconds,
            MaxSessionActivitySeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await TrackEventAsync(
                SessionActiveEventName,
                TelemetryEventStatus.Ok,
                metrics: new Dictionary<string, object?>
                {
                    ["session_open"] = true,
                    ["session_duration_seconds"] = GetSessionDurationSeconds()
                },
                cancellationToken: cancellationToken);
        }
    }

    private static bool HasValidConfiguration(TelemetrySettings settings, out string error)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            error = "Telemetry endpoint is missing.";
            return false;
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttp && endpointUri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Telemetry endpoint must be a valid HTTP/HTTPS URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.AppKey))
        {
            error = "Telemetry AppKey is missing.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void LogConfigurationWarning(string message)
    {
        if (configurationWarningLogged)
        {
            return;
        }

        configurationWarningLogged = true;
        logger.LogWarning("Telemetry is enabled but not configured correctly: {Message}", message);
    }

    private string BuildSource(TelemetrySettings settings)
    {
        return SanitizeIdentifier(settings.AppName, "razorreaper");
    }

    private Dictionary<string, object?> BuildBaseMetrics(string source)
    {
        var metrics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["app_name"] = source,
            ["install_id"] = GetOrCreateInstallId(),
            ["machine_name"] = Environment.MachineName,
            ["user_label"] = Environment.MachineName,
            ["framework"] = $".NET {Environment.Version}",
            ["process_id"] = Environment.ProcessId
        };

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            metrics["session_id"] = sessionId;
            metrics["session_started_at"] = sessionStartedAtUtc.ToString("O", CultureInfo.InvariantCulture);
        }

        try
        {
            metrics["app_version"] = AppInfo.Current.VersionString;
            metrics["app_build"] = AppInfo.Current.BuildString;
            metrics["platform"] = DeviceInfo.Platform.ToString().ToLowerInvariant();
            metrics["device_model"] = DeviceInfo.Model;
            metrics["os_version"] = DeviceInfo.VersionString;
            metrics["device_manufacturer"] = DeviceInfo.Manufacturer;
        }
        catch
        {
            // Keep base metrics only if MAUI device info is unavailable.
        }

        return metrics;
    }

    private int GetSessionDurationSeconds()
    {
        if (sessionStartedAtUtc == default)
        {
            return 0;
        }

        return Math.Max(0, (int)(DateTimeOffset.UtcNow - sessionStartedAtUtc).TotalSeconds);
    }

    private string GetOrCreateInstallId()
    {
        if (!string.IsNullOrWhiteSpace(installId))
        {
            return installId;
        }

        var existing = Preferences.Get(InstallIdPreferenceKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(existing) && Guid.TryParse(existing, out var existingGuid))
        {
            installId = existingGuid.ToString("D");
            return installId;
        }

        installId = Guid.NewGuid().ToString("D");
        Preferences.Set(InstallIdPreferenceKey, installId);
        return installId;
    }

    private static string SanitizeIdentifier(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        normalized = normalized.Replace(' ', '_');
        normalized = InvalidIdentifierChars.Replace(normalized, "_");
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        if (normalized.Length > 64)
        {
            normalized = normalized[..64];
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = message.Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string ToStatusText(TelemetryEventStatus status)
    {
        return status switch
        {
            TelemetryEventStatus.Ok => "ok",
            TelemetryEventStatus.Degraded => "degraded",
            TelemetryEventStatus.Down => "down",
            _ => "ok"
        };
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        return value > max ? max : value;
    }

    private static async Task<string> SafeReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength];
    }

    private sealed record CanonicalTelemetryPayload(
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("service")] string Service,
        [property: JsonPropertyName("timestamp")] string Timestamp,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("metrics")] Dictionary<string, object?> Metrics,
        [property: JsonPropertyName("message")] string? Message);
}
