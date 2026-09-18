using System.Drawing;
using RazorReaper.Services.Automation;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// Which screen a capture belongs to.
///
/// Desktop Duplication was pinned to DXGI output 0 — the primary monitor — for its whole life.
/// With ARK on the second screen the duplicated frame was the wrong screen's: the calibrated
/// region sat outside it, the grab was refused, and the GDI fallback took over without a word.
/// GDI cannot see a fullscreen game, so every vision script on a two-monitor setup watched the
/// desktop wallpaper and never matched anything.
///
/// The displays here are made up on purpose. A test that read the real ones would pass on the
/// machine it was written on and prove nothing anywhere else — and the machine it was written on
/// is exactly the single-monitor case that never had the bug.
/// </summary>
public sealed class MonitorSelectionTests
{
    // 1920x1080 primary at the origin, 2560x1440 to its right. Windows lays a second monitor
    // out in virtual-desktop coordinates like this, which is why an untranslated region read
    // off monitor 2 landed hundreds of pixels into monitor 1.
    private static readonly AttachedDisplay Primary =
        new(@"\\.\DISPLAY1", new Rectangle(0, 0, 1920, 1080), IsPrimary: true);

    private static readonly AttachedDisplay Secondary =
        new(@"\\.\DISPLAY2", new Rectangle(1920, 0, 2560, 1440), IsPrimary: false);

    private static readonly AttachedDisplay Above =
        new(@"\\.\DISPLAY3", new Rectangle(0, -1080, 1920, 1080), IsPrimary: false);

    private static IReadOnlyList<AttachedDisplay> Displays => new[] { Primary, Secondary, Above };

    [Fact]
    public void AWindowOnTheSecondMonitorPicksTheSecondMonitor()
    {
        var ark = new Rectangle(1920, 0, 2560, 1440);

        var chosen = MonitorSelection.Choose(Displays, ark);

        Assert.Equal(Secondary, chosen);
    }

    [Fact]
    public void AWindowOnTheMonitorAboveIsNotMistakenForTheOneBelowIt()
    {
        // Negative coordinates are ordinary on a stacked setup, and a naive "is it inside 0..W"
        // check reads every one of them as off-screen and falls back to the primary.
        var chosen = MonitorSelection.Choose(Displays, new Rectangle(200, -900, 1280, 720));

        Assert.Equal(Above, chosen);
    }

    /// <summary>
    /// A window dragged half across the seam belongs to the monitor holding most of it: that is
    /// the one scanning it out, and the one the player is looking at.
    /// </summary>
    [Theory]
    [InlineData(1400, false)] // 520 px of it on the primary, 1400 past the seam
    [InlineData(960, true)]   // 960 each — a dead heat keeps whichever was already chosen
    [InlineData(100, true)]   // only 100 px spills over: the primary keeps it
    public void AStraddlingWindowGoesToWhicheverMonitorHoldsMostOfIt(int spill, bool primaryWins)
    {
        // The seam is at x=1920, so a 1920-wide window starting at `spill` leaves 1920-spill
        // pixels on the primary and `spill` on the secondary. Same height on both, so the
        // comparison is purely how much of the window each one is showing.
        var window = new Rectangle(spill, 0, 1920, 1080);

        var chosen = MonitorSelection.Choose(Displays, window);

        Assert.Equal(primaryWins ? Primary : Secondary, chosen);
    }

    /// <summary>
    /// No window — ARK is not running, or BattlEye refused the handle. The primary is the only
    /// answer that is never absurd, and it is what the capture path used to assume unconditionally.
    /// </summary>
    [Fact]
    public void NoWindowFallsBackToThePrimary()
    {
        Assert.Equal(Primary, MonitorSelection.Choose(Displays, Rectangle.Empty));
        Assert.Equal(Primary, MonitorSelection.Choose(Displays, new Rectangle(500, 500, 0, 0)));
    }

    /// <summary>A window nowhere near any display (dragged off, or a stale rectangle) is not a crash.</summary>
    [Fact]
    public void AWindowOffEveryDisplayFallsBackToThePrimary()
    {
        Assert.Equal(Primary, MonitorSelection.Choose(Displays, new Rectangle(-9000, -9000, 800, 600)));
    }

    [Fact]
    public void NoDisplaysAtAllIsNullRatherThanAGuess()
    {
        Assert.Null(MonitorSelection.Choose(Array.Empty<AttachedDisplay>(), new Rectangle(0, 0, 100, 100)));
        Assert.Null(MonitorSelection.Primary(Array.Empty<AttachedDisplay>()));
    }

    /// <summary>
    /// Windows always reports one primary, but a remote session or a driver mid-reset has been
    /// seen to report none. The first display beats returning nothing.
    /// </summary>
    [Fact]
    public void WithNoPrimaryFlagTheFirstDisplayStandsIn()
    {
        var none = new[] { Secondary, Above };

        Assert.Equal(Secondary, MonitorSelection.Primary(none));
        Assert.Equal(Secondary, MonitorSelection.Choose(none, Rectangle.Empty));
    }

    /// <summary>
    /// The number in "captured on Monitor 2". The device path is what DXGI and GDI both hand
    /// back; nobody reads it aloud.
    /// </summary>
    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY2", 2)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    [InlineData(@"\\.\DISPLAY", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void TheMonitorNumberComesOffTheDeviceName(string? deviceName, int expected)
    {
        Assert.Equal(expected, MonitorSelection.IndexFromDeviceName(deviceName));
    }

    [Fact]
    public void ADisplayReportsItsOwnResolutionKeyTheWayTheCalibrationStoreSpellsIt()
    {
        Assert.Equal("2560x1440", Secondary.ResolutionKey);
        Assert.Equal("1920x1080", Primary.ResolutionKey);
        Assert.Equal(2, Secondary.Index);
    }
}
