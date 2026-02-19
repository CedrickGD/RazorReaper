namespace RazorReaper.Telemetry;

public interface ITelemetryStartupService
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
