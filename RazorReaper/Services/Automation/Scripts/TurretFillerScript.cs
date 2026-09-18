using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>What one run does with the turret inventory that is open.</summary>
public enum TurretFillMode
{
    /// <summary>Sends a set number of transfers, so every turret on a wall gets the same.</summary>
    EvenSplit,
    /// <summary>Keeps transferring until nothing moves any more, up to the same press cap.</summary>
    Fill,
    /// <summary>Types the search text and stops — the presses stay yours.</summary>
    FilterOnly
}

/// <summary>How much of the hovered stack one press moves.</summary>
public enum TurretFillAmount
{
    /// <summary>The whole stack — the transfer key on its own.</summary>
    WholeStack,
    /// <summary>Half of it — Shift and the transfer key.</summary>
    HalfStack,
    /// <summary>One item — Ctrl and the transfer key.</summary>
    SingleItem
}

/// <summary>
/// Turret Filler: acts once on the turret inventory you have open. Hover your own ammo, tap the
/// hotkey, and it sends the transfer key a set number of times (Even split), until the game stops
/// taking (Fill), or only narrows the list with the inventory search box (Filter only). Calibrate
/// the region on a distinctive part of the open turret inventory; the filter modes also need the
/// search box and the first ammo slot captured as points.
/// </summary>
public sealed class TurretFillerScript : CalibratableScriptBase
{
    private const string Key = "turretfill";
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkBack = 0x08;

    /// <summary>Backspaces sent before the filter text — ARK's search box has no "select all".</summary>
    private const int ClearKeystrokes = 40;

    /// <summary>A channel has to move by more than this for its pixel to count as changed.</summary>
    private const byte ChangeTolerance = 24;

    /// <summary>The name of the calibrated point over the inventory search box.</summary>
    public const string SearchPointName = "turretfill-search";

    /// <summary>The name of the calibrated point over the first ammo slot.</summary>
    public const string SlotPointName = "turretfill-slot";

    private readonly IInputSimulator _input;

    public TurretFillMode Mode { get; set; } = TurretFillMode.EvenSplit;

    public TurretFillAmount Amount { get; set; } = TurretFillAmount.WholeStack;

    /// <summary>Presses one run sends — exact in Even split, a hard cap in Fill.</summary>
    public int PressesPerTurret { get; set; } = 1;

    public string TransferKey { get; set; } = "T";

    /// <summary>Milliseconds between a press and the capture that judges it.</summary>
    public int PressDelayMs { get; set; } = 200;

    /// <summary>Similarity % at which the turret inventory counts as open.</summary>
    public double MatchThresholdPercent { get; set; } = 90;

    /// <summary>How much of the calibrated area has to change for a press to count as moved.</summary>
    public double ChangeThresholdPercent { get; set; } = 0.2;

    public bool UseFilter { get; set; }

    /// <summary>Part of the item name as the game spells it, typed into the search box.</summary>
    public string FilterText { get; set; } = "";

    /// <summary>Milliseconds the filtered list gets to redraw before the first press.</summary>
    public int FilterSettleMs { get; set; } = 250;

