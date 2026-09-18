using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// A row that holds a &lt;Dropdown&gt;, a button and a number field held three different heights.
/// Measured in a headless browser against the shipped stylesheets: the trigger rendered 28.84px,
/// .btn and everything buttons.css folds onto it 32.13px, and the number field 34px — so the Mode
/// picker on Scripts sat 5px under the field above it, the three filters on Steam mods sat 13px
/// under the search box beside them, and Stretched resolution's Apply button never lined up with
/// the width and height fields it belongs to.
///
/// One token settles it: <c>--control-h</c>, in the same block as the spacing scale, at the number
/// field's own 34px — the field's height is set by readable type and padding rather than by taste,
/// so the others move to meet it. It is a floor (min-height), not a fixed height, so a control with
/// more in it still grows and the app's one tile-sized button design — .fov-btn-large on Scope and
/// Custom FOV, padded 1rem 1.5rem — is untouched.
///
/// These pin the token, the controls that take it, and — just as important — the ones that
/// deliberately do not: the field inside Crosshair's .num-stepper, the one inside Auto clicker's
/// .interval-cell, and .rr-pill-btn.sm. Each of those is a chip whose visible control is the thing
/// around it, and a 34px floor would push all three open.
/// </summary>
public sealed class ControlHeightTests
{
    private static string Theme() => Shared("theme.css");
    private static string Primitives() => Shared("primitives.css");
    private static string Buttons() => Shared("buttons.css");

    private static string Shared(string file) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", file));

    private static string PageCss(string file) => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages", file));

    /// <summary>
    /// One token, declared beside the spacing scale it is used with, and nowhere else. A second
    /// definition is how a "shared" height becomes two heights again.
    /// </summary>
    [Fact]
    public void ThereIsExactlyOneControlHeightToken()
    {
        var declarations = Directory
            .GetFiles(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css"), "*.css", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(WithoutComments(File.ReadAllText(path)), @"--control-h\s*:")
                .Select(_ => Path.GetFileName(path)))
            .ToArray();

        Assert.Equal(new[] { "theme.css" }, declarations);
        Assert.Contains("--control-h: 34px;", Theme(), StringComparison.Ordinal);

        // Beside --space-*, not off on its own at the top of the file.
        var tokens = Theme();
        var space7 = tokens.IndexOf("--space-7:", StringComparison.Ordinal);
        var control = tokens.IndexOf("--control-h:", StringComparison.Ordinal);
        var rootEnd = tokens.IndexOf("\n}", StringComparison.Ordinal);
        Assert.True(space7 > 0 && control > space7 && control < rootEnd,
            "--control-h belongs in the :root token block, after the spacing scale.");
    }

