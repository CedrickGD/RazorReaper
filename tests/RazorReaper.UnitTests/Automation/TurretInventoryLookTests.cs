using RazorReaper.Services.Automation;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// The turret check and the ammo classifier on real pixels from the owner's 1920x1080 screenshots
/// (UIScaling 1.0). To keep the files small, everything the checks never read is blacked out:
/// the kept pixels are 17 px bands round each frame and grid line at the heights they are sampled,
/// plus the item cells that are classified — every pixel a check reads is original.
///
/// <list type="bullet">
/// <item><c>turret-watch-generator.png</c>: the owner's "T01", which is a Tek Generator (100 slots,
/// Element Shards in the first) — a full structure grid, so NOT a turret.</item>
/// <item><c>turret-watch-synthetic.png</c>: the same frame with every structure cell after the fifth
/// replaced by the player panel's unbordered grid at the same place, 1126 px to the left — a
/// five-slot structure like the reported Tek turret. No real turret inventory was available.</item>
/// <item><c>turret-watch-transmitter.png</c>: a Tek Transmitter's tribute tab — full grid, not a turret.</item>
/// <item><c>turret-player-transmitter.png</c>: the player panel with Element and a repair tool.</item>
/// <item><c>turret-ammo-cells.png</c>: a 15-slot structure panel holding ten bullet stacks and one
/// shard stack (the owner's crop), first row and the bordered empty cell 12 kept.</item>
/// </list>
/// </summary>
public sealed class TurretInventoryLookTests
{
    private static readonly Rectangle FullHd = new(0, 0, 1920, 1080);
    private static readonly TurretInventoryLayout Layout = new(FullHd, 1.0);

    /// <summary>Where each crop's top-left pixel sat on the 1080p screen.</summary>
    private static readonly Point WatchOrigin = new(1171, 142), PlayerOrigin = new(109, 224);

    [Fact]
    public void TheLayoutLandsOnTheMeasuredPixels()
    {
        Assert.Equal(WatchOrigin, Layout.WatchRegion.Location);
        Assert.Equal(PlayerOrigin, Layout.PlayerRegion.Location);
        Assert.Equal(new Point(117, 232), Layout.PlayerGrid);
        Assert.Equal(new Point(1243, 232), Layout.TurretGrid);
        Assert.Equal(new Point(354, 186), Layout.TransferAll);
        Assert.Equal(new Point(163, 278), Layout.PlayerCell(0));
        Assert.Equal(new Point(628, 371), Layout.PlayerCell(11));
    }

    [Fact]
    public void AFiveSlotStructureIsATurret()
    {
        var seen = TurretInventoryLook.Read(Load("turret-watch-synthetic.png"), WatchOrigin, Layout);

        Assert.Equal(new TurretInventoryLook.Signature(9, 5, 0, 0), seen);
        Assert.True(seen.IsTurret);
    }

    [Theory]
    [InlineData("turret-watch-generator.png")]
    [InlineData("turret-watch-transmitter.png")]
    public void AFullStructureGridIsNotATurret(string file)
    {
        var seen = TurretInventoryLook.Read(Load(file), WatchOrigin, Layout);

        Assert.Equal(9, seen.FrameHits);
        Assert.Equal(6, seen.Row2);
        Assert.False(seen.IsTurret);
    }

    /// <summary>The same two screens at 3840x2160: every line two pixels wide, every offset doubled.</summary>
    [Theory]
    [InlineData("turret-watch-synthetic.png", true)]
    [InlineData("turret-watch-generator.png", false)]
    public void ItHoldsAtTwiceTheSize(string file, bool turret)
    {
        var layout = new TurretInventoryLayout(new Rectangle(0, 0, 3840, 2160), 1.0);
        var origin = new Point(1920 + 2 * (WatchOrigin.X - 960), 1080 + 2 * (WatchOrigin.Y - 540));

        var seen = TurretInventoryLook.Read(Double(Load(file)), origin, layout);

        Assert.Equal(origin, layout.WatchRegion.Location);
        Assert.Equal(turret, seen.IsTurret);
    }

    [Fact]
    public void NothingOnScreenIsNotATurret()
    {
        var black = new ScreenCapture(675, 377, new byte[675 * 377 * 4]);

        Assert.False(TurretInventoryLook.Read(black, WatchOrigin, Layout).IsTurret);
    }

