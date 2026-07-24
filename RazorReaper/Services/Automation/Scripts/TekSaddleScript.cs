using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Tek Saddle rapid-fire: while the Tek Saddle buff is visible in the calibrated HUD region AND you
/// physically hold left-click, spams extra left-clicks to maximize attack/ability output. The hold
/// check (GetAsyncKeyState) means it only fires while you're actually shooting, and the buff check
/// means it only fires while on the saddle — so it never clicks in the open world.
/// </summary>
public sealed class TekSaddleScript : CalibratableScriptBase
{
    private const string Key = "teksaddle";
    private const int VkLButton = 0x01;
    private readonly IInputSimulator _input;

    /// <summary>Similarity (50–100%) at/above which the buff counts as active.</summary>
    public double MatchThresholdPercent { get; set; } = 90;

    /// <summary>Milliseconds between spammed clicks (20 ≈ 50 clicks/sec).</summary>
    public int ClickDelayMs { get; set; } = 20;

    public TekSaddleScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<TekSaddleScript> logger)
        : base(Key, "Tek Saddle", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(ClickDelayMs, async c =>
        {
            var holdingLeft = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
            if (holdingLeft && IsTargetVisible(MatchThresholdPercent))
                await _input.ClickAsync(MouseButton.Left, ct: c);
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ClickDelayMs = Math.Clamp(ClickDelayMs, 5, 500);
        try
        {
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.clickdelay", ClickDelayMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Tek Saddle SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            ClickDelayMs = Preferences.Get($"{Key}.clickdelay", 20);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Tek Saddle LoadSettings failed"); }
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ClickDelayMs = Math.Clamp(ClickDelayMs, 5, 500);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
