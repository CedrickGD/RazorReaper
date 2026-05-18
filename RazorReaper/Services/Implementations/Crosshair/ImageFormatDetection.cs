namespace RazorReaper.Services.Implementations;

/// <summary>
/// Pure helpers for detecting image/video formats and recognising the on-disk shapes the
/// crosshair library stores. Extracted from <see cref="CrosshairService"/> so the service
/// stops mixing format-sniffing with state management.
///
/// Two concepts live here:
///  • <b>Format sniffing</b> — read the magic bytes of a payload to decide whether it's a
///    native image System.Drawing can decode directly, or a video container we need to extract
///    frames out of, or something else (SkiaSharp fallback handled by the caller).
///  • <b>On-disk shapes</b> — the library stores each import as either a single image file
///    or a <c>&lt;guid&gt;.frames</c> folder of PNGs (for video imports). Helpers here let the
///    rest of the service treat both as one thing.
/// </summary>
internal static class ImageFormatDetection
{
    /// <summary>
    /// File extensions the library is willing to import as image content. Native formats land in
    /// System.Drawing's path; the rest go through SkiaSharp; the video extensions go through
    /// Windows.Media.Editing frame extraction.
    /// </summary>
    public static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif",
          ".webp", ".tiff", ".tif", ".ico", ".heic", ".heif", ".avif",
          ".mp4", ".webm", ".mov", ".avi", ".mkv", ".m4v" };

    /// <summary>File extensions accepted as crosshair-config files inside workshop bundles.</summary>
    public static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".ini", ".cfg", ".txt", ".crosshair" };

    /// <summary>True if <paramref name="path"/> is an extracted video-frame folder we own
    /// (named <c>&lt;guid&gt;.frames</c>, created by the video importer).</summary>
    public static bool IsFramesFolder(string path)
        => !string.IsNullOrEmpty(path)
           && path.EndsWith(".frames", StringComparison.OrdinalIgnoreCase)
           && Directory.Exists(path);

    /// <summary>True if the path points at something we still know how to render — either a
    /// regular image file or a frames folder. Centralises the ad-hoc File.Exists checks that
    /// would otherwise be scattered through the service.</summary>
    public static bool ImageSourceExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || IsFramesFolder(path));

    /// <summary>For a regular image path, returns the path itself. For a frames folder, returns
    /// the path of the first numbered frame PNG (used by single-frame consumers like the thumbnail
    /// renderer and default-scale calculator). Returns null if nothing usable is on disk.</summary>
    public static string? FirstFrameFile(string path)
    {
        if (File.Exists(path)) return path;
        if (!IsFramesFolder(path)) return null;
        return Directory.EnumerateFiles(path, "*.png")
            .Where(f => Path.GetFileNameWithoutExtension(f).All(char.IsDigit))
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Detect the real format of an image payload by its magic bytes. Returns the
    /// canonical extension (".png" / ".jpg" / ".gif" / ".bmp") on match, or null if the bytes
    /// don't look like one of the formats System.Drawing handles natively. Non-native formats
    /// (WEBP, HEIF, AVIF, TIFF, ICO …) fall to the SkiaSharp transcode path.</summary>
    public static string? SniffNativeImageExtension(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            return ".png";
        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        // GIF: "GIF87a" or "GIF89a"
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38
            && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61)
            return ".gif";
        // BMP: "BM"
        if (bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ".bmp";
        return null;
    }

    /// <summary>Detect common video container formats by magic bytes. Returns the canonical
    /// extension when the payload looks like something Windows.Media.Editing can read, or null
    /// otherwise. Not exhaustive — just the formats people actually drop on a crosshair editor.</summary>
    public static string? SniffVideoExtension(byte[] bytes)
    {
        if (bytes.Length < 16) return null;
        // MP4 / MOV / M4V — ISO base media file: 4 size bytes, then "ftyp", then a brand.
        if (bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            // "qt  " brand → MOV; everything else → MP4 family.
            if (bytes[8] == 0x71 && bytes[9] == 0x74) return ".mov";
            return ".mp4";
        }
        // WebM / MKV — EBML signature 1A 45 DF A3.
        if (bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
            return ".webm";
        // AVI — RIFF…AVI .
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x41 && bytes[9] == 0x56 && bytes[10] == 0x49 && bytes[11] == 0x20)
            return ".avi";
        return null;
    }

    /// <summary>Crop fully-transparent rows/columns off the edges of an SKBitmap. Returns a new
    /// bitmap containing just the bounding box of opaque pixels, or null if the source is fully
    /// transparent or already tight (in which case the caller keeps using the original). This is
    /// how we make "image bounds == content bounds" for the crosshair preview/overlay — random
    /// crosshair PNGs often have huge transparent canvases with the design in one corner.</summary>
    public static SkiaSharp.SKBitmap? AutoCropTransparentBorders(SkiaSharp.SKBitmap src)
    {
        if (src.Width == 0 || src.Height == 0) return null;
        // No alpha channel → every pixel is opaque → nothing to crop.
        if (src.ColorType != SkiaSharp.SKColorType.Rgba8888
            && src.ColorType != SkiaSharp.SKColorType.Bgra8888
            && src.AlphaType == SkiaSharp.SKAlphaType.Opaque)
            return null;

        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;

        // Pull pixel data once via Pixels rather than calling GetPixel() in a hot loop — that
        // API resolves colours through a colour-management pipeline and is several orders of
        // magnitude slower for a per-pixel sweep.
        var pixels = src.Pixels;
        for (int y = 0; y < src.Height; y++)
        {
            int rowStart = y * src.Width;
            for (int x = 0; x < src.Width; x++)
            {
                if (pixels[rowStart + x].Alpha != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        // Fully transparent — don't crop to a zero-size; let the caller render the original.
        if (maxX < 0) return null;
        // Already tight — no work to do.
        if (minX == 0 && minY == 0 && maxX == src.Width - 1 && maxY == src.Height - 1) return null;

        int newW = maxX - minX + 1;
        int newH = maxY - minY + 1;
        var cropped = new SkiaSharp.SKBitmap(newW, newH, src.ColorType, src.AlphaType);
        using (var canvas = new SkiaSharp.SKCanvas(cropped))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            canvas.DrawBitmap(src, new SkiaSharp.SKRect(minX, minY, maxX + 1, maxY + 1),
                                   new SkiaSharp.SKRect(0, 0, newW, newH));
        }
        return cropped;
    }
}
