using System.Drawing;
using System.Drawing.Imaging;
using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// End-to-end centring checks against a real rendered bitmap. These are the tests that would have
/// caught the original defect: the arithmetic in the renderer looked plausible, but the pixels it
/// produced were half a pixel up-and-left of the canvas centre and even-thickness arms came out
/// smeared over an extra column at 50 % alpha.
/// </summary>
public class CrosshairRendererCentringTests
{
    private static CrosshairProfile Cross(int thickness, int outline = 0, int size = 20, int gap = 4) => new()
    {
        Type = CrosshairType.Cross,
        Size = size,
        Thickness = thickness,
        Gap = gap,
        OutlineThickness = outline,
        Opacity = 100,
        ShowDot = false,
        Rotation = 0,
        Animation = CrosshairAnimation.None,
        Rainbow = false,
        Color = "#FFFFFF",
        OutlineColor = "#000000",
    };

    private static Bitmap Render(CrosshairProfile p, out int canvasSize)
    {
        canvasSize = CrosshairRenderer.ComputeCanvasSize(p, null, 1080);
        var bmp = new Bitmap(canvasSize, canvasSize, PixelFormat.Format32bppPArgb);
        CrosshairRenderer.RenderInto(bmp, p, 0.0, null);
        return bmp;
    }

    /// <summary>Columns with a non-transparent pixel in the given row, with their alpha.</summary>
    private static List<(int Index, int Alpha)> Row(Bitmap bmp, int y)
    {
        var hits = new List<(int, int)>();
        for (var x = 0; x < bmp.Width; x++)
        {
            var a = bmp.GetPixel(x, y).A;
            if (a != 0) hits.Add((x, a));
        }
        return hits;
    }

    private static List<(int Index, int Alpha)> Column(Bitmap bmp, int x)
    {
        var hits = new List<(int, int)>();
        for (var y = 0; y < bmp.Height; y++)
        {
            var a = bmp.GetPixel(x, y).A;
            if (a != 0) hits.Add((y, a));
        }
        return hits;
    }

