using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// The "What's new &amp; inbox" overlay (Components/Shared/WhatsNewOverlay.razor): its open/closed
/// state, opened from the sidebar's notification icon (NotificationIndicator), and which release's
/// notes have already been opened. Kept in a service, like <see cref="ILicenseOverlayService"/>, so
/// the icon can open the view without a reference to it and both can agree on what counts as new.
/// </summary>
public interface IWhatsNewService
{
    bool IsOpen { get; }

    /// <summary>Raised after <see cref="IsOpen"/> or the seen release changes. Fires on the caller's thread.</summary>
    event Action? OnStateChanged;

    void Open();
    void Close();

    /// <summary>
    /// The release whose notes have not been opened yet: the manifest's version from
    /// <paramref name="lastCheck"/> when it is the running version or newer and is not the one
    /// last marked seen. Null when there is no check result yet, the manifest is behind the
    /// running build (a dev build), or the notes were already opened.
    /// </summary>
    Version? UnseenRelease(UpdateCheckResult? lastCheck, Version current);

    /// <summary>Persists <paramref name="release"/> as seen, so it no longer counts as new.</summary>
    void MarkReleaseSeen(Version release);
}
