using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Gamma;
using RazorReaper.Services.Overlay;

namespace RazorReaper.Navigation;

public interface IPaletteCommandProvider
{
    /// <summary>
    /// The commands available right now. Rebuilt per palette open because some of them are
    /// user data (gamma presets) that can change between openings.
    /// </summary>
    IReadOnlyList<PaletteItem> GetCommands();
}

/// <summary>
/// Turns the app's existing services into palette rows you can run without leaving the
/// keyboard. Nothing here owns state — every command delegates straight to the singleton
/// service that already backs the corresponding page, so the palette and the page can't
/// disagree about what's running.
/// </summary>
public sealed class PaletteCommandProvider : IPaletteCommandProvider
{
    private const string ScriptCategory = "Script";
    private const string CommandCategory = "Command";

    private readonly IEnumerable<AutomationScriptBase> _scripts;
    private readonly IGammaService _gamma;
    private readonly ICrosshairService _crosshair;
    private readonly IHudOverlayService _hud;
    private readonly IAutoAntidoteService _antidote;
    private readonly IFedSuitMacro _fedSuit;
    private readonly IArkLauncher _launcher;
    private readonly INotificationService _notifications;

    public PaletteCommandProvider(
        IEnumerable<AutomationScriptBase> scripts,
        IGammaService gamma,
        ICrosshairService crosshair,
        IHudOverlayService hud,
        IAutoAntidoteService antidote,
        IFedSuitMacro fedSuit,
        IArkLauncher launcher,
        INotificationService notifications)
    {
        _scripts = scripts;
        _gamma = gamma;
        _crosshair = crosshair;
        _hud = hud;
        _antidote = antidote;
        _fedSuit = fedSuit;
        _launcher = launcher;
        _notifications = notifications;
    }

    public IReadOnlyList<PaletteItem> GetCommands()
    {
        var items = new List<PaletteItem>();
        AddScripts(items);
        AddOverlays(items);
        AddGamma(items);
        AddGame(items);
        return items;
    }

    // ---- Automation scripts ------------------------------------------------