    [Fact]
    public void ComputeCanvasSize_IsAlwaysEven()
    {
        // The whole placement chain relies on this: an even canvas has an integer centre, which is
        // what lets the bitmap's centre coincide exactly with an even-width monitor's centre.
        foreach (var type in Enum.GetValues<CrosshairType>())
        foreach (var size in new[] { 1, 7, 16, 33, 150 })
        foreach (var rotation in new[] { 0, 45 })
        {
            var p = Cross(2, size: size);
            p.Type = type;
            p.Rotation = rotation;
            p.DotSize = size % 20 + 1;
            var n = CrosshairRenderer.ComputeCanvasSize(p, null, 1080);
            Assert.True(n % 2 == 0, $"{type} size={size} rot={rotation} produced an odd canvas {n}");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void EvenThicknessArms_AreCrispAndExactlyCentred(int thickness)
    {
        using var bmp = Render(Cross(thickness), out var n);
        var centre = n / 2;

        // A row that crosses only the vertical arms.
        var row = Row(bmp, n / 4);
        Assert.Equal(thickness, row.Count);
        Assert.All(row, hit => Assert.Equal(255, hit.Alpha));
        Assert.Equal(centre - thickness / 2, row[0].Index);
        Assert.Equal(centre + thickness / 2 - 1, row[^1].Index);

        // …and the mirrored check on the horizontal arms.
        var col = Column(bmp, n / 4);
        Assert.Equal(thickness, col.Count);
        Assert.All(col, hit => Assert.Equal(255, hit.Alpha));
        Assert.Equal(centre - thickness / 2, col[0].Index);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void OddThicknessArms_AreCrispAndOffByAtMostHalfAPixel(int thickness)
    {
        // An odd-width stroke cannot be symmetric about a pixel boundary. The renderer keeps it
        // crisp and accepts a half-pixel bias rather than smearing it across thickness+1 columns.
        using var bmp = Render(Cross(thickness), out var n);
        var centre = n / 2;

        var row = Row(bmp, n / 4);
        Assert.Equal(thickness, row.Count);
        Assert.All(row, hit => Assert.Equal(255, hit.Alpha));

        var strokeCentre = (row[0].Index + row[^1].Index + 1) / 2.0;
        Assert.Equal(0.5, Math.Abs(strokeCentre - centre));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    public void OutlinedArms_StayCrispAndKeepTheSameCentre(int thickness, int outline)
    {
        using var plain = Render(Cross(thickness), out var plainN);
        using var outlined = Render(Cross(thickness, outline), out var outlinedN);

        var plainRow = Row(plain, plainN / 4);
        var outlinedRow = Row(outlined, outlinedN / 4);

        Assert.All(outlinedRow, hit => Assert.Equal(255, hit.Alpha));
        // Outline widens the arm by `outline` on each side and nothing else.
        Assert.Equal(thickness + outline * 2, outlinedRow.Count);

        var plainCentre = (plainRow[0].Index + plainRow[^1].Index + 1) / 2.0 - plainN / 2.0;
        var outlinedCentre = (outlinedRow[0].Index + outlinedRow[^1].Index + 1) / 2.0 - outlinedN / 2.0;
        Assert.Equal(plainCentre, outlinedCentre);
    }

    [Fact]
    public void Arms_AreSymmetricAboutTheCanvasCentre()
    {
        // Left arm and right arm must be mirror images; likewise top and bottom. This catches an
        // off-by-one in the gap/length arithmetic that a single-axis check would miss.
        using var bmp = Render(Cross(2, size: 18, gap: 5), out var n);
        var centre = n / 2;

        for (var d = 1; d <= centre - 1; d++)
        {
            var left = bmp.GetPixel(centre - d, centre - 1).A;
            var right = bmp.GetPixel(centre + d - 1, centre - 1).A;
            Assert.True(left == right, $"horizontal asymmetry at distance {d}: {left} vs {right}");

            var top = bmp.GetPixel(centre - 1, centre - d).A;
            var bottom = bmp.GetPixel(centre - 1, centre + d - 1).A;
            Assert.True(top == bottom, $"vertical asymmetry at distance {d}: {top} vs {bottom}");
        }
    }

    [Theory]
    [InlineData(CrosshairType.Dot)]
    [InlineData(CrosshairType.Circle)]
    [InlineData(CrosshairType.Cross)]
    public void DrawnGlyph_IsBoundedSymmetricallyAroundTheCentre(CrosshairType type)
    {
        var p = Cross(2);
        p.Type = type;
        p.DotSize = 3;
        using var bmp = Render(p, out var n);
        var (minX, maxX, minY, maxY) = Bounds(bmp, n);
        Assert.True(maxX >= 0, $"{type} rendered nothing");

        // The glyph's bounding box must be centred on the canvas to within the half-pixel that an
        // odd extent unavoidably costs.
        Assert.True(Math.Abs((minX + maxX + 1) / 2.0 - n / 2.0) <= 0.5, $"{type} x-bounds [{minX}..{maxX}] in {n}");
        Assert.True(Math.Abs((minY + maxY + 1) / 2.0 - n / 2.0) <= 0.5, $"{type} y-bounds [{minY}..{maxY}] in {n}");
    }

    [Fact]
    public void TStyle_IsCentredHorizontallyAndStartsAtTheAimPointVertically()
    {
        // A T deliberately drops the top arm, so only the horizontal axis can be symmetric. What
        // must still hold is that the horizontal bar straddles the aim point rather than sitting
        // below it — that is the part a centring regression would break.
        var p = Cross(2);
        p.Type = CrosshairType.TStyle;
        using var bmp = Render(p, out var n);
        var (minX, maxX, minY, maxY) = Bounds(bmp, n);

        Assert.Equal(n / 2.0, (minX + maxX + 1) / 2.0);
        Assert.Equal(n / 2 - 1, minY);              // horizontal bar's top row, thickness 2
        Assert.True(maxY > n / 2, "the stem must extend below the aim point");
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) Bounds(Bitmap bmp, int n)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            if (bmp.GetPixel(x, y).A == 0) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (minX, maxX, minY, maxY);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void PixelArt_CentreCellLandsOnTheCanvasCentre(int cellScale)
    {
        const int grid = 8;
        var data = new char[grid * grid];
        Array.Fill(data, '0');
        // The editor marks cell (grid/2, grid/2) as the centre; the renderer must agree.
        data[grid / 2 * grid + grid / 2] = '1';

        var p = new CrosshairProfile
        {
            Type = CrosshairType.Pixel,
            PixelGridSize = grid,
            DotSize = cellScale,
            PixelArtData = new string(data),
            OutlineThickness = 0,
            Opacity = 100,
            Color = "#FFFFFF",
            Animation = CrosshairAnimation.None,
        };

        using var bmp = Render(p, out var n);
        var centre = n / 2;

        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var lit = bmp.GetPixel(x, y).A != 0;
            var inCell = x >= centre - cellScale / 2 && x < centre + cellScale / 2
                      && y >= centre - cellScale / 2 && y < centre + cellScale / 2;
            Assert.True(lit == inCell, $"pixel ({x},{y}) lit={lit} expected={inCell} (canvas {n}, scale {cellScale})");
        }
    }
}
