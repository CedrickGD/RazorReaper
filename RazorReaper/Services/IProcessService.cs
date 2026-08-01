using System.Diagnostics;

namespace RazorReaper.Services;

/// <summary>
/// Service for process management operations.
/// </summary>
public interface IProcessService
{
    /// <summary>
    /// Gets all processes with the specified name.
    /// </summary>
    /// <param name="processName">The process name to search for (without .exe extension).</param>
    /// <returns>An array of Process objects matching the name.</returns>
    Process[] GetProcessesByName(string processName);

    /// <summary>
    /// Checks if a process with the specified name is currently running.
    /// </summary>
    /// <param name="processName">The process name to check (without .exe extension).</param>
    /// <returns>True if at least one process with that name is running; otherwise, false.</returns>
    bool IsProcessRunning(string processName);

    /// <summary>
    /// Gets the full image path of a running process.
    /// </summary>
    /// <param name="process">The process to resolve.</param>
    /// <returns>The full path to the executable, or <c>null</c> when it cannot be determined.</returns>
    /// <remarks>
    /// Anti-cheat drivers (BattlEye, once ARK joins a protected server) strip module-read rights from
    /// every handle opened to the game, which makes <see cref="Process.MainModule"/> come back null and
    /// <see cref="Process.Modules"/> come back empty. Resolving through QueryFullProcessImageName only
    /// needs PROCESS_QUERY_LIMITED_INFORMATION, which stays granted, so this keeps working in-game.
    /// </remarks>
    string? GetExecutablePath(Process process);

    /// <summary>
    /// Starts a new process with the specified file path.
    /// </summary>
    /// <param name="filePath">The path to the executable file or URI (e.g. steam://...).</param>
    /// <returns>
    /// The started <see cref="Process"/> object, or <c>null</c> when launching a shell-executed URI
    /// scheme (which does not produce a parent process handle). Callers that capture the value are
    /// responsible for disposing it.
    /// </returns>
    Process? Start(string filePath);

    /// <summary>
    /// Kills a process.
    /// </summary>
    /// <param name="process">The process to kill.</param>
    void Kill(Process process);
}
