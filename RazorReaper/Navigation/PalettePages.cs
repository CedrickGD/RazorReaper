using RazorReaper.Services.Localization;

namespace RazorReaper.Navigation;

/// <summary>
/// Turns catalog pages into command-palette rows, in the language the palette was opened in.
/// </summary>
/// <remarks>
/// Its own class rather than a private helper in GlobalSearch.razor because the interesting part
/// is not the row, it is what stays findable: the title moves to the active language, so the
/// English name has to survive as a keyword or the palette stops answering to it. Someone who
/// learned the app in English and reads it in German types "ini changer", and "INI-Wechsler"
/// contains none of that.
/// </remarks>
public static class PalettePages
{
    /// <summary>The row for <paramref name="page"/> with its name in the active language.</summary>
    public static PaletteItem ToItem(NavPage page, ILocalizer localizer)
        => ToItem(page, localizer.T(page.LabelKey));

    /// <param name="title">
    /// The page's name as it should read. Passed in rather than looked up so the language
    /// this produces can be chosen by a test as well as by the localizer.
    /// </param>
    public static PaletteItem ToItem(NavPage page, string title) => new()
    {
        Kind = PaletteKind.Page,
        Id = $"page:{page.Route}",
        Title = title,
        Subtitle = page.Description,
        Category = page.Category,
        IconSvg = page.IconSvg,
        Keywords = Searchable(page, title),
        Route = page.Route,
    };

    /// <summary>
    /// The catalog's keywords, plus the English name when the title is no longer it. In English
    /// that would be the same word twice — the matcher takes the best hit per term, so it would
    /// change nothing, but a row carrying its own title as a keyword reads like a mistake.
    /// </summary>
    private static IReadOnlyList<string> Searchable(NavPage page, string title)
        => string.Equals(title, page.Label, StringComparison.Ordinal)
            ? page.Keywords
            : [.. page.Keywords, page.Label];
}
