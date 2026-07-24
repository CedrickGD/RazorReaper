using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Take All: rapidly clicks the container's "Take All" button while it is on screen. To avoid
/// clicking into the game world, it only clicks when the calibrated button region still matches the
/// reference snapshot (captured with the button visible). Pure screen-capture + click,
/// foreground-gated. Region/reference plumbing comes from <see cref="CalibratableScriptBase"/>.
/// </summary>
public sealed class TakeAllScript : CalibratableScriptBase
{
    private const string Key = "takeall";
    private readonly IInputSimulator _input;

    /// <summary>Milliseconds between scan + click attempts.</summary>
    public int ClickIntervalMs { get; set; } = 50;

    /// <summary>Similarity (50–100%) at/above which the button counts as visible.</summary>
    public double MatchThresholdPercent { get; set; } = 90;

    public TakeAllScript(
        IInputSimulator input,
        IScreenSampler sampler,
        ICalibrationService calibration,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<TakeAllScript> logger)
        : base(Key, "Take All", string.Empty, sampler, calibration, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(ClickIntervalMs, async c =>
        {
            if (TryGetRegion(out Rectangle region) && IsTargetVisible(MatchThresholdPercent))
            {
                var target = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
                await _input.ClickAsync(MouseButton.Left, target, ct: c);
            }
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        ClickIntervalMs = Math.Clamp(ClickIntervalMs, 20, 2000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
        try
        {
            Preferences.Set($"{Key}.interval", ClickIntervalMs);
            Preferences.Set($"{Key}.threshold", MatchThresholdPercent);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Take All SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            ClickIntervalMs = Preferences.Get($"{Key}.interval", 50);
            MatchThresholdPercent = Preferences.Get($"{Key}.threshold", 90.0);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Take All LoadSettings failed"); }
        ClickIntervalMs = Math.Clamp(ClickIntervalMs, 20, 2000);
        MatchThresholdPercent = Math.Clamp(MatchThresholdPercent, 50, 100);
    }
}
