using RazorReaper.Navigation;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The catalog keeps two spellings of every page name now: the English <c>Label</c>, which
/// diagnostics, telemetry and Discord Rich Presence report, and the dictionary entry the sidebar
/// renders. They are the same words, so the one thing worth pinning is that they stay the same
/// words — an English name changed in the catalog and nowhere else would leave the sidebar
/// showing the old one to everybody, English readers included.
/// </summary>
public sealed class NavigationTranslationTests
{
    [Fact]
    public void EveryPageAndGroupHasAnEnglishEntryThatMatchesTheCatalog()
    {
        var english = TranslationParityTests.Read("en");

        foreach (var group in NavCatalog.Groups)
        {
            Assert.True(english.TryGetValue(group.NameKey, out var name), $"missing {group.NameKey}");
            Assert.Equal(group.Name, name);
        }

        foreach (var page in NavCatalog.Pages)
        {
            Assert.True(english.TryGetValue(page.LabelKey, out var label), $"missing {page.LabelKey}");
            Assert.Equal(page.Label, label);
        }
    }

    /// <summary>The route is the key, so two pages can never share one entry.</summary>
    [Fact]
    public void EveryPageKeyIsDistinct()
    {
        var keys = NavCatalog.Pages.Select(p => p.LabelKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(NavCatalog.Groups.Count,
            NavCatalog.Groups.Select(g => g.NameKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("Core", "nav.group.core")]
    [InlineData("ARK Tweaks", "nav.group.ark-tweaks")]
    [InlineData("Mods & Intel", "nav.group.mods-intel")]
    [InlineData("Help & About", "nav.group.help-about")]
    public void GroupKeysAreSluggedFromTheEnglishName(string name, string expected)
        => Assert.Equal(expected, NavCatalog.Groups.First(g => g.Name == name).NameKey);

    [Theory]
    [InlineData("/home", "nav.page.home")]
    [InlineData("/guides/dino-level", "nav.page.guides.dino-level")]
    public void PageKeysAreDerivedFromTheRoute(string route, string expected)
        => Assert.Equal(expected, NavCatalog.FindByRoute(route)!.LabelKey);

    /// <summary>
    /// A label that does not fit is a layout question, and translation is what makes it one:
    /// English fits the 240px panel and German and Russian do not — "Глобальные горячие клавиши"
    /// wants 187px where the row has 123px. Every one of those has to ellipsise, on every row,
    /// which is what these three declarations together say. The rule was once scoped to rows
    /// carrying a marker; the scoping was never what made it work, so this pins the unscoped
    /// form rather than the accident.
    /// </summary>
    [Fact]
    public void EveryNavLabelMayEllipsiseNotJustTheOnesWithAMarker()
    {
        var css = File.ReadAllText(Path.Combine(
            TranslationParityTests.RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "navbar.css"));

        var start = css.IndexOf(".panel-label {", StringComparison.Ordinal);
        Assert.True(start > 0, "navbar.css must still style .panel-label.");
        var block = css[start..css.IndexOf('}', start)];

        Assert.Contains("min-width: 0", block, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis", block, StringComparison.Ordinal);
        Assert.DoesNotContain(".panel-badge) .panel-label", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The group's English name is its identity: the sidebar remembers the open group by it and
    /// NavPage.Category is matched against it. A translated Name would break both silently.
    /// </summary>
    [Fact]
    public void TheGroupNameStaysTheIdentity()
    {
        foreach (var group in NavCatalog.Groups)
        {
            Assert.All(group.Pages, page => Assert.Equal(group.Name, page.Category));
        }
    }
}
