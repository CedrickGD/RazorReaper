using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using RazorReaper.Models;
// MAUI's implicit usings pull in Microsoft.Maui.Graphics.* and Microsoft.Maui.Controls.Image, which
// collide with most System.Drawing types. Aliasing here so every usage resolves to System.Drawing.
using Color = System.Drawing.Color;
using PointF = System.Drawing.PointF;
using LineCap = System.Drawing.Drawing2D.LineCap;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Pure renderer: turns a CrosshairProfile + a 0..1 animation phase into a 32bpp ARGB Bitmap.
/// Called from both the overlay (UpdateLayeredWindow) and the Blazor preview (data URL).
/// </summary>
internal static class CrosshairRenderer
{
    /// <summary>
    /// Render the crosshair into a square bitmap large enough to fit the longest line at full extension.
    /// Centered on (canvasSize/2, canvasSize/2). The caller positions the bitmap on the screen.
    /// </summary>
    public static Bitmap Render(CrosshairProfile profile, double phase, Bitmap? cachedImage)
    {
        var canvasSize = ComputeCanvasSize(profile, cachedImage);
        var bmp = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppPArgb);
        RenderInto(bmp, profile, phase, cachedImage);
        return bmp;
    }

    /// <summary>
    /// Draw into a caller-owned bitmap (must be 32bpp PArgb, ideally sized via ComputeCanvasSize).
    /// Lets the overlay and preview pools reuse a single buffer instead of allocating 4–16 MB
    /// every frame — the per-frame allocation churn was triggering OOMs at high crosshair sizes.
    /// </summary>
    public static void RenderInto(Bitmap target, CrosshairProfile profile, double phase, Bitmap? cachedImage)
    {
        using var g = Graphics.FromImage(target);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Bicubic (non-"HighQuality") is 3-5× faster on big images and visually indistinguishable
        // at the sizes a crosshair would ever use. HighQualityBicubic was burning ~80 ms per render
        // on a 1080p source, which is what was freezing the editor when the user dragged a slider.
        g.InterpolationMode = profile.Type == CrosshairType.Image
            ? InterpolationMode.Bicubic
            : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // SourceCopy + Clear gives us a clean transparent canvas without blending against the
        // previous frame's pixels (would happen with the default SourceOver).
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceOver;

        // Translate to center. For an even-width canvas the geometric centre is on the BOUNDARY
        // between two pixels — using `width/2f` puts the world origin at the right/bottom edge of
        // the centre pixel, which then shows as a half-pixel down-right drift after the browser
        // scales the bitmap. Subtracting 0.5 puts the origin on the actual pixel-grid centre.
        var center = (target.Width - 1) / 2f;
        g.TranslateTransform(center, center);

        var rotation = profile.Rotation;
        if (profile.Animation == CrosshairAnimation.Rotate)
        {
            rotation += (int)(phase * 360.0);
        }
        if (rotation != 0)
        {
            g.RotateTransform(rotation);
        }

        // Animation can scale either size or opacity. We compute multipliers here so the type
        // branches below stay simple.
        double sizeMul = 1.0;
        double opacityMul = 1.0;
        if (profile.Animation == CrosshairAnimation.Pulse)
        {
            // 0.85..1.15 with a smooth sine
            sizeMul = 1.0 + 0.15 * Math.Sin(phase * Math.PI * 2.0);
        }
        else if (profile.Animation == CrosshairAnimation.Breath)
        {
            // 0.55..1.0 opacity sweep
            opacityMul = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(phase * Math.PI * 2.0));
        }

        var bodyColor = ResolveColor(profile, phase);
        var outlineColor = ParseColor(profile.OutlineColor);
        var alpha = (int)Math.Clamp(profile.Opacity / 100.0 * 255.0 * opacityMul, 0, 255);
        bodyColor = Color.FromArgb(alpha, bodyColor);
        outlineColor = Color.FromArgb(alpha, outlineColor);

        switch (profile.Type)
        {
            case CrosshairType.Cross:
                DrawCross(g, profile, sizeMul, bodyColor, outlineColor);
                break;
            case CrosshairType.Dot:
                DrawDot(g, profile, sizeMul, bodyColor, outlineColor);
                break;
            case CrosshairType.Circle:
                DrawCircle(g, profile, sizeMul, bodyColor, outlineColor);
                break;
            case CrosshairType.TStyle:
                DrawTStyle(g, profile, sizeMul, bodyColor, outlineColor);
                break;
            case CrosshairType.Image:
                DrawImage(g, profile, sizeMul, cachedImage, alpha);
                break;
        }
    }

    public static int ComputeCanvasSize(CrosshairProfile profile, Bitmap? cachedImage = null, int? maxBound = null)
    {
        // Pick a canvas large enough that lines, outline, and rotation never clip. For images we
        // need to honour the *actual* image dimensions × user-selected scale — otherwise anything
        // larger than the assumed 256px gets silently cropped to nothing.
        int basis;
        if (profile.Type == CrosshairType.Image)
        {
            if (cachedImage != null)
            {
                var maxDim = Math.Max(cachedImage.Width, cachedImage.Height);
                basis = (int)Math.Ceiling(maxDim * (profile.ImageScale / 100.0)) + 16;
                basis = Math.Max(basis, 32);
            }
            else
            {
                // No image yet — pick a small placeholder canvas so we don't churn memory.
                basis = 64;
            }
        }
        else
        {
            basis = profile.Type switch
            {
                CrosshairType.Dot => Math.Max(profile.DotSize * 6, 32),
                CrosshairType.Circle => profile.Size * 4 + profile.OutlineThickness * 4 + 16,
                _ => (profile.Size + Math.Max(0, profile.Gap)) * 2 + profile.Thickness * 2 + profile.OutlineThickness * 4 + 16
            };
        }

        if (basis % 2 != 0) basis++;
        // sqrt(2) headroom when rotation might apply, so corners don't clip.
        if (profile.Rotation != 0 || profile.Animation == CrosshairAnimation.Rotate)
        {
            basis = (int)Math.Ceiling(basis * 1.5);
            if (basis % 2 != 0) basis++;
        }
        // Apply both the global ceiling AND the caller-supplied bound (typically the active
        // monitor's smaller dimension for the overlay, or a small preview-pane size for the
        // editor). This is what enforces "never bigger than one monitor".
        var bound = Math.Min(MaxCanvasSize, maxBound ?? MaxCanvasSize);
        basis = Math.Min(basis, bound);
        if (basis % 2 != 0) basis++;
        return basis;
    }

    /// <summary>Hard cap on a single canvas dimension. 1024 keeps each render under 4 MB so we can
    /// pool the buffer and stay well clear of LOH pressure even when several render paths overlap.</summary>
    public const int MaxCanvasSize = 1024;

    private static void DrawCross(Graphics g, CrosshairProfile p, double sizeMul, Color body, Color outline)
    {
        var len = (float)(p.Size * sizeMul);
        var gap = p.Gap;
        var thick = Math.Max(1, p.Thickness);

        using var bodyPen = new Pen(body, thick) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

        // Draw outline first by stroking with a thicker pen of outline color.
        if (p.OutlineThickness > 0)
        {
            using var outlinePen = new Pen(outline, thick + p.OutlineThickness * 2)
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat
            };
            DrawCrossLines(g, p, gap, len, outlinePen);
        }

        DrawCrossLines(g, p, gap, len, bodyPen);

        if (p.ShowDot)
        {
            DrawCenterDot(g, p, body, outline);
        }
    }

    private static void DrawCrossLines(Graphics g, CrosshairProfile p, int gap, float len, Pen pen)
    {
        // Lines are drawn from gap..gap+len in each cardinal direction.
        if (p.ShowTopLine)
            g.DrawLine(pen, 0, -gap, 0, -gap - len);
        if (p.ShowBottomLine)
            g.DrawLine(pen, 0, gap, 0, gap + len);
        if (p.ShowLeftLine)
            g.DrawLine(pen, -gap, 0, -gap - len, 0);
        if (p.ShowRightLine)
            g.DrawLine(pen, gap, 0, gap + len, 0);
    }

    private static void DrawDot(Graphics g, CrosshairProfile p, double sizeMul, Color body, Color outline)
    {
        var r = (float)Math.Max(1, p.DotSize * sizeMul);

        if (p.OutlineThickness > 0)
        {
            using var ob = new SolidBrush(outline);
            var or = r + p.OutlineThickness;
            g.FillEllipse(ob, -or, -or, or * 2, or * 2);
        }

        using var bb = new SolidBrush(body);
        g.FillEllipse(bb, -r, -r, r * 2, r * 2);
    }

    private static void DrawCircle(Graphics g, CrosshairProfile p, double sizeMul, Color body, Color outline)
    {
        var r = (float)(p.Size * sizeMul);
        var thick = Math.Max(1, p.Thickness);

        if (p.OutlineThickness > 0)
        {
            using var outlinePen = new Pen(outline, thick + p.OutlineThickness * 2);
            g.DrawEllipse(outlinePen, -r, -r, r * 2, r * 2);
        }

        using var bodyPen = new Pen(body, thick);
        g.DrawEllipse(bodyPen, -r, -r, r * 2, r * 2);

        if (p.ShowDot)
        {
            DrawCenterDot(g, p, body, outline);
        }
    }

    private static void DrawTStyle(Graphics g, CrosshairProfile p, double sizeMul, Color body, Color outline)
    {
        // Like Cross but the bottom line is always off — classic T/inverted-T look.
        var clone = new CrosshairProfile
        {
            Size = p.Size,
            Gap = p.Gap,
            Thickness = p.Thickness,
            OutlineThickness = p.OutlineThickness,
            ShowTopLine = false,
            ShowBottomLine = true,
            ShowLeftLine = true,
            ShowRightLine = true,
            ShowDot = p.ShowDot,
            DotSize = p.DotSize
        };
        DrawCross(g, clone, sizeMul, body, outline);
    }

    private static void DrawImage(Graphics g, CrosshairProfile p, double sizeMul, Bitmap? cachedImage, int alpha)
    {
        if (cachedImage == null) return;

        var scale = (float)((p.ImageScale / 100.0) * sizeMul);
        var w = cachedImage.Width * scale;
        var h = cachedImage.Height * scale;
        var dest = new RectangleF(-w / 2f, -h / 2f, w, h);

        // Fast path — full opacity is the common case and the ColorMatrix-attributed DrawImage is
        // ~3× slower than the bare DrawImage(image, rect). Only fall through to ColorMatrix when
        // the user explicitly dialled down opacity.
        if (alpha >= 255)
        {
            g.DrawImage(cachedImage, dest);
            return;
        }

        var a = alpha / 255f;
        var matrix = new ColorMatrix(new[]
        {
            new float[] {1, 0, 0, 0, 0},
            new float[] {0, 1, 0, 0, 0},
            new float[] {0, 0, 1, 0, 0},
            new float[] {0, 0, 0, a, 0},
            new float[] {0, 0, 0, 0, 1}
        });
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);

        g.DrawImage(
            cachedImage,
            new[] { new PointF(dest.Left, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) },
            new RectangleF(0, 0, cachedImage.Width, cachedImage.Height),
            GraphicsUnit.Pixel,
            attrs);
    }

    private static void DrawCenterDot(Graphics g, CrosshairProfile p, Color body, Color outline)
    {
        var r = Math.Max(1, p.DotSize);

        if (p.OutlineThickness > 0)
        {
            using var ob = new SolidBrush(outline);
            var or = r + p.OutlineThickness;
            g.FillEllipse(ob, -or, -or, or * 2, or * 2);
        }

        using var bb = new SolidBrush(body);
        g.FillEllipse(bb, -r, -r, r * 2, r * 2);
    }

    private static Color ResolveColor(CrosshairProfile profile, double phase)
    {
        if (profile.Rainbow)
        {
            // HSV cycle — phase 0..1 maps to 0..360°
            return FromHsv(phase * 360.0, 1.0, 1.0);
        }
        return ParseColor(profile.Color);
    }

    public static Color ParseColor(string? hex)
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

    /// <summary>Render to a PNG byte array (used for the in-page preview as a data URL).</summary>
    public static byte[] RenderPng(CrosshairProfile profile, double phase, Bitmap? cachedImage)
    {
        using var bmp = Render(profile, phase, cachedImage);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
