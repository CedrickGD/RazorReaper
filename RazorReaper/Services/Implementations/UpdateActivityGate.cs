using Microsoft.Extensions.Logging;
using RazorReaper.Services.Automation;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Reads the "is anything running?" flags the rest of the app already keeps, so the update path
/// does not grow a second answer to a question four services can already answer. Nothing here
/// polls: every property is a cheap read of live state at the moment it is asked.
/// </summary>
public sealed class UpdateActivityGate : IUpdateActivityGate
{
    private readonly IGameIniService gameIni;
    private readonly IAutoClickerRuntime autoClicker;
    private readonly IMacroEngine macros;
    private readonly ILogger<UpdateActivityGate> logger;

    public UpdateActivityGate(
        IGameIniService gameIni,
        IAutoClickerRuntime autoClicker,
        IMacroEngine macros,
        ILogger<UpdateActivityGate> logger)
    {
        this.gameIni = gameIni;
        this.autoClicker = autoClicker;
        this.macros = macros;
        this.logger = logger;
    }

    /// <summary>The same check the INI Builder and Stretched Res pages use — the configured
    /// ARK process name, through <c>IProcessService</c>.</summary>
    public bool IsArkRunning => Safe(() => gameIni.IsArkRunning(), "ARK");

    public bool IsMacroRunning =>
        Safe(() => autoClicker.IsRunning, "auto clicker")
        || Safe(() => AutomationScriptBase.AnyRunning, "automation scripts")
        || Safe(() => macros.Runners.Any(runner => runner.State != MacroRunnerState.Idle), "macro runners")
        // A key we are holding right now: a script that is between state changes still counts.
        || Safe(() => SynthesizedInput.AnyActive, "synthesized input");

    /// <summary>
    /// Failing open would restart the app mid-session, failing closed would only delay an update
    /// — so a flag that throws reads as "busy".
    /// </summary>
    private bool Safe(Func<bool> read, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Update gate could not read {What}; treating it as busy", what);
            return true;
        }
    }
}
