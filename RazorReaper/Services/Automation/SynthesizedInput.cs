using System.Collections.Concurrent;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Tracks which virtual keys RazorReaper is currently pressing itself, so a global hotkey never
/// fires on our own synthetic input.
///
/// Without this, scripts start each other. Observed for real: Auto-Walk holds <c>W</c>; if the
/// user's Ctrl and Alt are still physically down from the keypress that started it, Windows sees
/// Ctrl+Alt+W and toggles whatever is bound to that — and it cascades, because that script then
/// presses <c>T</c>, which lands as Ctrl+Alt+T and starts a third. A single keypress ended up
/// running three scripts and walking the character off across the map.
///
/// <c>RegisterHotKey</c> gives no way to tell an injected keystroke from a real one — WM_HOTKEY
/// carries no source information — so the input layer records what it is pressing and the hotkey
/// pump consults it. Static rather than injected: there is one input pipeline per process, and
/// threading it through would mean a dependency cycle between the simulator and the hotkey service.
/// </summary>
public static class SynthesizedInput
{
    /// <summary>
    /// How long a key stays "ours" after we release it. Key-up and the hotkey message race each
    /// other, and a hotkey that arrives just after our release is still caused by us.
    /// </summary>
    private const long ReleaseGraceMs = 60;

    private static readonly ConcurrentDictionary<int, int> DownCounts = new();
    private static readonly ConcurrentDictionary<int, long> ReleasedAt = new();

    /// <summary>Records that we pressed <paramref name="virtualKey"/>.</summary>
    public static void Pressed(int virtualKey)
    {
        if (virtualKey <= 0) return;
        DownCounts.AddOrUpdate(virtualKey, 1, static (_, current) => current + 1);
        ReleasedAt.TryRemove(virtualKey, out _);
    }

    /// <summary>Records that we released <paramref name="virtualKey"/>.</summary>
    public static void Released(int virtualKey)
    {
        if (virtualKey <= 0) return;

        DownCounts.AddOrUpdate(virtualKey, 0, static (_, current) => current > 0 ? current - 1 : 0);
        ReleasedAt[virtualKey] = Environment.TickCount64;
    }

    /// <summary>
    /// True while <paramref name="virtualKey"/> is held by us, or was released within the grace
    /// window. A hotkey on this key should be ignored.
    /// </summary>
    public static bool IsActive(int virtualKey)
    {
        if (virtualKey <= 0) return false;

        if (DownCounts.TryGetValue(virtualKey, out var count) && count > 0) return true;

        return ReleasedAt.TryGetValue(virtualKey, out var released)
               && Environment.TickCount64 - released < ReleaseGraceMs;
    }

    /// <summary>Clears all state. For tests.</summary>
    internal static void Reset()
    {
        DownCounts.Clear();
        ReleasedAt.Clear();
    }
}
