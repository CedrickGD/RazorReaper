namespace RazorReaper.Services;

public enum TelemetryEventStatus
{
    Ok,
    Degraded,
    Down
}

public interface ITelemetryService
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task TrackEventAsync(
        string eventName,
        TelemetryEventStatus status = TelemetryEventStatus.Ok,
        string? message = null,
        IReadOnlyDictionary<string, object?>? metrics = null,
        CancellationToken cancellationToken = default);
}
