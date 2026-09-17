namespace RazorReaper.Services;

/// <summary>
/// Open/closed state of the full-window license overlay (Components/Shared/LicenseOverlay.razor).
/// The sidebar's tier line opens it; the overlay itself closes it. Kept in a service rather than
/// a cascading value so any component can open it without a reference to the overlay.
/// </summary>
public interface ILicenseOverlayService
{
    bool IsOpen { get; }

    /// <summary>Raised after <see cref="IsOpen"/> changes. Fires on the caller's thread.</summary>
    event Action? OnStateChanged;

    void Open();
    void Close();
}
