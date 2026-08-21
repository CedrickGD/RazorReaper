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
    /// Telemetry and remote dashboard reporting settings.
    /// </summary>
    public TelemetrySettings Telemetry { get; set; } = new();

    /// <summary>
    /// Admin panel API settings (announcements + feedback).
    /// </summary>
    public AdminPanelSettings AdminPanel { get; set; } = new();
}

/// <summary>
/// Settings for the admin panel HTTP API that serves announcements and receives feedback.
/// Same surface the license flow already talks to.
/// </summary>
public class AdminPanelSettings
{
    /// <summary>
    /// Base URL of the admin panel (Cloudflare Pages) API. No trailing slash.
    /// </summary>
    public string BaseUrl { get; set; } = "https://rr-admin-panel.pages.dev";

    /// <summary>
    /// Per-request timeout in seconds for announcement/feedback calls.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How often the app re-fetches active announcements, in minutes.
    /// </summary>
    public int AnnouncementRefreshMinutes { get; set; } = 15;

    /// <summary>
    /// How often the app re-checks its access status (suspension/ban), in seconds. Kept short so a
    /// suspension — or a lift — takes effect within one cycle. Clamped to [15, 3600].
    /// </summary>
    public int AccessCheckIntervalSeconds { get; set; } = 60;
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
/// Telemetry reporting settings.
/// </summary>
public class TelemetrySettings
{
    /// <summary>
    /// Enables telemetry pushes to the admin panel backend.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Backend endpoint for telemetry events.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Logical application name shown in the dashboard.
    /// </summary>
    public string AppName { get; set; } = "razorreaper";

    /// <summary>
    /// Per-request timeout in seconds.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Enables best-effort device geolocation collection for telemetry sessions.
    /// </summary>
    public bool CaptureDeviceLocation { get; set; } = true;

    /// <summary>
    /// Maximum time spent waiting for the device location provider.
    /// </summary>
    public int DeviceLocationTimeoutSeconds { get; set; } = 12;

    /// <summary>
    /// Refresh window for cached device coordinates.
    /// </summary>
    public int DeviceLocationRefreshMinutes { get; set; } = 15;
}
