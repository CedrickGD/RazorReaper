using System.Globalization;
using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;
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
/// <summary>
/// The calibration-row titles a script can publish. Listing them is what lets
/// TranslatedSurfaceTests read them off a real catalog: neither key is ever a literal inside a
/// <c>T(…)</c> call, so a grep would call both of them dead.
/// </summary>
public static class RegionTitles
{
    public const string Button = "scripts.region.button";
    public const string Durability = "scripts.region.durability";

    public static IReadOnlyList<string> All { get; } = new[] { Button, Durability };
}

public abstract class CalibratableScriptBase : AutomationScriptBase, ICalibratableScript
{
    protected readonly IScreenSampler Sampler;
    protected readonly ICalibrationService Calibration;

    private readonly string _regionKey;

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
        ILocalizer localizer,
        ILogger logger)
        : base(scriptKey, displayName, defaultHotkey, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        Sampler = sampler;
        Calibration = calibration;
        _regionKey = $"{scriptKey}-region";
    }

    /// <summary>Vision scripts are unquota'd — the shared input-script quota skips them.</summary>
    public override bool UsesVision => true;

    public bool HasRegion => Calibration.HasRegion(_regionKey);
    /// <summary>
    /// Asks the sampler outright rather than tracking a "captured this session" flag: the
    /// snapshot is stored on disk now, so one taken yesterday is just as valid — and the flag
    /// would have hidden it, leaving the page saying "not captured" over a perfectly good file.
    /// </summary>
    public bool HasReference => Sampler.HasReference(_regionKey);

    /// <summary>Snapshot matching is the default; OCR scripts override this to false.</summary>
    public virtual bool UsesReference => true;

    public virtual string RegionTitleKey => RegionTitles.Button;

    /// <summary>
    /// The 312 and the 29,607 behind "312 of 29,607 px compared", once the background has been
    /// masked out; null before that and null while the whole region is still compared, which is
    /// the state the page shows its hint for. The words stay the page's even now that this class
    /// can translate: the line sits on a card for as long as the calibration does, and a sentence
    /// built here would keep the language it was built in through a switch. A toast is the other
    /// case — it is gone before a switch can reach it, so those are worded here.
    /// </summary>
    public (int Kept, int Total)? MaskCoverage
    {
        get
        {
            if (!HasReference) return null;
            var (kept, total) = Sampler.ReferenceMaskInfo(_regionKey);
            return kept == total ? null : (kept, total);
        }
    }

    public Rectangle? CalibratedRegion =>
        Calibration.TryGetRegion(_regionKey, out Rectangle r) ? r : null;

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
        if (!HasRegion) { reason = Localizer.T("scripts.cannotstart.region"); return false; }
        if (!HasReference) { reason = Localizer.T("scripts.cannotstart.reference"); return false; }
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
            Notifications.ShowInfo(Localizer.T("scripts.toast.regionset"));
            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Script} region capture failed", DisplayName);
            Notifications.ShowError(Localizer.T("scripts.toast.regionfailed"));
            return false;
        }
    }

    public bool CaptureReference()
    {
        if (!Calibration.TryGetRegion(_regionKey, out Rectangle region))
        {
            Notifications.ShowWarning(Localizer.T("scripts.toast.needregion"));
            return false;
        }
        var capture = Sampler.CaptureRegion(region);
        if (capture.IsEmpty)
        {
            Notifications.ShowError(Localizer.T("scripts.toast.referencefailed"));
            return false;
        }
        Sampler.CaptureReference(_regionKey, region);
        Notifications.ShowSuccess(Localizer.T("scripts.toast.referencecaptured"));
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Second half of the reference capture: same element on screen, different background behind
    /// it. Everything that moved is written off as background and stops being compared. Without
    /// this a HUD element that floats over the live world can never be matched — the world alone
    /// blows past any tolerance the moment the camera turns.
    /// </summary>
    public bool RefineReferenceMask()
    {
        if (!HasReference)
        {
            Notifications.ShowWarning(Localizer.T("scripts.toast.needreference"));
            return false;
        }
        if (!Calibration.TryGetRegion(_regionKey, out Rectangle region)) return false;

        if (!Sampler.RefineReferenceMask(_regionKey, region, out var kept))
        {
            Notifications.ShowWarning(Localizer.T("scripts.toast.masknothing"));
            return false;
        }

        Notifications.ShowSuccess(Localizer.T("scripts.toast.maskdone", kept.ToString("N0", CultureInfo.InvariantCulture)));
        RaiseChanged();
        return true;
    }

    public void ClearReference()
    {
        try { Sampler.ClearReference(_regionKey); }
        catch (Exception ex) { Logger.LogWarning(ex, "{Script} reference clear failed", DisplayName); }
        RaiseChanged();
    }
}
