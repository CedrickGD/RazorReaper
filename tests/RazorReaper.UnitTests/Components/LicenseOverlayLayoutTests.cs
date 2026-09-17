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
        Assert.Contains("aria-label=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("e.Key == \"Escape\"", overlay, StringComparison.Ordinal);
        Assert.Contains("class=\"license-key-input\"", overlay, StringComparison.Ordinal);
        Assert.Contains("LicenseService.ActivateLicenseAsync(", overlay, StringComparison.Ordinal);
        Assert.Contains("https://rr.sellhub.cx", overlay, StringComparison.Ordinal);
        Assert.Contains("Bound to this PC", overlay, StringComparison.Ordinal);
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

    /// <summary>Both columns carry content to the bottom: the key tile, the facts, the benefits grid and the help row.</summary>
    [Fact]
    public void TheOverlayFillsBothColumns()
    {
        var overlay = File.ReadAllText(ComponentPath("Shared", "LicenseOverlay.razor"));

        Assert.Contains("class=\"license-hero-help\"", overlay, StringComparison.Ordinal);
        Assert.Contains("Need help?", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"/feedback?section=support\" @onclick=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"/inbox\" @onclick=\"Close\"", overlay, StringComparison.Ordinal);
        Assert.Contains("class=\"license-benefit-grid\"", overlay, StringComparison.Ordinal);
        Assert.Contains("Included with Premium", overlay, StringComparison.Ordinal);
        Assert.Contains("Premium unlocks", overlay, StringComparison.Ordinal);
        Assert.Contains("<dt>Status</dt>", overlay, StringComparison.Ordinal);

        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "license-overlay.css"));
        Assert.Contains(".license-benefit-grid {", css, StringComparison.Ordinal);
        Assert.Contains(".license-hero-help {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("minmax(280px, 1.05fr) minmax(0, 1.6fr)", css, StringComparison.Ordinal);
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
