using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Exo-Mek fuel/element farming (Gen2-style): with a transmitter/terminal in front of you, opens your
/// inventory, filters the search for "exo" and repeatedly presses the transfer key to pull the
/// filtered items, then closes and repeats. Keys/counts/delays are configurable for your bindings.
/// Pure input via <see cref="IInputSimulator"/>, foreground-gated.
/// </summary>
public sealed class ExoSuitScript : AutomationScriptBase
{
    private const string Key = "exosuit";
    private readonly IInputSimulator _input;

    /// <summary>Key that opens your inventory (default: I).</summary>
    public string AccessKey { get; set; } = "I";

    /// <summary>Text typed into the inventory search filter.</summary>
    public string SearchTerm { get; set; } = "exo";

    /// <summary>Transfer key pressed to move the filtered items.</summary>
    public string TransferKey { get; set; } = "T";

    /// <summary>How many times the transfer key is pressed per cycle.</summary>
    public int TransferPresses { get; set; } = 5;

    /// <summary>Milliseconds between transfer presses.</summary>
    public int PerPressDelayMs { get; set; } = 120;

    /// <summary>Milliseconds between cycles.</summary>
    public int RepeatDelayMs { get; set; } = 800;

    public ExoSuitScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<ExoSuitScript> logger)
        : base(Key, "Exo Suit", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(RepeatDelayMs, async c =>
        {
            var accessVk = HotkeyParser.TryParseKey(AccessKey, out var ak) ? ak : 'I';
            var transferVk = HotkeyParser.TryParseKey(TransferKey, out var tk) ? tk : 'T';

            await _input.KeyPressAsync(accessVk, ct: c);          // open inventory
            await _input.DelayAsync(350, ct: c);
            if (!string.IsNullOrWhiteSpace(SearchTerm))
                await _input.TypeTextAsync(SearchTerm, ct: c);    // filter to "exo"
            await _input.DelayAsync(200, ct: c);

            for (var i = 0; i < Math.Clamp(TransferPresses, 1, 50); i++)
            {
                await _input.KeyPressAsync(transferVk, ct: c);
                if (i < TransferPresses - 1)
                    await _input.DelayAsync(Math.Clamp(PerPressDelayMs, 10, 2000), ct: c);
            }

            await _input.DelayAsync(150, ct: c);
            await _input.KeyPressAsync(accessVk, ct: c);          // close inventory
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        AccessKey = string.IsNullOrWhiteSpace(AccessKey) ? "I" : AccessKey.Trim();
        TransferKey = string.IsNullOrWhiteSpace(TransferKey) ? "T" : TransferKey.Trim();
        SearchTerm = SearchTerm?.Trim() ?? "exo";
        TransferPresses = Math.Clamp(TransferPresses, 1, 50);
        PerPressDelayMs = Math.Clamp(PerPressDelayMs, 10, 2000);
        RepeatDelayMs = Math.Clamp(RepeatDelayMs, 100, 20000);
        try
        {
            Preferences.Set($"{Key}.access", AccessKey);
            Preferences.Set($"{Key}.search", SearchTerm);
            Preferences.Set($"{Key}.transfer", TransferKey);
            Preferences.Set($"{Key}.presses", TransferPresses);
            Preferences.Set($"{Key}.perpress", PerPressDelayMs);
            Preferences.Set($"{Key}.repeat", RepeatDelayMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Exo Suit SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            AccessKey = Preferences.Get($"{Key}.access", "I");
            SearchTerm = Preferences.Get($"{Key}.search", "exo");
            TransferKey = Preferences.Get($"{Key}.transfer", "T");
            TransferPresses = Preferences.Get($"{Key}.presses", 5);
            PerPressDelayMs = Preferences.Get($"{Key}.perpress", 120);
            RepeatDelayMs = Preferences.Get($"{Key}.repeat", 800);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Exo Suit LoadSettings failed"); }
        TransferPresses = Math.Clamp(TransferPresses, 1, 50);
        PerPressDelayMs = Math.Clamp(PerPressDelayMs, 10, 2000);
        RepeatDelayMs = Math.Clamp(RepeatDelayMs, 100, 20000);
    }
}
