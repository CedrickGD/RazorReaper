using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// The license moved out of Home into a full-window overlay opened from the sidebar's tier
/// line. These pin the shape that move promised: a real button as the trigger, the overlay
/// mounted where it covers the whole app, activation living only there, and no motion.
/// </summary>
public sealed class LicenseOverlayLayoutTests
{
    [Fact]
    public void TheSidebarTierLineIsAButtonThatOpensTheOverlay()
    {
        var navbar = File.ReadAllText(ComponentPath("Shared", "SharedNavbar.razor"));

        Assert.Contains("@inject ILicenseOverlayService LicenseOverlay", navbar, StringComparison.Ordinal);
        Assert.Contains("LicenseOverlay.Open()", navbar, StringComparison.Ordinal);
        Assert.Contains("LicenseOverlay.OnStateChanged +=", navbar, StringComparison.Ordinal);
        Assert.Contains("LicenseOverlay.OnStateChanged -=", navbar, StringComparison.Ordinal);

        // The status line is a <button type="button" ... class="nav-status-line" ...>, not a div.
        var trigger = Regex.Match(navbar, @"<button\s+type=""button""\s+class=""nav-status-line""[^>]*>", RegexOptions.Singleline);
        Assert.True(trigger.Success, "The sidebar tier line must be a <button type=\"button\" class=\"nav-status-line\">.");
        Assert.Contains("@onclick=\"OpenLicenseOverlay\"", trigger.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"nav-status-line\"", navbar, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTriggerDoesNotMove()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "navbar.css"));
        var start = css.IndexOf(".nav-status-line {", StringComparison.Ordinal);
        var end = css.IndexOf(".nav-status-dot {", start, StringComparison.Ordinal);

        Assert.True(start > 0);
        Assert.True(end > start);
        var block = css[start..end];
        Assert.Empty(MotionProperties(block));
        Assert.DoesNotContain("@keyframes", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayIsMountedFullWindowAndRegistered()
    {
        var layout = File.ReadAllText(ComponentPath("Layout", "MainLayout.razor"));
        Assert.Contains("<LicenseOverlay />", layout, StringComparison.Ordinal);

        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "MauiProgram.cs"));
        Assert.Contains("services.AddSingleton<ILicenseOverlayService, LicenseOverlayService>();", program, StringComparison.Ordinal);

