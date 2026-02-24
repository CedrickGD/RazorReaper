using System.Reflection;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;

namespace RazorReaper.Telemetry;

public sealed class TelemetryService : ITelemetryService
{
    private static readonly Version FallbackVersion = new(0, 0, 0, 0);
    private const string DefaultWorkerName = "razorreaper-app-telemetry";

    private readonly ILogger<TelemetryService> _logger;
    private readonly AppConfiguration _configuration;
    private readonly IInstallIdProvider _installIdProvider;
    private readonly ITelemetryClient _telemetryClient;
    private readonly ITelemetryStateStore _stateStore;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly DateTimeOffset _sessionStartedUtc = DateTimeOffset.UtcNow;
    private int _sessionEndTracked;

    public TelemetryService(
        ILogger<TelemetryService> logger,
        IOptions<AppConfiguration> configuration,
        IInstallIdProvider installIdProvider,
        ITelemetryClient telemetryClient,
        ITelemetryStateStore stateStore)
    {
        _logger = logger;
        _configuration = configuration.Value;
        _installIdProvider = installIdProvider;
        _telemetryClient = telemetryClient;
        _stateStore = stateStore;
    }

    public async Task EnsureInstallTrackedAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldSendTelemetry())
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (state.InstallEventSent)
            {
                return;
            }

            var sent = await TrackEventCoreAsync(TelemetryEventNames.InstallFirstRun, cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return;
            }

            state.InstallEventSent = true;
            await _stateStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed while tracking first install event.");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task TrackAppStartAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldSendTelemetry())
        {
            return;
        }

        var properties = new Dictionary<string, string>
        {
            ["session_started_utc"] = _sessionStartedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
        };

        await TrackEventCoreAsync(TelemetryEventNames.AppStart, cancellationToken, properties).ConfigureAwait(false);
    }

    public async Task TrackAppSessionEndAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldSendTelemetry())
        {
            return;
        }

        if (Interlocked.Exchange(ref _sessionEndTracked, 1) != 0)
        {
            return;
        }

        var endedAtUtc = DateTimeOffset.UtcNow;
        var durationSeconds = Math.Max((int)(endedAtUtc - _sessionStartedUtc).TotalSeconds, 0);
        var properties = new Dictionary<string, string>
        {
            ["session_started_utc"] = _sessionStartedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["session_ended_utc"] = endedAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["duration_seconds"] = durationSeconds.ToString(CultureInfo.InvariantCulture),
            ["end_reason"] = "app_exit"
        };

        var sent = await TrackEventCoreAsync(TelemetryEventNames.AppSessionEnd, cancellationToken, properties).ConfigureAwait(false);
        if (!sent)
        {
            Interlocked.Exchange(ref _sessionEndTracked, 0);
        }
    }

    public async Task TrackHeartbeatIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldSendTelemetry())
        {
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var intervalHours = Math.Clamp(_configuration.Telemetry.HeartbeatIntervalHours, 1, 24 * 30);
            var heartbeatInterval = TimeSpan.FromHours(intervalHours);

            if (state.LastHeartbeatUtc.HasValue &&
                now - state.LastHeartbeatUtc.Value < heartbeatInterval)
            {
                return;
            }

            var sent = await TrackEventCoreAsync(TelemetryEventNames.Heartbeat, cancellationToken).ConfigureAwait(false);
            if (!sent)
            {
                return;
            }

            state.LastHeartbeatUtc = now;
            await _stateStore.WriteAsync(state, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed while tracking heartbeat event.");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task TrackUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (!ShouldSendTelemetry())
        {
            return;
        }

        await TrackEventCoreAsync(TelemetryEventNames.UpdateCheck, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrackEventCoreAsync(
        string eventName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalProperties = null)
    {
        if (!TelemetryEventNames.IsSupported(eventName))
        {
            _logger.LogDebug("Ignored unsupported telemetry event: {EventName}", eventName);
            return false;
        }

        try
        {
            var identity = await _installIdProvider.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var properties = CreateBaseProperties();

            if (additionalProperties != null)
            {
                foreach (var (key, value) in additionalProperties)
                {
                    var trimmedKey = key?.Trim();
                    var trimmedValue = value?.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedKey) || string.IsNullOrWhiteSpace(trimmedValue))
                    {
                        continue;
                    }

                    properties[trimmedKey] = trimmedValue;
                }
            }

            var telemetryEvent = new TelemetryEvent
            {
                InstallId = identity.InstallId,
                EventName = eventName,
                AppVersion = GetCurrentVersionLabel(),
                TimestampUtc = DateTimeOffset.UtcNow,
                Platform = GetPlatformLabel(),
                Properties = properties
            };

            return await _telemetryClient.SendAsync(telemetryEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry event {EventName} failed.", eventName);
            return false;
        }
    }

    private bool ShouldSendTelemetry()
    {
        var enabledByPreference = AppDiagnostics.GetTelemetryEnabled(_configuration.Telemetry.Enabled);
        if (!enabledByPreference)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(_configuration.Telemetry.Endpoint);
    }

    private Dictionary<string, string> CreateBaseProperties()
    {
        return new Dictionary<string, string>
        {
            ["worker_name"] = ResolveWorkerName(),
            ["session_id"] = _sessionId
        };
    }

    private string ResolveWorkerName()
    {
        var configured = _configuration.Telemetry.WorkerName?.Trim();
        return string.IsNullOrWhiteSpace(configured) ? DefaultWorkerName : configured;
    }

    private static string GetCurrentVersionLabel()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? FallbackVersion;
        if (version.Build >= 0)
        {
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"{version.Major}.{version.Minor}";
    }

    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacCatalyst())
        {
            return "maccatalyst";
        }

        if (OperatingSystem.IsAndroid())
        {
            return "android";
        }

        if (OperatingSystem.IsIOS())
        {
            return "ios";
        }

        return "unknown";
    }
}
