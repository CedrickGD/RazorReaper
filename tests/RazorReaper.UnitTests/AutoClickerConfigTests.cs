using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests;

public class AutoClickerConfigTests
{
    [Theory]
    [InlineData(0, 0, 0, 100, 100)]
    [InlineData(0, 0, 1, 0, 1000)]
    [InlineData(0, 1, 0, 0, 60_000)]
    [InlineData(1, 0, 0, 0, 3_600_000)]
    [InlineData(0, 0, 1, 500, 1500)]
    public void IntervalIsSummedAcrossAllUnits(int h, int m, int s, int ms, int expected)
    {
        var config = new AutoClickerConfig { Hours = h, Minutes = m, Seconds = s, Milliseconds = ms };

        Assert.Equal(expected, config.TotalMilliseconds);
    }

    [Fact]
    public void IntervalNeverDropsBelowOneMillisecond()
    {
        // An all-zero interval would otherwise mean a zero-period timer.
        var config = new AutoClickerConfig { Hours = 0, Minutes = 0, Seconds = 0, Milliseconds = 0 };

        Assert.Equal(1, config.TotalMilliseconds);
    }

    [Fact]
    public void PositionsSurviveARoundTrip()
    {
        var points = new[]
        {
            new AutoClickerPoint(0, 0),
            new AutoClickerPoint(1645, 1036),
            new AutoClickerPoint(-1920, -47),   // a monitor left of the primary
        };

        var restored = AutoClickerConfigStore.ParsePositions(
            AutoClickerConfigStore.FormatPositions(points));

        Assert.Equal(points, restored);
    }

    [Fact]
    public void EmptyPositionListRoundTripsToEmpty()
    {
        Assert.Empty(AutoClickerConfigStore.ParsePositions(
            AutoClickerConfigStore.FormatPositions([])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("1,2,3")]      // too many parts
    [InlineData("x,y")]        // not numbers
    [InlineData(";;;")]
    public void MalformedPositionsAreSkippedRatherThanThrowing(string? raw)
    {
        // A corrupt preference should cost the saved points, not the ability to open the page.
        Assert.Empty(AutoClickerConfigStore.ParsePositions(raw));
    }

    [Fact]
    public void PartiallyMalformedInputKeepsTheValidPoints()
    {
        var restored = AutoClickerConfigStore.ParsePositions("10,20;broken;30,40");

        Assert.Equal([new AutoClickerPoint(10, 20), new AutoClickerPoint(30, 40)], restored);
    }

    [Fact]
    public void DefaultsAreASaneClicker()
    {
        var config = new AutoClickerConfig();

        Assert.Equal(MouseButton.Left, config.Button);
        Assert.Equal(AutoClickerClickType.Single, config.ClickType);
        Assert.Equal(AutoClickerRepeatMode.Infinite, config.RepeatMode);
        Assert.Equal(AutoClickerPositionMode.Current, config.PositionMode);
        Assert.Equal(AutoClickerBurstMode.Continuous, config.Mode);
        Assert.False(config.Randomize);
        Assert.Empty(config.MultiPositions);
        Assert.Equal(100, config.TotalMilliseconds);
    }
}
