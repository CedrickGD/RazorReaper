using RazorReaper.Models;

namespace RazorReaper.Services;

/// <summary>
/// Drives ARK's in-game console: focuses the window, opens the console with the user's
/// configured key, types/pastes a command, presses Enter, and restores the clipboard.
/// Used by the Game page's console controls. "Sent" means keystrokes were delivered — not
/// that the game accepted the CVar (the console gives no read-back).
/// </summary>
public interface IGameConsoleService
{
    /// <summary>True if at least one ShooterGame process is running.</summary>
    bool IsGameRunning { get; }

    /// <summary>Re-read the console key from Preferences ("GameConsoleKey"). Call after the user rebinds it.</summary>
    void RefreshConsoleKey();

    /// <summary>
    /// Send one command. <paramref name="useClipboard"/> pastes (best for long strings),
    /// otherwise types char-by-char. Returns false if ARK isn't running / has no focusable window / paste failed.
    /// </summary>
    Task<bool> SendCommandAsync(string command, bool useClipboard, CancellationToken ct = default);

    /// <summary>Send many commands sequentially (shared window + clipboard) with a small inter-command delay.</summary>
    Task<ConsoleBatchResult> SendCommandsAsync(IEnumerable<string> commands, bool useClipboard, CancellationToken ct = default);
}
