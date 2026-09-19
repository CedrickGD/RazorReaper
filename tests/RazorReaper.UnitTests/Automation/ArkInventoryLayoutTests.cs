using RazorReaper.Services.Automation;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The Fed-Suit macro aims its clicks with arithmetic instead of a calibration the player has to
/// do by hand, so the arithmetic is what has to be pinned: the points it produces at the size
/// they were measured at, and the way they follow a different resolution, a smaller interface
/// scale and a window that is not at the top left of the primary screen.
///
/// The measurements come from a 1920x1080 borderless client at <c>UIScaling=1.000000</c>,
/// read off the game by hand. A pixel of slack either way — the slots are around 70 px across,
/// and the right column was read at 1123 against a model that says 1124.
/// </summary>
public sealed class ArkInventoryLayoutTests
{
    private static readonly Rectangle FullHd = new(0, 0, 1920, 1080);

    [Fact]
    public void ThePointsAtTheSizeTheyWereMeasuredAt()
    {
        Near(new Point(770, 115), ArkInventoryLayout.PlayerTab(FullHd, 1.0));

        var slots = ArkInventoryLayout.ArmorSlots(FullHd, 1.0);
        Assert.Equal(5, slots.Length);
        Near(new Point(796, 201), slots[0]);  // head
        Near(new Point(796, 299), slots[1]);  // chest
        Near(new Point(796, 395), slots[2]);  // legs
        Near(new Point(1123, 201), slots[3]); // hands
        Near(new Point(1123, 395), slots[4]); // feet
    }

    /// <summary>
    /// The row between hands and feet is the offhand slot. Transferring it would post the tool
    /// or shoulder pet the player is carrying into the transmitter along with the suit.
    /// </summary>
    [Fact]
    public void TheOffhandSlotIsNotOneOfThem()
    {
        var slots = ArkInventoryLayout.ArmorSlots(FullHd, 1.0);

        Assert.DoesNotContain(slots, p => Math.Abs(p.X - 1123) <= 1 && Math.Abs(p.Y - 299) <= 1);
    }

    [Fact]
    public void ABiggerScreenMovesEveryPointOutFromItsCentre()
    {
        var qhd = new Rectangle(0, 0, 2560, 1440);

        Near(new Point(1027, 153), ArkInventoryLayout.PlayerTab(qhd, 1.0));

        var slots = ArkInventoryLayout.ArmorSlots(qhd, 1.0);
        Near(new Point(1061, 268), slots[0]);
        Near(new Point(1061, 399), slots[1]);
        Near(new Point(1061, 527), slots[2]);
        Near(new Point(1499, 268), slots[3]);
        Near(new Point(1499, 527), slots[4]);
    }

    /// <summary>
    /// The owner's reason for computing any of this: "the app should find where things are
    /// itself, for instance when the interface scale is smaller".
    /// </summary>
    [Fact]
    public void ASmallerInterfaceScalePullsEveryPointTowardsTheCentre()
    {
        var full = ArkInventoryLayout.PlayerTab(FullHd, 1.0);
        var small = ArkInventoryLayout.PlayerTab(FullHd, 0.7);

        Assert.Equal((full.X - 960) * 0.7, small.X - 960, 1.0);
        Assert.Equal((full.Y - 540) * 0.7, small.Y - 540, 1.0);
    }

    /// <summary>A windowed game on the second monitor: the origin has to travel with it.</summary>
    [Fact]
    public void AWindowThatIsNotAtTheOriginCarriesThePointsWithIt()
    {
        var windowed = new Rectangle(2020, 60, 1280, 720);

        Near(new Point(2533, 137), ArkInventoryLayout.PlayerTab(windowed, 1.0));

        var slots = ArkInventoryLayout.ArmorSlots(windowed, 1.0);
        Near(new Point(2551, 194), slots[0]);
        Near(new Point(2769, 323), slots[4]);
    }

    [Fact]
    public void TheInterfaceScaleComesOutOfTheGamesOwnConfig()
    {
        var install = WriteGameUserSettings("UIScaling=0.750000");

        Assert.Equal(0.75, ArkInventoryLayout.ReadUiScaling(install));
    }

    /// <summary>
    /// No install, no file, no key, or a number the game would never write: one is what the
    /// panel is drawn at on a stock install, and guessing anything else aims every click wrong.
    /// </summary>
    [Theory]
    [InlineData("ResolutionSizeX=1920")]
    [InlineData("UIScaling=nonsense")]
    [InlineData("UIScaling=0.000000")]
    public void AnythingUnreadableIsOne(string contents)
    {
        Assert.Equal(1.0, ArkInventoryLayout.ReadUiScaling(WriteGameUserSettings(contents)));
        Assert.Equal(1.0, ArkInventoryLayout.ReadUiScaling(null));
        Assert.Equal(1.0, ArkInventoryLayout.ReadUiScaling(Path.Combine(Path.GetTempPath(), "no-ark-here")));
    }

    private static string WriteGameUserSettings(string line)
    {
        var root = Path.Combine(Path.GetTempPath(), "rr-fedsuit-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsNoEditor");
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(config, "GameUserSettings.ini"),
            "[/Script/ShooterGame.ShooterGameUserSettings]" + Environment.NewLine + line + Environment.NewLine);
        return root;
    }

    private static void Near(Point expected, Point actual)
    {
        Assert.True(
            Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1,
            $"expected {expected} ±1, got {actual}");
    }
}
