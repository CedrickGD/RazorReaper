using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Yutyrannus courage-roar spammer: presses the roar key on a fixed interval while mounted, so the
/// courage/fear buff stays up during a fight without manual spamming. Pure keypress via
/// <see cref="IInputSimulator"/>, gated on ARK being the foreground window.
/// </summary>
public sealed class YutyScript : AutomationScriptBase
{
    private const string Key = "yuty";
    private readonly IInputSimulator _input;

    /// <summary>Key that triggers the roar (ARK default is C while mounted).</summary>
    public string RoarKey { get; set; } = "C";

    /// <summary>Milliseconds between roars.</summary>
    public int IntervalMs { get; set; } = 5000;

    public YutyScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<YutyScript> logger)
        : base(Key, "Yuty", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        await RunLoopAsync(IntervalMs, async c =>
        {
            var vk = HotkeyParser.TryParseKey(RoarKey, out var k) ? k : 'C';
            await _input.KeyPressAsync(vk, ct: c);
        }, foregroundOnly: true, ct);
    }

    public void SaveSettings()
    {
        RoarKey = string.IsNullOrWhiteSpace(RoarKey) ? "C" : RoarKey.Trim();
        IntervalMs = Math.Clamp(IntervalMs, 200, 60000);
        try
        {
            Preferences.Set($"{Key}.roarkey", RoarKey);
            Preferences.Set($"{Key}.interval", IntervalMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Yuty SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            RoarKey = Preferences.Get($"{Key}.roarkey", "C");
            IntervalMs = Preferences.Get($"{Key}.interval", 5000);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Yuty LoadSettings failed"); }
        IntervalMs = Math.Clamp(IntervalMs, 200, 60000);
    }
}
