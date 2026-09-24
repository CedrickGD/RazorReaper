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

    /// <summary>
    /// Which display the reference was captured on, and which one the game is on now. Null when
    /// there is no region, or when the entry predates the stamp — an old calibration that still
    /// works is not something to start refusing.
    /// </summary>
    public CalibrationMonitorInfo? Monitor
    {
        get
        {
            var stored = Calibration.GetRegion(_regionKey);
            if (stored?.MonitorDeviceName is null || stored.MonitorResolution is null) return null;

            var current = Calibration.CurrentGameMonitor;
            if (current is null) return null;

            return new CalibrationMonitorInfo(
                MonitorSelection.IndexFromDeviceName(stored.MonitorDeviceName),
                stored.MonitorResolution,
                current.Index,
                current.ResolutionKey);
        }
    }

    /// <summary>
    /// True when the stored reference describes a different screen from the one ARK is on. The
    /// pixels would still compare — to noise — and a threshold slider cannot fix noise, so the
    /// scan is skipped and the page says why instead.
    /// </summary>
    public bool MonitorMismatch => Monitor is { Mismatch: true };

    /// <summary>Gets the calibrated region for the current resolution.</summary>
    protected bool TryGetRegion(out Rectangle region) => Calibration.TryGetRegion(_regionKey, out region);

    /// <summary>True when the calibrated region currently matches the reference at/above <paramref name="thresholdPercent"/>.</summary>
    protected bool IsTargetVisible(double thresholdPercent)
    {
        if (!Calibration.TryGetRegion(_regionKey, out Rectangle region)) return false;

        // A reference from another screen compares to noise, and noise clears a low threshold
        // as readily as a real match does. "Never fires" is a bug people report; "fires at
        // random" is one they blame on the game.
        if (MonitorMismatch) return false;

        // The percentage rather than the boolean, so the scan's own number is what the page
        // shows. The two are the same comparison — MatchesReference thresholds on the mean
        // difference this percentage is derived from — and asking for the boolean would mean a
        // second capture per tick just to put a number on a card.
        var similarity = Sampler.SimilarityPercent(_regionKey, region);
        RecordSimilarity(similarity);
        return similarity is { } value && value >= Math.Clamp(thresholdPercent, 50, 100);
    }

    // ─── Live similarity ───────────────────────────────────────────────────────

    private readonly object _similarityGate = new();
    private double? _similarity;
    private long _similarityAt = long.MinValue;

    /// <summary>
    /// How long a cached reading stands before the page pays for a capture of its own. A running
    /// script refreshes it every scan; this only matters while one is not.
    /// </summary>
    private const int SimilarityRefreshMs = 400;

    private void RecordSimilarity(double? value)
    {
        lock (_similarityGate)
        {
            _similarity = value;
            _similarityAt = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Similarity of the region against its reference, 0–100, or null when there is nothing to
    /// compare. A running script's own scan feeds this; with the script stopped it captures on
    /// demand, throttled, because the Scripts page repaints on a timer and a full region grab per
    /// render would be a capture storm in service of one number.
    ///
    /// The number is the whole reason a threshold can be set rather than guessed: it moves while
    /// you watch, so the score of "target present" and the score of "target absent" can be read
    /// off your own screen and the cut-off put between them. Auto-Antidote has had this since it
    /// shipped; the five scripts on this base did not, and their sliders were set blind.
    /// </summary>
    public double? CurrentSimilarityPercent
    {
        get
        {
            if (!HasReference || MonitorMismatch) return null;
            if (!Calibration.TryGetRegion(_regionKey, out Rectangle region)) return null;

            lock (_similarityGate)
            {
                var now = Environment.TickCount64;

                // The sentinel is compared, not subtracted from — see ForegroundGate for what
                // arithmetic on long.MinValue did the last time.
                if (_similarityAt != long.MinValue && now - _similarityAt < SimilarityRefreshMs)
                    return _similarity;

                _similarity = Sampler.SimilarityPercent(_regionKey, region);
                _similarityAt = now;
                return _similarity;
            }
        }
    }

    protected override bool CanStart(out string? reason)
    {
        if (!HasRegion) { reason = Localizer.T("scripts.cannotstart.region"); return false; }
        if (!HasReference) { reason = Localizer.T("scripts.cannotstart.reference"); return false; }
        if (Monitor is { Mismatch: true } monitor)
        {
            // Refused at the press rather than started into a run that can never match: a script
            // that is "running" and silent is the state this whole wave exists to stop producing.
            reason = Localizer.T(
                "scripts.cannotstart.monitor",
                monitor.StoredIndex, monitor.StoredResolution, monitor.CurrentIndex, monitor.CurrentResolution);
            return false;
        }
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
            // The old snapshot belonged to the old rectangle — and, on a second monitor, to a
            // different screen. Dropping it is what forces the two to be captured together.
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
        if (capture.IsFlat)
        {
            // The black frame a fullscreen ARK hands the capture path, most often. Stored, it
            // would match every later black frame at 100 % — Take All clicking on every tick with
            // no inventory open is exactly that. Refused here with the reason, since the sampler
            // refuses it silently.
            Notifications.ShowWarning(Localizer.T("scripts.toast.referenceblank"));
            return false;
        }
        Sampler.CaptureReference(_regionKey, region);

        // The snapshot is what gets compared from here on, so the display it came off is the one
        // worth recording — the region may have been calibrated on a different screen.
        Calibration.StampRegionMonitor(_regionKey);

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