    [Theory]
    [InlineData(0, AmmoKind.Bullet)]
    [InlineData(1, AmmoKind.Shard)]
    [InlineData(2, AmmoKind.Bullet)]
    [InlineData(3, AmmoKind.Bullet)]
    [InlineData(4, AmmoKind.Bullet)]
    [InlineData(5, AmmoKind.Bullet)]
    [InlineData(11, AmmoKind.Empty)]
    public void AmmoIsToldApartByItsLook(int cell, AmmoKind expected)
    {
        var capture = Load("turret-ammo-cells.png");

        var kind = TurretInventoryLook.Classify(capture, PlayerOrigin, Layout.PlayerCell(cell), Layout.Scale);

        var s = TurretInventoryLook.Shares(capture, PlayerOrigin, Layout.PlayerCell(cell), Layout.Scale);
        Assert.True(expected == kind, $"cell {cell}: {kind} {s}");
    }

    /// <summary>With margin: every bullet at least 1.25x the olive floor, the shard at twice the grey one.</summary>
    [Fact]
    public void TheMeasuredSharesClearTheThresholdsWithRoom()
    {
        var capture = Load("turret-ammo-cells.png");
        foreach (var cell in new[] { 0, 2, 3, 4, 5 })
        {
            var s = TurretInventoryLook.Shares(capture, PlayerOrigin, Layout.PlayerCell(cell), Layout.Scale);
            Assert.True(s.Olive >= 1.25 * TurretInventoryLook.BulletOliveMin && s.Copper >= 2 * TurretInventoryLook.BulletCopperMin, $"cell {cell}: {s}");
        }
        var shard = TurretInventoryLook.Shares(capture, PlayerOrigin, Layout.PlayerCell(1), Layout.Scale);
        Assert.True(shard.Grey >= 2 * TurretInventoryLook.ShardGreyMin, $"shard: {shard}");
    }

    /// <summary>
    /// The two lookalikes on the owner's screen: Element's orange rim reads as copper, and the
    /// white repair tool's anti-aliased edge as pale grey. Neither is ammo.
    /// </summary>
    [Fact]
    public void ElementAndAToolAreNotAmmo()
    {
        var cells = TurretInventoryLook.ScanPlayer(Load("turret-player-transmitter.png"), PlayerOrigin, Layout);

        Assert.Equal(new[] { AmmoKind.Other, AmmoKind.Other }, cells);
    }

    /// <summary>The 15-slot container: 6, 6, then 3 bordered cells — so it would never pass for a turret.</summary>
    [Fact]
    public void BorderedCellsAreCountedRowByRow()
        => Assert.Equal(15, TurretInventoryLook.BorderedCells(Load("turret-ammo-cells.png"), PlayerOrigin, Layout, Layout.PlayerGrid, 3));

    [Fact]
    public void TheShardsInAStructureAreSeen()
        => Assert.Equal(AmmoKind.Shard, TurretInventoryLook.TurretAmmo(Load("turret-watch-generator.png"), WatchOrigin, Layout, 5));

    [Fact]
    public void TheTooltipSettingIsReadFromAUtf16File()
    {
        var root = Directory.CreateTempSubdirectory("rr-turret-ini").FullName;
        try
        {
            var dir = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsNoEditor");
            Directory.CreateDirectory(dir);
            var ini = Path.Combine(dir, "GameUserSettings.ini");

            File.WriteAllText(ini, "[/Script/ShooterGame.ShooterGameUserSettings]\r\nUIScaling=1.000000\r\nbEnableInventoryItemTooltips=True\r\n", System.Text.Encoding.Unicode);
            Assert.True(TurretInventoryLook.ReadTooltipsEnabled(root));
            Assert.Equal(1.0, ArkInventoryLayout.ReadUiScaling(root));

            File.WriteAllText(ini, "bEnableInventoryItemTooltips=False\r\n", System.Text.Encoding.Unicode);
            Assert.False(TurretInventoryLook.ReadTooltipsEnabled(root));
        }
        finally { Directory.Delete(root, recursive: true); }

        Assert.False(TurretInventoryLook.ReadTooltipsEnabled(null));
    }

    internal static ScreenCapture Load(string file)
    {
        var image = TemplateImage.FromFile(Path.Combine(AppContext.BaseDirectory, "Automation", "TestData", file));
        Assert.NotNull(image);
        return new ScreenCapture(image.Width, image.Height, image.Bgra);
    }

    private static ScreenCapture Double(ScreenCapture c)
    {
        var bgra = new byte[c.Width * c.Height * 16];
        for (var y = 0; y < c.Height * 2; y++)
            for (var x = 0; x < c.Width * 2; x++)
                Array.Copy(c.Bgra, ((y / 2) * c.Width + x / 2) * 4, bgra, (y * c.Width * 2 + x) * 4, 4);
        return new ScreenCapture(c.Width * 2, c.Height * 2, bgra);
    }
}
