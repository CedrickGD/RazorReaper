using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Flak armor-swap: watches a calibrated region for a low-durability / broken-armor indicator and,
/// when it shows, presses a hotbar key bound to your replacement armor to re-equip. Calibrate the
/// region on the durability warning and capture the reference with it visible. Best-effort framework —
/// bind your replacement piece to the equip key and tune in-game.
/// </summary>
public sealed class FlakScript : CalibratableScriptBase
{
    private const string Key = "flak";
    private readonly IInputSimulator _input;

    public double MatchThresholdPercent { get; set; } = 90;

    /// <summary>Hotbar key bound to the replacement armor piece.</summary>
    public string EquipKey { get; set; } = "0";

    public int ScanIntervalMs { get; set; } = 1000;

    public FlakScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<FlakScript> logger)
        : base(Key, "Flak", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(ScanIntervalMs, async c =>
        {
            if (!IsTargetVisible(MatchThresholdPercent)) return;
            var vk = HotkeyParser.TryParseKey(EquipKey, out var k) ? k : '0';
            await _input.KeyPressAsync(vk, ct: c);
            await _input.DelayAsync(800, ct: c); // brief cooldown so one warning = one swap
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        EquipKey = string.IsNullOrWhiteSpace(EquipKey) ? "0" : EquipKey.Trim();
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 200, 5000);
        try
        {
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.equip", EquipKey);
            Preferences.Set($"{Key}.scaninterval", ScanIntervalMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Flak SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            EquipKey = Preferences.Get($"{Key}.equip", "0");
            ScanIntervalMs = Preferences.Get($"{Key}.scaninterval", 1000);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Flak LoadSettings failed"); }
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 200, 5000);
    }
}
