namespace RazorReaper.Services.Localization;

/// <summary>
/// The app's translated strings, and the language they are read in.
///
/// Every user-facing literal that has been migrated goes through <see cref="T(string)"/>. The
/// key is the English string's address, not the string itself, so a wording fix in English is
/// one JSON edit rather than a search across the razor files.
/// </summary>
public interface ILocalizer
{
    /// <summary>The active language code — always one of <see cref="AppLanguages.All"/>.</summary>
    string Language { get; }

    /// <summary>The languages the picker offers, in the order it shows them.</summary>
    IReadOnlyList<AppLanguage> Languages { get; }

    /// <summary>Raised after <see cref="SetLanguage"/> actually changed the language.</summary>
    event Action? LanguageChanged;

    /// <summary>
    /// Switches language and persists the choice. An unknown or unchanged code is ignored, so
    /// the event only ever fires for a real change.
    /// </summary>
    void SetLanguage(string code);

    /// <summary>
    /// The translation for <paramref name="key"/>: current language, then English, then the key
    /// itself. Never throws and never returns null — a missing key shows as the key, which is
    /// ugly on screen and therefore easy to spot.
    /// </summary>
    string T(string key);

    /// <summary>
    /// <see cref="T(string)"/> with <c>{0}</c>-style placeholders filled in. A template whose
    /// placeholders do not match <paramref name="args"/> is returned unformatted rather than
    /// throwing: a bad translation must not be able to take a page down.
    /// </summary>
    string T(string key, params object?[] args);
}
