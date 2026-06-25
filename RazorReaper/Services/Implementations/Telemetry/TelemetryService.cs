using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Ships allowlisted telemetry events (session lifecycle, app errors, feature usage) to the
/// configured HTTP endpoint. Stateless formatting and validation live in <see cref="TelemetryFormatting"/>;
/// identity (install/hardware ids) lives in the <c>TelemetryService.Identity.cs</c> partial.
/// </summary>
public sealed partial class TelemetryService : ITelemetryService
{
    private const string InstallIdPreferenceKey = "rr.telemetry.install_id";
    private const string SessionStartEventName = "session_start";
    private const string SessionActiveEventName = "session_active";
    private const string SessionEndEventName = "session_end";
    private const string AppErrorEventName = "app_error";
    private const int MinTimeoutSeconds = 3;
    private const int MaxTimeoutSeconds = 60;
    // The backend coalesces heartbeats into session-row updates (no event rows), so this
    // cadence keeps the Live page accurate without burning Cloudflare's write budget.
    private const int HeartbeatIntervalSeconds = 120;
    private static readonly HashSet<string> AllowedEventNames = new(StringComparer.OrdinalIgnoreCase)
    {
        SessionStartEventName,
        SessionActiveEventName,
        SessionEndEventName,
        AppErrorEventName,
        // Feature usage events shown on the admin panel. Heartbeat-style noise stays out.
        "process_start",
        "process_kill",
        "update_check",
        "ini_preset_add",
        "ini_preset_remove",
        "ini_preset_image_set",
        "ini_preset_image_reset",
        "custom_lab.sky_inject",
        "custom_lab.sky_restore",
        "discord_rpc_toggle",
        "crosshair_overlay"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<AppConfiguration> options;
    private readonly IDeviceLocationService deviceLocationService;
    private readonly ILogger<TelemetryService> logger;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);

    private bool isStarted;
    private bool configurationWarningLogged;
    private string? installId;
    private string? hardwareId;
    private string? sessionId;
    private DateTimeOffset sessionStartedAtUtc;
    private CancellationTokenSource? heartbeatCts;

    public TelemetryService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppConfiguration> options,
        IDeviceLocationService deviceLocationService,
        ILogger<TelemetryService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.deviceLocationService = deviceLocationService;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.Telemetry;
        if (!settings.Enabled)
        {
            return;
        }

        if (!TelemetryFormatting.HasValidConfiguration(settings, out var configurationError))
        {
            LogConfigurationWarning(configurationError);
            return;
        }

