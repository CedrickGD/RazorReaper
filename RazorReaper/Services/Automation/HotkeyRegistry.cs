using RazorReaper.Services.Localization;
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

    /// <summary>
    /// Shown as the row label, e.g. "Yuty Roar". A feature's own name, which stays as the
    /// feature is called — the scripts are listed here under the names the community uses for
    /// them, and those are the same words in every language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Dictionary key for the section heading on the hotkeys page.</summary>
    public required string GroupKey { get; init; }

    /// <summary>Dictionary key for what pressing it does.</summary>
    public required string DescriptionKey { get; init; }

    /// <summary>Route of the feature this belongs to, for the link back.</summary>
    public required string OwnerRoute { get; init; }

    public required Func<string> Get { get; init; }

    /// <summary>Applies and persists. Owners validate; an unusable combo is left unchanged.</summary>
    public required Action<string> Set { get; init; }

    /// <summary>
    /// Dictionary key for <see cref="Name"/>, where the row's label is a description rather than
    /// a feature name. Null leaves <see cref="Name"/> as it stands.
    /// </summary>
    public string? NameKey { get; init; }

    /// <summary>Live on/off state where the owner has one, for the status dot.</summary>
    public Func<bool>? IsActive { get; init; }

    /// <summary>
    /// True when a combo is stored but Windows refused to hand it over, so nothing is listening.
    /// Null for owners that cannot yet tell — the page only warns where it knows.
    /// </summary>
    public Func<bool>? HasFailed { get; init; }

    /// <summary>
    /// The RazorReaper feature holding the key <see cref="HasFailed"/> is about, already in the
    /// reader's language. Null where nothing here holds it — then another app does.
    /// </summary>
    public Func<string?>? FailureOwner { get; init; }

    /// <summary>
    /// True when the binding can only be edited on its own page. Set for owners whose
    /// registration still lives in page-local state.
    /// </summary>
    public bool ReadOnlyHere { get; init; }

    /// <summary>The row's label in the reader's language: the feature name, or its dictionary entry.</summary>
    public string LocalName(ILocalizer localizer) => NameKey is null ? Name : localizer.T(NameKey);

    /// <summary>
    /// Whether this binding really has <paramref name="combo"/>: the same key, and live. One that
    /// Windows refused is only stored here — someone else holds that key, so naming this feature
    /// as the holder would be the very misattribution the lookup exists to avoid.
    /// </summary>
    public bool Holds(string? combo) => HotkeyParser.SameCombo(combo, Get()) && HasFailed?.Invoke() != true;
}

public interface IHotkeyRegistry
{
    /// <summary>Every system-wide binding, rebuilt on each call so live state is current.</summary>
    IReadOnlyList<HotkeyBinding> GetBindings();

    /// <summary>The bindings owned by one page, for its read-only summary.</summary>
    IReadOnlyList<HotkeyBinding> ForRoute(string route);

    /// <summary>
    /// Which RazorReaper binding already holds <paramref name="combo"/>, ignoring the one with
    /// <paramref name="exceptId"/> (the asker's own). Null when nothing here owns it — which is
    /// what makes "another app has this key" an honest thing to say rather than a guess.
    /// </summary>
    HotkeyBinding? OwnerOf(string? combo, string? exceptId = null);
}

