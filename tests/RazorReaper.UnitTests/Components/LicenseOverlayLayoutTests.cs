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

    [Fact]
    public void TheOverlayHasNoEntranceOrExitMotion()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "shared", "license-overlay.css"));

        Assert.DoesNotContain("@keyframes", css, StringComparison.Ordinal);
        Assert.Empty(MotionProperties(css));
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
