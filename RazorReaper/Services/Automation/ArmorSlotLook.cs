// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Whether an armour slot on the player tab holds a piece, read off that slot's own pixels — no
/// empty look to compare against, so the first cycle is judged like every other, and no text, so
/// the language does not matter.
///
/// A worn piece is drawn as pale grey armour art; an empty slot is the dark, teal-tinted panel
/// with the world faintly behind it. So the measure is the share of the slot's interior that is
/// bright in every channel and nearly colourless. The interior skips the top strip (the empty
/// label "KOPF"/"BEINE"… or the worn "RÜSTUNG: 20.0" header, both white text) and the bottom strip
/// (the worn "0%  6.0" line). The old test, the mean of a 13 px box at the slot centre, read the
/// trousers as empty: the gap between the two legs sits exactly at the centre.
///
/// Measured on the owner's screenshots (1920x1080, UIScaling 1.0) and 254 recorder frames of four
/// runs with the player tab up, at 960x540: every empty slot 0.000, every worn piece 0.092 or more
/// (legs 0.127–0.154, feet 0.149–0.182, hands 0.092–0.162, head 0.244–0.265, chest 0.324–0.335).
///
/// ponytail: tuned on one base at night and in daylight. A world behind the panel that is itself
/// pale grey and bright (snow, white structures) shows through the slot only faintly, but it is
/// the thing that would push an empty slot up; raise <see cref="FilledShare"/> if that ever happens.
/// </summary>
public static class ArmorSlotLook
{
    /// <summary>
    /// The interior around a slot centre, in pixels at 1080p and interface scale 1.0. The slot is
    /// about 93 px square (±46); the top 20 px carry the label or header, the bottom 18 the stats line.
    /// </summary>
    private const int InteriorLeft = -40, InteriorRight = 40, InteriorTop = -26, InteriorBottom = 28;

    /// <summary>Darkest channel at least this bright (0–255): armour art, not the dark panel.</summary>
    private const int PaleMin = 90;

    /// <summary>Brightest minus darkest channel at most this: grey, not the panel's teal or a red drawn over it.</summary>
    private const int GreyMax = 40;

    /// <summary>Share of pale pixels from which a slot counts as holding a piece — the middle of the measured 0.000–0.092 gap.</summary>
    public const double FilledShare = 0.045;

    /// <summary>The interior of the slot centred at <paramref name="centre"/>, scaled like the panel.</summary>
    public static Rectangle Interior(Point centre, double scale) => Rectangle.FromLTRB(
        centre.X + (int)Math.Round(InteriorLeft * scale),
        centre.Y + (int)Math.Round(InteriorTop * scale),
        centre.X + (int)Math.Round(InteriorRight * scale) + 1,
        centre.Y + (int)Math.Round(InteriorBottom * scale) + 1);

    /// <summary>
    /// Share (0–1) of pale, colourless pixels in <paramref name="region"/> of a capture whose
    /// top-left pixel sits at <paramref name="origin"/>. Pixels outside the capture are skipped.
    /// </summary>
    public static double PaleShare(ScreenCapture capture, Point origin, Rectangle region)
    {
        int pale = 0, total = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            var row = y - origin.Y;
            if (row < 0 || row >= capture.Height) continue;

            for (var x = region.Left; x < region.Right; x++)
            {
                var column = x - origin.X;
                if (column < 0 || column >= capture.Width) continue;

                var i = (row * capture.Width + column) * 4;
                int b = capture.Bgra[i], g = capture.Bgra[i + 1], r = capture.Bgra[i + 2];
                var min = Math.Min(r, Math.Min(g, b));
                var max = Math.Max(r, Math.Max(g, b));
                if (min >= PaleMin && max - min <= GreyMax) pale++;
                total++;
            }
        }
        return total == 0 ? 0 : pale / (double)total;
    }

    /// <summary>Whether the slot centred at <paramref name="centre"/> holds a piece.</summary>
    public static bool IsFilled(ScreenCapture capture, Point origin, Point centre, double scale)
        => PaleShare(capture, origin, Interior(centre, scale)) >= FilledShare;
}
