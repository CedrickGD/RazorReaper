using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests;

public class CalibrationRegionRulesTests
{
    [Fact]
    public void SameCornerTwiceIsRejected()
    {
        // The exact accident this guards: the countdown expires before the cursor is moved, so
        // both corners read the same pixel and the region collapses to 0x0.
        Assert.False(CalibrationRegionRules.IsUsableSize(1257, 431, 1257, 431));
    }

    [Theory]
    [InlineData(0, 0, 3, 100)]   // too narrow
    [InlineData(0, 0, 100, 3)]   // too short
    [InlineData(0, 0, 0, 0)]     // degenerate
    public void RegionsBelowTheMinimumSideAreRejected(int left, int top, int right, int bottom)
    {
        Assert.False(CalibrationRegionRules.IsUsableSize(left, top, right, bottom));
    }

    [Theory]
    [InlineData(0, 0, 4, 4)]           // exactly the minimum
    [InlineData(781, 881, 1142, 913)]  // a real captured Take All region
    [InlineData(1112, 844, 1143, 874)] // a real captured stat-button region
    public void RegionsAtOrAboveTheMinimumSideAreAccepted(int left, int top, int right, int bottom)
    {
        Assert.True(CalibrationRegionRules.IsUsableSize(left, top, right, bottom));
    }

    [Fact]
    public void MinimumIsAppliedToBothAxesIndependently()
    {
        var min = CalibrationRegionRules.MinimumSidePx;
        Assert.True(CalibrationRegionRules.IsUsableSize(0, 0, min, min));
        Assert.False(CalibrationRegionRules.IsUsableSize(0, 0, min - 1, min));
        Assert.False(CalibrationRegionRules.IsUsableSize(0, 0, min, min - 1));
    }
}
