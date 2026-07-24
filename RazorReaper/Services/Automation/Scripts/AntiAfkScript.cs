using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Anti-AFK: opens and closes your inventory on an interval to reset the server's idle-kick timer
/// while you're away. Pure keypress via <see cref="IInputSimulator"/>, only pulses while ARK is the
/// foreground window.
/// </summary>
public sealed class AntiAfkScript : AutomationScriptBase
{
    private const string Key = "antiafk";
    private readonly IInputSimulator _input;

    /// <summary>Inventory key to pulse (ARK default is I).</summary>
    public string InventoryKey { get; set; } = "I";

    /// <summary>Seconds between pulses.</summary>
    public int IntervalSeconds { get; set; } = 600;

    public AntiAfkScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AntiAfkScript> logger)
        : base(Key, "Anti-AFK", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(Math.Clamp(IntervalSeconds, 30, 3600) * 1000, ct); }
            catch (OperationCanceledException) { return; }

            if (!Foreground.IsGameForeground()) continue;

            var vk = HotkeyParser.TryParseKey(InventoryKey, out var k) ? k : 'I';
            await _input.KeyPressAsync(vk, ct: ct);      // open inventory
            await _input.DelayAsync(600, ct: ct);
            await _input.KeyPressAsync(vk, ct: ct);      // close inventory
        }
    }

    public void SaveSettings()
    {
        InventoryKey = string.IsNullOrWhiteSpace(InventoryKey) ? "I" : InventoryKey.Trim();
        IntervalSeconds = Math.Clamp(IntervalSeconds, 30, 3600);
        try
        {
            Preferences.Set($"{Key}.invkey", InventoryKey);
            Preferences.Set($"{Key}.interval", IntervalSeconds);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Anti-AFK SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            InventoryKey = Preferences.Get($"{Key}.invkey", "I");
            IntervalSeconds = Preferences.Get($"{Key}.interval", 600);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Anti-AFK LoadSettings failed"); }
        IntervalSeconds = Math.Clamp(IntervalSeconds, 30, 3600);
    }
}
