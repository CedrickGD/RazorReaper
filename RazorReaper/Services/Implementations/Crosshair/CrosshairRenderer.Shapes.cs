using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RazorReaper.Models;
using Color = System.Drawing.Color;
using PointF = System.Drawing.PointF;
using LineCap = System.Drawing.Drawing2D.LineCap;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Per-shape drawing primitives used by <see cref="CrosshairRenderer.RenderInto"/>. All methods
/// assume the <see cref="Graphics"/> transform has already been positioned at the crosshair's
/// centre and rotated to taste — every coordinate here is relative to that local origin.
/// </summary>
internal static partial class CrosshairRenderer
{
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

    private static void DrawPixel(Graphics g, CrosshairProfile p, double sizeMul, Color body, Color outline)
    {
        // Pixel-art crosshair. PixelArtData is a row-major '0'/'1' grid of size NxN where
        // N = PixelGridSize; every '1' becomes a sharp DotSize-px block. Empty data → fall
        // back to a single centre pixel so a freshly-picked Pixel type still renders.
        var gridSize = Math.Max(1, p.PixelGridSize);
        var scale = Math.Max(1, (int)Math.Round(p.DotSize * sizeMul));
        var data = p.PixelArtData ?? string.Empty;

        // Save AA/interp modes; pixel art wants crisp, integer-aligned blocks.
        var savedSmoothing = g.SmoothingMode;
        var savedPixelOffset = g.PixelOffsetMode;
        var savedInterp = g.InterpolationMode;
        try
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;

            // World origin sits at the GEOMETRIC CENTRE of the cell flagged as "centre" in
            // the editor — which is cell (halfCells, halfCells). That means the cell's
            // top-left corner must be at (-scale/2, -scale/2), not (0, 0). Without this
            // shift the rendered crosshair lands ½-cell down and right of the aim point
            // and the marked centre cell visibly diverges from the rendered centre.
            var halfCells = gridSize / 2;
            // Shift every cell so cell (halfCells, halfCells) is centred on the origin.
            var originShiftX = -scale / 2f;
            var originShiftY = -scale / 2f;

            // If the user hasn't painted anything yet, draw a single centre pixel so the
            // crosshair is visible. Same outline-rect logic as a populated grid.
            var noData = string.IsNullOrEmpty(data) || data.IndexOf('1') < 0;

            using var bodyBrush = new SolidBrush(body);
            using var outlineBrush = p.OutlineThickness > 0 ? new SolidBrush(outline) : null;
            var ot = Math.Max(0, p.OutlineThickness);

            if (noData)
            {
                if (outlineBrush != null)
                    g.FillRectangle(outlineBrush, originShiftX - ot, originShiftY - ot, scale + ot * 2, scale + ot * 2);
                g.FillRectangle(bodyBrush, originShiftX, originShiftY, scale, scale);
                return;
            }

            // Two-pass paint: outline first (so it doesn't overdraw body of adjacent cells),
            // then bodies. Outline is drawn per-cell because adjacent-cell outlines don't need
            // to merge into one big rectangle — pixel art typically has gaps.
            if (outlineBrush != null)
            {
                for (int r = 0; r < gridSize; r++)
                {
                    for (int c = 0; c < gridSize; c++)
                    {
                        var idx = r * gridSize + c;
                        if (idx >= data.Length || data[idx] != '1') continue;
                        var x = (c - halfCells) * scale + originShiftX;
                        var y = (r - halfCells) * scale + originShiftY;
                        g.FillRectangle(outlineBrush, x - ot, y - ot, scale + ot * 2, scale + ot * 2);
                    }
                }
            }

            for (int r = 0; r < gridSize; r++)
            {
                for (int c = 0; c < gridSize; c++)
                {
                    var idx = r * gridSize + c;
                    if (idx >= data.Length || data[idx] != '1') continue;
                    var x = (c - halfCells) * scale + originShiftX;
                    var y = (r - halfCells) * scale + originShiftY;
                    g.FillRectangle(bodyBrush, x, y, scale, scale);
                }
            }
        }
        finally
        {
            g.SmoothingMode = savedSmoothing;
            g.PixelOffsetMode = savedPixelOffset;
            g.InterpolationMode = savedInterp;
        }
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
}
