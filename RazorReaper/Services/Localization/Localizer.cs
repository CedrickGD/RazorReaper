using System.Globalization;

namespace RazorReaper.Services.Localization;

/// <summary>
/// The shipped <see cref="ILocalizer"/>. Holds the active language, its dictionary, and the
/// English one it falls back through.
/// </summary>
/// <remarks>
/// Deliberately not built on <c>CultureInfo.CurrentUICulture</c> and satellite assemblies. The
/// app is a MAUI Blazor Hybrid: the UI renders on the renderer's dispatcher while services raise
/// events from timers and pool threads, so a thread-affine ambient culture would be right on some
/// of those threads and wrong on the rest. A plain injected lookup has one answer everywhere.
/// It also leaves number and date formatting alone — ARK coordinates, INI values and hotkey
/// labels are parsed and printed invariantly all over the app, and a language switch that
/// silently turned "1.5" into "1,5" would break them.
/// </remarks>
public sealed class Localizer : ILocalizer
{
    /// <summary>Persisted language choice. Absent until the user picks one.</summary>
    public const string PreferenceKey = "rr.ui.language";

    private readonly IPreferencesStore preferences;
    private readonly IReadOnlyDictionary<string, string> english;

    private IReadOnlyDictionary<string, string> active;
    private string language;

    public Localizer(IPreferencesStore preferences)
        : this(preferences, CultureInfo.CurrentUICulture)
    {
    }

    /// <param name="osCulture">
    /// The culture the language defaults to on a fresh install. Injected so the default can be
    /// tested without changing the test runner's own culture.
    /// </param>
    public Localizer(IPreferencesStore preferences, CultureInfo? osCulture)
    {
        this.preferences = preferences;
        english = LanguageCatalog.Load(AppLanguages.Default);

        language = ResolveInitialLanguage(preferences, osCulture);
        active = LoadFor(language);
    }

    public string Language => language;

    public IReadOnlyList<AppLanguage> Languages => AppLanguages.All;

    public event Action? LanguageChanged;

    public void SetLanguage(string code)
    {
        var canonical = AppLanguages.Canonical(code);
        if (canonical is null || canonical == language) return;

        language = canonical;
        active = LoadFor(canonical);
        preferences.Set(PreferenceKey, canonical);
        LanguageChanged?.Invoke();
    }

    public string T(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (active.TryGetValue(key, out var translated) && translated.Length > 0) return translated;
        if (english.TryGetValue(key, out var fallback) && fallback.Length > 0) return fallback;
        return key;
    }

    public string T(string key, params object?[] args)
    {
        var template = T(key);
        if (args.Length == 0) return template;

        try
        {
            // Invariant: the placeholders carry names and versions, not formatted numbers, and a
            // translator must never be able to change how a version string reads.
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            // A translation with a stray brace or a wrong placeholder count. Showing the raw
            // template is wrong but readable; throwing here would blank the page that used it.
            return template;
        }
    }

    private IReadOnlyDictionary<string, string> LoadFor(string code) =>
        code == AppLanguages.Default ? english : LanguageCatalog.Load(code);

    private static string ResolveInitialLanguage(IPreferencesStore preferences, CultureInfo? osCulture)
    {
        var stored = AppLanguages.Canonical(preferences.Get(PreferenceKey, string.Empty));
        if (stored is not null) return stored;

        return AppLanguages.FromCulture(osCulture) ?? AppLanguages.Default;
    }
}
