namespace RazorReaper.Services;

/// <summary>
/// Who may open a Lifetime-only page.
///
/// Premium is not one thing: the shop sells a perpetual key next to four subscription lengths,
/// and <see cref="ILicenseService.IsPremium"/> is true for all of them. A page the owner sells
/// as part of the perpetual key therefore cannot ask "is this premium?" — it has to ask this.
///
/// The rule reads off the two facts the store sends with a key:
/// <list type="bullet">
///   <item>the plan name — "lifetime" is the perpetual one, "1-month" … "12-months" and "trial"
///         are not;</item>
///   <item>the expiry — a perpetual key carries none.</item>
/// </list>
/// The expiry is the fallback, not a second rule: keys activated before the plan name was cached
/// come back with no plan at all (see LicenseService's cached-credentials path), and for those the
/// absent expiry is the only evidence there is. A key that *does* name a subscription plan is taken
/// at its word even if its expiry went missing — a plan name is never guessed, so trusting it over
/// a hole in the data cannot hand the guide to a monthly subscriber.
/// </summary>
public static class LifetimeAccess
{
    /// <summary>The store's name for the perpetual plan, as it arrives — lowercase.</summary>
    public const string LifetimePlan = "lifetime";

    /// <summary>True when this licence is the perpetual one.</summary>
    public static bool IsLifetime(ILicenseService? license)
        => license is not null
           && IsLifetime(license.IsPremium, license.LicenseType, license.ExpiresAt);

    /// <summary>
    /// The rule itself, over the three facts it needs, so it can be read and tested without a
    /// licence service behind it.
    /// </summary>
    public static bool IsLifetime(bool isPremium, string? licenseType, string? expiresAt)
    {
        // Every gate starts here: an expired or never-activated key is not premium at all.
        if (!isPremium) return false;

        var plan = licenseType?.Trim();

        // No plan name cached — fall back to the expiry. Perpetual keys have none.
        if (string.IsNullOrEmpty(plan)) return string.IsNullOrWhiteSpace(expiresAt);

        return plan.Equals(LifetimePlan, StringComparison.OrdinalIgnoreCase);
    }
}
