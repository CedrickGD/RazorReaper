using RazorReaper.Models;
using RazorReaper.Services.Implementations;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// Pure placement math — "is the crosshair actually in the middle of the screen".
/// Convention: pixel <c>i</c> covers <c>[i, i+1)</c>, so the centre of an even-width monitor is
/// the integer <c>width/2</c> (a boundary between two pixels).
/// </summary>
public class CrosshairPlacementTests
{
    // ─── Canvas origin ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(32, 16f)]
    [InlineData(64, 32f)]
    [InlineData(68, 34f)]
    [InlineData(1024, 512f)]
    public void CanvasOrigin_IsTheGeometricCentre(int canvasSize, float expected)
    {
        Assert.Equal(expected, CrosshairPlacement.CanvasOrigin(canvasSize));
    }

    [Fact]
    public void CanvasOrigin_OnEvenCanvas_LandsOnAPixelBoundary()
    {
        // Every size the renderer can produce is even, so the origin is always an integer.
        for (var size = 32; size <= CrosshairRenderer.MaxCanvasSize; size += 2)
        {
            var origin = CrosshairPlacement.CanvasOrigin(size);
            Assert.Equal(origin, MathF.Floor(origin));
        }
    }

    // ─── Stroke snapping ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, -1)]
    [InlineData(2, -1)]
    [InlineData(3, -2)]
    [InlineData(4, -2)]
    [InlineData(5, -3)]
    [InlineData(20, -10)]
    public void StrokeStart_SnapsToWholePixels(int thickness, int expectedStart)
    {
        Assert.Equal(expectedStart, CrosshairPlacement.StrokeStart(thickness));
    }

