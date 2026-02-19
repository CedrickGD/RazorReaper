namespace RazorReaper.Telemetry;

public sealed class TelemetryState
{
    public bool InstallEventSent { get; set; }
    public DateTimeOffset? LastHeartbeatUtc { get; set; }
}
