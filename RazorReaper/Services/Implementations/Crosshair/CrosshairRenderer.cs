using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RazorReaper.Models;
// MAUI's implicit usings pull in Microsoft.Maui.Graphics.* and Microsoft.Maui.Controls.Image, which
// collide with most System.Drawing types. Aliasing here so every usage resolves to System.Drawing.
using Color = System.Drawing.Color;
using ImageFormat = System.Drawing.Imaging.ImageFormat;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Pure renderer: turns a CrosshairProfile + a 0..1 animation phase into a 32bpp ARGB Bitmap.
/// Called from both the overlay (UpdateLayeredWindow) and the Blazor preview (data URL).
///
/// Implementation is split across partial files:
///  • <c>CrosshairRenderer.cs</c> — public API, canvas sizing, type dispatch (you are here).
///  • <c>CrosshairRenderer.Shapes.cs</c> — per-shape drawing primitives.
///
/// Colour helpers live in <see cref="CrosshairColor"/>.
/// </summary>
internal static partial class CrosshairRenderer
{
    /// <summary>Hard cap on a single canvas dimension. 1024 keeps each render under 4 MB so we can
    /// pool the buffer and stay well clear of LOH pressure even when several render paths overlap.</summary>
    public const int MaxCanvasSize = 1024;

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

        var bodyColor = CrosshairColor.Resolve(profile, phase);
        var outlineColor = CrosshairColor.ParseHex(profile.OutlineColor);
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
            case CrosshairType.Pixel:
                DrawPixel(g, profile, sizeMul, bodyColor, outlineColor);
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
                CrosshairType.Pixel =>
                    Math.Max((Math.Max(1, profile.PixelGridSize) + 1) * Math.Max(1, profile.DotSize)
                             + profile.OutlineThickness * 4 + 8, 32),
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

    /// <summary>Render to a PNG byte array (used for the in-page preview as a data URL).</summary>
    public static byte[] RenderPng(CrosshairProfile profile, double phase, Bitmap? cachedImage)
    {
        using var bmp = Render(profile, phase, cachedImage);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
