using System.Globalization;
using Microsoft.Maui.Storage;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Persists the Auto Clicker's full configuration.
///
/// Only the timing half used to be stored (ac.hours … ac.repeatcount); button, click type,
/// continuous/burst, target position and randomisation were page-local fields that reset on every
/// visit. That was survivable while the clicker only ever ran from its own page, but once a
/// system-wide hotkey can start it from anywhere those settings have to outlive the page too —
/// otherwise pressing the key from another screen would silently click with defaults instead of
/// what the user last chose.
///
/// The original keys keep their names and meanings so existing installs carry their timing over.
/// </summary>
public static class AutoClickerConfigStore
{
    // Pre-existing keys — do not rename, they carry user data forward.
    private const string KeyHours = "ac.hours";
    private const string KeyMinutes = "ac.minutes";
    private const string KeySeconds = "ac.seconds";
    private const string KeyMs = "ac.ms";
    private const string KeyHoldMs = "ac.holdms";
    private const string KeyPreDelay = "ac.predelay";
    private const string KeyRepeatCount = "ac.repeatcount";

    // Added so a hotkey start from outside the page uses the real settings.
    private const string KeyButton = "ac.button";
    private const string KeyClickType = "ac.clicktype";
    private const string KeyRepeatMode = "ac.repeatmode";
    private const string KeyPositionMode = "ac.positionmode";
    private const string KeyFixedX = "ac.fixedx";
    private const string KeyFixedY = "ac.fixedy";
    private const string KeyMulti = "ac.multipositions";
    private const string KeyMode = "ac.mode";
    private const string KeyBurstCount = "ac.burstcount";
    private const string KeyBurstPause = "ac.burstpause";
    private const string KeyRandomize = "ac.randomize";
    private const string KeyVariance = "ac.variance";

    public static AutoClickerConfig Load()
    {
        var defaults = new AutoClickerConfig();

        return new AutoClickerConfig
        {
            Hours = Preferences.Get(KeyHours, defaults.Hours),
            Minutes = Preferences.Get(KeyMinutes, defaults.Minutes),
            Seconds = Preferences.Get(KeySeconds, defaults.Seconds),
            Milliseconds = Preferences.Get(KeyMs, defaults.Milliseconds),
            HoldMs = Preferences.Get(KeyHoldMs, defaults.HoldMs),
            PreStartDelaySeconds = Preferences.Get(KeyPreDelay, defaults.PreStartDelaySeconds),
            RepeatCount = Preferences.Get(KeyRepeatCount, defaults.RepeatCount),

            Button = ReadEnum(KeyButton, defaults.Button),
            ClickType = ReadEnum(KeyClickType, defaults.ClickType),
            RepeatMode = ReadEnum(KeyRepeatMode, defaults.RepeatMode),
            PositionMode = ReadEnum(KeyPositionMode, defaults.PositionMode),
            FixedX = Preferences.Get(KeyFixedX, defaults.FixedX),
            FixedY = Preferences.Get(KeyFixedY, defaults.FixedY),
            MultiPositions = ParsePositions(Preferences.Get(KeyMulti, string.Empty)),
            Mode = ReadEnum(KeyMode, defaults.Mode),
            BurstClickCount = Preferences.Get(KeyBurstCount, defaults.BurstClickCount),
            BurstPauseSeconds = Preferences.Get(KeyBurstPause, defaults.BurstPauseSeconds),
            Randomize = Preferences.Get(KeyRandomize, defaults.Randomize),
            RandomVarianceMs = Preferences.Get(KeyVariance, defaults.RandomVarianceMs),
        };
    }

    public static void Save(AutoClickerConfig c)
    {
        ArgumentNullException.ThrowIfNull(c);

        Preferences.Set(KeyHours, c.Hours);
        Preferences.Set(KeyMinutes, c.Minutes);
        Preferences.Set(KeySeconds, c.Seconds);
        Preferences.Set(KeyMs, c.Milliseconds);
        Preferences.Set(KeyHoldMs, c.HoldMs);
        Preferences.Set(KeyPreDelay, c.PreStartDelaySeconds);
        Preferences.Set(KeyRepeatCount, c.RepeatCount);

        Preferences.Set(KeyButton, (int)c.Button);
        Preferences.Set(KeyClickType, (int)c.ClickType);
        Preferences.Set(KeyRepeatMode, (int)c.RepeatMode);
        Preferences.Set(KeyPositionMode, (int)c.PositionMode);
        Preferences.Set(KeyFixedX, c.FixedX);
        Preferences.Set(KeyFixedY, c.FixedY);
        Preferences.Set(KeyMulti, FormatPositions(c.MultiPositions));
        Preferences.Set(KeyMode, (int)c.Mode);
        Preferences.Set(KeyBurstCount, c.BurstClickCount);
        Preferences.Set(KeyBurstPause, c.BurstPauseSeconds);
        Preferences.Set(KeyRandomize, c.Randomize);
        Preferences.Set(KeyVariance, c.RandomVarianceMs);
    }

    /// <summary>Serialises target points as "x,y;x,y". Kept text so it stays readable in Preferences.</summary>
    internal static string FormatPositions(IReadOnlyList<AutoClickerPoint> points)
    {
        if (points is null || points.Count == 0) return string.Empty;

        return string.Join(';', points.Select(p =>
            p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Parses "x,y;x,y". Anything malformed is skipped rather than throwing — a corrupt
    /// preference should cost the user their saved points, not the ability to open the page.
    /// </summary>
    internal static IReadOnlyList<AutoClickerPoint> ParsePositions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var result = new List<AutoClickerPoint>();
        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)) continue;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)) continue;
            result.Add(new AutoClickerPoint(x, y));
        }

        return result;
    }

    private static TEnum ReadEnum<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum
    {
        var stored = Preferences.Get(key, Convert.ToInt32(fallback, CultureInfo.InvariantCulture));
        // A value written by a newer build (or a hand-edited preference) must not crash the page.
        return Enum.IsDefined(typeof(TEnum), stored)
            ? (TEnum)Enum.ToObject(typeof(TEnum), stored)
            : fallback;
    }
}
