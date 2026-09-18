using System.Globalization;
using RazorReaper.Navigation;
using RazorReaper.Services.Localization;
using RazorReaper.UnitTests.Infrastructure;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The repaint, not the dictionary.
///
/// Picking "Deutsch" on Settings turned that page German and left the sidebar English until some
/// later render pushed it through. The lookups were never the problem — the sidebar already
/// resolved every label at render time — so this pins the two halves that were: the labels
/// really do follow the language, and the sidebar really does hear it change.
///
/// The sidebar heard it through a subscription of its own, once. It now hears it the way every
/// other component in the app does, through MainLayout's Language cascade; the mechanism and the
/// scan that keeps it honest live in <see cref="LanguageCascadeTests"/>, and what stays here is
/// the sidebar's own end of it.
/// </summary>
public sealed class LanguageSwitchReachesTheChromeTests
{
    private static Localizer New()
        => new(new FakePreferencesStore(), CultureInfo.GetCultureInfo("en-US"));

    private static string Source(params string[] relativePath)
        => File.ReadAllText(Path.Combine(
            new[] { TranslationParityTests.RepositoryRoot(), "RazorReaper" }.Concat(relativePath).ToArray()));

    // ---- The labels follow the language ------------------------------------

    [Fact]
    public void EveryCatalogLabelChangesWithTheLanguage()
    {
        var localizer = New();
        var german = TranslationParityTests.Read("de");

        Assert.All(NavCatalog.Pages, page => Assert.Equal(page.Label, localizer.T(page.LabelKey)));
        Assert.All(NavCatalog.Groups, group => Assert.Equal(group.Name, localizer.T(group.NameKey)));

        localizer.SetLanguage(AppLanguages.German);

        Assert.All(NavCatalog.Pages, page => Assert.Equal(german[page.LabelKey], localizer.T(page.LabelKey)));
        Assert.All(NavCatalog.Groups, group => Assert.Equal(german[group.NameKey], localizer.T(group.NameKey)));
    }

    /// <summary>
    /// Equality against the dictionary passes just as well if the dictionary is a copy of the
    /// English one, which is what a half-done translation looks like. Most of the sidebar has to
    /// actually read differently, and the handful that does not — "Gamma", "Server", "Credits" —
    /// are the words German keeps.
    /// </summary>
    [Fact]
    public void MostOfTheSidebarReallyReadsDifferently()
    {
        var localizer = New();
        localizer.SetLanguage(AppLanguages.German);

        var changed = NavCatalog.Pages.Count(p => localizer.T(p.LabelKey) != p.Label);

        Assert.True(changed > NavCatalog.Pages.Count / 2,
            $"only {changed} of {NavCatalog.Pages.Count} page labels differ in German.");
    }

    [Theory]
    [InlineData(AppLanguages.German, "Startseite", "Allgemein")]
    [InlineData(AppLanguages.Russian, "Главная", "Основное")]
    [InlineData(AppLanguages.SimplifiedChinese, "主页", "核心")]
    public void SwitchingAndSwitchingBackIsSymmetric(string code, string home, string core)
    {
        var localizer = New();
        var homePage = NavCatalog.FindByRoute("/home")!;
        var coreGroup = NavCatalog.Groups[0];

        localizer.SetLanguage(code);
        Assert.Equal(home, localizer.T(homePage.LabelKey));
        Assert.Equal(core, localizer.T(coreGroup.NameKey));

        localizer.SetLanguage(AppLanguages.English);
        Assert.Equal("Home", localizer.T(homePage.LabelKey));
        Assert.Equal("Core", localizer.T(coreGroup.NameKey));
    }

    // ---- The sidebar hears the switch --------------------------------------

