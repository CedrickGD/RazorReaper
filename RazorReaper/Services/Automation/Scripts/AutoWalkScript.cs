using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Holds the forward key down so the character keeps running hands-free (AFK travel / treadmill
/// tames), optionally with sprint held as well. Keys are held only while ARK is the foreground
/// window and are always released when the game loses focus or the script stops, so a keypress
/// never leaks into another app.
/// </summary>
public sealed class AutoWalkScript : AutomationScriptBase
{
    private const string Key = "autowalk";
    private readonly IInputSimulator _input;

    /// <summary>The movement key to hold (ARK default forward is W).</summary>
    public string ForwardKey { get; set; } = "W";

    /// <summary>Hold the sprint key as well, instead of walking at normal speed.</summary>
    public bool Sprint { get; set; }

    /// <summary>The key held to sprint (ARK default is Left Shift).</summary>
    public string SprintKey { get; set; } = "LeftShift";

    public AutoWalkScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<AutoWalkScript> logger)
        : base(Key, "Auto-Walk", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var forwardVk = HotkeyParser.TryParseKey(ForwardKey, out var f) ? f : 'W';

        // Resolved once per run: flipping the toggle mid-run would otherwise leave the old key held.
        var sprint = Sprint;
        var sprintVk = HotkeyParser.TryParseKey(SprintKey, out var s) ? s : 0xA0;

        var isDown = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var foreground = Foreground.IsGameForeground();

                if (foreground && !isDown)
                {
                    // Sprint goes down first: ARK reads it as a modifier on the movement that
                    // follows, and pressing it after forward can start the run as a walk.
                    if (sprint) HoldKey(_input, sprintVk);
                    HoldKey(_input, forwardVk);
                    isDown = true;
                }
                else if (!foreground && isDown)
                {
                    ReleaseKey(_input, forwardVk);
                    if (sprint) ReleaseKey(_input, sprintVk);
                    isDown = false;
                }

                await Task.Delay(150, ct);
            }
        }
        finally
        {
            // The scaffold drains held keys on every exit path anyway; doing it here as well
            // gets them up the moment the loop unwinds instead of one stop-path later.
            ReleaseHeldKeys();
        }
    }

    public void SaveSettings()
    {
        ForwardKey = string.IsNullOrWhiteSpace(ForwardKey) ? "W" : ForwardKey.Trim();
        SprintKey = string.IsNullOrWhiteSpace(SprintKey) ? "LeftShift" : SprintKey.Trim();
        try
        {
            Preferences.Set($"{Key}.forwardkey", ForwardKey);
            Preferences.Set($"{Key}.sprint", Sprint);
            Preferences.Set($"{Key}.sprintkey", SprintKey);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            ForwardKey = Preferences.Get($"{Key}.forwardkey", ArkKeyDefaults.For(ArkActions.MoveForward, "W"));
            Sprint = Preferences.Get($"{Key}.sprint", false);
            SprintKey = Preferences.Get($"{Key}.sprintkey", ArkKeyDefaults.For(ArkActions.Run, "LeftShift"));
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk LoadSettings failed"); }
    }
}
