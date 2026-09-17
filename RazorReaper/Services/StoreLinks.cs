namespace RazorReaper.Services;

/// <summary>
/// Where Premium is bought and renewed. "Buy Premium" on My account and "Buy / renew" in the
/// license overlay are the same shop, so the address is written once — a second copy is the one
/// that goes stale when the shop moves.
/// </summary>
public static class StoreLinks
{
    public const string Store = "https://rr.sellhub.cx";
}
