using RazorReaper.Services.Automation;
using Point = System.Drawing.Point;

namespace RazorReaper.UnitTests.Automation;

/// <summary>
/// Worn or empty, read off real pixels: armour slots cut from the owner's 1920x1080 screenshots
/// (all five worn, all five empty) and from the recorder's 960x540 frames of run 220650 — frame 040
/// is the one where the old centre box read the worn trousers as missing and the run stopped.
/// Each crop is centred on the slot centre ArkInventoryLayout gives, 96 px at full size, 48 at half.
/// </summary>
public sealed class ArmorSlotLookTests
{
    [Theory]
    [InlineData("fedsuit-worn-head.png", 1.0)]
    [InlineData("fedsuit-worn-chest.png", 1.0)]
    [InlineData("fedsuit-worn-legs.png", 1.0)]
    [InlineData("fedsuit-worn-hands.png", 1.0)]
    [InlineData("fedsuit-worn-feet.png", 1.0)]
    [InlineData("fedsuit-worn-half-legs.png", 0.5)]
    [InlineData("fedsuit-worn-half-feet.png", 0.5)]
    public void AWornPieceReadsFilled(string file, double scale)
    {
        var share = Share(file, scale);

        // Twice the threshold at least — the lowest measured worn piece sits at 0.09.
        Assert.True(share >= 2 * ArmorSlotLook.FilledShare, $"{file}: {share:0.000}");
    }

    [Theory]
    [InlineData("fedsuit-empty-head.png", 1.0)]
    [InlineData("fedsuit-empty-chest.png", 1.0)]
    [InlineData("fedsuit-empty-legs.png", 1.0)]
    [InlineData("fedsuit-empty-hands.png", 1.0)]
    [InlineData("fedsuit-empty-feet.png", 1.0)]
    [InlineData("fedsuit-empty-half-legs.png", 0.5)]
    [InlineData("fedsuit-empty-half-feet.png", 0.5)]
    public void AnEmptySlotReadsEmpty(string file, double scale)
    {
        var share = Share(file, scale);

        // The label ("BEINE", "FÜßE") is white text too; it sits above the interior.
        Assert.True(share <= ArmorSlotLook.FilledShare / 2, $"{file}: {share:0.000}");
    }

    private static double Share(string file, double scale)
    {
        var capture = Load(file);
        var centre = new Point(capture.Width / 2, capture.Height / 2);
        return ArmorSlotLook.PaleShare(capture, Point.Empty, ArmorSlotLook.Interior(centre, scale));
    }

    private static ScreenCapture Load(string file)
    {
        var image = TemplateImage.FromFile(Path.Combine(AppContext.BaseDirectory, "Automation", "TestData", file));
        Assert.NotNull(image);
        return new ScreenCapture(image.Width, image.Height, image.Bgra);
    }
}
