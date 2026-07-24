using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Mammoth war-drum AFK farm: alternates left/right clicks on an interval to keep drumming the war
/// drum for the courage-style buff without holding the mouse. Pure clicks via
/// <see cref="IInputSimulator"/>, gated on ARK being the foreground window.
/// </summary>
public sealed class MammothScript : AutomationScriptBase
{
    private const string Key = "mammoth";
    private readonly IInputSimulator _input;

    /// <summary>Milliseconds between drum cycles (one cycle = a left then a right click).</summary>
    public int IntervalMs { get; set; } = 700;

    public MammothScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<MammothScript> logger)
        : base(Key, "Mammoth", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(IntervalMs, async c =>
        {
            await _input.ClickAsync(MouseButton.Left, ct: c);
            await _input.DelayAsync(150, ct: c);
            await _input.ClickAsync(MouseButton.Right, ct: c);
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        IntervalMs = Math.Clamp(IntervalMs, 100, 10000);
        try { Preferences.Set($"{Key}.interval", IntervalMs); }
        catch (Exception ex) { Logger.LogWarning(ex, "Mammoth SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try { IntervalMs = Preferences.Get($"{Key}.interval", 700); }
        catch (Exception ex) { Logger.LogWarning(ex, "Mammoth LoadSettings failed"); }
        IntervalMs = Math.Clamp(IntervalMs, 100, 10000);
    }
}
