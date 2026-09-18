namespace RazorReaper.Services.Implementations;

/// <summary>What one poll of the ARK process said, once the debounce has had its say.</summary>
public enum ArkPresenceChange
{
    /// <summary>Nothing worth acting on: the state held, or an absence is still being confirmed.</summary>
    None,

    /// <summary>ARK was confirmed down and is now up. Brings a tray-hidden instance back into view.</summary>
    CameUp,

    /// <summary>ARK was up and is now confirmed down. Closes the app when close-with-ARK is on.</summary>
    WentDown
}

/// <summary>
/// The state machine behind <see cref="ArkLinkService"/>'s watch loop, with the timer, the process
/// lookup and the closing of the app taken out of it.
///
/// It exists so the two decisions that actually matter can be exercised. Process enumeration
/// transiently misses a live process, so a single "not running" poll must not close the user's
/// app — that is the whole reason for <see cref="ExitConfirmPolls"/>, and a regression in it costs
/// the user their session mid-raid with nothing on screen to explain it. The other is the first
/// poll: the app is usually started while ARK is already running, and treating that as a
/// down → up transition would pop the window to the front at every launch.
///
/// Deliberately not thread-safe: one watch loop owns one instance, start to finish.
/// </summary>
public sealed class ArkPresenceDebounce
{
    /// <summary>
    /// Consecutive "not running" polls required before an exit is trusted. Two at the loop's 3s
    /// cadence means a blip has six seconds to take itself back.
    /// </summary>
    public const int ExitConfirmPolls = 2;

    private readonly int _exitConfirmPolls;

    /// <summary>Null until the first poll settles — "unknown" is not the same as "not running".</summary>
    private bool? _running;

    private int _missedPolls;

    public ArkPresenceDebounce(int exitConfirmPolls = ExitConfirmPolls)
        => _exitConfirmPolls = Math.Max(1, exitConfirmPolls);

    /// <summary>What the debounce believes right now, or null before the first poll.</summary>
    public bool? IsArkRunning => _running;

    /// <summary>Feeds one poll in and returns the transition it completes, if any.</summary>
    public ArkPresenceChange Observe(bool processRunning)
    {
        if (processRunning)
        {
            _missedPolls = 0;

            if (_running == true) return ArkPresenceChange.None;

            // The first poll of a session only records where ARK is. Reporting it as a start
            // would raise the window every time the app launches next to a running game.
            var cameUp = _running == false;
            _running = true;
            return cameUp ? ArkPresenceChange.CameUp : ArkPresenceChange.None;
        }

        if (_running is null)
        {
            _running = false;
            return ArkPresenceChange.None;
        }

        // Already known to be down: nothing left to confirm, and the counter must not creep
        // upward while ARK stays closed.
        if (_running == false) return ArkPresenceChange.None;

        if (++_missedPolls < _exitConfirmPolls) return ArkPresenceChange.None;

        _running = false;
        _missedPolls = 0;
        return ArkPresenceChange.WentDown;
    }
}

/// <summary>
/// What to do with the single combined toggle the pre-split builds stored, decided without a
/// preference store so the decision can be read and tested on its own.
/// </summary>
/// <param name="RemoveLegacyKey">
/// Always true once the legacy key exists: it is dead either way, and leaving it behind would
/// re-run the migration on every launch and undo a toggle the user has since turned off.
/// </param>
/// <param name="SetStartWithArk">Write start-with-ARK on. Never true when a value is already stored.</param>
/// <param name="SetCloseWithArk">Write close-with-ARK on. Never true when a value is already stored.</param>
public readonly record struct ArkLinkLegacyMigration(
    bool RemoveLegacyKey,
    bool SetStartWithArk,
    bool SetCloseWithArk)
{
    /// <summary>The legacy key was never there — nothing to carry over and nothing to clean up.</summary>
    public static readonly ArkLinkLegacyMigration None = new(false, false, false);

    /// <summary>
    /// Carries the one old toggle into the two new ones.
    /// </summary>
    /// <param name="hasLegacyKey">The combined key exists in the store.</param>
    /// <param name="legacyValue">What it said.</param>
    /// <param name="hasStartWithArk">A start-with-ARK value is already stored, so it wins.</param>
    /// <param name="hasCloseWithArk">A close-with-ARK value is already stored, so it wins.</param>
    public static ArkLinkLegacyMigration Plan(
        bool hasLegacyKey,
        bool legacyValue,
        bool hasStartWithArk,
        bool hasCloseWithArk)
    {
        if (!hasLegacyKey) return None;

        // An old toggle that was off carries nothing: both new options default to off, and
        // writing them explicitly would only freeze that default in the store.
        if (!legacyValue) return new ArkLinkLegacyMigration(RemoveLegacyKey: true, false, false);

        return new ArkLinkLegacyMigration(
            RemoveLegacyKey: true,
            SetStartWithArk: !hasStartWithArk,
            SetCloseWithArk: !hasCloseWithArk);
    }
}
