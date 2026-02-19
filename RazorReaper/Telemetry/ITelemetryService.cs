namespace RazorReaper.Telemetry;

public interface ITelemetryService
{
    Task EnsureInstallTrackedAsync(CancellationToken cancellationToken = default);
    Task TrackAppStartAsync(CancellationToken cancellationToken = default);
    Task TrackHeartbeatIfDueAsync(CancellationToken cancellationToken = default);
    Task TrackUpdateCheckAsync(CancellationToken cancellationToken = default);
}
