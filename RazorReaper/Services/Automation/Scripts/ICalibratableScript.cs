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

    /// <summary>What the region should be calibrated over, e.g. "Button region".</summary>
    string RegionTitle { get; }

    /// <summary>True when a reference snapshot has been captured this session.</summary>
    bool HasReference { get; }

    /// <summary>Human-readable summary of the calibrated region, or "" when none.</summary>
    string RegionSummary { get; }

    /// <summary>Runs the two-corner countdown capture for the region (stops the script first).</summary>
    Task<bool> CalibrateRegionAsync(IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Snapshots the calibrated region right now (target must be visible) as the reference.</summary>
    bool CaptureReference();

    /// <summary>
    /// Masks out everything that moved since the reference was taken. Call with the element still
    /// on screen but a different background behind it; needed for anything drawn over the world.
    /// </summary>
    bool RefineReferenceMask();

    /// <summary>How much of the region survives the mask, or "" while the whole region is compared.</summary>
    string MaskSummary { get; }

    /// <summary>Discards the reference snapshot.</summary>
    void ClearReference();
}
