using System.Globalization;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Where ARK draws the two things the Fed-Suit macro has to hit: the player tab of the middle
/// panel and the five worn armour slots.
///
/// Offsets from the centre of the game's client area rather than points, because that panel is
/// centred and scales uniformly with the window height and the player's interface scale — so one
/// measurement describes every resolution, and nobody has to calibrate a slot by hand on the
/// machine they play on. The numbers were read off a 1920x1080 borderless client at
/// <c>UIScaling=1.0</c>, whose centre is 960,540.
///
/// ponytail: only verified at 16:9 1080p, scale 1.0 — every other size is this model's word for
/// it. If players report the clicks missing, the upgrade path is a manual override per point, not
/// a second model.
/// </summary>
public static class ArkInventoryLayout
{
    /// <summary>Client height the offsets below were measured at.</summary>
    private const double MeasuredHeight = 1080.0;

    /// <summary>
    /// The player tab ("DU" / "YOU"). It has to be clicked every cycle: opening a transmitter
    /// brings the transmitter's own tab to the front, and the transfer key only moves what the
    /// panel in front is showing — which is why the old macro's twenty blind presses moved
    /// nothing at all.
    /// </summary>
    private static readonly Point TabOffset = new(-190, -425);

    /// <summary>
    /// Head, chest, legs (left column), then hands and feet (right column) — the five pieces of
    /// the exo suit, in the order they are transferred. The slot between hands and feet is the
    /// offhand one: it holds whatever the player is carrying rather than a piece of the suit, so
    /// it is deliberately not in this list.
    /// </summary>
    private static readonly Point[] SlotOffsets =
    [
        new(-164, -339), new(-164, -241), new(-164, -145),
        new(164, -339), new(164, -145)
    ];

    /// <summary>The player tab's centre, in screen pixels.</summary>
    public static Point PlayerTab(Rectangle client, double uiScaling) => Project(client, uiScaling, TabOffset);

    /// <summary>The five armour slot centres, in screen pixels, head first.</summary>
    public static Point[] ArmorSlots(Rectangle client, double uiScaling)
        => Array.ConvertAll(SlotOffsets, offset => Project(client, uiScaling, offset));

    /// <summary>
    /// The player's <c>UIScaling</c>, read from the same <c>Saved\Config\WindowsNoEditor</c>
    /// folder the key-binding scan takes Input.ini from. 1.0 when there is no install, no file,
    /// no key, or a value the game itself would not accept: a wrong scale aims every click at
    /// the wrong place, and the stock scale at least aims them where most installs want them.
    /// </summary>
    public static double ReadUiScaling(string? arkInstallPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(arkInstallPath)) return 1.0;

            var path = Path.Combine(
                arkInstallPath, "ShooterGame", "Saved", "Config", "WindowsNoEditor", "GameUserSettings.ini");
            if (!File.Exists(path)) return 1.0;

            foreach (var line in File.ReadLines(path))
            {
                var text = line.AsSpan().Trim();
                if (!text.StartsWith("UIScaling=", StringComparison.OrdinalIgnoreCase)) continue;

                return double.TryParse(
                    text["UIScaling=".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)
                    && scale is >= 0.25 and <= 4.0
                        ? scale
                        : 1.0;
            }
        }
        catch
        {
            // A file being rewritten by the game mid-read is not worth a failed script start.
        }

        return 1.0;
    }

    private static Point Project(Rectangle client, double uiScaling, Point offset)
    {
        var scale = client.Height / MeasuredHeight * (uiScaling > 0 ? uiScaling : 1.0);
        return new Point(
            client.Left + client.Width / 2 + (int)Math.Round(offset.X * scale),
            client.Top + client.Height / 2 + (int)Math.Round(offset.Y * scale));
    }
}