        var index = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "index.html"));
        Assert.Contains("css/shared/license-overlay.css", index, StringComparison.Ordinal);
        Assert.Contains("js/license-overlay.js", index, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayOwnsActivationAndTheLicenseFacts()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "LicenseOverlay.razor"));

        Assert.Contains("role=\"dialog\"", overlay, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@Localizer.T(\"common.close\")\"", overlay, StringComparison.Ordinal);
        Assert.Contains("e.Key == \"Escape\"", overlay, StringComparison.Ordinal);
        Assert.Contains("class=\"license-key-input\"", overlay, StringComparison.Ordinal);
        Assert.Contains("LicenseService.ActivateLicenseAsync(", overlay, StringComparison.Ordinal);

        // The shop link, through the one constant My account also uses.
        Assert.Contains("StoreLinks.Store", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"@StoreUrl\"", overlay, StringComparison.Ordinal);

        // "Manage or renew" promised a portal that does not exist — the shop sells and renews.
        // The two labels are dictionary entries now; TranslatedSurfaceTests pins their English.
        Assert.Contains(
            "@(premium ? Localizer.T(\"license.buy.renew\") : Localizer.T(\"license.buy.premium\"))",
            overlay,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Manage or renew", overlay, StringComparison.Ordinal);
        Assert.Contains("@Localizer.T(\"license.fact.device.bound\")", overlay, StringComparison.Ordinal);
        Assert.Contains("is-expired", overlay, StringComparison.Ordinal);
        Assert.Contains("is-soon", overlay, StringComparison.Ordinal);
        Assert.Contains("Overlay.Close()", overlay, StringComparison.Ordinal);

        // Subscriptions come off again, through the gate.
        Assert.Contains("this.StopRenderDispatch()", overlay, StringComparison.Ordinal);
        Assert.Contains("Overlay.OnStateChanged -=", overlay, StringComparison.Ordinal);
        Assert.Contains("LicenseService.OnLicenseStateChanged -=", overlay, StringComparison.Ordinal);

        // Nothing else in the app may activate a key.
        var others = Directory.GetFiles(Path.Combine(RepositoryRoot(), "RazorReaper", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "LicenseOverlay.razor")
            .Where(path => File.ReadAllText(path).Contains("ActivateLicenseAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(others);
    }

    /// <summary>
    /// Both full-window overlays are no-motion surfaces: the license view and the "What's new
    /// &amp; inbox" view that shares its frame family. The one moving part on that path is the
    /// dot on the sidebar icon, and that lives in navbar.css.
    /// </summary>
    [Theory]
    [InlineData("license-overlay.css")]
    [InlineData("whats-new-overlay.css")]
    public void TheOverlayHasNoEntranceOrExitMotion(string sheet)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", sheet));

        Assert.DoesNotContain("@keyframes", css, StringComparison.Ordinal);
        Assert.Empty(MotionProperties(css));
    }

    /// <summary>
    /// .btn and .rr-pill-btn carry an app-wide transition and a press scale, which the overlays
    /// inherit. The sheets above may not spell "transform" at all, so the override lives next
    /// to the rules that add the motion, and it must cover both overlays.
    /// </summary>
    [Theory]
    [InlineData("theme.css", ".btn")]
    [InlineData("primitives.css", ".rr-pill-btn")]
    public void ButtonsInsideTheOverlaysDoNotPress(string sheet, string button)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", sheet));

        foreach (var overlay in new[] { ".license-overlay", ".whats-new-overlay" })
        {
            var rest = css.IndexOf($"{overlay} {button}", StringComparison.Ordinal);
            Assert.True(rest >= 0, $"{sheet} must override {button} inside {overlay}.");

            var active = css.IndexOf($"{overlay} {button}:active:not(:disabled)", StringComparison.Ordinal);
            Assert.True(active >= 0, $"{sheet} must override {button}:active inside {overlay}.");
            var block = css[active..css.IndexOf('}', active)];
            Assert.Contains("transform: none;", block, StringComparison.Ordinal);
            // A colour change stands in for the press.
            Assert.Contains("background:", block, StringComparison.Ordinal);
        }

        var transition = css.IndexOf($".license-overlay {button},", StringComparison.Ordinal);
        Assert.True(transition >= 0);
        Assert.Contains("transition: none;", css[transition..css.IndexOf('}', transition)], StringComparison.Ordinal);
    }

    [Fact]
    public void TheOverlayKeepsTabInside()
    {
        var js = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "js", "license-overlay.js"));

        Assert.Contains("event.key !== 'Tab'", js, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener('keydown', trapTab, true);", js, StringComparison.Ordinal);
        Assert.Contains("document.removeEventListener('keydown', trapTab, true);", js, StringComparison.Ordinal);
        Assert.Contains("if (event.shiftKey)", js, StringComparison.Ordinal);
        Assert.Contains("window.razorReaperLicenseOverlay = createOverlayRuntime('license-overlay-open')", js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Closing an overlay — X or Escape, both of which are just Overlay.Close() — puts focus
    /// back on the sidebar button that opened it, so the keyboard carries on from where it
    /// left off instead of at the top of the page underneath. The runtime remembers the
    /// element that had focus when it opened and focuses it again on close; each overlay has
    /// its own runtime, so one never restores the other's opener.
    /// </summary>
    [Theory]
    [InlineData("LicenseOverlay.razor", "razorReaperLicenseOverlay")]
    [InlineData("WhatsNewOverlay.razor", "razorReaperWhatsNewOverlay")]
    public void ClosingTheOverlayHandsFocusBackToItsOpener(string component, string runtime)
    {
        var js = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "js", "license-overlay.js"));

        Assert.Contains("restoreFocusTo = active instanceof HTMLElement ? active : null;", js, StringComparison.Ordinal);
        Assert.Contains("const target = restoreFocusTo;", js, StringComparison.Ordinal);
        Assert.Contains("target.focus({ preventScroll: true });", js, StringComparison.Ordinal);

        // Open and close both run from OnAfterRenderAsync, after the render that added or
        // removed the markup, and the X and Escape share the one Close.
        var overlay = File.ReadAllText(ComponentPath("Shared", component));
        Assert.Contains($"await JS.InvokeVoidAsync(\"{runtime}.open\", _rootRef);", overlay, StringComparison.Ordinal);
        Assert.Contains($"await JS.InvokeVoidAsync(\"{runtime}.close\");", overlay, StringComparison.Ordinal);
        Assert.Contains("_syncJsPending = false;", overlay, StringComparison.Ordinal);
        Assert.Contains("private void Close() => Overlay.Close();", overlay, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("e.Key == \"Escape\"", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing may focus a heading on the app's first paint. &lt;FocusOnNavigate&gt; counts the
    /// very first render as a navigation and queues one focusBySelector call for it; that call
    /// lands whenever the interop channel gets to it, stamps tabindex="-1" on the match and
    /// focuses it. An overlay opened and closed in that window handed focus back to the
    /// sidebar button correctly and then lost it again to the page &lt;h1&gt;. The router goes
    /// through FocusOnPageChange instead, which holds the framework component back until the
    /// route has actually moved between two different page types.
    /// </summary>
    [Fact]
    public void TheRouterDoesNotFocusTheHeadingOnTheFirstPaint()
    {
        var components = Path.Combine(RepositoryRoot(), "RazorReaper", "Components");
        var routes = File.ReadAllText(Path.Combine(components, "Routes.razor"));

        Assert.Contains("<FocusOnPageChange RouteData=\"@routeData\" Selector=\"h1\" />", routes, StringComparison.Ordinal);

        var gate = File.ReadAllText(Path.Combine(components, "FocusOnPageChange.razor"));
        Assert.Contains("<FocusOnNavigate RouteData=\"@RouteData\" Selector=\"@Selector\" />", gate, StringComparison.Ordinal);
        Assert.Contains("@if (_armed)", gate, StringComparison.Ordinal);
        // The first page type is remembered, not acted on; a different one arms the component.
        Assert.Contains("_firstPageType = RouteData.PageType;", gate, StringComparison.Ordinal);
        Assert.Contains("if (RouteData.PageType != _firstPageType)", gate, StringComparison.Ordinal);
        Assert.Contains("_armed = true;", gate, StringComparison.Ordinal);

        // The gate is the only place the framework component may be mounted.
        var offenders = Directory.GetFiles(components, "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != "FocusOnPageChange.razor")
            .Where(path => File.ReadAllText(path).Contains("<FocusOnNavigate", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// The Freemium tier is red in the sidebar (#d8524f dot) and was purple in the overlay. The
    /// overlay now takes the theme's red tokens for that state; Premium stays green in both.
    /// </summary>
    [Fact]
    public void TheFreemiumAccentIsTheSidebarsRed()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "license-overlay.css"));

        var freemium = css.IndexOf(".license-overlay.is-freemium {", StringComparison.Ordinal);
        Assert.True(freemium > 0);
        var block = css[freemium..css.IndexOf('}', freemium)];
        Assert.Contains("--lic-accent: var(--accent-red);", block, StringComparison.Ordinal);
        Assert.Contains("--lic-accent-rgb: var(--accent-red-rgb);", block, StringComparison.Ordinal);
        Assert.DoesNotContain("--accent-purple", block, StringComparison.Ordinal);

        var root = css.IndexOf(".license-overlay {", StringComparison.Ordinal);
        Assert.Contains("--lic-accent: var(--accent-green);", css[root..css.IndexOf('}', root)], StringComparison.Ordinal);

        var navbar = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "navbar.css"));
        Assert.Contains(".nav-status-dot.freemium {", navbar, StringComparison.Ordinal);
    }

    /// <summary>
    /// theme.css sets every h2 to 1.75rem with a gradient text fill and a 1.5rem margin, all
    /// !important. The two overlay h2s are an eyebrow and a card-size title, so they pin
    /// their own size, margin and fill the way the page sheets do — and the unread badge
    /// inside one keeps its own fill so a gradient-clipped parent cannot blank it.
    /// </summary>
    [Theory]
    [InlineData("license-overlay.css", ".license-benefits-title {", "0.7rem", "var(--text-secondary)")]
    [InlineData("whats-new-overlay.css", ".whats-new-inbox-title {", "1.05rem", "var(--text-primary)")]
    public void TheOverlayHeadingsEscapeTheGlobalH2Rule(string sheet, string selector, string size, string fill)
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", sheet));

        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{sheet} must style {selector.TrimEnd(' ', '{')}.");
        var block = css[start..css.IndexOf('}', start)];

        Assert.Contains($"font-size: {size} !important;", block, StringComparison.Ordinal);
        Assert.Contains("margin: 0 !important;", block, StringComparison.Ordinal);
        Assert.Contains("background: none !important;", block, StringComparison.Ordinal);
        Assert.Contains($"-webkit-text-fill-color: {fill} !important;", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnreadBadgeKeepsItsOwnFillInsideTheHeading()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "whats-new-overlay.css"));

        var start = css.IndexOf(".whats-new-unread {", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Contains("-webkit-text-fill-color: var(--accent-purple-light);", css[start..css.IndexOf('}', start)], StringComparison.Ordinal);
    }

    /// <summary>
    /// Both columns carry content to the same foot line: the hero has the tier story and the
    /// benefits list, the panel the key (or activation), six facts and the help row, and the
    /// frame hugs the taller column instead of stretching to the window around a void.
    /// </summary>
    [Fact]
    public void TheOverlayFillsBothColumns()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "LicenseOverlay.razor"));

        var heroEnd = overlay.IndexOf("</aside>", StringComparison.Ordinal);
        var panelStart = overlay.IndexOf("<section class=\"license-panel\"", StringComparison.Ordinal);
        Assert.True(heroEnd > 0 && panelStart > heroEnd);

        // The benefits list lives in the hero, under the perks.
        var benefits = overlay.IndexOf("class=\"license-benefit-list\"", StringComparison.Ordinal);
        Assert.True(benefits > 0 && benefits < heroEnd, "The benefits list belongs to the hero column.");
        Assert.True(overlay.IndexOf("class=\"license-perks\"", StringComparison.Ordinal) < benefits);
        Assert.Contains("license.benefits.title.premium", overlay, StringComparison.Ordinal);
        Assert.Contains("license.benefits.title.free", overlay, StringComparison.Ordinal);

        // "Need help?" closes the panel column.
        var help = overlay.IndexOf("class=\"license-help\"", StringComparison.Ordinal);
        Assert.True(help > panelStart, "The help row belongs to the panel column.");
        Assert.Contains("license.help.title", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"/feedback?section=support\" @onclick=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"/inbox\" @onclick=\"Close\"", overlay, StringComparison.Ordinal);

        // Six facts on both tiers, Status among them.
        Assert.Contains("<dt>@Localizer.T(\"license.fact.status\")</dt>", overlay, StringComparison.Ordinal);
        Assert.Equal(12, Regex.Matches(overlay, "<dt>").Count);

        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "license-overlay.css"));
        Assert.Contains(".license-benefit-list {", css, StringComparison.Ordinal);
        Assert.Contains(".license-help {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("license-hero-help", css, StringComparison.Ordinal);
        Assert.DoesNotContain("minmax(280px, 1.05fr) minmax(0, 1.6fr)", css, StringComparison.Ordinal);

        var root = css.IndexOf(".license-overlay {", StringComparison.Ordinal);
        Assert.Contains("align-items: center;", css[root..css.IndexOf('}', root)], StringComparison.Ordinal);
        var frame = css.IndexOf(".license-overlay-frame {", StringComparison.Ordinal);
        var frameBlock = css[frame..css.IndexOf('}', frame)];
        Assert.Contains("max-height: 100%;", frameBlock, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: minmax(0, 1fr);", frameBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The motion properties declared in a block of CSS: transform, animation and transition,
    /// including their longhands. Matched at property position so <c>text-transform</c> and
    /// prose in comments do not count.
    /// </summary>
    private static string[] MotionProperties(string css)
        => Regex.Matches(css, @"(?<![\w-])(transform|animation|transition)(?:-[\w-]+)?\s*:", RegexOptions.IgnoreCase)
            .Select(match => match.Value.Trim())
            .ToArray();

    private static string ComponentPath(string folder, string file)
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
