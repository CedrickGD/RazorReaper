using SkiaSharp;

namespace RazorReaper.Services.Implementations;

/// <summary>Re-encodes a bounded, square avatar; original metadata and full-size photos never leave the machine.</summary>
public static class AccountAvatar
{
    public static string Create(byte[] bytes)
    {
        if (bytes.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Choose a picture under 8 MB.");
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            throw new InvalidOperationException("Choose a PNG, JPG or WebP picture.");
        if (codec.Info.Width > 4096 || codec.Info.Height > 4096 || codec.Info.Width < 1 || codec.Info.Height < 1)
            throw new InvalidOperationException("Choose a picture up to 4096 × 4096 pixels.");
        using var source = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("The picture could not be opened.");
        using var target = new SKBitmap(256, 256);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Transparent);
            var size = Math.Min(source.Width, source.Height);
            var x = (source.Width - size) / 2f;
            var y = (source.Height - size) / 2f;
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawBitmap(source, new SKRect(x, y, x + size, y + size), new SKRect(0, 0, 256, 256), paint);
        }
        using var image = SKImage.FromBitmap(target);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 88);
        return "data:image/webp;base64," + Convert.ToBase64String(encoded.ToArray());
    }
}
