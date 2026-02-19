namespace RazorReaper.Telemetry;

public interface ITelemetryClient
{
    Task<bool> SendAsync(TelemetryEvent telemetryEvent, CancellationToken cancellationToken = default);
}
