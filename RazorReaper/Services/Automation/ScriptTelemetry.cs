namespace RazorReaper.Services.Automation;

/// <summary>Why a script run ended. Goes on the wire as the stop event's <c>reason</c>.</summary>
public enum ScriptStopReason
{
    /// <summary>
    /// The user ended it — the tile, the hotkey, the app closing, or a one-shot simply running
    /// its course. All of those are the user's start finishing the way they asked it to, which
    /// is why they share a reason: the panel is looking for the two that are not.
    /// </summary>
    User,

    /// <summary>The monthly free quota was used up and the scaffold stopped the run again.</summary>
    Quota,

    /// <summary>The run loop threw and the run ended with it.</summary>
    Error
}

/// <summary>
/// The three names the automation scripts put on the wire.
///
/// Named here rather than spelled out at each end because the allowlist in TelemetryService
/// drops an unknown name <i>silently</i>, inside the sender, with no log line — which is exactly
/// how the update flow's three events looked correct at every call site and never arrived. A
/// typo in either half of a pair of string literals would do the same thing again.
/// </summary>
public static class ScriptTelemetryEvents
{
    /// <summary>A run began: which script, whether the user is premium, whether a hotkey did it.</summary>
    public const string Start = "script_start";

    /// <summary>A run ended: how long it lasted, how many effects it produced, and why it ended.</summary>
    public const string Stop = "script_stop";

    /// <summary>
    /// A run ended having done nothing at all. It is also on the stop event as <c>noop</c>; this
    /// is the row the panel can count without reading a payload.
    /// </summary>
    public const string Noop = "script_noop";

    /// <summary>
    /// Which screen-capture path is serving the vision scripts: <c>duplication</c>, which can see
    /// a fullscreen ARK, or <c>gdi</c>, which cannot and hands back the desktop instead. Sent on
    /// the switch, not per capture. How many installs are stuck on the blind path is the one
    /// question a log file on one machine cannot answer, and until the output was picked by where
    /// the game actually is, every second-monitor user was on it.
    /// </summary>
    public const string Capture = "script_capture";

    /// <summary>All four, for the allowlist and for the test that pins it.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Start, Stop, Noop, Capture };
}
