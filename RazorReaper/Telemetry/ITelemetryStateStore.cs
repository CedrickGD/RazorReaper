namespace RazorReaper.Telemetry;

public interface ITelemetryStateStore
{
    Task<TelemetryState> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(TelemetryState state, CancellationToken cancellationToken = default);
}
