using RazorReaper.Navigation;

namespace RazorReaper.UnitTests.Localization;

/// <summary>
/// The labels that sit in a control whose width is decided by the layout rather than by the
/// word in it. English wrote those controls, so English fits them; a translation is 20–70%
/// longer and does not, and the app had no way to say so — a clipped label looks like a label.
///
/// Measured over CDP in the real WebView at 1424x713 with the shipped CSS, one page at a time,
/// every route in <see cref="NavCatalog"/>, German and Russian: 39 clipped labels in German and
/// 81 in Russian, against 5 in English. They came from four controls.
///
/// <list type="bullet">
///   <item><b>The sidebar's page rows</b> — 109.2px for the label, measured in
///   <see cref="NavigationTranslationTests.TheIconMarkerLeavesTheLabelASlotEveryShortSpellingFits"/>
///   and ellipsised past it. Nine German and Russian names wanted up to 187.3px
///   ("Глобальные горячие клавиши").</item>
///   <item><b>The sidebar's group heading</b> — 143.8px, same treatment. One name over
///   ("Помощь и о программе", 156.2px).</item>
///   <item><b>Server's action buttons</b> — a 1fr column of a two-column grid, 83.4px inside the
///   padding, <c>overflow: hidden</c> and no ellipsis, so the text is cut mid-word at both ends.
///   "Zu Steam hinzufügen" wanted 124.7px.</item>
///   <item><b>The HUD's placement grid</b> — 280px over three columns is 74px a cell, and a label
///   that does not fit takes a second line and makes the whole nine-cell grid taller. Four of the
///   Russian corner names did.</item>
/// </list>
///
/// The fix is the wording, not the control: a name that needs a tooltip to be read is a name
/// nobody reads. What is pinned here is the short spelling and the measured width it was chosen
/// for, so the next pass at these files cannot quietly put the long one back.
/// </summary>
public sealed class TranslatedLabelsFitTheirControlsTests
{
    /// <summary>
    /// The spellings, and what each was measured at against its control's budget. Rendered in
    /// the shipped WebView with the shipped CSS — the same harness that found them over.
    /// </summary>
    [Theory]
    // Sidebar page row — 109.2px. Was: 130.2, 127.6, 126.8, 122.9, 121.1, 113.9.
    [InlineData("de", "nav.page.char-manager", "Char-Verwaltung", 100.1, SidebarRow)]
    [InlineData("de", "nav.page.stretched-res", "Gestreckte Res", 86.9, SidebarRow)]
    [InlineData("de", "nav.page.inbox", "Posteingang", 73.8, SidebarRow)]
    [InlineData("de", "nav.page.hotkeys", "Tastenkürzel", 73.9, SidebarRow)]
    [InlineData("de", "nav.page.feedback", "Feedback", 56.1, SidebarRow)]
    [InlineData("de", "nav.page.underwater-drops", "Tiefsee-Drops", 82.6, SidebarRow)]
    // Was: 187.3, 147.3, 146.4, 136.9, 133.3, 131.9, 122.0, 120.4, 116.8, 116.4.
    [InlineData("ru", "nav.page.hotkeys", "Клавиши", 56.7, SidebarRow)]
    [InlineData("ru", "nav.page.stretched-res", "Растяжка экрана", 102.8, SidebarRow)]
    [InlineData("ru", "nav.page.char-manager", "Персонажи", 71.5, SidebarRow)]
    [InlineData("ru", "nav.page.inbox", "Входящие", 62.8, SidebarRow)]
    [InlineData("ru", "nav.page.feedback", "Поддержка", 71.3, SidebarRow)]
    [InlineData("ru", "nav.page.vision", "Видимость", 67.6, SidebarRow)]
    [InlineData("ru", "nav.page.launch-options", "Опции запуска", 92.5, SidebarRow)]
    [InlineData("ru", "nav.page.file-modifier", "Правка файлов", 95.5, SidebarRow)]
    [InlineData("ru", "nav.page.underwater-drops", "Подводный лут", 96.6, SidebarRow)]
    [InlineData("ru", "nav.page.line-list", "Список линий", 87.2, SidebarRow)]
    // Sidebar group heading — 143.8px, uppercased with 0.1em tracking. Was 156.2.
    [InlineData("ru", "nav.group.help-about", "Помощь и о нас", 108.1, SidebarHeading)]
    // Server's action buttons — 83.4px. Was 124.7, 110.2, 87.9.
    [InlineData("de", "server.addtosteam", "Zu Favoriten", 73.7, ServerButton)]
    [InlineData("ru", "server.addtosteam", "В избранное", 76.8, ServerButton)]
    [InlineData("ru", "server.connect", "Подключить", 74.2, ServerButton)]
    // Notifier's sound dropdown — 88px, ellipsised. Was 98.9.
    [InlineData("de", "notifier.sound.notification", "Hinweiston", 62.6, SoundDropdown)]
    // HUD placement cell — 74px, and a second line grows the whole grid. Was 77.6, 85.3, 78.8
    // for the three corners over; the edges and the fourth corner came with them so the nine
    // cells read as one set rather than two.
    [InlineData("ru", "hud.anchor.topleft", "Верх слева", 64.2, HudPlacementCell)]
    [InlineData("ru", "hud.anchor.top", "Верх", 27.8, HudPlacementCell)]
    [InlineData("ru", "hud.anchor.topright", "Верх справа", 71.8, HudPlacementCell)]
    [InlineData("ru", "hud.anchor.bottomleft", "Низ слева", 58.8, HudPlacementCell)]
    [InlineData("ru", "hud.anchor.bottom", "Низ", 22.5, HudPlacementCell)]
    [InlineData("ru", "hud.anchor.bottomright", "Низ справа", 66.5, HudPlacementCell)]
    public void TheShortenedLabelIsTheOneThatWasMeasured(
        string code, string key, string expected, double measuredPx, double budgetPx)
    {
        Assert.Equal(expected, TranslationParityTests.Read(code)[key]);

        // There is no browser here, so the width is a recorded measurement rather than something
        // this test can take again. Pinning it against the control's budget is still worth the
        // line: it is where a replacement wording gets checked, and it fails loudly if a budget
        // above is ever edited to fit a word rather than the other way round.
        Assert.True(measuredPx < budgetPx,
            $"{code} {key} = \"{expected}\" was measured at {measuredPx}px in a {budgetPx}px control.");
    }