        CancellationToken heartbeatToken;
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
            heartbeatCts = new CancellationTokenSource();
            heartbeatToken = heartbeatCts.Token;
        }
        finally
        {
            lifecycleGate.Release();
        }

        // Heartbeats start regardless of whether the session_start send succeeds — the
        // server creates the session row from the first heartbeat if the start got lost.
        _ = RunHeartbeatLoopAsync(heartbeatToken);

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

    /// <summary>
    /// Periodic liveness ping so the admin panel's Live view keeps showing the session
    /// beyond the server's 6-minute activity timeout. Ends with the session.
    /// </summary>
    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await TrackEventAsync(
                    SessionActiveEventName,
                    TelemetryEventStatus.Ok,
                    metrics: new Dictionary<string, object?>
                    {
                        ["session_open"] = true
                    },
                    cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry heartbeat loop stopped unexpectedly.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (!isStarted)
            {
                return;
            }

            isStarted = false;
            heartbeatCts?.Cancel();
            heartbeatCts?.Dispose();
            heartbeatCts = null;
            await TrackEventAsync(
                SessionEndEventName,
                TelemetryEventStatus.Ok,
                "Session ended.",
                metrics: new Dictionary<string, object?>
                {
                    ["session_open"] = false,
                    ["session_duration_seconds"] = GetSessionDurationSeconds()
                },
                cancellationToken: cancellationToken);

            sessionId = null;
            sessionStartedAtUtc = default;
        }
        finally
        {
            lifecycleGate.Release();
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

        if (!TelemetryFormatting.HasValidConfiguration(settings, out var configurationError))
        {
            LogConfigurationWarning(configurationError);
            return;
        }

        // Most call sites fire-and-forget this task (`_ = TrackEventAsync(...)`), so the
        // returned task must NEVER fault: a discarded faulted task resurfaces on the finalizer
        // thread as TaskScheduler.UnobservedTaskException and gets reported as an RR-E1003
        // app error. Every failure — metric building, serialization, or the send itself —
        // is logged and silently dropped here instead.
        try
        {
            await SendEventAsync(eventName, status, message, metrics, settings, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller canceled request.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry push failed for event {Event}.", eventName);
        }
    }

    private async Task SendEventAsync(
        string eventName,
        TelemetryEventStatus status,
        string? message,
        IReadOnlyDictionary<string, object?>? metrics,
        TelemetrySettings settings,
        CancellationToken cancellationToken)
    {
        var normalizedEventName = TelemetryFormatting.SanitizeIdentifier(eventName, "event").ToLowerInvariant();
        if (!AllowedEventNames.Contains(normalizedEventName))
        {
            return;
        }

        var source = BuildSource(settings);
        var metricPayload = await BuildBaseMetricsAsync(source, cancellationToken);
        metricPayload["telemetry_schema"] = "rr.session.v2";

        if (normalizedEventName is SessionEndEventName)
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
            TelemetryFormatting.ToStatusText(status),
            metricPayload,
            TelemetryFormatting.NormalizeMessage(message));

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
        client.Timeout = TimeSpan.FromSeconds(TelemetryFormatting.Clamp(settings.RequestTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds));

        using var response = await client.SendAsync(request, cancellationToken);
        logger.LogInformation("Telemetry event {Service} -> HTTP {Status}", normalizedEventName, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            await LogFailedResponseAsync(response, normalizedEventName, cancellationToken);
        }
    }

    private async Task LogFailedResponseAsync(HttpResponseMessage response, string normalizedEventName, CancellationToken cancellationToken)
    {
        var responseText = await TelemetryFormatting.SafeReadResponseAsync(response, cancellationToken);
        var statusCode = (int)response.StatusCode;

        if (statusCode >= 300 && statusCode < 400)
        {
            var location = response.Headers.Location?.ToString() ?? "n/a";
            logger.LogWarning(
                "Telemetry push redirected ({StatusCode}) for service {Service}. Endpoint may be Access-protected. Location: {Location}. Response: {Response}",
                statusCode,
                normalizedEventName,
                location,
                TelemetryFormatting.Truncate(responseText, 200));
            return;
        }

        if (statusCode is 401 or 403)
        {
            logger.LogWarning(
                "Telemetry push unauthorized ({StatusCode}) for service {Service}. Verify telemetry token matches backend ingest secret. Response: {Response}",
                statusCode,
                normalizedEventName,
                TelemetryFormatting.Truncate(responseText, 200));
            return;
        }

        logger.LogWarning(
            "Telemetry push failed ({StatusCode}) for service {Service}. Response: {Response}",
            statusCode,
            normalizedEventName,
            TelemetryFormatting.Truncate(responseText, 200));
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
        => TelemetryFormatting.SanitizeIdentifier(settings.AppName, "razorreaper");

    private async Task<Dictionary<string, object?>> BuildBaseMetricsAsync(string source, CancellationToken cancellationToken)
    {
        var hwid = GetOrCreateHardwareId();
        var metrics = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["app_name"] = source,
            ["install_id"] = GetOrCreateInstallId(),
            ["hwid"] = hwid,
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
            var ver = AppInfo.Current.Version;
            metrics["app_version"] = ver.Build > 0 ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : $"{ver.Major}.{ver.Minor}";
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

        try
        {
            metrics["rpc_enabled"] = Preferences.Get(IDiscordPresenceService.EnabledPreferenceKey, true);

            // Last Discord account the RPC client connected as (kept even while RPC is off).
            var discordUser = Preferences.Get(IDiscordPresenceService.ConnectedUserPreferenceKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(discordUser))
            {
                metrics["discord_user"] = discordUser;
            }
        }
        catch
        {
            // Preferences unavailable; the panel treats a missing value as "unknown".
        }

        var location = await deviceLocationService.GetBestEffortLocationAsync(cancellationToken);
        if (location is not null)
        {
            metrics["client_geo_source"] = location.Source;
            if (!string.IsNullOrWhiteSpace(location.SignalSource))
            {
                metrics["client_geo_signal_source"] = location.SignalSource;
            }
            metrics["client_latitude"] = location.Latitude;
            metrics["client_longitude"] = location.Longitude;
            metrics["client_geo_captured_at"] = location.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture);

            if (location.AccuracyMeters is > 0)
            {
                metrics["client_accuracy_meters"] = location.AccuracyMeters.Value;
            }
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
}
