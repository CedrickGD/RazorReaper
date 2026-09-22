using Microsoft.Extensions.Logging;
using RazorReaper.Services.Localization;

namespace RazorReaper.Services.Automation.Scripts;

/// <summary>
/// The Fed Suit transmitter loop: open, transfer a batch, exit, repeat.
///
/// The loop itself lives in <see cref="IFedSuitMacro"/>, which predates the script catalogue and
/// carried its own start and stop hotkeys until they were removed — nothing ever registered them,
/// while the start toast told people to press one of them. This wrapper puts the macro in the
/// Scripts list so it behaves like every other script: one toggle, one hotkey, one place to find it.
/// </summary>
public sealed class FedSuitScript : AutomationScriptBase
{
    private const string Key = "fedsuit";

    private readonly IFedSuitMacro _macro;

    /// <summary>
    /// The macro's cycle number as this script last saw it. <see cref="IFedSuitMacro.Changed"/>
    /// fires for settings and counters alike, so the cycle is what says input actually went
    /// out: it steps to N when the Nth cycle's first step starts, and that step is the key
    /// that opens the transmitter.
    /// </summary>
    private int _lastCycle;

    public FedSuitScript(
        IFedSuitMacro macro,
        IForegroundGate foreground,
        IAutomationHotkeyService hotkeys,
        INotificationService notifications,
        IActivityService activity,
        ILocalizer localizer,
        ILogger<FedSuitScript> logger)
        : base(Key, "Fed Suit", string.Empty, foreground, hotkeys, notifications, activity, localizer, logger)
    {
        _macro = macro;

        // The loop can end on its own (invalid key, finished run). Mirror that so the Scripts list
        // never shows it running after it stopped.
        _macro.Changed += OnMacroChanged;
    }

    /// <summary>The underlying macro, for the settings UI.</summary>
    public IFedSuitMacro Macro => _macro;

    public override void FollowArkKeys() => _macro.FollowArkKeys();

    protected override async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _lastCycle, _macro.CurrentCycle);

        // alreadyMetered: the scaffold's Start() has already charged this run against the shared
        // input-script quota. The macro carries its own fed_suit quota from before the Scripts
        // list existed, and letting both fire meant one toggle of this tile cost a free user two
        // of their monthly runs.
        if (!_macro.Start(alreadyMetered: true))
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
        // Above zero only: the macro parks the cycle back at 0 when a run ends, and winding
        // down is not an action.
        var cycle = _macro.CurrentCycle;
        if (Interlocked.Exchange(ref _lastCycle, cycle) != cycle && cycle > 0 && IsRunning)
            ReportEffect();

        if (!_macro.IsRunning && IsRunning)
        {
            Stop();
            return;
        }

        RaiseChanged();
    }

    protected override void OnStopped() => _macro.Stop();
}
