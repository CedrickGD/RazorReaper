using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Inv Size (experimental): holds Shift and rapidly right-clicks to inflate visible inventory size
/// via the blueprint trick. Set up your blueprint hotkeys first, hover the hotbar item, then start.
/// Warning (as in the tool it mirrors): 4000+ slots in single-player can cause severe lag and items
/// may be lost if you drop a bag or die. Pure input, foreground-gated; Shift is always released on stop.
/// </summary>
public sealed class InvSizeScript : AutomationScriptBase
{
    private const string Key = "invsize";
    private const int VkShift = 0x10;
    private readonly IInputSimulator _input;

    /// <summary>Milliseconds between Shift+right-click pulses.</summary>
    public int IntervalMs { get; set; } = 60;

    public InvSizeScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<InvSizeScript> logger)
        : base(Key, "Inv Size", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Foreground.IsGameForeground())
                {
                    HoldKey(_input, VkShift);
                    await _input.ClickAsync(MouseButton.Right, ct: ct);
                    ReleaseKey(_input, VkShift);
                }
                await Task.Delay(Math.Clamp(IntervalMs, 20, 2000), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Cancellation lands inside ClickAsync more often than anywhere else, which is
            // exactly between the Shift going down and coming back up.
            ReleaseHeldKeys();
        }
    }

    public void SaveSettings()
    {
        IntervalMs = Math.Clamp(IntervalMs, 20, 2000);
        try { Preferences.Set($"{Key}.interval", IntervalMs); }
        catch (Exception ex) { Logger.LogWarning(ex, "Inv Size SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try { IntervalMs = Preferences.Get($"{Key}.interval", 60); }
        catch (Exception ex) { Logger.LogWarning(ex, "Inv Size LoadSettings failed"); }
        IntervalMs = Math.Clamp(IntervalMs, 20, 2000);
    }
}
