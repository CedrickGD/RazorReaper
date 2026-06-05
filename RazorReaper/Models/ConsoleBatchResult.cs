namespace RazorReaper.Models;

/// <summary>
/// Aggregate result of sending a batch of console commands to the live game.
/// "Sent" means the keystrokes were delivered — NOT that the game accepted the CVar
/// (the console gives us no read-back; some CVars are protected/whitelisted).
/// </summary>
public sealed record ConsoleBatchResult(
    int Total,
    int Sent,
    int Failed,
    bool GameWasRunning,
    IReadOnlyList<string> FailedCommands);
