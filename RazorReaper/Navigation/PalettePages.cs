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
        => ToItem(page, localizer.T(page.LabelKey), localizer.T(page.DescriptionKey), localizer.T(CategoryKey(page)));

    /// <param name="title">
    /// The page's name as it should read. Passed in rather than looked up so the language
    /// this produces can be chosen by a test as well as by the localizer.
    /// </param>
    /// <param name="subtitle">The page's description, in the same language as the title.</param>
    /// <param name="category">The badge on the right of the row — the group name, translated.</param>
    public static PaletteItem ToItem(NavPage page, string title, string? subtitle = null, string? category = null) => new()
    {
        Kind = PaletteKind.Page,
        Id = $"page:{page.Route}",
        Title = title,
        Subtitle = subtitle ?? page.Description,
        Category = category ?? page.Category,
        IconSvg = page.IconSvg,
        Keywords = Searchable(page, title),
        Route = page.Route,
    };

    /// <summary>
    /// The dictionary key for a page's group name. <see cref="NavPage.Category"/> is the group's
    /// English identity — the sidebar matches on it — so the badge is looked up rather than shown
    /// as stored.
    /// </summary>
    internal static string CategoryKey(NavPage page) => "nav.group." + NavCatalog.Slug(page.Category);

    /// <summary>
    /// The catalog's keywords, plus the English name and description when the title is no longer
    /// the English one. In English those would be the same words twice — the matcher takes the
    /// best hit per term, so it would change nothing, but a row carrying its own title as a
    /// keyword reads like a mistake. The description joins them because it is the row's subtitle
    /// in English and the matcher scores the subtitle: once the subtitle is translated, the
    /// English phrasing would stop finding the page without this.
    /// </summary>
    private static IReadOnlyList<string> Searchable(NavPage page, string title)
        => string.Equals(title, page.Label, StringComparison.Ordinal)
            ? page.Keywords
            : [.. page.Keywords, page.Label, page.Description];
}
