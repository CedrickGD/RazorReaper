namespace RazorReaper.Telemetry;

public static class TelemetryEventNames
{
    public const string InstallFirstRun = "install_first_run";
    public const string AppStart = "app_start";
    public const string AppSessionEnd = "app_session_end";
    public const string Heartbeat = "heartbeat";
    public const string UpdateCheck = "update_check";

    public static bool IsSupported(string eventName)
    {
        return eventName == InstallFirstRun
            || eventName == AppStart
            || eventName == AppSessionEnd
            || eventName == Heartbeat
            || eventName == UpdateCheck;
    }
}
