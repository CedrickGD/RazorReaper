using RazorReaper.Services;

namespace RazorReaper.UnitTests.Licensing;

/// <summary>
/// The paywall on /guides/dino-level. "Premium" is five products — one perpetual key and four
/// subscription lengths — and <see cref="ILicenseService.IsPremium"/> is true for every one of
/// them, so a gate written against IsPremium would hand a Lifetime-only page to a monthly
/// subscriber. These are the cases that separate the two.
/// </summary>
public sealed class LifetimeAccessTests
{
    [Fact]
    public void TheLifetimePlanIsUnlocked()
        => Assert.True(LifetimeAccess.IsLifetime(isPremium: true, licenseType: "lifetime", expiresAt: null));

    /// <summary>The store sends plan names lowercase; nothing should depend on that holding.</summary>
    [Theory]
    [InlineData("Lifetime")]
    [InlineData("LIFETIME")]
    [InlineData("  lifetime  ")]
    public void ThePlanNameIsMatchedCaseAndWhitespaceInsensitively(string plan)
        => Assert.True(LifetimeAccess.IsLifetime(isPremium: true, licenseType: plan, expiresAt: null));

    /// <summary>The case the whole class exists for: premium, paying, and still locked out.</summary>
    [Theory]
    [InlineData("1-month")]
    [InlineData("3-months")]
    [InlineData("6-months")]
    [InlineData("12-months")]
    [InlineData("trial")]
    public void ASubscriptionIsLockedEvenThoughItIsPremium(string plan)
        => Assert.False(LifetimeAccess.IsLifetime(
            isPremium: true, licenseType: plan, expiresAt: "2027-01-01T00:00:00Z"));

    [Fact]
    public void TheFreeTierIsLocked()
        => Assert.False(LifetimeAccess.IsLifetime(isPremium: false, licenseType: null, expiresAt: null));

    /// <summary>
    /// A lapsed perpetual key cannot exist, but a lapsed *anything* must not walk through on the
    /// plan name alone — IsPremium is the first gate, not a hint.
    /// </summary>
    [Fact]
    public void AnExpiredLicenseIsLockedEvenWhenItStillNamesTheLifetimePlan()
        => Assert.False(LifetimeAccess.IsLifetime(
            isPremium: false, licenseType: "lifetime", expiresAt: "2020-01-01T00:00:00Z"));

    /// <summary>
    /// Keys activated before the plan name was cached come back with no plan at all. For those the
    /// absent expiry is the only evidence there is, and a perpetual key is the one thing that
    /// never carries one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PremiumWithNoPlanAndNoExpiryIsTreatedAsLifetime(string? plan)
        => Assert.True(LifetimeAccess.IsLifetime(isPremium: true, licenseType: plan, expiresAt: null));

    /// <summary>…and the same key with a date on it is a subscription whose name was lost.</summary>
    [Fact]
    public void PremiumWithNoPlanButAnExpiryIsLocked()
        => Assert.False(LifetimeAccess.IsLifetime(
            isPremium: true, licenseType: null, expiresAt: "2027-01-01T00:00:00Z"));

    /// <summary>
    /// Contradictory data — a subscription plan with no expiry — resolves to locked. A plan name
    /// is never guessed, so it outranks a hole in the rest of the record, and the failure lands on
    /// the side that does not give paid content away.
    /// </summary>
    [Fact]
    public void ASubscriptionPlanWithAMissingExpiryStaysLocked()
        => Assert.False(LifetimeAccess.IsLifetime(isPremium: true, licenseType: "12-months", expiresAt: null));

    // ---- Through the service, the way PremiumLock asks ------------------------

    [Fact]
    public void ReadsTheDecisionOffTheLicenseService()
    {
        Assert.True(LifetimeAccess.IsLifetime(
            new FakeLicense { IsPremium = true, LicenseType = "lifetime", ExpiresAt = null }));

        Assert.False(LifetimeAccess.IsLifetime(
            new FakeLicense { IsPremium = true, LicenseType = "12-months", ExpiresAt = "2027-01-01T00:00:00Z" }));

        Assert.False(LifetimeAccess.IsLifetime(
            new FakeLicense { IsPremium = false, LicenseType = null, ExpiresAt = null }));

        Assert.True(LifetimeAccess.IsLifetime(
            new FakeLicense { IsPremium = true, LicenseType = null, ExpiresAt = null }));
    }

    /// <summary>A gate that throws on a missing service would fail open at the worst moment.</summary>
    [Fact]
    public void ANullServiceIsLockedRatherThanAnException()
        => Assert.False(LifetimeAccess.IsLifetime(null));

    private sealed class FakeLicense : ILicenseService
    {
        public bool IsPremium { get; init; }
        public string? LicenseType { get; init; }
        public string? ExpiresAt { get; init; }

        public bool IsActivated => IsPremium;
        public bool IsFreeTier => !IsPremium;
        public string CurrentLicenseKey => string.Empty;

        public event Action OnLicenseStateChanged { add { } remove { } }
        public event Action OnLicenseActivated { add { } remove { } }

        public Task<(bool Success, string Message)> ActivateLicenseAsync(string licenseKey)
            => Task.FromResult((false, "not used"));

        public Task<(bool Success, string Message)> ValidateLicenseAsync()
            => Task.FromResult((false, "not used"));
    }
}
