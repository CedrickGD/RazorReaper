using System.Globalization;

namespace RazorReaper.Services.Localization;

/// <summary>
/// One language the UI can be shown in.
/// </summary>
/// <param name="Code">
/// The dictionary name and the persisted value — a BCP-47 tag, lowercase for the script-less
/// ones. It is also the file name of the embedded JSON, so it must stay stable once shipped.
/// </param>
/// <param name="EnglishName">The language in English, for logs and diagnostics.</param>
/// <param name="NativeName">
/// The language in itself. This is what the picker shows: someone who ended up in the wrong
/// language has to be able to find their own without reading the one they cannot.
/// </param>
public sealed record AppLanguage(string Code, string EnglishName, string NativeName);

/// <summary>
/// The four languages the app ships. Adding a fifth means one entry here plus one JSON file
/// in Resources/i18n — the picker, the tests and the fallback chain all read this list.
/// </summary>
public static class AppLanguages
{
    public const string English = "en";
    public const string German = "de";
    public const string Russian = "ru";
    public const string SimplifiedChinese = "zh-Hans";

    /// <summary>The language used when nothing else resolves, and the fallback for a missing key.</summary>
    public const string Default = English;

    public static readonly IReadOnlyList<AppLanguage> All =
    [
        new AppLanguage(English, "English", "English"),
        new AppLanguage(German, "German", "Deutsch"),
        new AppLanguage(Russian, "Russian", "Русский"),
        new AppLanguage(SimplifiedChinese, "Simplified Chinese", "中文 (简体)"),
    ];

    public static bool IsSupported(string? code) =>
        code is not null && All.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The canonical spelling of <paramref name="code"/>, or null when it is not shipped.</summary>
    public static string? Canonical(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>
    /// Picks the shipped language closest to an OS culture, or null when none is.
    /// </summary>
    /// <remarks>
    /// Windows hands out names like "de-AT", "ru-RU" and "zh-CN"/"zh-TW", so an exact match
    /// against the four codes would only ever hit plain "en". Matching walks the culture's own
    /// parent chain, which carries the script: zh-CN's parent is zh-Hans, zh-TW's is zh-Hant.
    /// Traditional script is not shipped, and it is checked before the bare "zh" fallback so a
    /// Taiwanese user gets English rather than a script they do not read.
    /// </remarks>
    public static string? FromCulture(CultureInfo? culture)
    {
        for (var c = culture; c is not null && c.Name.Length > 0; c = c.Parent)
        {
            var match = Canonical(c.Name);
            if (match is not null) return match;

            if (c.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)) return null;

            // The chain ends at the neutral culture, so a plain "zh" never reaches zh-Hans by
            // itself. Simplified is the default script for unqualified Chinese.
            if (string.Equals(c.Name, "zh", StringComparison.OrdinalIgnoreCase)) return SimplifiedChinese;
        }

        return null;
    }
}
