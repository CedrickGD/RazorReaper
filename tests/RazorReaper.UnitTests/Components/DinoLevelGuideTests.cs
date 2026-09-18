using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// /guides/dino-level is the first page in the app that is actually sold separately: it ships with
/// the perpetual key and not with the monthly plans. That makes two things load-bearing, and both
/// are one careless edit away from silently breaking —
/// <list type="number">
///   <item>the gate is the hard one (PremiumLock's RequiresLifetime), not the soft blur that
///         EnforceLock has switched off app-wide;</item>
///   <item>the guide's text lives inside that gate, so a locked visitor gets no DOM to read.</item>
/// </list>
/// A blur is a picture of the text; these tests are here so nobody turns the paywall back into one.
/// </summary>
public sealed class DinoLevelGuideTests
{
    private const string Route = "/guides/dino-level";
    private const string GateOpen = "<PremiumLock RequiresLifetime=\"true\">";
    private const string GateClose = "</PremiumLock>";

    // ---- The route and its place in the sidebar -----------------------------

    [Fact]
    public void ThePageOwnsTheGuidesDinoLevelRoute()
    {
        var page = Page();

        Assert.StartsWith("@page \"" + Route + "\"", page, StringComparison.Ordinal);
        Assert.Contains("<h1 class=\"page-title\">Higher dino levels</h1>", page, StringComparison.Ordinal);
        Assert.Contains("class=\"page-subtitle\"", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// It belongs with Bosses and TP Locations — the other ARK knowledge pages. A paywall is not a
    /// category, so it does not get a section of its own.
    /// </summary>
    [Fact]
    public void TheSidebarListsItWithTheOtherArkKnowledgePages()
    {
        var catalog = Catalog();

        var intel = catalog.IndexOf("new NavGroup(\"Mods & Intel\"", StringComparison.Ordinal);
        var nextGroup = catalog.IndexOf("new NavGroup(\"Utilities\"", StringComparison.Ordinal);
        var entry = catalog.IndexOf(
            "new NavPage(\"Dino levels\", \"" + Route + "\", \"Mods & Intel\"",
            StringComparison.Ordinal);

        Assert.True(intel > 0, "the Mods & Intel group must exist");
        Assert.True(entry > intel && entry < nextGroup, "the guide must sit inside Mods & Intel");
    }

    /// <summary>The row says which plan it needs, so nobody clicks it to find out.</summary>
    [Fact]
    public void TheNavRowCarriesTheLifetimeMarker()
    {
        var catalog = Catalog();
        var entry = catalog.IndexOf("new NavPage(\"Dino levels\"", StringComparison.Ordinal);
        var end = catalog.IndexOf("new NavPage(\"TP Locations\"", entry, StringComparison.Ordinal);

        Assert.Contains("Badge: \"Lifetime\"", catalog[entry..end], StringComparison.Ordinal);

        // Markers are for restrictions only — a second one means a second kind of lock, not a
        // decoration someone liked the look of.
        Assert.Single(Regex.Matches(catalog, @"Badge: ""[^""]+"""));

        var navbar = Component("Shared", "SharedNavbar.razor");
        Assert.Contains("<span class=\"panel-badge\">@entry.Badge</span>", navbar, StringComparison.Ordinal);
        Assert.Contains("@if (!string.IsNullOrWhiteSpace(entry.Badge))", navbar, StringComparison.Ordinal);
    }

    // ---- The gate -----------------------------------------------------------

    [Fact]
    public void TheGuideSitsBehindTheLifetimeGate()
    {
        var page = Page();

        Assert.Contains(GateOpen, page, StringComparison.Ordinal);

        // Not the soft one: RequiresPremium is switched off app-wide by EnforceLock, so a page
        // that asked for it would be open to everyone.
        Assert.DoesNotContain("RequiresPremium", page, StringComparison.Ordinal);
        Assert.DoesNotContain("EnforceLock", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The locked branch renders the panel and nothing else. If ChildContent ever appears in it —
    /// blurred, hidden, collapsed, however — the guide is back in the DOM and the paywall is
    /// decoration.
    /// </summary>
    [Fact]
    public void TheLockedBranchRendersNoGuideAtAll()
    {
        var lockComponent = Component("Shared", "PremiumLock.razor");

        var start = lockComponent.IndexOf("@if (IsLifetimeLocked)", StringComparison.Ordinal);
        Assert.True(start > 0, "PremiumLock must branch on IsLifetimeLocked");

        var elseAt = lockComponent.IndexOf("else", start, StringComparison.Ordinal);
        Assert.True(elseAt > start, "the locked branch must have an else branch after it");

        var locked = lockComponent[start..elseAt];
        Assert.DoesNotContain("ChildContent", locked, StringComparison.Ordinal);
        Assert.DoesNotContain("blur", locked, StringComparison.OrdinalIgnoreCase);

        // …and the unlocked branch is the only place the content is rendered.
        Assert.Single(Regex.Matches(lockComponent, @"@ChildContent"));
    }

    /// <summary>
    /// RequiresLifetime enforces on its own. EnforceLock is the owner's app-wide "do not lock
    /// anything yet" switch and stays false; wiring the paywall through it would switch the
    /// paywall off too.
    /// </summary>
    [Fact]
    public void TheLifetimeGateDoesNotDependOnEnforceLock()
    {
        var lockComponent = Component("Shared", "PremiumLock.razor");

        var rule = Regex.Match(lockComponent, @"private bool IsLifetimeLocked =>[^;]+;", RegexOptions.Singleline);
        Assert.True(rule.Success, "PremiumLock must define IsLifetimeLocked");
        Assert.DoesNotContain("EnforceLock", rule.Value, StringComparison.Ordinal);
        Assert.Contains("LifetimeAccess.IsLifetime(LicenseService)", rule.Value, StringComparison.Ordinal);

        // The global default the owner asked for is untouched.
        Assert.Contains("public bool EnforceLock { get; set; } = false;", lockComponent, StringComparison.Ordinal);
        Assert.Contains("public bool RequiresLifetime { get; set; } = false;", lockComponent, StringComparison.Ordinal);
    }

    /// <summary>Locked is not a dead end: buy the key, or redeem one you already have.</summary>
    [Fact]
    public void TheLockedPanelOffersTheShopAndTheKeyForm()
    {
        var lockComponent = Component("Shared", "PremiumLock.razor");

        Assert.Contains("<h2 class=\"lifetime-gate-title\">Lifetime only</h2>", lockComponent, StringComparison.Ordinal);

        var buy = Regex.Match(lockComponent, @"<a[^>]*>Buy Premium</a>", RegexOptions.Singleline);
        Assert.True(buy.Success, "\"Buy Premium\" must be a link to the shop.");
        Assert.Contains("href=\"@StoreLinks.Store\"", buy.Value, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", buy.Value, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener\"", buy.Value, StringComparison.Ordinal);

        var redeem = Regex.Match(lockComponent, @"<button[^>]*>\s*Redeem key\s*</button>", RegexOptions.Singleline);
        Assert.True(redeem.Success, "\"Redeem key\" must be a <button>, not a link.");
        Assert.Contains("aria-haspopup=\"dialog\"", redeem.Value, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OpenLicenseOverlay\"", redeem.Value, StringComparison.Ordinal);
        Assert.Contains("private void OpenLicenseOverlay() => LicenseOverlay.Open();", lockComponent, StringComparison.Ordinal);

        // The shop address is the one in StoreLinks, not a second copy that goes stale.
        Assert.DoesNotContain("sellhub", lockComponent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Redeeming a key has to open the gate under it, without a navigation.</summary>
    [Fact]
    public void TheGateRedrawsWhenTheLicenseChanges()
    {
        var lockComponent = Component("Shared", "PremiumLock.razor");

        Assert.Contains("LicenseService.OnLicenseStateChanged += OnLicenseStateChangedHandler;", lockComponent, StringComparison.Ordinal);
        Assert.Contains("LicenseService.OnLicenseStateChanged -= OnLicenseStateChangedHandler;", lockComponent, StringComparison.Ordinal);

        // RR-E1003: a render dispatched from outside the renderer goes through DispatchRender, and
        // the component stops dispatching when it is disposed.
        Assert.Contains("this.DispatchRender(() => InvokeAsync(StateHasChanged));", lockComponent, StringComparison.Ordinal);
        Assert.Contains("this.StopRenderDispatch();", lockComponent, StringComparison.Ordinal);
    }

    // ---- What is, and is not, readable while locked --------------------------

    /// <summary>
    /// The method is the thing being sold. It may appear only inside the gate — the header, which
    /// renders above it either way, says what the page is about and not how it is done.
    /// </summary>
    [Fact]
    public void TheMethodItselfExistsOnlyInsideTheGate()
    {
        var page = Page();
        var open = page.IndexOf(GateOpen, StringComparison.Ordinal);
        var close = page.IndexOf(GateClose, StringComparison.Ordinal);

        Assert.True(open > 0 && close > open, "the page must wrap its body in the gate");

        var outside = page[..open] + page[(close + GateClose.Length)..];
        foreach (var giveaway in new[] { "Noglin", "transmitter", "unclaim", "render distance" })
        {
            Assert.DoesNotContain(giveaway, outside, StringComparison.OrdinalIgnoreCase);
        }

        // The nav row and the palette hit are visible to everyone, so they must not leak it either.
        var catalog = Catalog();
        var start = catalog.IndexOf("new NavPage(\"Dino levels\"", StringComparison.Ordinal);
        var description = catalog[start..catalog.IndexOf("new[]", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("Noglin", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The owner's notes, in the owner's order. The order is the trick.</summary>
    [Fact]
    public void TheStepsFollowTheOwnersNotesInOrder()
    {
        var page = Page();
        var steps = Regex.Matches(page, @"<span class=""dlg-step-head"">([^<]+)</span>")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Unclaim the tamed dino",
                "Catch it with a Noglin",
                "Upload the Noglin at a transmitter",
                "Leave render distance",
                "Wait until the Noglin is gone from render",
                "Come back",
                "Tame it again",
                "Repeat",
            },
            steps);

        // Every step carries its one line of detail.
        Assert.Equal(steps.Length, Regex.Matches(page, @"<span class=""dlg-step-detail"">").Count);
    }

    [Fact]
    public void TheGuideCarriesItsRequirementsCaveatsAndAWayToReportDrift()
    {
        var page = Page();

        Assert.Contains("<h2 class=\"dlg-card-title\">Requirements</h2>", page, StringComparison.Ordinal);
        Assert.Contains("<h2 class=\"dlg-card-title\">Tips &amp; caveats</h2>", page, StringComparison.Ordinal);

        // No video was ever made, and the guide says so rather than leaving a gap where one goes.
        Assert.Contains("no video for it yet", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<HostedVideo", page, StringComparison.Ordinal);

        // Server settings differ; the page says that without inventing a server or a number.
        Assert.Contains("server settings", page, StringComparison.OrdinalIgnoreCase);

        var footer = Regex.Match(page, @"<p class=""dlg-footer"">.*?</p>", RegexOptions.Singleline);
        Assert.True(footer.Success, "the page must end with the feedback line");
        Assert.Contains("Something changed?", footer.Value, StringComparison.Ordinal);
        Assert.Contains("href=\"/feedback\"", footer.Value, StringComparison.Ordinal);
    }

    /// <summary>A page stylesheet nobody linked is a page with no layout.</summary>
    [Fact]
    public void ThePageStylesheetIsLoaded()
    {
        var index = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));

        Assert.Contains("css/pages/dino-level-guide-styles.css", index, StringComparison.Ordinal);

        var css = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages", "dino-level-guide-styles.css"));

        // Page stylesheets tint from the theme's tokens; a literal hex here would be a new colour.
        Assert.DoesNotContain("#", css, StringComparison.Ordinal);
    }

    // ---- Paths --------------------------------------------------------------

    private static string Page() => Component("Pages", "DinoLevelGuide.razor");

    private static string Catalog() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "RazorReaper", "Navigation", "NavCatalog.cs"));

    private static string Component(string folder, string file) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file)));

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
