using System.Globalization;
using RazorReaper.Services;
using RazorReaper.Services.Automation;
using RazorReaper.Services.Gamma;
using RazorReaper.Services.Localization;
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
    private string ScriptCategory => _localizer.T("palette.category.script");
    private string CommandCategory => _localizer.T("palette.category.command");

    private readonly IEnumerable<AutomationScriptBase> _scripts;
    private readonly IGammaService _gamma;
    private readonly ICrosshairService _crosshair;
    private readonly IHudOverlayService _hud;
    private readonly IAutoAntidoteService _antidote;
    private readonly IFedSuitMacro _fedSuit;
    private readonly IArkLauncher _launcher;
    private readonly INotificationService _notifications;
    private readonly ILocalizer _localizer;

    public PaletteCommandProvider(
        IEnumerable<AutomationScriptBase> scripts,
        IGammaService gamma,
        ICrosshairService crosshair,
        IHudOverlayService hud,
        IAutoAntidoteService antidote,
        IFedSuitMacro fedSuit,
        IArkLauncher launcher,
        INotificationService notifications,
        ILocalizer localizer)
    {
        _scripts = scripts;
        _gamma = gamma;
        _crosshair = crosshair;
        _hud = hud;
        _antidote = antidote;
        _fedSuit = fedSuit;
        _launcher = launcher;
        _notifications = notifications;
        _localizer = localizer;
    }

    /// <summary>
    /// Shorthand for the dictionary. Resolved when the row is built — the palette rebuilds its
    /// candidates on every open, so a language switch is picked up the next time it is opened,
    /// and a Status callback re-reads its own string on every render.
    /// </summary>
    private string T(string key) => _localizer.T(key);

    private string T(string key, params object?[] args) => _localizer.T(key, args);

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
                Subtitle = T("palette.cmd.script.subtitle"),
                Category = ScriptCategory,
                IconSvg = NavIcons.ScriptsHub,
                Keywords = [captured.ScriptKey, "script", "start", "stop", "toggle", "run", "automation"],
                Status = () => captured.IsRunning ? T("palette.status.running") : null,
                Invoke = () =>
                {
                    captured.Toggle();
                    _notifications.ShowInfo(T(
                        captured.IsRunning ? "palette.cmd.script.started" : "palette.cmd.script.stopped",
                        captured.DisplayName));
                    return Task.CompletedTask;
                }
            });
        }

        // Panic button: one keystroke to stop everything currently running.
        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "script:stop-all",
            Title = T("palette.cmd.stopall.title"),
            Subtitle = T("palette.cmd.stopall.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.Stop,
            Keywords = ["stop", "all", "scripts", "halt", "kill", "panic", "abort"],
            Status = () =>
            {
                var running = _scripts.Count(s => s.IsRunning);
                return running > 0 ? T("palette.cmd.stopall.status", running) : null;
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

                if (stopped > 0)
                    _notifications.ShowSuccess(T(
                        stopped == 1 ? "palette.cmd.stopall.done.one" : "palette.cmd.stopall.done.many", stopped));
                else _notifications.ShowInfo(T("palette.cmd.stopall.none"));
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
            Title = T("palette.cmd.crosshair.title"),
            Subtitle = T("palette.cmd.crosshair.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.Crosshair,
            Keywords = ["crosshair", "overlay", "toggle", "reticle", "aim", "dot"],
            Status = () => _crosshair.IsOverlayActive ? T("palette.status.on") : null,
            Invoke = () =>
            {
                _crosshair.ToggleOverlay();
                _notifications.ShowInfo(T(_crosshair.IsOverlayActive
                    ? "palette.cmd.crosshair.on"
                    : "palette.cmd.crosshair.off"));
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:hud-toggle",
            Title = T("palette.cmd.hud.title"),
            Subtitle = T("palette.cmd.hud.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.Hud,
            Keywords = ["hud", "overlay", "toggle", "clock", "timer", "osd", "on-screen"],
            Status = () => _hud.IsRunning ? T("palette.status.on") : null,
            Invoke = () =>
            {
                _hud.Toggle();
                _notifications.ShowInfo(T(_hud.IsRunning ? "palette.cmd.hud.on" : "palette.cmd.hud.off"));
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:antidote-toggle",
            Title = T("palette.cmd.antidote.title"),
            Subtitle = T("palette.cmd.antidote.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.Antidote,
            Keywords = ["antidote", "auto", "toggle", "watcher", "debuff", "cure"],
            Status = () => _antidote.State switch
            {
                AutoAntidoteState.Watching => T("palette.cmd.antidote.state.watching"),
                AutoAntidoteState.Cooldown => T("palette.cmd.antidote.state.cooldown"),
                _ => null
            },
            Invoke = () =>
            {
                _antidote.Toggle();
                if (_antidote.State == AutoAntidoteState.Off)
                {
                    // Start() refuses when the icon region or reference snapshot is missing,
                    // so say why rather than silently doing nothing.
                    if (!_antidote.HasRegion || !_antidote.HasReference)
                        _notifications.ShowWarning(T("palette.cmd.antidote.calibrate"));
                    else
                        _notifications.ShowInfo(T("palette.cmd.antidote.stopped"));
                }
                else
                {
                    _notifications.ShowInfo(T("palette.cmd.antidote.watching"));
                }
                return Task.CompletedTask;
            }
        });

        items.Add(new PaletteItem
        {
            Kind = PaletteKind.Command,
            Id = "cmd:fedsuit-toggle",
            Title = T("palette.cmd.fedsuit.title"),
            Subtitle = T("palette.cmd.fedsuit.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.FedSuit,
            Keywords = ["fed suit", "federation", "transmitter", "toggle", "start", "stop", "grind"],
            Status = () => _fedSuit.IsRunning ? T("palette.cmd.fedsuit.status", _fedSuit.CurrentCycle) : null,
            Invoke = () =>
            {
                if (_fedSuit.IsRunning)
                {
                    _fedSuit.Stop();
                    _notifications.ShowInfo(T("palette.cmd.fedsuit.stopped"));
                }
                else if (_fedSuit.Start())
                {
                    _notifications.ShowInfo(T("palette.cmd.fedsuit.started"));
                }
                else
                {
                    _notifications.ShowWarning(T("palette.cmd.fedsuit.failed"));
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
                Title = T("palette.cmd.gamma.title", name),
                Subtitle = T("palette.cmd.gamma.subtitle", value.ToString("0.00", CultureInfo.InvariantCulture)),
                Category = CommandCategory,
                IconSvg = NavIcons.Gamma,
                Keywords = [name, "gamma", "brightness", "preset", "apply", "screen", "night", "dark"],
                Invoke = () =>
                {
                    switch (_gamma.ApplyPreset(id))
                    {
                        case GammaController.ApplyResult.Success:
                            _notifications.ShowSuccess(T("palette.cmd.gamma.applied", name));
                            break;
                        case GammaController.ApplyResult.ClampedByWindows:
                            _notifications.ShowWarning(T("palette.cmd.gamma.clamped", name));
                            break;
                        default:
                            _notifications.ShowError(T("palette.cmd.gamma.rejected", name));
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
            Title = T("palette.cmd.gamma.reset.title"),
            Subtitle = T("palette.cmd.gamma.reset.subtitle"),
            Category = CommandCategory,
            IconSvg = NavIcons.Gamma,
            Keywords = ["gamma", "reset", "default", "restore", "normal", "brightness"],
            Invoke = () =>
            {
                _gamma.ResetToDefault();
                _notifications.ShowSuccess(T("palette.cmd.gamma.reset.done"));
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
            Title = T("palette.cmd.launch.title"),
            Subtitle = T("palette.cmd.launch.subtitle"),
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
