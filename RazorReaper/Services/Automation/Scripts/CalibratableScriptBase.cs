using Microsoft.Extensions.Logging;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Base for scripts that react to a user-calibrated HUD region matching a reference snapshot
/// ("is the Take-All button / this buff icon visible?"). Owns the whole region+reference plumbing
/// (calibrate, capture reference, clear, visibility check) so vision scripts only implement their
/// action. Detection is pure screen capture (GDI BitBlt via <see cref="IScreenSampler"/>) — no game
/// memory. Region/reference are keyed off the script key, scoped per resolution by the calibration
/// service.
/// </summary>
public abstract class CalibratableScriptBase : AutomationScriptBase, ICalibratableScript
{
    protected readonly IScreenSampler Sampler;
    protected readonly ICalibrationService Calibration;

    private readonly string _regionKey;
    private bool _hasReference;

    protected CalibratableScriptBase(
        string scriptKey,
        string displayName,
        string defaultHotkey,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger logger)
        : base(scriptKey, displayName, defaultHotkey, foreground, hotkeys, notifications, activity, logger)
    {
        Sampler = sampler;
        Calibration = calibration;
        _regionKey = $"{scriptKey}-region";
    }

    public bool HasRegion => Calibration.HasRegion(_regionKey);
    public bool HasReference => _hasReference && Sampler.HasReference(_regionKey);
    public string RegionSummary => Calibration.TryGetRegion(_regionKey, out Rectangle r)
        ? $"{r.Width}x{r.Height} px at {r.X}, {r.Y}"
        : "";

    /// <summary>Gets the calibrated region for the current resolution.</summary>
    protected bool TryGetRegion(out Rectangle region) => Calibration.TryGetRegion(_regionKey, out region);

    /// <summary>True when the calibrated region currently matches the reference at/above <paramref name="thresholdPercent"/>.</summary>
    protected bool IsTargetVisible(double thresholdPercent)
    {
        if (!Calibration.TryGetRegion(_regionKey, out Rectangle region)) return false;
        var tolerance = (1.0 - Math.Clamp(thresholdPercent, 50, 100) / 100.0) * 255.0;
        return Sampler.MatchesReference(_regionKey, region, tolerance);
    }

    protected override bool CanStart(out string? reason)
    {
        if (!HasRegion) { reason = "Calibrate the detection region first."; return false; }
        if (!HasReference) { reason = "Capture a reference snapshot with the target visible."; return false; }
        reason = null;
        return true;
    }

    public async Task<bool> CalibrateRegionAsync(IProgress<RegionCaptureProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsRunning) Stop();
        try
        {
            var region = await Calibration.CaptureRegionAsync(_regionKey, 3, progress, ct);
            if (region is null) return false;
            ClearReference();
            Notifications.ShowInfo("Region set — now capture a reference with the target visible.");
            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Script} region capture failed", DisplayName);
            Notifications.ShowError("Failed to capture the region.");
            return false;
        }
    }

    public bool CaptureReference()
    {
        if (!Calibration.TryGetRegion(_regionKey, out Rectangle region))
        {
            Notifications.ShowWarning("Calibrate the region first.");
            return false;
        }
        var capture = Sampler.CaptureRegion(region);
        if (capture.IsEmpty)
        {
            Notifications.ShowError("Could not capture the reference snapshot.");
            return false;
        }
        Sampler.CaptureReference(_regionKey, region);
        _hasReference = true;
        Notifications.ShowSuccess("Reference snapshot captured.");
        RaiseChanged();
        return true;
    }

    public void ClearReference()
    {
        _hasReference = false;
        try { Sampler.ClearReference(_regionKey); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} reference clear failed", DisplayName); }
        RaiseChanged();
    }
}
