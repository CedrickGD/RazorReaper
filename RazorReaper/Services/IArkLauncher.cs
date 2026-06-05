namespace RazorReaper.Services;

public sealed record ArkLaunchResult(bool Ok, string Message);

/// <summary>
/// Launches ARK via the "Play ARK: No BattlEye Anti-Cheat (Unofficial Servers Only)" path —
/// i.e. ShooterGame.exe started directly — while preserving the user's Steam launch options
/// (e.g. <c>culture=Global</c> for custom fonts). Also detects whether BattlEye is active so
/// the Live Apply / proxy features can refuse to run under anti-cheat.
/// </summary>
public interface IArkLauncher
{
    /// <summary>The Steam launch options configured for ARK (e.g. "culture=Global"); empty if none.</summary>
    string GetSteamLaunchOptions();

    /// <summary>
    /// True if BattlEye looks active (the game was started WITH anti-cheat, or a BE process/module
    /// is present). Live Apply must refuse in this case — the proxy DLL would be a ban risk.
    /// </summary>
    bool IsBattlEyeActive();

    /// <summary>
    /// Start ShooterGame.exe directly (No BattlEye, Unofficial only) with the Steam launch options
    /// appended, so custom fonts / culture options still apply.
    /// </summary>
    ArkLaunchResult LaunchNoBattlEye();
}