    public TurretFillerScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<TurretFillerScript> logger)
        : base(Key, "Turret Filler", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
        LoadSettings();
    }

    /// <summary>
    /// The region check proves a turret inventory is open, and the change check proves pixels in
    /// that rectangle moved between two presses. Neither proves what moved, or which way: a stack
    /// taken out of the turret, a countdown ticking over, or a tooltip fading all read as a
    /// successful transfer, and a server that answers slower than the press delay reads as a full
    /// turret and ends the run.
    /// </summary>
    public override bool IsExperimental => true;

    // ─── Point calibration ─────────────────────────────────────────────────────

    public bool HasSearchPoint => Calibration.HasPoint(SearchPointName);

    public bool HasSlotPoint => Calibration.HasPoint(SlotPointName);

    public bool TryGetPoint(string name, out Point point) => Calibration.TryGetPoint(name, out point);

    public Task<CalibrationPoint?> CapturePointAsync(string name, IProgress<int>? countdown, CancellationToken ct = default)
        => Calibration.CapturePointAsync(name, 3, countdown, ct);

    public bool ClearPoint(string name) => Calibration.DeletePoint(name);

    // ─── Run ───────────────────────────────────────────────────────────────────

    // The region and the reference come from the base; the two added refusals are the ones a
    // hotkey-driven script needs, because the tile is not where it gets started from.
    protected override bool CanStart(out string? reason)
    {
        if (!base.CanStart(out reason)) return false;
        if (!Foreground.IsGameForeground()) { reason = Localizer.T("scripts.cannotstart.turretinventory"); return false; }
        if (UseFilter && !(HasSearchPoint && HasSlotPoint))
        {
            reason = Localizer.T("scripts.cannotstart.filterpoints");
            return false;
        }
        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        // The only thing between a mistimed hotkey and a transfer key going into the world.
        if (!IsTargetVisible(MatchThresholdPercent))
        {
            TryActivity(Localizer.T("scripts.turretfill.activity.noinventory"), "warning", "scripts.turretfill.activity.noinventory");
            return;
        }

        if (UseFilter) await ApplyFilterAsync(ct);
        if (Mode == TurretFillMode.FilterOnly) return;
        if (!TryGetRegion(out Rectangle region)) return;

        var vk = HotkeyParser.TryParseKey(TransferKey, out var k) ? k : 'T';
        var modifier = Amount switch
        {
            TurretFillAmount.HalfStack => VkShift,
            TurretFillAmount.SingleItem => VkControl,
            _ => 0
        };
        var presses = Math.Clamp(PressesPerTurret, 1, 20);
        var threshold = Math.Clamp(ChangeThresholdPercent, 0.02, 10);

        var before = Sampler.CaptureRegion(region);
        var sent = 0;
        var moved = 0;
        var quiet = 0;
        var change = 0.0;

        for (var i = 0; i < presses && !ct.IsCancellationRequested; i++)
        {
            // Per press, not once per run: an alt-tab between two presses sends the rest of them
            // into whatever window took the focus.
            if (!Foreground.IsGameForeground()) break;

            if (modifier != 0) HoldKey(_input, modifier);
            await _input.KeyPressAsync(vk, ct: ct);
            if (modifier != 0) ReleaseKey(_input, modifier);
            sent++;

            await _input.DelayAsync(Math.Clamp(PressDelayMs, 60, 2000), 0.15, ct);

            var after = Sampler.CaptureRegion(region);
            change = ChangedPercent(before, after);
            before = after;

            if (change >= threshold)
            {
                moved++;
                quiet = 0;
                ReportEffect();
                continue;
            }

            // Fill is topping a turret up, so the first press the game swallows is the answer.
            // Even split owes the user an exact count, so it gives one press back to lag before
            // it decides the turret is full.
            if (Mode == TurretFillMode.Fill || ++quiet >= 2) break;
        }

        TryActivity(
            Localizer.T("scripts.turretfill.activity.done", moved, sent, change.ToString("0.00", CultureInfo.InvariantCulture)),
            moved > 0 ? "success" : "warning",
            "scripts.turretfill.activity.done");
    }

    private async Task ApplyFilterAsync(CancellationToken ct)
    {
        if (!Calibration.TryGetPoint(SearchPointName, out Point search)) return;
        if (!Calibration.TryGetPoint(SlotPointName, out Point slot)) return;

        await _input.ClickAsync(MouseButton.Left, search, ct: ct);
        await _input.DelayAsync(150, ct: ct);
        for (var i = 0; i < ClearKeystrokes; i++) await _input.KeyPressAsync(VkBack, ct: ct);
        await _input.TypeTextAsync(FilterText, ct: ct);
        ReportEffect();
        await _input.DelayAsync(Math.Clamp(FilterSettleMs, 100, 2000), ct: ct);

        // A click, not just a move: it takes keyboard focus out of the search box, so the
        // transfer key that follows is a key bind again and not one more typed letter.
        await _input.ClickAsync(MouseButton.Left, slot, ct: ct);
        await _input.DelayAsync(80, ct: ct);
    }

    /// <summary>
    /// The share of pixels that moved between two captures, 0–100. A share rather than a mean:
    /// one stack leaving a slot changes a few hundred pixels out of tens of thousands, and a mean
    /// over the whole rectangle drowns that in the frame around it. Captures of different sizes
    /// and empty ones score zero — a failed capture has no business claiming anything moved.
    /// </summary>
    internal static double ChangedPercent(ScreenCapture a, ScreenCapture b)
    {
        if (a.IsEmpty || b.IsEmpty) return 0;
        if (a.Width != b.Width || a.Height != b.Height || a.Bgra.Length != b.Bgra.Length) return 0;

        var pixels = a.Bgra.Length / 4;
        if (pixels == 0) return 0;

        var changed = 0;
        for (var i = 0; i < pixels; i++)
        {
            var p = i * 4;
            if (Math.Abs(a.Bgra[p] - b.Bgra[p]) > ChangeTolerance
                || Math.Abs(a.Bgra[p + 1] - b.Bgra[p + 1]) > ChangeTolerance
                || Math.Abs(a.Bgra[p + 2] - b.Bgra[p + 2]) > ChangeTolerance)
            {
                changed++;
            }
        }
        return changed * 100.0 / pixels;
    }

    public void SaveSettings()
    {
        TransferKey = string.IsNullOrWhiteSpace(TransferKey) ? "T" : TransferKey.Trim();
        FilterText = FilterText?.Trim() ?? "";
        if (FilterText.Length > 40) FilterText = FilterText[..40];
        PressesPerTurret = Math.Clamp(PressesPerTurret, 1, 20);
        PressDelayMs = Math.Clamp(PressDelayMs, 60, 2000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ChangeThresholdPercent = Math.Clamp(ChangeThresholdPercent, 0.02, 10);
        FilterSettleMs = Math.Clamp(FilterSettleMs, 100, 2000);
        try
        {
            Preferences.Set($"{Key}.mode", (int)Mode);
            Preferences.Set($"{Key}.amount", (int)Amount);
            Preferences.Set($"{Key}.presses", PressesPerTurret);
            Preferences.Set($"{Key}.transfer", TransferKey);
            Preferences.Set($"{Key}.pressdelay", PressDelayMs);
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.changethreshold", ChangeThresholdPercent);
            Preferences.Set($"{Key}.usefilter", UseFilter);
            Preferences.Set($"{Key}.filtertext", FilterText);
            Preferences.Set($"{Key}.settle", FilterSettleMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Filler SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            var mode = Preferences.Get($"{Key}.mode", (int)TurretFillMode.EvenSplit);
            Mode = Enum.IsDefined(typeof(TurretFillMode), mode) ? (TurretFillMode)mode : TurretFillMode.EvenSplit;
            var amount = Preferences.Get($"{Key}.amount", (int)TurretFillAmount.WholeStack);
            Amount = Enum.IsDefined(typeof(TurretFillAmount), amount) ? (TurretFillAmount)amount : TurretFillAmount.WholeStack;
            PressesPerTurret = Preferences.Get($"{Key}.presses", 1);
            TransferKey = Preferences.Get($"{Key}.transfer", ArkKeyDefaults.For(ArkActions.TransferItem, "T"));
            PressDelayMs = Preferences.Get($"{Key}.pressdelay", 200);
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            ChangeThresholdPercent = Preferences.Get($"{Key}.changethreshold", 0.2);
            UseFilter = Preferences.Get($"{Key}.usefilter", false);
            FilterText = Preferences.Get($"{Key}.filtertext", "");
            FilterSettleMs = Preferences.Get($"{Key}.settle", 250);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Filler LoadSettings failed"); }
        PressesPerTurret = Math.Clamp(PressesPerTurret, 1, 20);
        PressDelayMs = Math.Clamp(PressDelayMs, 60, 2000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ChangeThresholdPercent = Math.Clamp(ChangeThresholdPercent, 0.02, 10);
        FilterSettleMs = Math.Clamp(FilterSettleMs, 100, 2000);
    }
}
