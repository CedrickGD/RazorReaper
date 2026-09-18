using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// Nine pages hold an <c>input[type="number"]</c> and, until this was shared, each of them
/// answered the question "what does a number field look like?" on its own. Desync, HUD overlay,
/// Gamma and the Line list drew a 34px box; Scripts, Stretched resolution and Custom FOV declared
/// nothing and fell through to the global <c>input{}</c> rule in theme.css, so the same control
/// was 43px tall there. Worse, the up/down spinners are drawn by Chromium from the OS light
/// theme, and only Auto clicker had ever hidden them — 56 of the 67 fields wore a pair of pale
/// native glyphs on a dark field.
///
/// One rule in shared/primitives.css owns the box now, the way the slider above it already did.
/// These pin that it is one rule and not ten, that the native parts stay hidden, that a disabled
/// field says so, and that the decimal fields keep the invariant-culture pattern that stops a
/// de-DE machine from writing "1,30" into a dot-only control.
/// </summary>
public sealed class NumberFieldTests
{
    private static string Primitives() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "primitives.css"));

    private static string PageCss(string file) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages", file));

    /// <summary>
    /// The box itself: padding from the spacing tokens, one type size, and digits in the mono
    /// stack so the number under the caret does not re-flow as it is typed. Radius, dark fill and
    /// focus ring are deliberately absent — theme.css already gives every input one of each, and
    /// repeating them here would be a second place to change them.
    /// </summary>
    [Fact]
    public void OneRuleGivesEveryNumberFieldItsBox()
    {
        var rule = Rule("input[type=\"number\"]");

        // !important is required, not decorative: theme.css:372 sets padding and font-size on
        // every input with !important, so a plain declaration here would never reach a field.
        Assert.Contains("padding: var(--space-2) var(--space-3) !important;", rule, StringComparison.Ordinal);
        Assert.Contains("font-size: 0.85rem !important;", rule, StringComparison.Ordinal);
        Assert.Contains("font-family: var(--font-mono);", rule, StringComparison.Ordinal);
        Assert.Contains("font-variant-numeric: tabular-nums;", rule, StringComparison.Ordinal);

        // Tells the engine the chrome it still draws itself is being painted on dark.
        Assert.Contains("color-scheme: dark;", rule, StringComparison.Ordinal);

        // Hard-coded pixels would be a second scale beside the tokens.
        Assert.DoesNotContain("padding: 0.", rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// The native spinners are the part the owner actually sees, and they are hidden in exactly
    /// one place. Auto clicker's own copy of this rule is gone — if a page reintroduces one, the
    /// app is back to "themed here, native there", which is the defect this replaced.
    /// </summary>
    [Fact]
    public void TheNativeSpinnersAreHiddenOnceForTheWholeApp()
    {
        var primitives = Primitives();

        Assert.Contains("input[type=\"number\"]::-webkit-outer-spin-button,", primitives, StringComparison.Ordinal);
        Assert.Contains("input[type=\"number\"]::-webkit-inner-spin-button {", primitives, StringComparison.Ordinal);
        Assert.Contains("-moz-appearance: textfield;", Rule("input[type=\"number\"]"), StringComparison.Ordinal);

        var pages = Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages");
        var offenders = Directory.GetFiles(pages, "*.css")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"spin-button|-moz-appearance:\s*textfield"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// And the pages that used to bring their own box no longer declare one. What is left on each
    /// of them is only what is genuinely theirs: a width, and whether the digits sit centred.
    /// </summary>
    [Fact]
    public void NoPageStillSizesItsOwnNumberField()
    {
        foreach (var (file, selector) in new[]
        {
            ("desync-styles.css", "input.dsy-num"),
            ("hud-overlay-styles.css", "input.hud-num"),
            ("gamma-styles.css", "input.gm-preset-val"),
            ("stretched-res-styles.css", ".stretched-num"),
            ("vision-styles.css", ".vision-fov-number"),
        })
        {
            var rule = Rule(PageCss(file), selector);
            Assert.DoesNotContain("padding", rule, StringComparison.Ordinal);
            Assert.DoesNotContain("font-size", rule, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Scripts is the one page that has to repeat the box, and for a reason worth pinning: its
    /// .script-input class is worn by 31 number fields AND by 16 text fields holding a key name
    /// or a console command. The shared rule can only reach the number half, so without this the
    /// two halves of the same column would sit at two different heights.
    /// </summary>
    [Fact]
    public void ScriptsRepeatsTheBoxOnlyBecauseItsTextFieldsShareTheClass()
    {
        var rule = Rule(PageCss("scripts-styles.css"), ".script-input");

        Assert.Contains("padding: var(--space-2) var(--space-3) !important;", rule, StringComparison.Ordinal);
        Assert.Contains("font-size: 0.85rem !important;", rule, StringComparison.Ordinal);

        var page = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Components", "Pages", "Scripts.razor"));
        var wearers = Tags(page).Where(tag => tag.Contains("script-input", StringComparison.Ordinal)).ToArray();

        Assert.Contains(wearers, tag => tag.Contains("type=\"number\"", StringComparison.Ordinal));
        Assert.Contains(wearers, tag => !tag.Contains("type=\"number\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing in the app said "this field is off" before: a disabled number input was
    /// pixel-identical to a live one. The treatment is the one the slider and the buttons already
    /// use, so disabled reads the same whatever the control is.
    /// </summary>
    [Fact]
    public void ADisabledNumberFieldLooksDisabled()
    {
        var number = Rule("input[type=\"number\"]:disabled");
        var slider = Rule("input[type=\"range\"]:disabled");

        foreach (var declaration in new[] { "cursor: not-allowed;", "opacity: 0.45;" })
        {
            Assert.Contains(declaration, number, StringComparison.Ordinal);
            Assert.Contains(declaration, slider, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The culture trap, pinned where it can regress. @bind formats a double with the current
    /// culture, and this machine runs de-DE, so it writes value="1,30" — which a number input
    /// (dot-only, like a range input) throws away, snapping to the midpoint of min/max. Gamma and
    /// Custom FOV both document having been bitten by it. Any field that steps by a fraction has
    /// to render its value through InvariantCulture and read it back by hand.
    /// </summary>
    [Fact]
    public void ADecimalFieldNeverGoesThroughBind()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");
        var decimalFields = new List<(string Page, string Tag)>();

        foreach (var path in Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories))
        {
            foreach (var tag in Tags(File.ReadAllText(path)))
            {
                if (!tag.Contains("type=\"number\"", StringComparison.Ordinal)) continue;
                // A fractional step is the tell. min/max can hold a C# expression with a dot in
                // it (StretchedResService.MinDimension), which says nothing about the value.
                if (!Regex.IsMatch(tag, @"step=""\d*\.\d")) continue;
                decimalFields.Add((Path.GetFileName(path)!, tag));
            }
        }

        // The pages that were actually bitten, so this cannot quietly pass on an empty set.
        Assert.Contains(decimalFields, f => f.Page == "Gamma.razor");
        Assert.Contains(decimalFields, f => f.Page == "CustomFov.razor");

        foreach (var (page, tag) in decimalFields)
        {
            Assert.DoesNotContain("@bind", tag, StringComparison.Ordinal);
            Assert.True(
                tag.Contains("CultureInfo.InvariantCulture", StringComparison.Ordinal),
                $"{page} writes a decimal number field without InvariantCulture: {tag}");
        }
    }

    /// <summary>
    /// A wheel over a focused field used to spin it. Chromium does that itself whenever the field
    /// is focused and the wheel lands on it, so scrolling a page with the pointer resting on a
    /// field the user had just typed into rewrote it — and on the pages that apply on change, the
    /// new value was live before anyone noticed. Blurring in the capture phase is what stops it:
    /// the engine runs its default action after dispatch and asks whether the element is focused.
    /// </summary>
    [Fact]
    public void TheWheelCannotEditANumberField()
    {
        var guard = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "js", "number-field.js"));

        Assert.Contains("if (!focused || focused.type !== 'number') return;", guard, StringComparison.Ordinal);
        Assert.Contains("focused.blur();", guard, StringComparison.Ordinal);

        // capture, so it runs before anything else can act on the wheel; passive, so the scroll
        // the user actually asked for stays on the compositor thread — and so that cancelling the
        // wheel is not even available here, which is the point. Stopping the spin by cancelling
        // it would stop the scroll with it.
        Assert.Contains("{ passive: true, capture: true }", guard, StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));
        Assert.Contains("js/number-field.js", index, StringComparison.Ordinal);
    }

    /// <summary>
    /// The nine pages the shared rule reaches. Listed so the blast radius of a change to it is
    /// stated rather than rediscovered — these are the routes to look at after touching it.
    /// </summary>
    [Fact]
    public void TheSharedRuleReachesEveryPageThatHasANumberField()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");
        var pages = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("type=\"number\"", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)!)
            .ToArray();

        foreach (var page in new[]
        {
            "Autoclicker.razor", "Crosshair.razor", "Desync.razor", "Gamma.razor", "HudOverlay.razor",
            "LineList.razor", "Scripts.razor", "StretchedRes.razor", "CustomFov.razor",
        })
        {
            Assert.Contains(page, pages);
        }
    }

    /// <summary>Every &lt;input&gt; tag in a Razor file, whole, including the ones spanning lines.</summary>
    private static IEnumerable<string> Tags(string markup)
        => Regex.Matches(markup, @"<input\b[\s\S]*?/>").Select(m => m.Value);

    /// <summary>One rule block in primitives.css, from its selector to its closing brace.</summary>
    private static string Rule(string selector) => Rule(Primitives(), selector);

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The stylesheet must still style {selector}.");
        return css[start..css.IndexOf('}', start)];
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
