using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// index.html loads all 42 page stylesheets on every route, so a bare <c>.content-card</c>
/// selector in any of them is not a page rule — it dresses every card in the app. server-styles.css
/// had one, and it is where the blur, the border and the <c>all</c> transition on every card in
/// RazorReaper actually came from. Nothing about that was visible from theme.css, and Home showed
/// what that costs: it opened with <c>backdrop-filter: none !important</c> on the same bare
/// selector, believing it opted out, and lost because it is linked one line earlier than Server.
/// Its cards have always been blurred.
///
/// The shipped look — blurred cards everywhere, Home included — is now declared once, in the base
/// rule in shared/theme.css, at the values it was already rendering. These tests pin that: the
/// base rule owns the card's surface, no page stylesheet reaches for the bare selector again, and
/// the two page copies stayed deleted instead of drifting back.
/// </summary>
public sealed class ContentCardOwnershipTests
{
    private static string Theme() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "theme.css"));

    private static string PageCssDirectory() => Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages");

    /// <summary>
    /// The bare selector, in a page stylesheet, at column zero. A page that wants to adjust its
    /// own cards scopes the selector to the page (<c>.uw-page .content-card</c>,
    /// <c>.game-page-container .content-card</c> — eight pages already do), which reaches its own
    /// route and no other. This is the rule that failed, so this is the rule that is pinned.
    /// </summary>
    [Fact]
    public void NoPageStylesheetDeclaresTheBareCardSelector()
    {
        var offenders = Directory.GetFiles(PageCssDirectory(), "*.css", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(
                WithoutComments(File.ReadAllText(path)),
                @"(?m)^\s*\.content-card\s*(,|\{)"))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The blur is the declaration the leak was really about: it is what makes every card a
    /// stacking context, which is why &lt;Dropdown&gt; has to paint its list in the top layer
    /// (see DropdownLayerTests) and why ColorField's popover needs care. A property with that much
    /// reach belongs where someone looking for it would look.
    /// </summary>
    [Fact]
    public void TheBaseRuleOwnsTheCardsSurface()
    {
        var rule = CardRule();

        Assert.Contains("backdrop-filter: blur(10px) !important;", rule, StringComparison.Ordinal);
        Assert.Contains("background: rgba(0, 0, 0, 0.15) !important;", rule, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid rgba(255, 255, 255, 0.1) !important;", rule, StringComparison.Ordinal);
        Assert.Contains("animation: fadeInUp 0.6s ease-out !important;", rule, StringComparison.Ordinal);

        // `all`, not the three named properties this rule used to list. The leaked copy said `all`
        // and loaded later, so `all` is what the app has been animating — writing the old list
        // back here would be a behaviour change dressed as a tidy-up.
        Assert.Contains("transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1) !important;", rule, StringComparison.Ordinal);

        // And it is the only place in the app that declares the blur for a card.
        var css = Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css");
        var elsewhere = Directory.GetFiles(css, "*.css", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "theme.css")
            .Where(path => Regex.IsMatch(
                WithoutComments(File.ReadAllText(path)),
                @"\.content-card[^{]*\{[^}]*backdrop-filter"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(elsewhere);
    }

    /// <summary>
    /// Home's <c>backdrop-filter: none</c> is gone rather than honoured. Deleting the rule that
    /// beat it would otherwise have handed Home the un-blurred cards it asked for four years ago
    /// — a visible redesign of the app's front page, arriving as a side effect of a cascade fix.
    /// The prose explaining that it never applied stays with it, so the next reader does not
    /// "restore" it.
    /// </summary>
    [Fact]
    public void HomeDoesNotAskToOptOutOfTheBlurAgain()
    {
        var home = File.ReadAllText(Path.Combine(PageCssDirectory(), "home-styles.css"));

        // Home styles no card at all now — not bare, not scoped. Its own panels still ask for
        // backdrop-filter: none, and those are unrelated classes that were never in this fight.
        Assert.DoesNotContain(".content-card", WithoutComments(home), StringComparison.Ordinal);
        Assert.Contains("It never applied", home, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card is deliberately absent from widget-highlights.css, and so are the three classes
    /// only ever worn alongside it. Those rules never reached a card — a forced <c>:hover</c> on
    /// one in the shipped app reports the resting border colour, not the 0.22 highlight — because
    /// the card's own <c>border</c> shorthand is <c>!important</c> at the same specificity and was
    /// declared later. Now that the shorthand lives in theme.css, which loads first, naming the
    /// card here again would switch a hover highlight on across all 40 routes.
    /// </summary>
    [Fact]
    public void TheHoverHighlightStaysOffForCards()
    {
        var highlights = WithoutComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "widget-highlights.css")));

        foreach (var worn in new[] { ".content-card", ".widget-card", ".ocbps-card", ".ocbps-intro" })
        {
            Assert.DoesNotContain(worn + ",", highlights, StringComparison.Ordinal);
            Assert.DoesNotContain(worn + Environment.NewLine, highlights, StringComparison.Ordinal);
        }

        // The panels that are not cards keep it — this is a correction, not a deletion.
        Assert.Contains(".troubleshooting-item", highlights, StringComparison.Ordinal);
        Assert.Contains("--widget-highlight-border", highlights, StringComparison.Ordinal);
    }

    /// <summary>The base .content-card block in theme.css, from its selector to its closing brace.</summary>
    private static string CardRule()
    {
        var css = Theme();
        var start = css.IndexOf("\n.content-card {", StringComparison.Ordinal);
        Assert.True(start >= 0, "theme.css must declare the base .content-card rule.");
        return css[start..css.IndexOf('}', start)];
    }

    /// <summary>
    /// CSS comments stripped, so the prose explaining why a declaration is gone is not read back
    /// as the declaration. Every file touched here carries that prose on purpose.
    /// </summary>
    private static string WithoutComments(string css)
        => Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
