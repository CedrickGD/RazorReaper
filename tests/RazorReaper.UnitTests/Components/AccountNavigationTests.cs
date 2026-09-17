using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// "Manage license" on My account pointed at /home, which lost the licence widget in da6e216 —
/// the click landed on a page with nothing to manage. It opens the overlay now, and the router
/// has a Not-found branch so the next stale route is visible instead of a blank window.
/// </summary>
public sealed class AccountNavigationTests
{
    [Fact]
    public void ManageLicenseOpensTheOverlayInsteadOfLinkingToHome()
    {
        var account = File.ReadAllText(ComponentPath("Pages", "Account.razor"));

        Assert.DoesNotContain("href=\"/home\"", account, StringComparison.Ordinal);
        Assert.Contains("@inject ILicenseOverlayService LicenseOverlay", account, StringComparison.Ordinal);
        Assert.Contains("private void OpenLicenseOverlay() => LicenseOverlay.Open();", account, StringComparison.Ordinal);

        var trigger = Regex.Match(account, @"<button[^>]*>Manage license</button>", RegexOptions.Singleline);
        Assert.True(trigger.Success, "\"Manage license\" must be a <button>, not a link.");
        Assert.Contains("type=\"button\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("class=\"rr-pill-btn ghost sm\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"dialog\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OpenLicenseOverlay\"", trigger.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRouterRendersARealNotFoundPageInTheAppLayout()
    {
        var routes = File.ReadAllText(ComponentPath(".", "Routes.razor"));

        var branch = Regex.Match(routes, @"<NotFound>.*?</NotFound>", RegexOptions.Singleline);
        Assert.True(branch.Success, "Routes.razor must have a <NotFound> branch.");
        Assert.Contains("<LayoutView Layout=\"typeof(Layout.MainLayout)\">", branch.Value, StringComparison.Ordinal);
        Assert.Contains("<RazorReaper.Components.Pages.NotFound />", branch.Value, StringComparison.Ordinal);

        // The component it points at exists, is not routable, and offers the way back.
        var page = File.ReadAllText(ComponentPath("Pages", "NotFound.razor"));
        Assert.False(Regex.IsMatch(page, @"^@page\b", RegexOptions.Multiline), "NotFound.razor must not be routable.");
        Assert.Contains("<h1 class=\"page-title\">Page not found</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<a class=\"rr-pill-btn ghost sm\" href=\"/home\">Back to Home</a>", page, StringComparison.Ordinal);
    }

    private static string ComponentPath(string folder, string file)
        => Path.GetFullPath(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file));

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
