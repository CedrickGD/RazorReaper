using Microsoft.Extensions.Logging;

namespace RazorReaper.Telemetry;

public sealed class TelemetryStartupService : ITelemetryStartupService
{
    private readonly ILogger<TelemetryStartupService> _logger;
    private readonly IInstallIdProvider _installIdProvider;
    private readonly ITelemetryService _telemetryService;

    public TelemetryStartupService(
        ILogger<TelemetryStartupService> logger,
        IInstallIdProvider installIdProvider,
        ITelemetryService telemetryService)
    {
        _logger = logger;
        _installIdProvider = installIdProvider;
        _telemetryService = telemetryService;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _installIdProvider.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);
            await _telemetryService.EnsureInstallTrackedAsync(cancellationToken).ConfigureAwait(false);
            await _telemetryService.TrackAppStartAsync(cancellationToken).ConfigureAwait(false);
            await _telemetryService.TrackHeartbeatIfDueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry startup flow failed.");
        }
    }
}
