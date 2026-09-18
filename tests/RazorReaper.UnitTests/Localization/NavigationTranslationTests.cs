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
    /// English fits the 240px panel and German and Russian did not — "Глобальные горячие клавиши"
    /// wanted 187.3px where the row has 109.2px. Every one of those has to ellipsise, on every
    /// row, which is what these three declarations together say. The rule was once scoped to rows
    /// carrying a marker; the scoping was never what made it work, so this pins the unscoped
    /// form rather than the accident.
    ///
    /// The declarations stay whatever the words do. That name is "Клавиши" now and the nine
    /// others that were over the slot were shortened with it —
    /// <see cref="TranslatedLabelsFitTheirControlsTests"/> holds those — because an ellipsis is a
    /// fallback, not a layout. English keeps one label over the slot, and it is the one that
    /// cannot move: the dictionary entry is the catalog's <c>Label</c>.
    /// </summary>
    [Fact]
    public void EveryNavLabelMayEllipsiseNotJustTheOnesWithAMarker()
    {
        var css = NavbarCss();

        var start = css.IndexOf(".panel-label {", StringComparison.Ordinal);
        Assert.True(start > 0, "navbar.css must still style .panel-label.");
        var block = css[start..css.IndexOf('}', start)];

        Assert.Contains("min-width: 0", block, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis", block, StringComparison.Ordinal);
        Assert.DoesNotContain(".panel-badge) .panel-label", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Lifetime row has the least room of any label in the sidebar, because it is the only
    /// one carrying a marker and the marker takes its space first. Measured in Chromium against
    /// the shipped navbar.css at the 226px sidebar — navbar.js uses that for both the default and
    /// the minimum, so it is the usual case and the worst one at once: 54px rail + 1px rail rule
    /// leave a 171px panel, the list's 0.5rem padding leaves 155px, the row's 0.6rem padding
    /// leaves 135.8px, and a 17px icon plus a 0.6rem gap leave 109.2px for label and marker
    /// together.
    ///
    /// The marker used to be the word: "LIFETIME" rendered 57.4px wide (0.62rem, 0.05em tracking,
    /// 0.34rem padding, 1px border), a second gap took 9.6px, and the label was left 42.2px — less
    /// than any spelling here. It is a 14px lock now and the label has 85.6px, which is what makes
    /// these four fit; <see cref="TheIconMarkerLeavesTheLabelASlotEveryShortSpellingFits"/> holds
    /// the numbers.
    ///
    /// They stay the short spellings even so. 85.6px is enough for a name, not for a sentence, and
    /// "Higher dino levels" wants 107px of it. The page's own heading keeps the full name — a
    /// heading has a column to itself.
    /// </summary>
    [Theory]
    [InlineData("en", "Dino levels")]
    [InlineData("de", "Dino-Level")]
    [InlineData("ru", "Уровни дино")]
    [InlineData("zh-Hans", "恐龙等级")]
    public void TheMarkedRowsLabelIsTheShortSpelling(string code, string expected)
        => Assert.Equal(expected, TranslationParityTests.Read(code)["nav.page.guides.dino-level"]);

    /// <summary>
    /// The slot, and the declarations that produce it.
    ///
    /// A word chip is measured — its width follows the font, the tracking, the padding and the
    /// border, and it is the label that pays for all four. A glyph is declared: 14px is 14px,
    /// and nothing about a future accent or font moves it. So the sum ends
    /// <c>109.2 - 9.6 (gap) - 14 (marker) = 85.6px</c>, against 65.0px for "Dino levels", 64.0px
    /// for "Dino-Level", 80.7px for "Уровни дино" and 52.5px for "恐龙等级" — all four whole, none
    /// ellipsised. Rendered in Chromium against the real navbar.css and theme.css; the same
    /// harness reproduces the old chip at 57.4px and the old slot at 42.2px, which is how it is
    /// known to be measuring the same thing the last one did.
    ///
    /// What this pins is the declarations, because they are what the numbers rest on: the width,
    /// and the absence of everything a chip had. Padding or a border back on .panel-badge takes
    /// the slot straight back below the Russian spelling.
    /// </summary>
    [Fact]
    public void TheIconMarkerLeavesTheLabelASlotEveryShortSpellingFits()
    {
        const double LabelAndMarker = 109.2;  // measured: 226px sidebar, less rail, paddings, icon
        const double Gap = 9.6;               // .panel-link's 0.6rem
        const double Marker = 14;             // declared below, not measured
        const double Slot = LabelAndMarker - Gap - Marker;

        Assert.Equal(85.6, Slot, 1);

        foreach (var (code, wanted) in new[]
        {
            ("en", 65.0), ("de", 64.0), ("ru", 80.7), ("zh-Hans", 52.5),
        })
        {
            Assert.True(wanted < Slot, $"the {code} spelling wants {wanted}px of an {Slot}px slot.");
        }

        var block = BadgeBlock();

        Assert.Contains($"width: {Marker}px", block, StringComparison.Ordinal);
        Assert.Contains($"height: {Marker}px", block, StringComparison.Ordinal);
        Assert.Contains("flex-shrink: 0", block, StringComparison.Ordinal);

        // A chip's worth of width, none of which the slot can afford again.
        foreach (var chip in new[] { "padding", "border", "font-size", "letter-spacing", "text-transform" })
        {
            Assert.DoesNotContain(chip, block, StringComparison.Ordinal);
        }
    }

    /// <summary>The marker keeps to the accent it already had — a state, not a warning.</summary>
    [Fact]
    public void TheMarkerIntroducesNoColourOfItsOwn()
    {
        var block = BadgeBlock();

        Assert.Contains("color: var(--accent-purple-light)", block, StringComparison.Ordinal);
        Assert.DoesNotContain("#", block, StringComparison.Ordinal);
        Assert.DoesNotContain("rgb", block, StringComparison.Ordinal);
    }

    /// <summary>The declarations of <c>.panel-badge</c> itself, without its comment.</summary>
    private static string BadgeBlock()
    {
        var css = NavbarCss();
        var start = css.IndexOf(".panel-badge {", StringComparison.Ordinal);
        Assert.True(start > 0, "navbar.css must still style .panel-badge.");

        return css[start..css.IndexOf('}', start)];
    }

    private static string NavbarCss() => File.ReadAllText(Path.Combine(
        TranslationParityTests.RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "navbar.css"));

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
