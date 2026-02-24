namespace RazorReaper.Configuration;

/// <summary>
/// Centralized configuration class for the RazorReaper application.
/// </summary>
public class AppConfiguration
{
    /// <summary>
    /// System monitoring configuration.
    /// </summary>
    public MonitoringSettings Monitoring { get; set; } = new();

    /// <summary>
    /// ARK game configuration.
    /// </summary>
    public ArkSettings Ark { get; set; } = new();

    /// <summary>
    /// Autoclicker configuration.
    /// </summary>
    public AutoclickerSettings Autoclicker { get; set; } = new();

    /// <summary>
    /// Anonymous telemetry configuration.
    /// </summary>
    public TelemetrySettings Telemetry { get; set; } = new();
}

/// <summary>
/// System monitoring settings.
/// </summary>
public class MonitoringSettings
{
    /// <summary>
    /// Resource monitoring update interval in milliseconds. Default: 2 seconds (2000ms).
    /// </summary>
    public int ResourceUpdateInterval { get; set; } = 2000;

    /// <summary>
    /// Statistics update interval in milliseconds. Default: 5 seconds (5000ms).
    /// </summary>
    public int StatisticsUpdateInterval { get; set; } = 5000;

    /// <summary>
    /// Maximum number of recent activities to keep in memory.
    /// </summary>
    public int MaxRecentActivities { get; set; } = 50;
}

/// <summary>
/// ARK game settings.
/// </summary>
public class ArkSettings
{
    /// <summary>
    /// Name of the ARK game process.
    /// </summary>
    public string GameProcessName { get; set; } = "ShooterGame";

    /// <summary>
    /// Path to the BaseDeviceProfiles.ini file relative to ARK installation.
    /// </summary>
    public string ConfigRelativePath { get; set; } = @"Engine\Config\BaseDeviceProfiles.ini";

    /// <summary>
    /// Path to the game executable relative to ARK installation.
    /// </summary>
    public string ExecutableRelativePath { get; set; } = @"ShooterGame\Binaries\Win64\ShooterGame.exe";
}

/// <summary>
/// Autoclicker settings.
/// </summary>
public class AutoclickerSettings
{
    /// <summary>
    /// Maximum number of click history items to keep. Default: 10,000.
    /// </summary>
    public int MaxClickHistory { get; set; } = 10000;

    /// <summary>
    /// Default click delay in milliseconds.
    /// </summary>
    public int DefaultClickDelay { get; set; } = 100;

    /// <summary>
    /// Hotkey monitoring interval in milliseconds.
    /// </summary>
    public int HotkeyMonitorInterval { get; set; } = 50;
}

/// <summary>
/// Anonymous telemetry settings.
/// </summary>
public class TelemetrySettings
{
    /// <summary>
    /// Enables anonymous telemetry collection.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Backend endpoint that receives telemetry events.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional shared key sent as X-App-Key header.
    /// </summary>
    public string AppKey { get; set; } = string.Empty;

    /// <summary>
    /// Worker identifier written into telemetry properties for backend worker monitoring.
    /// </summary>
    public string WorkerName { get; set; } = "razorreaper-app-telemetry";

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 3;

    /// <summary>
    /// Heartbeat interval in hours.
    /// </summary>
    public int HeartbeatIntervalHours { get; set; } = 24;

    /// <summary>
    /// Max number of failed telemetry events kept for retry.
    /// </summary>
    public int RetryQueueMaxItems { get; set; } = 200;

    /// <summary>
    /// Max number of queued telemetry events retried per send attempt.
    /// </summary>
    public int RetryBatchSize { get; set; } = 20;
}
