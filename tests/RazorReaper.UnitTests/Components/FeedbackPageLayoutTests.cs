using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// /feedback is "Feedback &amp; Support": two in-page sections on one form. Feedback sends
/// without a snapshot, Support sends with one, and the Support side is reachable by URL so
/// the report buttons elsewhere can land on it.
/// </summary>
public sealed class FeedbackPageLayoutTests
{
    [Fact]
    public void ThePageHasAFeedbackAndASupportSection()
    {
        var page = File.ReadAllText(PagePath());

        Assert.Contains("Feedback & Support", page, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\"", page, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(page, @"<button\s+type=""button""\s+role=""tab""").Count);
        Assert.Contains("id=\"feedback-tab-feedback\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"feedback-tab-support\"", page, StringComparison.Ordinal);
        Assert.Contains("Open support inbox", page, StringComparison.Ordinal);
    }

    [Fact]
    public void FeedbackSendsPlainAndSupportSendsWithTheSnapshot()
    {
        var page = File.ReadAllText(PagePath());

        Assert.Contains("FeedbackService.SubmitAsync(_message, _contact)", page, StringComparison.Ordinal);
        Assert.Contains("FeedbackService.SubmitDiagnosticsAsync(_message, _contact,", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SubmitWithDiagnosticsAsync", page, StringComparison.Ordinal);
        Assert.Contains("A system and feature snapshot is attached when you send your report.", page, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(_message)", page, StringComparison.Ordinal);
        Assert.Contains("Report ID: @_reportId", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSupportSectionIsReachableByUrl()
    {
        var page = File.ReadAllText(PagePath());

        Assert.Contains("[SupplyParameterFromQuery(Name = \"section\")]", page, StringComparison.Ordinal);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"source\")]", page, StringComparison.Ordinal);
        Assert.Contains("SupportSectionKey = \"support\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSidebarListsFeedbackAndSupport()
    {
        var catalog = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "Navigation", "NavCatalog.cs"));

        Assert.Contains("new NavPage(\"Feedback & Support\", \"/feedback\"", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionTabsDoNotAnimate()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryRoot(), "RazorReaper", "wwwroot", "css", "pages", "feedback-styles.css"));
        var start = css.IndexOf(".feedback-tabs {", StringComparison.Ordinal);
        var end = css.IndexOf(".feedback-attach-note {", start, StringComparison.Ordinal);

        Assert.True(start > 0);
        Assert.True(end > start);
        var block = css[start..end];
        var motion = Regex.Matches(block, @"(?<![\w-])(transform|animation|transition)(?:-[\w-]+)?\s*:", RegexOptions.IgnoreCase)
            .Select(match => match.Value.Trim())
            .ToArray();
        Assert.Empty(motion);
    }

    private static string PagePath()
        => Path.Combine(RepositoryRoot(), "RazorReaper", "Components", "Pages", "Feedback.razor");

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
    }
}
