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
    private const int MinHeartbeatSeconds = 10;
    private const int MaxHeartbeatSeconds = 900;
    private const int MinTimeoutSeconds = 3;
    private const int MaxTimeoutSeconds = 60;
    private static readonly Regex InvalidIdentifierChars = new("[^a-zA-Z0-9._:-]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<AppConfiguration> options;
    private readonly ILogger<TelemetryService> logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private CancellationTokenSource? heartbeatCts;
    private Task? heartbeatTask;
    private DateTimeOffset startedAtUtc;
    private bool isStarted;
    private bool configurationWarningLogged;
    private string? installId;

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
            startedAtUtc = DateTimeOffset.UtcNow;
            heartbeatCts = new CancellationTokenSource();
            heartbeatTask = Task.Run(() => HeartbeatLoopAsync(heartbeatCts.Token), CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }

        await TrackEventAsync(
            "app_start",
            TelemetryEventStatus.Ok,
            "RazorReaper started.",
            cancellationToken: cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await TrackEventAsync(
            "app_stop",
            TelemetryEventStatus.Ok,
            "RazorReaper stopped.",
            cancellationToken: cancellationToken);

        CancellationTokenSource? ctsToCancel;
        Task? taskToWait;
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!isStarted)
            {
                return;
            }

            isStarted = false;
            ctsToCancel = heartbeatCts;
            taskToWait = heartbeatTask;
            heartbeatCts = null;
            heartbeatTask = null;
        }
        finally
        {
            lifecycleGate.Release();
        }

        if (ctsToCancel is null)
        {
            return;
        }

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
            // Expected when stopping heartbeat loop.
        }
        finally
        {
            ctsToCancel.Dispose();
        }
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

        var source = BuildSource(settings);
        var normalizedEventName = SanitizeIdentifier(eventName, "event");
        var outboundEventName = MapExternalEventName(normalizedEventName, status);
        var statusText = ToStatusText(status);

        var metricPayload = BuildBaseMetrics();
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

        metricPayload["worker_name"] = source;
        metricPayload["source"] = source;
        metricPayload["result"] = statusText;
        metricPayload["event_name"] = normalizedEventName;

        var payload = new LegacyTelemetryPayload(
            GetOrCreateInstallId(),
            outboundEventName,
            BuildAppVersion(),
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            BuildPlatformName(),
            metricPayload);

        var normalizedMessage = NormalizeMessage(message);
        if (!string.IsNullOrWhiteSpace(normalizedMessage))
        {
            payload.Properties["message"] = normalizedMessage;
        }

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
                        outboundEventName,
                        location,
                        Truncate(responseText, 200));
                    return;
                }

                if (statusCode is 401 or 403)
                {
                    logger.LogWarning(
                        "Telemetry push unauthorized ({StatusCode}) for service {Service}. Verify telemetry token matches backend ingest secret. Response: {Response}",
                        statusCode,
                        outboundEventName,
                        Truncate(responseText, 200));
                    return;
                }

                logger.LogWarning(
                    "Telemetry push failed ({StatusCode}) for service {Service}. Response: {Response}",
                    statusCode,
                    outboundEventName,
                    Truncate(responseText, 200));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller canceled request.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry push failed for service {Service}.", outboundEventName);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var heartbeatSeconds = Clamp(
            options.Value.Telemetry.HeartbeatIntervalSeconds,
            MinHeartbeatSeconds,
            MaxHeartbeatSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(heartbeatSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var uptimeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - startedAtUtc).TotalSeconds);
            await TrackEventAsync(
                "heartbeat",
                TelemetryEventStatus.Ok,
                metrics: new Dictionary<string, object?> { ["uptime_seconds"] = uptimeSeconds },
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
        var fallback = $"razorreaper-{GetOrCreateInstallId()[..8]}";
        return SanitizeIdentifier(settings.WorkerName, fallback);
    }

    private Dictionary<string, object?> BuildBaseMetrics()
    {
        var metrics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["install_id"] = GetOrCreateInstallId(),
            ["machine_name"] = Environment.MachineName,
            ["framework"] = $".NET {Environment.Version}",
            ["process_id"] = Environment.ProcessId
        };

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
        normalized = normalized.Replace("__", "_");
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

    private static string MapExternalEventName(string eventName, TelemetryEventStatus status)
    {
        if (status == TelemetryEventStatus.Down ||
            string.Equals(eventName, "app_error", StringComparison.OrdinalIgnoreCase))
        {
            return "app_error";
        }

        if (string.Equals(eventName, "app_start", StringComparison.OrdinalIgnoreCase))
        {
            return "app_start";
        }

        if (string.Equals(eventName, "app_stop", StringComparison.OrdinalIgnoreCase))
        {
            return "app_stop";
        }

        return "heartbeat";
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

    private string BuildAppVersion()
    {
        try
        {
            return AppInfo.Current.VersionString;
        }
        catch
        {
            return "unknown";
        }
    }

    private string BuildPlatformName()
    {
        try
        {
            return DeviceInfo.Platform.ToString().ToLowerInvariant();
        }
        catch
        {
            return "unknown";
        }
    }

    private sealed record LegacyTelemetryPayload(
        [property: JsonPropertyName("install_id")] string InstallId,
        [property: JsonPropertyName("event_name")] string EventName,
        [property: JsonPropertyName("app_version")] string AppVersion,
        [property: JsonPropertyName("timestamp_utc")] string TimestampUtc,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("properties")] Dictionary<string, object?> Properties);
}
