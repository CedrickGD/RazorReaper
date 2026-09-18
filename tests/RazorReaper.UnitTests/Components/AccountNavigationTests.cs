using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace RazorReaper.UnitTests.Components;

/// <summary>
/// "Manage license" on My account pointed at /home, which lost the licence widget in da6e216 —
/// the click landed on a page with nothing to manage. It opens the overlay now, and the router
/// has a Not-found branch so the next stale route is visible instead of a blank window.
///
/// "Manage" was wrong in a second way: there is no licence portal, only buying and redeeming.
/// The row says which of the two a click is, and the free tier gets the shop next to it.
/// </summary>
public sealed class AccountNavigationTests
{
    /// <summary>An activated licence has one thing to offer: look at it.</summary>
    [Fact]
    public void AnActivatedLicenseOffersViewLicenseAndOpensTheOverlay()
    {
        var account = File.ReadAllText(ComponentPath("Pages", "Account.razor"));

        Assert.DoesNotContain("href=\"/home\"", account, StringComparison.Ordinal);
        Assert.Contains("@inject ILicenseOverlayService LicenseOverlay", account, StringComparison.Ordinal);
        Assert.Contains("private void OpenLicenseOverlay() => LicenseOverlay.Open();", account, StringComparison.Ordinal);

        // The promise nothing in the app keeps.
        Assert.DoesNotContain("Manage license", account, StringComparison.Ordinal);

        var trigger = Regex.Match(account, @"<button[^>]*>View license</button>", RegexOptions.Singleline);
        Assert.True(trigger.Success, "\"View license\" must be a <button>, not a link.");
        Assert.Contains("type=\"button\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("class=\"rr-pill-btn ghost sm\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"dialog\"", trigger.Value, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OpenLicenseOverlay\"", trigger.Value, StringComparison.Ordinal);

        // Both branches hang off the one licence flag, so the row can never offer both.
        Assert.Contains("@if (License.IsActivated)", account, StringComparison.Ordinal);
    }

    /// <summary>
    /// The free tier has two: redeem a key you already own — the overlay's activation form is
    /// that UI — or go and buy one.
    /// </summary>
    [Fact]
    public void TheFreeTierOffersRedeemKeyAndABuyPremiumLink()
    {
        var account = File.ReadAllText(ComponentPath("Pages", "Account.razor"));

        var redeem = Regex.Match(account, @"<button[^>]*>Redeem key</button>", RegexOptions.Singleline);
        Assert.True(redeem.Success, "\"Redeem key\" must be a <button>, not a link.");
        Assert.Contains("type=\"button\"", redeem.Value, StringComparison.Ordinal);
        Assert.Contains("class=\"rr-pill-btn ghost sm\"", redeem.Value, StringComparison.Ordinal);
        Assert.Contains("aria-haspopup=\"dialog\"", redeem.Value, StringComparison.Ordinal);
        Assert.Contains("@onclick=\"OpenLicenseOverlay\"", redeem.Value, StringComparison.Ordinal);

        var buy = Regex.Match(account, @"<a[^>]*>Buy Premium</a>", RegexOptions.Singleline);
        Assert.True(buy.Success, "\"Buy Premium\" must be a link to the shop.");
        Assert.Contains("class=\"rr-pill-btn ghost sm\"", buy.Value, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", buy.Value, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener\"", buy.Value, StringComparison.Ordinal);

        // The shop's address is the license overlay's, not a second copy of it.
        Assert.Contains("href=\"@StoreLinks.Store\"", buy.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("sellhub", account, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One address, two call sites. A second literal is the one that goes stale.</summary>
    [Fact]
    public void TheShopAddressIsWrittenDownOnce()
    {
        var links = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "RazorReaper", "Services", "StoreLinks.cs"));
        Assert.Contains("public const string Store = \"https://rr.sellhub.cx\";", links, StringComparison.Ordinal);

        var overlay = File.ReadAllText(ComponentPath("Shared", "LicenseOverlay.razor"));
        Assert.Contains("private const string StoreUrl = StoreLinks.Store;", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("\"https://rr.sellhub.cx\"", overlay, StringComparison.Ordinal);
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
        // Translated since c6184d9: the wording itself lives in Resources/i18n, and
        // TranslatedSurfaceTests holds the English to what it was.
        Assert.Contains("<h1 class=\"page-title\">@Localizer.T(\"notfound.title\")</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<a class=\"rr-pill-btn ghost sm\" href=\"/home\">@Localizer.T(\"notfound.back\")</a>", page, StringComparison.Ordinal);
    }

    private static string ComponentPath(string folder, string file)
        => Path.GetFullPath(Path.Combine(RepositoryRoot(), "RazorReaper", "Components", folder, file));

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));
}
