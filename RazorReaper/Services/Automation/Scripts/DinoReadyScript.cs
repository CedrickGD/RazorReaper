using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Dino Ready (single-stat leveler): clicks the calibrated stat "+" button a set number of times to
/// pour levels into one stat while a tamed dino's inventory is open. Calibrate the region over the "+"
/// button of the stat you want; press Start (or the hotkey) to level. One-shot. A simplified take on
/// the full priority/points leveler — the multi-stat version needs per-stat calibration (future).
/// </summary>
public sealed class DinoReadyScript : CalibratableScriptBase
{
    private const string Key = "dinoready";
    private readonly IInputSimulator _input;

    /// <summary>How many times to click the "+" button.</summary>
    public int Presses { get; set; } = 10;

    /// <summary>Milliseconds between clicks.</summary>
    public int ClickDelayMs { get; set; } = 80;

    public DinoReadyScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<DinoReadyScript> logger)
        : base(Key, "Dino Ready", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    // Only needs the region (the button to click) — no reference snapshot required.
    protected override bool CanStart(out string? reason)
    {
        if (!HasRegion) { reason = "Calibrate the stat + button region first."; return false; }
        if (!Foreground.IsGameForeground()) { reason = "Open the dino's inventory in ARK first, then start."; return false; }
        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!TryGetRegion(out Rectangle region)) return;
        var target = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        var presses = Math.Clamp(Presses, 1, 200);
        for (var i = 0; i < presses && !ct.IsCancellationRequested; i++)
        {
            await _input.ClickAsync(MouseButton.Left, target, ct: ct);
            await _input.DelayAsync(Math.Clamp(ClickDelayMs, 20, 1000), ct: ct);
        }
        // one-shot
    }

    public void SaveSettings()
    {
        Presses = Math.Clamp(Presses, 1, 200);
        ClickDelayMs = Math.Clamp(ClickDelayMs, 20, 1000);
        try
        {
            Preferences.Set($"{Key}.presses", Presses);
            Preferences.Set($"{Key}.clickdelay", ClickDelayMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Dino Ready SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            Presses = Preferences.Get($"{Key}.presses", 10);
            ClickDelayMs = Preferences.Get($"{Key}.clickdelay", 80);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Dino Ready LoadSettings failed"); }
        Presses = Math.Clamp(Presses, 1, 200);
        ClickDelayMs = Math.Clamp(ClickDelayMs, 20, 1000);
    }
}
