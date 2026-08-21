using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// The Fed Suit transmitter loop: open, transfer a batch, exit, repeat.
///
/// The loop itself lives in <see cref="IFedSuitMacro"/>, which predates the script catalogue and
/// carried its own start and stop hotkeys. This wrapper puts it in the Scripts list so it behaves
/// like every other script — one toggle, one hotkey, one place to find it.
/// </summary>
public sealed class FedSuitScript : AutomationScriptBase
{
    private const string Key = "fedsuit";

    private readonly IFedSuitMacro _macro;

    public FedSuitScript(
        IFedSuitMacro macro,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILogger<FedSuitScript> logger)
        : base(Key, "Fed Suit", string.Empty, foreground, hotkeys, notifications, activity, logger)
    {
        _macro = macro;

        // The loop can end on its own (invalid key, finished run). Mirror that so the Scripts list
        // never shows it running after it stopped.
        _macro.Changed += OnMacroChanged;
    }

    /// <summary>The underlying macro, for the settings UI.</summary>
    public IFedSuitMacro Macro => _macro;

    protected override async Task RunAsync(CancellationToken ct)
    {
        if (!_macro.Start())
        {
            // Start() reports why (already running, or a key that will not parse).
            return;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        finally
        {
            _macro.Stop();
        }
    }

    private void OnMacroChanged()
    {
        if (!_macro.IsRunning && IsRunning)
        {
            Stop();
            return;
        }

        RaiseChanged();
    }

    protected override void OnStopped() => _macro.Stop();
}
