using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Implemented by scripts that watch a user-calibrated HUD region against a reference snapshot
/// (e.g. "is the Take All button / this buff icon visible?"). The Scripts page renders a shared
/// calibration section — "Calibrate region" + "Capture reference" — for any selected script that
/// implements this, so every vision script reuses the same two-step setup.
/// </summary>
public interface ICalibratableScript
{
    /// <summary>True when a region is calibrated for the current resolution.</summary>
    bool HasRegion { get; }

    /// <summary>
    /// Whether the script matches the region against a reference snapshot. OCR scripts
    /// read the region as text instead — for them the snapshot/mask workflow is dead
    /// weight and the page must not offer it.
    /// </summary>
    bool UsesReference { get; }

    /// <summary>
    /// A dictionary key for what the region should be calibrated over, not the words themselves:
    /// a script has no localizer, and the Scripts page resolves this where the row renders so a
    /// language switch re-words it. The keys a script may publish are <see cref="RegionTitles"/>.
    /// </summary>
    string RegionTitleKey { get; }

    /// <summary>True when a reference snapshot has been captured this session.</summary>
    bool HasReference { get; }

    /// <summary>
    /// The calibrated region for the current resolution, or null when there is none. The numbers,
    /// not the sentence: "1920x1080 px at 0, 0" built here would be English in a German window
    /// for the same reason <see cref="RegionTitleKey"/> is a key — a script has no localizer —
    /// and the Scripts page words it where the row renders.
    /// </summary>
    Rectangle? CalibratedRegion { get; }

    /// <summary>Runs the two-corner countdown capture for the region (stops the script first).</summary>
    Task<bool> CalibrateRegionAsync(IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Snapshots the calibrated region right now (target must be visible) as the reference.</summary>
    bool CaptureReference();

    /// <summary>
    /// Masks out everything that moved since the reference was taken. Call with the element still
    /// on screen but a different background behind it; needed for anything drawn over the world.
    /// </summary>
    bool RefineReferenceMask();

    /// <summary>
    /// How much of the region survives the mask, or null while the whole of it is compared — the
    /// two counts rather than the line they read as, for the reason above.
    /// </summary>
    (int Kept, int Total)? MaskCoverage { get; }

    /// <summary>
    /// Which display the reference was captured on against the one the game is on now, or null
    /// when the entry predates the stamp. Numbers, not the sentence — the page words it, for the
    /// same reason <see cref="RegionTitleKey"/> is a key.
    /// </summary>
    CalibrationMonitorInfo? Monitor { get; }

    /// <summary>Discards the reference snapshot.</summary>
    void ClearReference();
}
