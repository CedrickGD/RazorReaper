using RazorReaper.Services.Implementations;
using SkiaSharp;
using Xunit;

namespace RazorReaper.UnitTests.Crosshair;

/// <summary>
/// The overlay centres an image crosshair's canvas, so the canvas has to be the content. These
/// cover the trim that makes that true — the step the PNG import path used to skip, which left a
/// crosshair drawn in the corner of a padded canvas sitting visibly off the aim point.
/// </summary>
public class ImageAutoCropTests
{
    private static SKBitmap Canvas(int width, int height, params (int X, int Y, int W, int H)[] opaqueBlocks)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.White };
        foreach (var (x, y, w, h) in opaqueBlocks)
        {
            canvas.DrawRect(new SKRect(x, y, x + w, y + h), paint);
        }
        return bmp;
    }

    [Fact]
    public void OffCentreContent_IsTrimmedToItsBoundingBox()
    {
        // A 64×64 canvas whose only content is an 8×8 block near the top-left — the shape of every
        // crosshair PNG that renders off-centre.
        using var src = Canvas(64, 64, (4, 6, 8, 8));

        using var cropped = ImageFormatDetection.AutoCropTransparentBorders(src);

        Assert.NotNull(cropped);
        Assert.Equal(8, cropped!.Width);
        Assert.Equal(8, cropped.Height);
        // Every pixel of the result is content, so centring the canvas now centres the crosshair.
        for (var y = 0; y < cropped.Height; y++)
        for (var x = 0; x < cropped.Width; x++)
        {
            Assert.NotEqual(0, cropped.GetPixel(x, y).Alpha);
        }
    }

    [Fact]
    public void ContentSpanningSeveralBlocks_KeepsTheirCombinedBounds()
    {
        using var src = Canvas(100, 100, (10, 20, 4, 4), (60, 70, 6, 6));

        using var cropped = ImageFormatDetection.AutoCropTransparentBorders(src);

        Assert.NotNull(cropped);
        Assert.Equal(66 - 10, cropped!.Width);   // x 10 .. 65 inclusive
        Assert.Equal(76 - 20, cropped.Height);   // y 20 .. 75 inclusive
    }

    [Fact]
    public void AlreadyTightImage_IsLeftAlone()
    {
        // Returning null is what tells the import to store the original bytes rather than
        // re-encoding an image that needs no change.
        using var src = Canvas(16, 16, (0, 0, 16, 16));

        Assert.Null(ImageFormatDetection.AutoCropTransparentBorders(src));
    }

    [Fact]
    public void FullyTransparentImage_IsLeftAlone()
    {
        using var src = Canvas(16, 16);

        Assert.Null(ImageFormatDetection.AutoCropTransparentBorders(src));
    }

    [Fact]
    public void SinglePixel_SurvivesTheCrop()
    {
        using var src = Canvas(32, 32, (17, 5, 1, 1));

        using var cropped = ImageFormatDetection.AutoCropTransparentBorders(src);

        Assert.NotNull(cropped);
        Assert.Equal(1, cropped!.Width);
        Assert.Equal(1, cropped.Height);
        Assert.NotEqual(0, cropped.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void PngIsTheFormatThatNeededThis()
    {
        // Guards the branch condition in the import: PNG is sniffed as a native format, so before
        // the fix it went straight to disk without ever meeting the crop.
        var pngHeader = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(pngHeader, 0);

        Assert.Equal(".png", ImageFormatDetection.SniffNativeImageExtension(pngHeader));
    }
}
