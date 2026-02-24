using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Services;

namespace RazorReaper.Telemetry;

public sealed class TelemetryService : ITelemetryService
{
    private static readonly Version FallbackVersion = new(0, 0, 0, 0);
    private const string DefaultWorkerName = "razorreaper-telemetry-backend";

    private readonly ILogger<TelemetryService> _logger;
    private readonly AppConfiguration _configuration;
    private readonly IInstallIdProvider _installIdProvider;
    private readonly ITelemetryClient _telemetryClient;
    private readonly ITelemetryStateStore _stateStore;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

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

        await TrackEventCoreAsync(TelemetryEventNames.AppStart, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> TrackEventCoreAsync(string eventName, CancellationToken cancellationToken)
    {
        if (!TelemetryEventNames.IsSupported(eventName))
        {
            _logger.LogDebug("Ignored unsupported telemetry event: {EventName}", eventName);
            return false;
        }

        try
        {
            var identity = await _installIdProvider.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            var telemetryEvent = new TelemetryEvent
            {
                InstallId = identity.InstallId,
                EventName = eventName,
                AppVersion = GetCurrentVersionLabel(),
                TimestampUtc = DateTimeOffset.UtcNow,
                Platform = GetPlatformLabel(),
                Properties = new Dictionary<string, string>
                {
                    ["worker_name"] = ResolveWorkerName()
                }
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
