using System.Runtime.CompilerServices;

namespace RazorReaper.UnitTests;

public sealed class HomePageLayoutTests
{
    [Fact]
    public void HomePageDoesNotRenderTheUpdateWidget()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Home.razor"));

        Assert.DoesNotContain("content-card update-card", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Updates install themselves and restart the app.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject IAutoUpdateManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnAutoUpdateStateChanged", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The license widget and the "Something not working?" prompt both left Home: the license
    /// lives in the full-window overlay the sidebar opens, and problem reports go through the
    /// Support section of Feedback &amp; Support. Nothing on Home may grow them back.
    /// </summary>
    [Fact]
    public void HomePageNoLongerCarriesTheLicenseWidgetOrTheSupportPrompt()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Home.razor"));

        Assert.DoesNotContain("home-support-prompt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<SendDiagnosticsButton", source, StringComparison.Ordinal);
        Assert.DoesNotContain("license-widget", source, StringComparison.Ordinal);
        Assert.DoesNotContain("home-license-top", source, StringComparison.Ordinal);
        Assert.DoesNotContain("license-key-input", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateLicenseAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@inject ILicenseService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnLicenseStateChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Bound to this PC", source, StringComparison.Ordinal);

        var announcements = source.IndexOf("<AnnouncementBanner />", StringComparison.Ordinal);
        var widgets = source.IndexOf("class=\"dashboard-widgets\"", StringComparison.Ordinal);
        Assert.True(announcements > 0);
        Assert.True(announcements < widgets);
    }

    [Fact]
    public void DiagnosticsPromptIsLimitedToApprovedSupportSurfaces()
    {
        var pages = Directory.GetFiles(
            Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages"),
            "*.razor",
            SearchOption.AllDirectories);
        var pagesWithPrompt = pages
            .Where(path => File.ReadAllText(path).Contains("<SendDiagnosticsButton", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Credits.razor", "Troubleshoot.razor" }, pagesWithPrompt);

        var feedback = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Feedback.razor"));
        Assert.DoesNotContain("SubmitDiagnosticsOnly", feedback, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(_message)", feedback, StringComparison.Ordinal);

        // The prompt is a link into the Support section, not a form of its own.
        var prompt = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Shared", "SendDiagnosticsButton.razor"));
        Assert.Contains("/feedback?source=", prompt, StringComparison.Ordinal);
        Assert.Contains("section=support", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("IFeedbackService", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpNavigationLeadsWithFeedbackAndSupport()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Navigation", "NavCatalog.cs"));
        var help = source.IndexOf("new NavGroup(\"Help & About\"", StringComparison.Ordinal);
        var feedback = source.IndexOf("new NavPage(\"Feedback & Support\", \"/feedback\"", help, StringComparison.Ordinal);
        var troubleshoot = source.IndexOf("new NavPage(\"Troubleshoot\"", help, StringComparison.Ordinal);

        Assert.True(help > 0);
        Assert.True(feedback > help);
        Assert.True(feedback < troubleshoot);
        Assert.DoesNotContain("\"Report a Problem\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The widget's and the prompt's rules went with them. The overlay brings its own sheet
    /// (css/shared/license-overlay.css), so any .license-* rule back in here is a leftover.
    /// </summary>
    [Fact]
    public void HomeStylesNoLongerCarryTheLicenseWidgetOrTheSupportPrompt()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "RazorReaper",
            "wwwroot",
            "css",
            "pages",
            "home-styles.css"));

        Assert.DoesNotContain(".home-support-prompt", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".license-widget", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".home-license-top", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".license-key-", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".license-activation-area", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".license-fact", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".license-expiry-warning", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".premium-badge", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }
}