    /// <summary>
    /// The sidebar declares the cascading parameter, and nothing else. Its own subscription was
    /// correct and is now redundant — it renders inside MainLayout, which is where the cascade is
    /// published — and a second path to the same repaint is what left the last reader guessing
    /// which of the two was carrying the switch.
    /// </summary>
    [Fact]
    public void TheSidebarTakesTheSwitchFromTheCascadeAndNotFromAnEventOfItsOwn()
    {
        var navbar = Source("Components", "Shared", "SharedNavbar.razor");

        Assert.Contains("[CascadingParameter(Name = \"Language\")]", navbar, StringComparison.Ordinal);
        Assert.DoesNotContain("Localizer.LanguageChanged +=", navbar, StringComparison.Ordinal);
        Assert.DoesNotContain("Localizer.LanguageChanged -=", navbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate is still there for the subscriptions the sidebar does keep — the license state and
    /// the overlay both arrive off the renderer's thread, and an unobserved faulted dispatch from a
    /// component mounted for the whole session is RR-E1003.
    /// </summary>
    [Fact]
    public void TheSidebarsRemainingSubscriptionsStillRepaintThroughTheGuardedDispatch()
    {
        var navbar = Source("Components", "Shared", "SharedNavbar.razor");

        Assert.Contains(
            "private void OnLicenseStateChangedHandler() => this.DispatchRender(() => InvokeAsync(StateHasChanged));",
            navbar,
            StringComparison.Ordinal);
        Assert.Contains("this.StopRenderDispatch();", navbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout's subscription stays, and it is now the only one in the app: it repaints the
    /// layout, and the layout's render is what hands the cascade its new value. It may still not
    /// be described as reaching the sidebar directly, because that is the belief that left the
    /// sidebar English.
    /// </summary>
    [Fact]
    public void TheLayoutNoLongerClaimsItRepaintsTheSidebar()
    {
        var layout = Source("Components", "Layout", "MainLayout.razor");

        Assert.Contains("Localizer.LanguageChanged += HandleLanguageChanged", layout, StringComparison.Ordinal);
        Assert.Contains("Localizer.LanguageChanged -= HandleLanguageChanged", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("re-renders the sidebar", layout, StringComparison.Ordinal);
    }

    // ---- The palette --------------------------------------------------------

    [Fact]
    public void ThePaletteBuildsItsRowsInTheActiveLanguage()
    {
        var search = Source("Components", "Shared", "GlobalSearch.razor");

        Assert.Contains("@inject ILocalizer Localizer", search, StringComparison.Ordinal);
        Assert.Contains("PalettePages.ToItem(page, Localizer)", search, StringComparison.Ordinal);
        Assert.Contains("Localizer.T(\"palette.placeholder\")", search, StringComparison.Ordinal);
    }

    [Fact]
    public void APageRowReadsInTheActiveLanguage()
    {
        var localizer = New();
        var page = NavCatalog.FindByRoute("/home")!;

        Assert.Equal("Home", PalettePages.ToItem(page, localizer).Title);

        localizer.SetLanguage(AppLanguages.German);
        Assert.Equal("Startseite", PalettePages.ToItem(page, localizer).Title);
    }

    /// <summary>
    /// "INI Changer" is the case that proves it: its keywords are ini/config/presets and none of
    /// them is "changer", so once the row reads "INI-Wechsler" the English name is the only thing
    /// left that can answer the query someone types out of habit.
    /// </summary>
    [Fact]
    public void AnEnglishNameStaysFindableAfterTheRowIsTranslated()
    {
        var page = NavCatalog.FindByRoute("/ini-changer")!;
        var translated = PalettePages.ToItem(page, "INI-Wechsler");

        Assert.DoesNotContain("changer", page.Keywords, StringComparer.OrdinalIgnoreCase);

        Assert.Contains(page.Label, translated.Keywords);
        Assert.Contains(translated, PaletteSearch.Rank([translated], "ini changer"));
        Assert.Contains(translated, PaletteSearch.Rank([translated], "INI Changer"));

        // The active language answers through the title itself.
        Assert.Contains(translated, PaletteSearch.Rank([translated], "wechsler"));

        // Without the English name the row is unreachable that way — which is what this adds.
        var withoutIt = new PaletteItem
        {
            Kind = PaletteKind.Page,
            Id = translated.Id,
            Title = translated.Title,
            Subtitle = translated.Subtitle,
            Category = translated.Category,
            IconSvg = translated.IconSvg,
            Keywords = page.Keywords,
            Route = translated.Route,
        };

        Assert.Empty(PaletteSearch.Rank([withoutIt], "ini changer"));
    }

    /// <summary>An English row does not carry its own title twice.</summary>
    [Fact]
    public void TheEnglishRowIsUnchanged()
    {
        var page = NavCatalog.FindByRoute("/ini-changer")!;

        Assert.Equal(page.Keywords, PalettePages.ToItem(page, page.Label).Keywords);
    }
}
