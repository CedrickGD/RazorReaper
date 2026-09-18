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
/// really do follow the language, and the sidebar really is subscribed to hear it change.
///
/// MainLayout subscribing is not enough and cannot be: <c>&lt;SharedNavbar /&gt;</c> takes no
/// parameters, and Blazor's diff skips a retained child whose parameters are unchanged, so a
/// layout render stops at the layout.
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

    [Fact]
    public void TheSidebarSubscribesToTheLanguageAndUnsubscribes()
    {
        var navbar = Source("Components", "Shared", "SharedNavbar.razor");

        Assert.Contains("Localizer.LanguageChanged += OnLanguageChangedHandler", navbar, StringComparison.Ordinal);
        Assert.Contains("Localizer.LanguageChanged -= OnLanguageChangedHandler", navbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The switch arrives on whatever thread called SetLanguage, so the re-render goes through
    /// the gate rather than a bare InvokeAsync — an unobserved faulted dispatch here is RR-E1003,
    /// and the sidebar is mounted for the whole session.
    /// </summary>
    [Fact]
    public void TheSidebarRepaintsThroughTheGuardedDispatch()
    {
        var navbar = Source("Components", "Shared", "SharedNavbar.razor");

        Assert.Contains(
            "private void OnLanguageChangedHandler() => this.DispatchRender(() => InvokeAsync(StateHasChanged));",
            navbar,
            StringComparison.Ordinal);
        Assert.Contains("this.StopRenderDispatch();", navbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// The layout's subscription stays — it repaints the layout's own markup — but it may no
    /// longer be described as the one that covers the sidebar, because that is the belief that
    /// left the sidebar English.
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
