using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Noglin counter-measure: watches the calibrated HUD region for the Noglin mind-control icon. When
/// it appears, drops your FPS to 1 via the console (<c>t.maxfps 1</c>) so the attacker can't move
/// your character; after the icon has been gone for a few clean scans, restores your normal FPS.
/// Uses the existing console-command path (<see cref="IGameConsoleService"/>) — external input only.
/// </summary>
public sealed class NoglinScript : CalibratableScriptBase
{
    private const string Key = "noglin";
    private readonly IGameConsoleService _console;

    /// <summary>Similarity (50–100%) at/above which the mind-control icon counts as visible.</summary>
    public double MatchThresholdPercent { get; set; } = 90;

    /// <summary>Milliseconds between scans.</summary>
    public int ScanIntervalMs { get; set; } = 400;

    /// <summary>Consecutive icon-free scans before FPS is restored.</summary>
    public int RestoreAfterCleanScans { get; set; } = 3;

    /// <summary>FPS to clamp to while mind-controlled.</summary>
    public int ThrottledFps { get; set; } = 1;

    /// <summary>FPS to restore to afterwards.</summary>
    public int NormalFps { get; set; } = 1000;

    private bool _throttled;
    private int _cleanScans;

    public NoglinScript(
        IGameConsoleService console,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<NoglinScript> logger)
        : base(Key, "Noglin", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _console = console;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        _throttled = false;
        _cleanScans = 0;
        await RunLoopAsync(ScanIntervalMs, async c =>
        {
            var present = IsTargetVisible(MatchThresholdPercent);
            if (present && !_throttled)
            {
                await _console.SendCommandAsync($"t.maxfps {Math.Clamp(ThrottledFps, 1, 10)}", false, c);
                _throttled = true;
                _cleanScans = 0;
                TryActivity(Localizer.T("scripts.noglin.activity.throttled"), "warning", "scripts.noglin.activity.throttled");
                RaiseChanged();
            }
            else if (!present && _throttled)
            {
                if (++_cleanScans >= Math.Clamp(RestoreAfterCleanScans, 1, 20))
                {
                    await _console.SendCommandAsync($"t.maxfps {Math.Clamp(NormalFps, 30, 2000)}", false, c);
                    _throttled = false;
                    _cleanScans = 0;
                    TryActivity(Localizer.T("scripts.noglin.activity.restored"), "info", "scripts.noglin.activity.restored");
                    RaiseChanged();
                }
            }
        }, foregroundOnly: true, ct);
    }

    protected override void OnStopped()
    {
        // Never leave the game stuck at 1 FPS if we stop while throttled.
        if (_throttled)
        {
            // The try only ever saw the synchronous prefix: the discarded Task carried the real
            // failure — the game window gone — to the finalizer instead.
            _ = RestoreFpsAsync();
            _throttled = false;
        }
    }

    private async Task RestoreFpsAsync()
    {
        try { await _console.SendCommandAsync($"t.maxfps {Math.Clamp(NormalFps, 30, 2000)}", false, default); }
        catch (Exception ex) { Logger.LogWarning(ex, "Noglin FPS restore on stop failed"); }
    }

    public void SaveSettings()
    {
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
        RestoreAfterCleanScans = Math.Clamp(RestoreAfterCleanScans, 1, 20);
        try
        {
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
            Preferences.Set($"{Key}.scaninterval", ScanIntervalMs);
            Preferences.Set($"{Key}.cleanscans", RestoreAfterCleanScans);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Noglin SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
            ScanIntervalMs = Preferences.Get($"{Key}.scaninterval", 400);
            RestoreAfterCleanScans = Preferences.Get($"{Key}.cleanscans", 3);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Noglin LoadSettings failed"); }
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        ScanIntervalMs = Math.Clamp(ScanIntervalMs, 100, 5000);
        RestoreAfterCleanScans = Math.Clamp(RestoreAfterCleanScans, 1, 20);
    }
}
