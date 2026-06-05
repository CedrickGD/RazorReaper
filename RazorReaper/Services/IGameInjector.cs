namespace RazorReaper.Services;

/// <summary>Result of a DLL-injection attempt.</summary>
public sealed record InjectResult(bool Ok, bool AlreadyLoaded, string Message);

/// <summary>
/// Loads RazorReaper's native helper (rr_live.dll) into the running ARK process via
/// LoadLibraryW + CreateRemoteThread, and talks to it over a named pipe. This is the
/// in-process route that CAN do live texture reloads (the external Memory Patcher can't).
/// Intended for single-player / unofficial only.
/// </summary>
public interface IGameInjector
{
    /// <summary>True if rr_live.dll is already loaded in the running ShooterGame.</summary>
    bool IsLoadedInGame();

    /// <summary>Resolve the ShooterGame process + bundled rr_live.dll and inject it.</summary>
    InjectResult InjectIntoGame();

    /// <summary>Send a command line to the injected module over its named pipe (\\.\pipe\rr_live).</summary>
    Task<bool> SendCommandAsync(string command, CancellationToken ct = default);
}