    /// <summary>
    /// The three shared control primitives take the token. buttons.css repeats it for the same
    /// reason it repeats .btn's padding: the ~23 page button classes it folds onto the shared
    /// design are meant to render as .btn does, and a height set only on .btn would leave every
    /// one of them 1.9px short of the picker beside it.
    /// </summary>
    [Fact]
    public void EveryControlThatSharesARowTakesTheToken()
    {
        Assert.Contains("min-height: var(--control-h);", Rule(Theme(), ".btn"), StringComparison.Ordinal);
        Assert.Contains("min-height: var(--control-h);", Rule(Primitives(), ".rr-pill-btn"), StringComparison.Ordinal);
        Assert.Contains("min-height: var(--control-h);", Rule(Primitives(), ".rr-dd-trigger"), StringComparison.Ordinal);
        Assert.Contains("min-height: var(--control-h);", Rule(Primitives(), "input[type=\"number\"]"), StringComparison.Ordinal);

        // The alias list in buttons.css, which has to carry it too.
        Assert.Contains("min-height: var(--control-h) !important;", Buttons(), StringComparison.Ordinal);

        // A floor, not a height: a fixed height would clip a button whose label wraps and would
        // shrink the padded calls to action on Scope and Custom FOV — .fov-btn-large, which this
        // very alias list folds onto .btn — to a row control.
        foreach (var css in new[] { Theme(), Primitives(), Buttons() })
        {
            Assert.DoesNotContain("height: var(--control-h)", css.Replace("min-height: var(--control-h)", string.Empty), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The text fields that stand in a row of other controls take it directly, because a rule keyed
    /// on <c>type="number"</c> cannot reach them: Scripts' .script-input is worn by 16 text fields
    /// as well as 31 number ones, Notifier's add row puts two .nt-input beside a picker, and Steam
    /// mods' search box was falling through to theme.css's 42px box in a grid of 28.8px triggers.
    /// Text fields that are NOT in such a row — Server's address, Account's profile fields — keep
    /// the roomier box on purpose, so this list is short and stays short.
    /// </summary>
    [Fact]
    public void TheTextFieldsThatShareARowTakeItToo()
    {
        Assert.Contains("min-height: var(--control-h);", Rule(PageCss("scripts-styles.css"), ".script-input"), StringComparison.Ordinal);
        Assert.Contains("min-height: var(--control-h);", Rule(PageCss("notifier-styles.css"), "input.nt-input"), StringComparison.Ordinal);

        var steam = Rule(PageCss("steam-mods-styles.css"), ".steam-mods-input");
        Assert.Contains("min-height: var(--control-h);", steam, StringComparison.Ordinal);
        // Same box the other in-row fields use; the floor alone cannot shrink a 42px field.
        Assert.Contains("padding: var(--space-2) var(--space-3) !important;", steam, StringComparison.Ordinal);
        Assert.Contains("font-size: 0.85rem !important;", steam, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-outs, named. Two of these are a field painted borderless inside a chip — the chip is
    /// the control the owner sees, and the field measured 34px and 16.7px respectively for exactly
    /// that reason. The third is the dense pill, 25.5px on purpose in the toolbars that were made
    /// tight. Without these resets the shared rules above would reach all three.
    /// </summary>
    [Fact]
    public void TheControlsThatAreDeliberatelyDifferentOptOut()
    {
        Assert.Contains("min-height: 0;", Rule(PageCss("crosshair-styles.css"), ".crosshair-layout .num-input"), StringComparison.Ordinal);
        Assert.Contains("min-height: 0;", Rule(PageCss("autoclicker-styles.css"), ".autoclicker-layout .interval-input"), StringComparison.Ordinal);
        Assert.Contains("min-height: 0;", Rule(Primitives(), ".rr-pill-btn.sm"), StringComparison.Ordinal);

        // Crosshair's picker is sized to the steppers next to it rather than to the shared row,
        // and that two-part selector is what keeps it there.
        Assert.Contains("height: 36px;", Rule(PageCss("crosshair-styles.css"), ".crosshair-layout .field .rr-dd-trigger"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The three stylesheets that define the shared controls never spell a control height out. A
    /// literal anywhere near the row range — the next person reaching for 32px or 36px — is the
    /// start of a second scale, which is the state this round replaced. Page stylesheets are not
    /// scanned: a page pinning its own panel or textarea is a different question, and the pages
    /// that DO hold a row control take the token by name in the two tests above.
    /// </summary>
    [Fact]
    public void TheSharedControlStylesheetsNeverSpellAHeightOut()
    {
        var offenders = new List<string>();

        foreach (var (name, css) in new[] { ("theme.css", Theme()), ("primitives.css", Primitives()), ("buttons.css", Buttons()) })
        {
            foreach (Match match in Regex.Matches(WithoutComments(css), @"min-height:\s*(\d+(?:\.\d+)?)px"))
            {
                var pixels = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (pixels is >= 20 and <= 60)
                {
                    offenders.Add($"{name}: min-height: {match.Groups[1].Value}px");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>One rule block in a stylesheet, from its selector to its closing brace.</summary>
    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The stylesheet must still declare {selector}.");
        return css[start..css.IndexOf('}', start)];
    }

    private static string WithoutComments(string css)
        => Regex.Replace(css, @"/\*[\s\S]*?\*/", string.Empty);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
