using Microsoft.Extensions.Logging;

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
    private const int SpaceVk = 0x20;
    private readonly IInputSimulator _input;

    public AstroScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AstroScript> logger)
        : base(Key, "Astro", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
    }

    protected override bool CanStart(out string? reason)
    {
        if (!Foreground.IsGameForeground())
        {
            reason = "Focus ARK first — Astro fires its sequence immediately on start.";
            return false;
        }
        reason = null;
        return true;
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        await _input.ClickAsync(MouseButton.Left, ct: ct);
        await _input.DelayAsync(1925, ct: ct);
        await _input.KeyPressAsync(SpaceVk, ct: ct);

        var end = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < end && !ct.IsCancellationRequested)
        {
            await _input.ClickAsync(MouseButton.Left, ct: ct);
            await _input.DelayAsync(20, ct: ct);
        }
        // Returning ends the one-shot; the base flips the state back to Off.
    }
}
