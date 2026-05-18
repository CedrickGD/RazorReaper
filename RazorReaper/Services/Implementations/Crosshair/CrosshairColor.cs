using System.Drawing;
using System.Globalization;
using RazorReaper.Models;
// MAUI's implicit usings pull in Microsoft.Maui.Graphics.Color which collides with System.Drawing.Color.
using Color = System.Drawing.Color;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Colour helpers for the renderer: parse hex strings, compute the per-frame body colour for the
/// rainbow animation, and convert HSV → RGB. Extracted from <see cref="CrosshairRenderer"/> so
/// the renderer file can focus on the actual drawing logic.
/// </summary>
internal static class CrosshairColor
{
    /// <summary>Pick the body colour for the current animation phase. For non-rainbow profiles
    /// this is just the profile's static colour; for rainbow profiles it sweeps the hue wheel.</summary>
    public static Color Resolve(CrosshairProfile profile, double phase)
    {
        if (profile.Rainbow)
        {
            // HSV cycle — phase 0..1 maps to 0..360°
            return FromHsv(phase * 360.0, 1.0, 1.0);
        }
        return ParseHex(profile.Color);
    }

    /// <summary>Parse a hex colour string. Accepts <c>#rgb</c>, <c>#rrggbb</c>, or <c>#aarrggbb</c>
    /// (with or without leading <c>#</c>). Returns <see cref="Color.White"/> on any parse failure
    /// rather than throwing — a malformed colour shouldn't be enough to crash the render loop.</summary>
    public static Color ParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Color.White;

        hex = hex.Trim();
        if (hex.StartsWith("#")) hex = hex[1..];

        if (hex.Length == 3)
        {
            // Expand shorthand like #f0a → #ff00aa
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        if (hex.Length != 6 && hex.Length != 8)
            return Color.White;

        try
        {
            if (hex.Length == 6)
            {
                var r = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(255, r, g, b);
            }
            else
            {
                var a = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var r = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var g = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var b = byte.Parse(hex.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            return Color.White;
        }
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromArgb(255,
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}
