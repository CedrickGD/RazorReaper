using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// Automates a repeating chat command (default <c>/download</c>) for servers running the matching
/// plugin: opens chat with Enter, types the command, sends it, waits the configured delay, repeats.
/// Pure keyboard via <see cref="IInputSimulator"/> (Unicode typing, layout-independent), gated on ARK
/// being the foreground window.
/// </summary>
public sealed class AutoDownloadScript : AutomationScriptBase
{
    private const string Key = "autodownload";
    private const int EnterVk = 0x0D;
    private readonly IInputSimulator _input;

    /// <summary>The chat command to send each cycle.</summary>
    public string Command { get; set; } = "/download";

    /// <summary>Milliseconds to wait between sends.</summary>
    public int DelayMs { get; set; } = 5000;

    public AutoDownloadScript(
        IInputSimulator input,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<AutoDownloadScript> logger)
        : base(Key, "Auto Download", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _input = input;
        LoadSettings();
    }

    protected override Task RunAsync(CancellationToken ct) =>
        RunLoopAsync(DelayMs, async c =>
        {
            await _input.KeyPressAsync(EnterVk, ct: c);   // open chat
            await _input.DelayAsync(250, ct: c);
            await _input.TypeTextAsync(Command, ct: c);
            await _input.DelayAsync(120, ct: c);
            await _input.KeyPressAsync(EnterVk, ct: c);    // send
        }, foregroundOnly: true, ct);

    public void SaveSettings()
    {
        Command = string.IsNullOrWhiteSpace(Command) ? "/download" : Command.Trim();
        DelayMs = Math.Clamp(DelayMs, 500, 120000);
        try
        {
            Preferences.Set($"{Key}.command", Command);
            Preferences.Set($"{Key}.delay", DelayMs);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto Download SaveSettings failed"); }
        RaiseChanged();
    }

    private void LoadSettings()
    {
        try
        {
            Command = Preferences.Get($"{Key}.command", "/download");
            DelayMs = Preferences.Get($"{Key}.delay", 5000);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto Download LoadSettings failed"); }
        DelayMs = Math.Clamp(DelayMs, 500, 120000);
    }
}
