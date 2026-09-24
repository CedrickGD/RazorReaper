using System.Globalization;
using Microsoft.Win32;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// How ARK puts its window on screen, as far as the crosshair can see it. The overlay is an
/// ordinary topmost window: it always shows over Windowed Fullscreen, and over Fullscreen only
/// while Windows' fullscreen optimizations keep the desktop compositor drawing. With them off the
/// game owns the screen and no window can appear over it, so the page says so instead of letting
/// "Overlay active" stand alone.
/// </summary>
public static class ArkWindowMode
{
    /// <summary>ARK's <c>FullscreenMode</c> for Fullscreen. 1 is Windowed Fullscreen, 2 Windowed.</summary>
    public const int Fullscreen = 0;

    private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>
    /// <c>FullscreenMode</c> from GameUserSettings.ini, or null when there is no file, no key, or a
    /// value the game would not write. ARK saves the file when its settings are applied or when it
    /// exits, so a change made in game can show up here late.
    /// </summary>
    public static int? ReadFullscreenMode(string? gameUserSettingsPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameUserSettingsPath) || !File.Exists(gameUserSettingsPath)) return null;

            foreach (var line in File.ReadLines(gameUserSettingsPath))
            {
                var text = line.AsSpan().Trim();
                if (!text.StartsWith("FullscreenMode=", StringComparison.OrdinalIgnoreCase)) continue;

                return int.TryParse(text["FullscreenMode=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var mode)
                       && mode is >= 0 and <= 2
                    ? mode
                    : null;
            }
        }
        catch
        {
            // A file the game is rewriting mid-read is not worth a hint either way.
        }

        return null;
    }

    /// <summary>
    /// True when "Disable fullscreen optimizations" is ticked for ShooterGame.exe, for this user or
    /// for all users. Read only; nothing here ever writes the flag.
    /// </summary>
    public static bool FullscreenOptimizationsOffForArk()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(LayersKey);
                if (key is null) continue;

                foreach (var program in key.GetValueNames())
                {
                    if (DisablesFullscreenOptimizations(program, key.GetValue(program) as string)) return true;
                }
            }
            catch
            {
                // An unreadable hive says nothing; the softer hint still applies.
            }
        }

        return false;
    }

    /// <summary>
    /// One AppCompatFlags\Layers entry: the value name is the program's full path, the data the
    /// space-separated layers, e.g. <c>~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE</c>.
    /// </summary>
    internal static bool DisablesFullscreenOptimizations(string program, string? layers)
        => string.Equals(Path.GetFileName(program), "ShooterGame.exe", StringComparison.OrdinalIgnoreCase)
           && layers is not null
           && layers.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE", StringComparer.OrdinalIgnoreCase);
}