    private void AddScripts(List<PaletteItem> items)
    {
        foreach (var script in _scripts)
        {
            var captured = script;
            items.Add(new PaletteItem
            {
                Kind = PaletteKind.Command,
                Id = $"script:{captured.ScriptKey}",
                Title = captured.DisplayName,
                Subtitle = "Automation script — Enter to toggle",
                Category = ScriptCategory,
                IconSvg = NavIcons.ScriptsHub,
                Keywords = [captured.ScriptKey, "script", "start", "stop", "toggle", "run", "automation"],
                Status = () => captured.IsRunning ? "Running" : null,
                Invoke = () =>
                {
                    captured.Toggle();
                    _notifications.ShowInfo(captured.IsRunning
                        ? $"{captured.DisplayName} started."
                        : $"{captured.DisplayName} stopped.");
                    return Task.CompletedTask;
                }
            });
        }

        // Panic button: one keystroke to stop everything currently running.
        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "script:stop-all",
            Title = "Stop all scripts",
            Subtitle = "Halts every running automation script",
            Category = CommandCategory,
            IconSvg = NavIcons.Stop,
            Keywords = ["stop", "all", "scripts", "halt", "kill", "panic", "abort"],
            Status = () =>
            {
                var running = _scripts.Count(s => s.IsRunning);
                return running > 0 ? $"{running} running" : null;
            },
            Invoke = () =>
            {
                var stopped = 0;
                foreach (var script in _scripts)
                {
                    if (!script.IsRunning) continue;
                    script.Stop();
                    stopped++;
                }

                if (stopped > 0) _notifications.ShowSuccess($"Stopped {stopped} script{(stopped == 1 ? "" : "s")}.");
                else _notifications.ShowInfo("No scripts were running.");
                return Task.CompletedTask;
            }
        });
    }

    // ---- Overlays and watchers --------------------------------------------

    private void AddOverlays(List<PaletteItem> items)
    {
        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:crosshair-toggle",
            Title = "Toggle crosshair overlay",
            Subtitle = "Shows or hides the always-on-top crosshair",
            Category = CommandCategory,
            IconSvg = NavIcons.Crosshair,
            Keywords = ["crosshair", "overlay", "toggle", "reticle", "aim", "dot"],
            Status = () => _crosshair.IsOverlayActive ? "On" : null,
            Invoke = () =>
            {
                _crosshair.ToggleOverlay();
                _notifications.ShowInfo(_crosshair.IsOverlayActive ? "Crosshair on." : "Crosshair off.");
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:hud-toggle",
            Title = "Toggle HUD overlay",
            Subtitle = "Shows or hides the in-game HUD panel",
            Category = CommandCategory,
            IconSvg = NavIcons.Hud,
            Keywords = ["hud", "overlay", "toggle", "clock", "timer", "osd", "on-screen"],
            Status = () => _hud.IsRunning ? "On" : null,
            Invoke = () =>
            {
                _hud.Toggle();
                _notifications.ShowInfo(_hud.IsRunning ? "HUD overlay on." : "HUD overlay off.");
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:antidote-toggle",
            Title = "Toggle Auto Antidote",
            Subtitle = "Starts or stops the antidote HUD watcher",
            Category = CommandCategory,
            IconSvg = NavIcons.Antidote,
            Keywords = ["antidote", "auto", "toggle", "watcher", "debuff", "cure"],
            Status = () => _antidote.State == AutoAntidoteState.Off ? null : _antidote.State.ToString(),
            Invoke = () =>
            {
                _antidote.Toggle();
                if (_antidote.State == AutoAntidoteState.Off)
                {
                    // Start() refuses when the icon region or reference snapshot is missing,
                    // so say why rather than silently doing nothing.
                    if (!_antidote.HasRegion || !_antidote.HasReference)
                        _notifications.ShowWarning("Auto Antidote needs calibration first — open the page to set it up.");
                    else
                        _notifications.ShowInfo("Auto Antidote stopped.");
                }
                else
                {
                    _notifications.ShowInfo("Auto Antidote watching.");
                }
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:fedsuit-toggle",
            Title = "Toggle Fed Suit run",
            Subtitle = "Starts or stops the transmitter transfer loop",
            Category = CommandCategory,
            IconSvg = NavIcons.FedSuit,
            Keywords = ["fed suit", "federation", "transmitter", "toggle", "start", "stop", "grind"],
            Status = () => _fedSuit.IsRunning ? $"Cycle {_fedSuit.CurrentCycle}" : null,
            Invoke = () =>
            {
                if (_fedSuit.IsRunning)
                {
                    _fedSuit.Stop();
                    _notifications.ShowInfo("Fed Suit stopped.");
                }
                else if (_fedSuit.Start())
                {
                    _notifications.ShowInfo("Fed Suit started.");
                }
                else
                {
                    _notifications.ShowWarning("Fed Suit couldn't start — check calibration on its page.");
                }
                return Task.CompletedTask;
            }
        });
    }

    // ---- Gamma -------------------------------------------------------------

    private void AddGamma(List<PaletteItem> items)
    {
        // The user's own presets, so their names are searchable verbatim.
        foreach (var preset in _gamma.Presets)
        {
            var id = preset.Id;
            var name = preset.Name;
            var value = preset.Value;

            items.Add(new PaletteItem
            {
                Kind = PaletteKind.Command,
                Id = $"gamma:{id}",
                Title = $"Gamma: {name}",
                Subtitle = $"Apply gamma {value:0.00}",
                Category = CommandCategory,
                IconSvg = NavIcons.Gamma,
                Keywords = [name, "gamma", "brightness", "preset", "apply", "screen", "night", "dark"],
                Invoke = () =>
                {
                    switch (_gamma.ApplyPreset(id))
                    {
                        case GammaController.ApplyResult.Success:
                            _notifications.ShowSuccess($"Gamma set to {name}.");
                            break;
                        case GammaController.ApplyResult.ClampedByWindows:
                            _notifications.ShowWarning($"Windows clamped the {name} curve — the change may be partial.");
                            break;
                        default:
                            _notifications.ShowError($"The display driver rejected the {name} gamma curve.");
                            break;
                    }
                    return Task.CompletedTask;
                }
            });
        }

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "gamma:reset",
            Title = "Reset gamma to default",
            Subtitle = "Restores the system gamma ramp",
            Category = CommandCategory,
            IconSvg = NavIcons.Gamma,
            Keywords = ["gamma", "reset", "default", "restore", "normal", "brightness"],
            Invoke = () =>
            {
                _gamma.ResetToDefault();
                _notifications.ShowSuccess("Gamma reset to default.");
                return Task.CompletedTask;
            }
        });
    }

    // ---- Game --------------------------------------------------------------

    private void AddGame(List<PaletteItem> items)
    {
        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:launch-ark",
            Title = "Launch ARK",
            Subtitle = "Starts the game through Steam, BattlEye intact",
            Category = CommandCategory,
            IconSvg = NavIcons.Play,
            Keywords = ["launch", "start", "play", "ark", "game", "steam", "run"],
            Invoke = () =>
            {
                var result = _launcher.LaunchNormal();
                if (result.Ok) _notifications.ShowSuccess(result.Message);
                else _notifications.ShowError(result.Message);
                return Task.CompletedTask;
            }
        });
    }
}
