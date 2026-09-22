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
    public string ForwardKey { get => _forwardKey.Value; set => _forwardKey.Value = value; }
    private readonly ArkKeySetting _forwardKey = new($"{Key}.forwardkey", ArkActions.MoveForward, "W");

    /// <summary>Hold the sprint key as well, instead of walking at normal speed.</summary>
    public bool Sprint { get; set; }

    /// <summary>The key held to sprint (ARK default is Left Shift).</summary>
    public string SprintKey { get => _sprintKey.Value; set => _sprintKey.Value = value; }
    private readonly ArkKeySetting _sprintKey = new($"{Key}.sprintkey", ArkActions.Run, "LeftShift");

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

    /// <summary>Follows ARK's Move Forward and Run unless the player typed keys of their own.</summary>
    public override void FollowArkKeys()
    {
        _forwardKey.Follow();
        _sprintKey.Follow();
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

                    // The effect of this script is the key going down, not a repeated press:
                    // it stays down for as long as ARK keeps focus, so one report per leg is
                    // the honest count. Alt-tabbing out and back reports a second one.
                    ReportEffect();
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
        try
        {
            Preferences.Set($"{Key}.sprint", Sprint);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            Sprint = Preferences.Get($"{Key}.sprint", false);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk LoadSettings failed"); }
    }
}
