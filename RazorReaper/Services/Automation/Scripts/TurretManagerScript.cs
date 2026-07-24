using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Turret Manager: when a turret's inventory is open (detected via the calibrated region + reference),
/// presses the transfer key to push ammo from your inventory into the turret. Calibrate the region on
/// a distinctive part of the open turret inventory, capture the reference with it open. Configurable
/// transfer key/presses. Best-effort framework — tune to your bindings and ammo layout in-game.
/// </summary>
public sealed class TurretManagerScript : CalibratableScriptBase
{
    private const string Key = "turret";
    private readonly IInputSimulator _input;

    public double MatchThresholdPercent { get; set; } = 90;
    public string TransferKey { get; set; } = "T";
    public int TransferPresses { get; set; } = 3;
    public int ScanIntervalMs { get; set; } = 500;

    public TurretManagerScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<TurretManagerScript> logger)
        : base(Key, "Turret Manager", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(ScanIntervalMs, async c =>
        {
            if (!IsTargetVisible(MatchThresholdPercent)) return;
            var vk = HotkeyParser.TryParseKey(TransferKey, out var k) ? k : 'T';
            for (var i = 0; i < Math.Clamp(TransferPresses, 1, 20); i++)
            {
                await _input.KeyPressAsync(vk, ct: c);
                await _input.DelayAsync(120, ct: c);
            }
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        TransferKey = string.IsNullOrWhiteSpace(TransferKey) ? "T" : TransferKey.Trim();
        TransferPresses = Math.Clamp(TransferPresses, 1, 20);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
        try
        {
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.transfer", TransferKey);
            Preferences.Set($"{Key}.presses", TransferPresses);
            Preferences.Set($"{Key}.scaninterval", ScanIntervalMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Manager SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            TransferKey = Preferences.Get($"{Key}.transfer", "T");
            TransferPresses = Preferences.Get($"{Key}.presses", 3);
            ScanIntervalMs = Preferences.Get($"{Key}.scaninterval", 500);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Turret Manager LoadSettings failed"); }
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        TransferPresses = Math.Clamp(TransferPresses, 1, 20);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
    }
}
