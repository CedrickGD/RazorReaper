using RazorReaper.Services.Overlay;

namespace RazorReaper.Services.Automation;

/// <summary>
/// One system-wide hotkey, wired to whichever service actually owns it.
///
/// Only bindings that fire from anywhere belong here. Keys a macro *sends into ARK*
/// (Fed Suit's transmitter keys, Auto Antidote's burst key, a script's roar key) are
/// per-feature configuration and stay on their own pages.
/// </summary>
public sealed class HotkeyBinding
{
    public required string Id { get; init; }

    /// <summary>Shown as the row label, e.g. "Yuty Roar".</summary>
    public required string Name { get; init; }

    /// <summary>Section heading on the hotkeys page.</summary>
    public required string Group { get; init; }

    /// <summary>What pressing it does.</summary>
    public required string Description { get; init; }

    /// <summary>Route of the feature this belongs to, for the link back.</summary>
    public required string OwnerRoute { get; init; }

    public required Func<string> Get { get; init; }

    /// <summary>Applies and persists. Owners validate; an unusable combo is left unchanged.</summary>
    public required Action<string> Set { get; init; }

    /// <summary>Live on/off state where the owner has one, for the status dot.</summary>
    public Func<bool>? IsActive { get; init; }

    /// <summary>
    /// True when the binding can only be edited on its own page. Set for owners whose
    /// registration still lives in page-local state.
    /// </summary>
    public bool ReadOnlyHere { get; init; }
}

public interface IHotkeyRegistry
{
    /// <summary>Every system-wide binding, rebuilt on each call so live state is current.</summary>
    IReadOnlyList<HotkeyBinding> GetBindings();

    /// <summary>The bindings owned by one page, for its read-only summary.</summary>
    IReadOnlyList<HotkeyBinding> ForRoute(string route);
}

public sealed class HotkeyRegistry : IHotkeyRegistry
{
    private readonly IEnumerable<AutomationScriptBase> scripts;
    private readonly IAutoAntidoteService antidote;
    private readonly IFedSuitMacro fedSuit;
    private readonly ICrosshairService crosshair;

    public HotkeyRegistry(
        IEnumerable<AutomationScriptBase> scripts,
        IAutoAntidoteService antidote,
        IFedSuitMacro fedSuit,
        ICrosshairService crosshair)
    {
        this.scripts = scripts;
        this.antidote = antidote;
        this.fedSuit = fedSuit;
        this.crosshair = crosshair;
    }

    public IReadOnlyList<HotkeyBinding> GetBindings()
    {
        var list = new List<HotkeyBinding>();
        AddScripts(list);
        AddAutomation(list);
        AddOverlays(list);
        AddAutoClicker(list);
        return list;
    }

    public IReadOnlyList<HotkeyBinding> ForRoute(string route)
    {
        var key = route.Trim('/');
        return GetBindings()
            .Where(b => string.Equals(b.OwnerRoute.Trim('/'), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void AddScripts(List<HotkeyBinding> list)
    {
        foreach (var script in scripts)
        {
            var s = script;
            list.Add(new HotkeyBinding
            {
                Id = $"script:{s.ScriptKey}",
                Name = s.DisplayName,
                Group = "Scripts",
                Description = "Starts or stops the script.",
                OwnerRoute = "/scripts",
                Get = () => s.StartStopHotkey ?? "",
                Set = value =>
                {
                    s.StartStopHotkey = value ?? "";
                    s.SaveHotkey();
                },
                IsActive = () => s.IsRunning
            });
        }
    }

    private void AddAutomation(List<HotkeyBinding> list)
    {
        list.Add(new HotkeyBinding
        {
            Id = "antidote:toggle",
            Name = "Auto Antidote",
            Group = "Automation",
            Description = "Starts or stops the antidote watcher.",
            OwnerRoute = "/auto-antidote",
            Get = () => antidote.Settings.ToggleHotkey ?? "",
            Set = value =>
            {
                antidote.Settings.ToggleHotkey = value ?? "";
                antidote.SaveSettings();
            },
            IsActive = () => antidote.State != AutoAntidoteState.Off
        });

        list.Add(new HotkeyBinding
        {
            Id = "fedsuit:start",
            Name = "Fed Suit — start",
            Group = "Automation",
            Description = "Starts the transmitter transfer loop.",
            OwnerRoute = "/fed-suit",
            Get = () => fedSuit.Settings.StartHotkey ?? "",
            Set = value =>
            {
                var settings = fedSuit.Settings;
                settings.StartHotkey = value ?? "";
                fedSuit.UpdateSettings(settings);
            },
            IsActive = () => fedSuit.IsRunning
        });

        list.Add(new HotkeyBinding
        {
            Id = "fedsuit:stop",
            Name = "Fed Suit — stop",
            Group = "Automation",
            Description = "Hard-stops the transmitter loop.",
            OwnerRoute = "/fed-suit",
            Get = () => fedSuit.Settings.StopHotkey ?? "",
            Set = value =>
            {
                var settings = fedSuit.Settings;
                settings.StopHotkey = value ?? "";
                fedSuit.UpdateSettings(settings);
            }
        });
    }

    private void AddOverlays(List<HotkeyBinding> list)
    {
        list.Add(new HotkeyBinding
        {
            Id = "crosshair:toggle",
            Name = "Crosshair overlay",
            Group = "Overlays",
            Description = "Shows or hides the crosshair.",
            OwnerRoute = "/crosshair",
            Get = () => crosshair.GetHotkey().Label ?? "",
            Set = value =>
            {
                // The service stores the parsed key plus the label it displays, so the
                // combo string has to be resolved before it can be handed over.
                if (string.IsNullOrWhiteSpace(value))
                {
                    crosshair.SetHotkey("", 0, false, false, false);
                    return;
                }

                if (HotkeyParser.TryParseHotkey(value, out var vk, out var ctrl, out var alt, out var shift))
                {
                    crosshair.SetHotkey(value, vk, ctrl, alt, shift);
                }
            },
            IsActive = () => crosshair.IsOverlayActive
        });
    }

    /// <summary>
    /// Auto Clicker's hotkey used to be page-local, stored in the browser's localStorage where
    /// nothing outside the page could read it — so it was listed here but not editable. It lives
    /// in Preferences now, which any C# can reach, so it is an ordinary binding.
    /// </summary>
    private static void AddAutoClicker(List<HotkeyBinding> list)
    {
        list.Add(new HotkeyBinding
        {
            Id = "autoclicker:toggle",
            Name = "Auto Clicker",
            Group = "Automation",
            Description = "Starts or stops clicking.",
            OwnerRoute = "/autoclicker",
            Get = () => AutoClickerHotkey.Display,
            Set = value =>
            {
                // A combo the key map does not know would store a code of 0 and silently stop
                // the hotkey working, so an unusable one falls back instead.
                if (!AutoClickerHotkey.Set(value)) AutoClickerHotkey.Reset();
            }
        });
    }
}
