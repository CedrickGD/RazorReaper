using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Astrocetus downward-teleport helper: a one-shot input sequence (left-click, wait 1.925 s, press
/// space, then ~50 clicks/second for 5 seconds). Fire it while in first-person looking straight down
/// with the Astro tilted down. Runs once per start (not a loop) and then stops itself. Doesn't work
/// in single-player and isn't 100% reliable — same caveat as the tool it mirrors.
/// </summary>
public sealed class AstroScript : AutomationScriptBase
{
    private const string Key = "astro";

    // Not scanned from Input.ini: the tilt-and-drop trick needs the literal Space bar, not ARK's
    // Jump action — a player who moved Jump elsewhere still dismounts the Astro with Space.
    private const int SpaceVk = 0x20;
    private readonly IInputSimulator _input;

    public AstroScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<AstroScript> logger)
        : base(Key, "Astro", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
    }

    /// <summary>
    /// Fixed timings from start to finish: a click, 1.925 s, space, then five seconds of
    /// clicking. Nothing reads the screen, so a server that lags through the window produces
    /// the same keystrokes and no teleport, and the script cannot tell the two apart.
    /// </summary>
    public override bool IsExperimental => true;

    protected override bool CanStart(out string? reason)
    {
        if (!Foreground.IsGameForeground())
        {
            reason = Localizer.T("scripts.cannotstart.astro");
            return false;
        }
        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        await _input.ClickAsync(MouseButton.Left, ct: ct);
        ReportEffect();
        await _input.DelayAsync(1925, ct: ct);
        await _input.KeyPressAsync(SpaceVk, ct: ct);
        ReportEffect();

        var end = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < end && !ct.IsCancellationRequested)
        {
            await _input.ClickAsync(MouseButton.Left, ct: ct);
            ReportEffect();
            await _input.DelayAsync(20, ct: ct);
        }
        // Returning ends the one-shot; the base flips the state back to Off.
    }
}