public sealed class HotkeyRegistry : IHotkeyRegistry
{
    /// <summary>
    /// Every dictionary key a binding can carry. The page renders them off the binding, so a
    /// grep for T("…") cannot see them; TranslatedSurfaceTests reads this list instead, which
    /// keeps the "no key nothing renders" check honest without standing the registry up.
    /// </summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        "hotkeys.group.scripts",
        "hotkeys.group.overlays",
        "hotkeys.group.automation",
        "hotkeys.script.description",
        "hotkeys.crosshair.name",
        "hotkeys.crosshair.description",
        "hotkeys.autoclicker.description",
    ];

    private readonly IEnumerable<AutomationScriptBase> scripts;
    private readonly ICrosshairService crosshair;
    private readonly IAutoClickerHotkeyBinder autoClicker;
    private readonly ILocalizer localizer;

    public HotkeyRegistry(
        IEnumerable<AutomationScriptBase> scripts,
        ICrosshairService crosshair,
        IAutoClickerHotkeyBinder autoClicker,
        ILocalizer localizer)
    {
        this.scripts = scripts;
        this.crosshair = crosshair;
        this.autoClicker = autoClicker;
        this.localizer = localizer;
    }

    public IReadOnlyList<HotkeyBinding> GetBindings()
    {
        var list = new List<HotkeyBinding>();
        AddScripts(list);
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

    public HotkeyBinding? OwnerOf(string? combo, string? exceptId = null)
        => GetBindings().FirstOrDefault(b =>
            !string.Equals(b.Id, exceptId, StringComparison.Ordinal) && b.Holds(combo));

    private void AddScripts(List<HotkeyBinding> list)
    {
        foreach (var script in scripts)
        {
            var s = script;
            list.Add(new HotkeyBinding
            {
                Id = $"script:{s.ScriptKey}",
                Name = s.DisplayName,
                GroupKey = "hotkeys.group.scripts",
                DescriptionKey = "hotkeys.script.description",
                OwnerRoute = "/scripts",
                Get = () => s.StartStopHotkey ?? "",
                Set = value =>
                {
                    s.StartStopHotkey = value ?? "";
                    s.SaveHotkey();
                },
                IsActive = () => s.IsRunning,
                HasFailed = () => s.HotkeyFailed,
                FailureOwner = () => s.HotkeyConflictOwner
            });
        }
    }

    // Auto Antidote and Fed Suit are ordinary scripts now, so the script section above already
    // lists their toggles. They used to own separate bindings here (and a start/stop pair for Fed
    // Suit) back when each lived on its own page.

    private void AddOverlays(List<HotkeyBinding> list)
    {
        list.Add(new HotkeyBinding
        {
            Id = "crosshair:toggle",
            Name = "Crosshair overlay",
            NameKey = "hotkeys.crosshair.name",
            GroupKey = "hotkeys.group.overlays",
            DescriptionKey = "hotkeys.crosshair.description",
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
            IsActive = () => crosshair.IsOverlayActive,
            HasFailed = () => crosshair.HotkeyFailed,
            FailureOwner = () => OwnerOf(crosshair.GetHotkey().Label, "crosshair:toggle")?.LocalName(localizer)
        });
    }

    /// <summary>
    /// Auto Clicker's hotkey used to be page-local, stored in the browser's localStorage where
    /// nothing outside the page could read it — so it was listed here but not editable. It lives
    /// in Preferences now, which any C# can reach, so it is an ordinary binding.
    /// </summary>
    private void AddAutoClicker(List<HotkeyBinding> list)
    {
        list.Add(new HotkeyBinding
        {
            Id = "autoclicker:toggle",
            Name = "Auto Clicker",
            NameKey = "nav.page.autoclicker",
            GroupKey = "hotkeys.group.automation",
            DescriptionKey = "hotkeys.autoclicker.description",
            OwnerRoute = "/autoclicker",
            Get = () => AutoClickerHotkey.Display,
            Set = value =>
            {
                // A combo the key map does not know would store a code of 0 and silently stop
                // the hotkey working, so an unusable one falls back instead.
                if (!AutoClickerHotkey.Set(value)) AutoClickerHotkey.Reset();
            },
            // Released on purpose while its page records a new key; that is not a refusal.
            HasFailed = () => !autoClicker.IsBound && !autoClicker.IsSuspended,
            FailureOwner = () => OwnerOf(AutoClickerHotkey.Display, "autoclicker:toggle")?.LocalName(localizer)
        });
    }
}
