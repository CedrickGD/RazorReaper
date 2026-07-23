namespace RazorReaper.Services;

/// <summary>
/// Polls the admin panel for a machine-level access suspension/ban and exposes the result to the UI.
/// Unlike the license check (which only affects paying users), this gate is keyed by HWID + install
/// id, so it can lock out FREE users too. Fails open: a network/timeout error keeps the last known
/// state and never locks a user out over a flaky connection.
/// </summary>
public interface IAccessGateService
{
    /// <summary>True when this machine currently has an active suspension or ban.</summary>
    bool IsSuspended { get; }

    /// <summary>"ban" (permanent) or "suspend" (timed), when suspended.</summary>
    string? Mode { get; }

    /// <summary>Admin-supplied reason shown to the user, when suspended.</summary>
    string? Reason { get; }

    /// <summary>End of a timed suspension (local time), when known; null for a permanent ban.</summary>
    DateTimeOffset? BannedUntil { get; }

    /// <summary>Raised whenever the suspension state changes so the UI can re-render.</summary>
    event Action? OnAccessStateChanged;

    /// <summary>Runs an immediate check, then starts the background polling loop. Safe to call once.</summary>
    Task StartAsync();

    /// <summary>Forces an immediate re-check (e.g. the block screen's "Re-check" button).</summary>
    Task<bool> CheckNowAsync();
}
