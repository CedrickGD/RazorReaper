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

    [Fact]
    public void SupportPromptAndLicenseStayAheadOfDashboardContent()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Home.razor"));

        var support = source.IndexOf("class=\"content-card home-support-prompt\"", StringComparison.Ordinal);
        var license = source.IndexOf("class=\"license-key-input\"", StringComparison.Ordinal);
        var announcements = source.IndexOf("<AnnouncementBanner />", StringComparison.Ordinal);
        var widgets = source.IndexOf("class=\"dashboard-widgets\"", StringComparison.Ordinal);

        Assert.True(support > 0);
        Assert.True(license > 0);
        Assert.True(support < license);
        Assert.True(license < announcements);
        Assert.True(license < widgets);
        Assert.Contains("Bound to this PC", source, StringComparison.Ordinal);
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

        Assert.Equal(new[] { "Credits.razor", "Home.razor", "Troubleshoot.razor" }, pagesWithPrompt);

        var feedback = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Feedback.razor"));
        Assert.DoesNotContain("SubmitDiagnosticsOnly", feedback, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(_message)", feedback, StringComparison.Ordinal);
        var prompt = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Shared", "SendDiagnosticsButton.razor"));
        Assert.Contains("/feedback?source=", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("IFeedbackService", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpNavigationLeadsWithReportAProblem()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Navigation", "NavCatalog.cs"));
        var help = source.IndexOf("new NavGroup(\"Help & About\"", StringComparison.Ordinal);
        var report = source.IndexOf("new NavPage(\"Report a Problem\"", help, StringComparison.Ordinal);
        var troubleshoot = source.IndexOf("new NavPage(\"Troubleshoot\"", help, StringComparison.Ordinal);

        Assert.True(help > 0);
        Assert.True(report > help);
        Assert.True(report < troubleshoot);
    }

    [Fact]
    public void HomeSupportCopyStylesDoNotOverrideNestedControlColors()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "RazorReaper",
            "wwwroot",
            "css",
            "pages",
            "home-styles.css"));

        Assert.Contains(".home-support-prompt > div:first-child > span {", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".home-support-prompt span {", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
    }
}