    /// <summary>The label slot in a sidebar page row — see <see cref="NavigationTranslationTests"/>.</summary>
    private const double SidebarRow = 109.2;

    /// <summary>The panel's group heading: a 171px panel less 0.85rem of padding each side.</summary>
    private const double SidebarHeading = 143.8;

    /// <summary>One 1fr column of Server's two-column button grid, less 0.8rem of padding each side.</summary>
    private const double ServerButton = 83.4;

    /// <summary>The value inside Notifier's per-type sound dropdown.</summary>
    private const double SoundDropdown = 88;

    /// <summary>A cell of the HUD's 280px placement grid: three columns, 2px gaps, 0.5rem padding.</summary>
    private const double HudPlacementCell = 74;

    /// <summary>
    /// The cheap guard behind the measured ones: a character count, per language, set at what the
    /// longest name that fits actually is. It is a proxy and it is honest about being one — 16
    /// Cyrillic characters were 120.4px ("Изменение файлов") and 16 Latin ones 109px — so what it
    /// buys is not a proof that a new name fits, it is that a new name longer than anything here
    /// cannot land without somebody measuring it.
    ///
    /// English is 16 rather than 15 because "Underwater Drops" is 16 and fits, and it carries the
    /// one exception in the app: <c>nav.page.feedback</c> is 18 and wants 121.1px of the 109.2px
    /// row. That entry is <see cref="NavPage.Label"/> as well as a word — diagnostics, telemetry
    /// and Rich Presence report it, and
    /// <see cref="NavigationTranslationTests.EveryPageAndGroupHasAnEnglishEntryThatMatchesTheCatalog"/>
    /// pins the dictionary to the catalog — so it cannot be shortened here, and it is the reason
    /// the sidebar row carries a <c>title</c>. German and Russian shortened theirs instead.
    /// </summary>
    [Theory]
    [InlineData("en", 16)]
    [InlineData("de", 15)]
    [InlineData("ru", 15)]
    [InlineData("zh-Hans", 8)]
    public void NoSidebarNameIsLongerThanTheLongestOneThatFits(string code, int limit)
    {
        var dictionary = TranslationParityTests.Read(code);

        var offenders = SidebarNameKeys()
            .Where(key => dictionary[key].Length > limit)
            .Select(key => $"{key} = \"{dictionary[key]}\" ({dictionary[key].Length})")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

        string[] allowed = code == "en"
            ? ["nav.page.feedback = \"Feedback & Support\" (18)"]
            : [];

        Assert.Equal(allowed, offenders);
    }

    /// <summary>
    /// The row's way out for the name that could not be shortened, and for whatever a future
    /// translation makes too long. It is the label itself, resolved the same way the label is —
    /// a title holding a stale spelling would be worse than no title at all.
    /// </summary>
    [Fact]
    public void TheSidebarRowNamesItselfInATitle()
    {
        var markup = File.ReadAllText(Path.Combine(
            TranslationParityTests.RepositoryRoot(),
            "RazorReaper", "Components", "Shared", "SharedNavbar.razor"));

        Assert.Contains("title=\"@Localizer.T(entry.LabelKey)\"", markup, StringComparison.Ordinal);
        Assert.Contains("<span class=\"panel-label\">@Localizer.T(entry.LabelKey)</span>", markup, StringComparison.Ordinal);
    }

    /// <summary>Every page name and group name the sidebar renders, read off the catalog.</summary>
    private static IEnumerable<string> SidebarNameKeys()
        => NavCatalog.Groups.Select(g => g.NameKey)
            .Concat(NavCatalog.Pages.Select(p => p.LabelKey));
}
