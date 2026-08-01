namespace RazorReaper.Services;

/// <summary>
/// Links the RazorReaper lifecycle to ARK with two independent toggles: while
/// <see cref="StartWithArk"/> is on, a headless login watcher (see <see cref="ArkWatchArg"/>)
/// launches RazorReaper the moment ARK starts (and a tray-hidden instance pops back up);
/// while <see cref="CloseWithArk"/> is on, the app closes itself once ARK exits.
/// </summary>
public interface IArkLinkService
{
    /// <summary>
    /// Preferences key backing <see cref="StartWithArk"/>.
    /// </summary>
    const string StartWithArkPreferenceKey = "rr.arklink.start";

    /// <summary>
    /// Preferences key backing <see cref="CloseWithArk"/>.
    /// </summary>
    const string CloseWithArkPreferenceKey = "rr.arklink.close";

    /// <summary>
    /// The single combined toggle shipped before the options were split. Migrated to both
    /// new keys (and removed) on service construction.
    /// </summary>
    const string LegacyEnabledPreferenceKey = "rr.arklink.enabled";

    /// <summary>
    /// Command-line flag the login autostart entry passes: the process runs as a tiny
    /// headless watcher (no window, no MAUI) that launches a normal RazorReaper instance
    /// the moment ARK starts. Handled in Platforms/Windows/Program.cs before any UI boots.
    /// </summary>
    const string ArkWatchArg = "--arkwatch";

    /// <summary>
    /// The flag written by pre-watcher builds; treated exactly like <see cref="ArkWatchArg"/>
    /// so a stale Run entry never boots a visible instance at login.
    /// </summary>
    const string LegacyWaitForArkArg = "--waitforark";

    /// <summary>
    /// Open RazorReaper when ARK starts (persisted). Enabling registers the login
    /// autostart entry; disabling removes it.
    /// </summary>
    bool StartWithArk { get; set; }

    /// <summary>
    /// Close RazorReaper when ARK exits (persisted).
    /// </summary>
    bool CloseWithArk { get; set; }

    /// <summary>
    /// Raised (from a background thread) when ARK was detected and the main window should
    /// be brought into view. The Windows platform bootstrap subscribes and marshals to the
    /// UI thread.
    /// </summary>
    event Action? ShowAppRequested;

    /// <summary>
    /// Applies the saved preferences once at app startup: refreshes or cleans up the
    /// autostart entry and starts the ARK watcher when either toggle is on.
    /// </summary>
    void Start();
}
