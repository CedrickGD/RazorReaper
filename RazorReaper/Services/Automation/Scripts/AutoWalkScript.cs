using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Holds the forward key down so the character keeps running hands-free (AFK travel / treadmill
/// tames). The key is held only while ARK is the foreground window and is always released when the
/// game loses focus or the script stops, so the keypress never leaks into another app.
/// </summary>
public sealed class AutoWalkScript : AutomationScriptBase
{
    private const string Key = "autowalk";
    private readonly IInputSimulator _input;

    /// <summary>The movement key to hold (ARK default forward is W).</summary>
    public string ForwardKey { get; set; } = "W";

    public AutoWalkScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AutoWalkScript> logger)
        : base(Key, "Auto-Walk", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var vk = HotkeyParser.TryParseKey(ForwardKey, out var k) ? k : 'W';
        var isDown = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var foreground = Foreground.IsGameForeground();
                if (foreground && !isDown) { _input.KeyDown(vk); isDown = true; }
                else if (!foreground && isDown) { _input.KeyUp(vk); isDown = false; }
                await Task.Delay(150, ct);
            }
        }
        finally
        {
            if (isDown)
            {
                try { _input.KeyUp(vk); } catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk key release failed"); }
            }
        }
    }

    public void SaveSettings()
    {
        ForwardKey = string.IsNullOrWhiteSpace(ForwardKey) ? "W" : ForwardKey.Trim();
        try { Preferences.Set($"{Key}.forwardkey", ForwardKey); }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try { ForwardKey = Preferences.Get($"{Key}.forwardkey", "W"); }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-Walk LoadSettings failed"); }
    }
}
