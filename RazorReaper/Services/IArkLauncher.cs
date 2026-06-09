namespace RazorReaper.Services;

public sealed record ArkLaunchResult(bool Ok, string Message);

/// <summary>
/// Launches ARK the normal way through Steam (steam://rungameid) — the user's usual launch
/// option / BattlEye intact. This is the launch that shows file-injected sky changes in-game.
/// </summary>
public interface IArkLauncher
{
    /// <summary>
    /// Launch ARK through Steam (steam://rungameid) — the normal Play handoff, BattlEye intact.
    /// </summary>
    ArkLaunchResult LaunchNormal();
}