    [Fact]
    public void StrokeStart_ClampsNonPositiveThickness()
    {
        Assert.Equal(-1, CrosshairPlacement.StrokeStart(0));
        Assert.Equal(-1, CrosshairPlacement.StrokeStart(-5));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(10)]
    public void StrokeStart_EvenThickness_IsExactlyCentred(int thickness)
    {
        var start = CrosshairPlacement.StrokeStart(thickness);
        Assert.Equal(0f, start + thickness / 2f);
        Assert.Equal(0f, CrosshairPlacement.StrokeResidual(thickness));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    public void StrokeStart_OddThickness_IsOffByAtMostHalfAPixel(int thickness)
    {
        var start = CrosshairPlacement.StrokeStart(thickness);
        var centreOfStroke = start + thickness / 2f;
        Assert.Equal(-0.5f, centreOfStroke);
        Assert.Equal(centreOfStroke, CrosshairPlacement.StrokeResidual(thickness));
    }

    // ─── Screen placement ─────────────────────────────────────────────────────

    [Theory]
    // Bitmap centre must coincide with the monitor centre: 1920/2 - 64/2 = 896.
    [InlineData(0, 1920, 128, 0, 1920 / 2 - 128 / 2)]
    [InlineData(0, 2560, 68, 0, 2560 / 2 - 68 / 2)]
    [InlineData(0, 3840, 1024, 0, 3840 / 2 - 1024 / 2)]
    // A secondary monitor to the right of a 1920-wide primary.
    [InlineData(1920, 2560, 200, 0, 1920 + 2560 / 2 - 200 / 2)]
    // …and to the left (negative virtual-desktop coordinates).
    [InlineData(-2560, 2560, 200, 0, -2560 + 2560 / 2 - 200 / 2)]
    public void AxisTopLeft_CentresTheBitmapOnTheMonitor(
        int monitorOrigin, int monitorExtent, int bitmapExtent, int offset, int expected)
    {
        Assert.Equal(expected, CrosshairPlacement.AxisTopLeft(monitorOrigin, monitorExtent, bitmapExtent, offset));
    }

    [Fact]
    public void AxisTopLeft_AppliesTheUserOffsetVerbatim()
    {
        var centred = CrosshairPlacement.AxisTopLeft(0, 1920, 128, 0);
        Assert.Equal(centred + 7, CrosshairPlacement.AxisTopLeft(0, 1920, 128, 7));
        Assert.Equal(centred - 7, CrosshairPlacement.AxisTopLeft(0, 1920, 128, -7));
    }

    [Fact]
    public void AxisTopLeft_OddMonitorExtent_RoundsInsteadOfTruncatingTwice()
    {
        // 1921/2 = 960.5 → 960.5 - 32 = 928.5 → 929. The old inline
        // `monitor.Width / 2 - bmp.Width / 2` truncated to 960 - 32 = 928, a whole pixel low.
        Assert.Equal(929, CrosshairPlacement.AxisTopLeft(0, 1921, 64, 0));
    }

    [Fact]
    public void AxisTopLeft_IsMonotoneAcrossTheOrigin()
    {
        // A left-hand monitor must not be rounded in the opposite direction from a right-hand one,
        // or the same crosshair sits a pixel further left on one screen than on the other.
        var left = CrosshairPlacement.AxisTopLeft(-1921, 1921, 64, 0);
        var right = CrosshairPlacement.AxisTopLeft(0, 1921, 64, 0);
        Assert.Equal(1921, right - left);
    }

    /// <summary>
    /// Windows hands the overlay PHYSICAL pixels — the UI thread is per-monitor-DPI aware — so a
    /// 125 % or 150 % display changes nothing about the placement arithmetic. These are the real
    /// physical rectangles of common scaled setups; all three must centre exactly.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, 100)]   // 1080p @ 100 %
    [InlineData(2560, 1440, 125)]   // 1440p @ 125 %
    [InlineData(3840, 2160, 150)]   // 4K    @ 150 %
    public void AxisTopLeft_IsIndependentOfDisplayScaling(int physicalWidth, int physicalHeight, int scalePercent)
    {
        var monitor = new MonitorInfo("\\\\.\\DISPLAY1", "test", 0, 0, physicalWidth, physicalHeight, true);
        const int bitmap = 256;

        var (x, y) = CrosshairPlacement.ScreenTopLeft(monitor, bitmap, bitmap, 0, 0);

        Assert.Equal(physicalWidth / 2, x + bitmap / 2);
        Assert.Equal(physicalHeight / 2, y + bitmap / 2);
        Assert.True(scalePercent is 100 or 125 or 150); // documents what the row stands for
    }

    [Theory]
    [InlineData(2560, 1440, 125)]
    [InlineData(3840, 2160, 150)]
    public void AxisTopLeft_FedALogicalRect_WouldMissTheCentre(int physicalWidth, int physicalHeight, int scalePercent)
    {
        // Guard-rail test: quantifies what a DPI regression would cost. If a future change ever
        // queried the monitors from a DPI-unaware thread, Windows would report the LOGICAL size
        // and the crosshair would sit half the difference off-centre.
        var logicalWidth = physicalWidth * 100 / scalePercent;
        var logicalHeight = physicalHeight * 100 / scalePercent;
        const int bitmap = 256;

        var correct = CrosshairPlacement.AxisTopLeft(0, physicalWidth, bitmap, 0);
        var wrong = CrosshairPlacement.AxisTopLeft(0, logicalWidth, bitmap, 0);

        Assert.Equal((physicalWidth - logicalWidth) / 2, correct - wrong);
        Assert.Equal((physicalHeight - logicalHeight) / 2,
            CrosshairPlacement.AxisTopLeft(0, physicalHeight, bitmap, 0)
            - CrosshairPlacement.AxisTopLeft(0, logicalHeight, bitmap, 0));
    }

    [Fact]
    public void ScreenTopLeft_UsesBothAxesOfTheMonitorRect()
    {
        var monitor = new MonitorInfo("\\\\.\\DISPLAY2", "test", 1920, -180, 2560, 1440, false);
        var (x, y) = CrosshairPlacement.ScreenTopLeft(monitor, 100, 60, 3, -4);

        Assert.Equal(1920 + 1280 - 50 + 3, x);
        Assert.Equal(-180 + 720 - 30 - 4, y);
    }
}
