using SkiaSharp;

namespace RazorReaper.Services.Implementations.CustomLab;

/// <summary>
/// Skia-backed image transforms used by the Sky Injector. Pure functions over <see cref="SKBitmap"/>;
/// callers own bitmap lifetime. Mirrors t1m's PIL pipeline in inject_custom_sky.py.
/// </summary>
public static class SkyImagePipeline
{
    /// <summary>
    /// Decode an image file (PNG / JPG / BMP / TGA / WEBP / etc.) into an RGBA8888 bitmap.
    /// Returns null if Skia can't decode the file.
    /// </summary>
    public static SKBitmap? Load(string path)
    {
        using var stream = SKFileStream.OpenStream(path);
        if (stream is null) return null;
        var decoded = SKBitmap.Decode(stream);
        if (decoded is null) return null;

        // Normalize to RGBA8888 so downstream byte access is predictable.
        if (decoded.ColorType == SKColorType.Rgba8888)
            return decoded;

        var normalized = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.DrawBitmap(decoded, 0, 0);
        }
        decoded.Dispose();
        return normalized;
    }

    /// <summary>
    /// Synthesize a 4×4 RGBA bitmap filled with the given hex color (e.g. "#4488cc" or "4488cc").
    /// Alpha is forced to 255. Returns null if the hex string can't be parsed.
    /// </summary>
    public static SKBitmap? SynthesizeSolidColor(string hex)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return null;

        var bmp = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(r, g, b, 255));
        }
        return bmp;
    }

    /// <summary>
    /// Return a new bitmap flipped vertically. Caller disposes the input.
    /// </summary>
    public static SKBitmap FlipVertically(SKBitmap source)
    {
        var flipped = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(flipped))
        {
            canvas.Scale(1, -1);
            canvas.Translate(0, -source.Height);
            canvas.DrawBitmap(source, 0, 0);
        }
        return flipped;
    }

    /// <summary>
    /// Resize the source to <paramref name="targetW"/> × <paramref name="targetH"/>, optionally tiling
    /// the result <paramref name="tileSize"/>×<paramref name="tileSize"/> times within the target dimensions.
    /// tileSize of 1 produces a plain resize; 2 produces 2×2 tiles of (W/2 × H/2) each, etc.
    /// </summary>
    public static SKBitmap ResizeAndTile(SKBitmap source, int targetW, int targetH, int tileSize)
    {
        if (tileSize < 1) tileSize = 1;
        var canvasBmp = new SKBitmap(new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(canvasBmp);

        if (tileSize == 1)
        {
            using var resized = source.Resize(new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKSamplingOptions.Default);
            if (resized is null)
            {
                canvas.DrawBitmap(source, new SKRect(0, 0, targetW, targetH));
            }
            else
            {
                canvas.DrawBitmap(resized, 0, 0);
            }
            return canvasBmp;
        }

        var tileW = targetW / tileSize;
        var tileH = targetH / tileSize;
        using var tile = source.Resize(new SKImageInfo(tileW, tileH, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKSamplingOptions.Default);
        var src = tile ?? source;
        for (var ty = 0; ty < tileSize; ty++)
        {
            for (var tx = 0; tx < tileSize; tx++)
            {
                canvas.DrawBitmap(src, tx * tileW, ty * tileH);
            }
        }
        return canvasBmp;
    }

    /// <summary>
    /// Copy out the bitmap's pixels as an RGBA8888 byte buffer (width × height × 4 bytes).
    /// </summary>
    public static byte[] GetRgbaBytes(SKBitmap bitmap)
    {
        if (bitmap.ColorType != SKColorType.Rgba8888)
            throw new InvalidOperationException($"Expected RGBA8888, got {bitmap.ColorType}");
        var bytes = new byte[bitmap.ByteCount];
        if (!bitmap.GetPixelSpan().TryCopyTo(bytes))
            throw new InvalidOperationException("Failed to copy bitmap pixel span");
        return bytes;
    }

    /// <summary>
    /// Copy out the bitmap's pixels as BGRA8888 by swapping red and blue channels.
    /// </summary>
    public static byte[] GetBgraBytes(SKBitmap bitmap)
    {
        var rgba = GetRgbaBytes(bitmap);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        }
        return rgba;
    }

    private static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            r = Convert.ToByte(s[..2], 16);
            g = Convert.ToByte(s[2..4], 16);
            b = Convert.ToByte(s[4..6], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
