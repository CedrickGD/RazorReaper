namespace RazorReaper.Services;

/// <summary>
/// Server-side feature keys for the monthly free-tier quotas. Must match FREE_LIMITS in the
/// admin panel (functions/_lib/usage.ts) — the server is the single authority on limits, the
/// app only asks. Features not listed here are unlimited for everyone.
/// </summary>
public static class UsageFeatures
{
    public const string SkyChanger = "sky_changer";
    public const string LoadingScreen = "loading_screen";
    public const string Fonts = "fonts";
    public const string Desync = "desync";
    public const string StretchedRes = "stretched_res";
    public const string FedSuit = "fed_suit";
    public const string InputScripts = "input_scripts";
}

public sealed record UsageGateResult(bool Allowed, bool Unlimited, int? Remaining, int? Limit)
{
    public static readonly UsageGateResult UnlimitedResult = new(true, true, null, null);
}

public sealed record FeatureUsage(int Used, int Limit);

public interface IUsageGateService
{
    /// <summary>
    /// Consume one use of a quota'd feature. Premium licenses short-circuit to unlimited without
    /// a network call. Free tier asks the server; if the server is unreachable the gate fails
    /// OPEN (a quota must never brick a feature because of bad wifi).
    /// </summary>
    Task<UsageGateResult> TryConsumeAsync(string feature);

    /// <summary>
    /// Per-feature used/limit for the current month, for the quota chips. Null when premium
    /// (no chips to show) or when the server is unreachable. Served from a short-lived cache
    /// that consume calls keep fresh.
    /// </summary>
    Task<IReadOnlyDictionary<string, FeatureUsage>?> GetStatusAsync();

    /// <summary>Raised after any consume so open pages can refresh their chips.</summary>
    event Action? OnUsageChanged;
}
