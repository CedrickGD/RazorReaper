namespace RazorReaper.Services;

/// <summary>
/// Drives the Discord Rich Presence shown on the user's profile while Razor Reaper
/// is running ("Playing Razor Reaper" + current tool + version + GitHub buttons).
/// Connects to the local Discord desktop client over IPC; every operation is a no-op
/// when Rich Presence is disabled, Discord isn't running, or no Application ID is set.
/// </summary>
public interface IDiscordPresenceService
{
    /// <summary>
    /// User toggle (persisted across launches). Flipping it live connects/disconnects
    /// the Discord client and re-applies the last activity.
    /// </summary>
    bool IsEnabled { get; set; }

    /// <summary>Raised when <see cref="IsEnabled"/> changes so the UI can refresh.</summary>
    event Action? StateChanged;

    /// <summary>Connect to Discord and publish the initial presence. Idempotent.</summary>
    void Initialize();

    /// <summary>
    /// Update the presence to reflect the page the user just navigated to.
    /// Accepts a base-relative path (e.g. "autoclicker", "vision/scope"); the
    /// implementation maps it to a friendly tool label.
    /// </summary>
    void SetActivityForPath(string relativePath);

    /// <summary>
    /// Set the displayed activity label directly, for pages that want to override the
    /// route-derived label (e.g. the Sky Changer page sets "Sky Changer").
    /// </summary>
    void SetActivityLabel(string label);

    /// <summary>
    /// Reflect the window hiding to / restoring from the system tray. While in the tray the
    /// presence shows an idle state instead of a specific tool ("no page").
    /// </summary>
    void SetMinimizedToTray(bool minimized);

    /// <summary>Clear the presence and dispose the Discord client. Safe to call repeatedly.</summary>
    void Shutdown();
}
