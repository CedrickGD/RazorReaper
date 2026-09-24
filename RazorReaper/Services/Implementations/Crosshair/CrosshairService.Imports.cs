using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Inbound import pipeline — turning an image or video stream into a stored library asset. The
/// heavy lifting is in single-purpose helpers (<see cref="ImageFormatDetection"/>,
/// <see cref="VideoFrameExtractor"/>); this connects them to the service's state (library cache,
/// notifications, persistence). Crosshair codes are <see cref="CrosshairCode"/>, which needs none.
/// </summary>
public partial class CrosshairService
{
    public async Task<string?> ImportImageAsync(Stream source, string fileName)
    {
        try
        {
            // Read the full payload — we need to sniff the real format before deciding how to
            // store it (extensions lie: WEBP files often arrive as .png from clipboard/screenshot
            // tools), and we may need to re-encode via SkiaSharp anyway.
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0)
            {
                _notifications.ShowError(_localizer.T("crosshair.import.error.empty"));
                return null;
            }

            // Native formats — keep as-is. System.Drawing handles these directly, and that
            // preserves animation for GIFs and alpha for PNGs without a transcode round-trip.
            var nativeExt = ImageFormatDetection.SniffNativeImageExtension(bytes);
            if (nativeExt != null)
            {
                // …except that a still PNG still has to be trimmed to its content. The overlay
                // centres the image's CANVAS, so a crosshair sitting off-centre inside a padded
                // canvas renders off-centre on screen — which is exactly the "it's never really in
                // the middle" complaint, and most crosshair PNGs found online are padded like
                // that. The SkiaSharp path below has always cropped; the native path did not, so
                // the commonest format was the one that skipped it. GIF is left alone (cropping it
                // would mean re-encoding the animation) and JPEG has no alpha to crop.
                if (nativeExt == ".png")
                {
                    var trimmed = TryTrimTransparentBorders(bytes);
                    if (trimmed != null) bytes = trimmed;
                }
                return await SaveImportedAsync(bytes, nativeExt);
            }

            // Video container — extract a frame sequence, save as a folder of PNGs that the
            // AnimatedImage loader knows how to read. We can't transcode straight to animated
            // GIF because System.Drawing's GIF encoder doesn't let us set per-frame delays,
            // so we use our own on-disk frame-sequence format (a `.frames` directory + manifest).
            if (ImageFormatDetection.SniffVideoExtension(bytes) != null)
            {
                _notifications.ShowInfo(_localizer.T("crosshair.import.video.extracting"));
                var framesFolder = await _videoExtractor.ExtractToFolderAsync(bytes, fileName, _imagesDir);
                if (framesFolder == null)
                {
                    _notifications.ShowError(_localizer.T("crosshair.import.error.frames", fileName));
                    return null;
                }
                lock (_libraryLock) { _libraryCache.Insert(0, framesFolder); }
                LibraryChanged?.Invoke();
                _notifications.ShowSuccess(_localizer.T("crosshair.import.video.done"));
                return framesFolder;
            }

            // Anything else (WEBP, HEIF/HEIC, AVIF, TIFF, ICO, …) — try SkiaSharp.
            // It decodes a much wider format set; we re-encode the result to PNG so the
            // rest of the pipeline (System.Drawing-based thumbnail / preview / overlay)
            // works without any per-format branching downstream.
            //
            // While we have the pixel buffer, we also auto-crop fully-transparent borders
            // so the image's bounds match its visible content. Crosshair PNGs from random
            // sources often have huge transparent canvases with the design in one corner —
            // without cropping, the preview centres the *canvas*, which makes the visible
            // design look off-centre. Cropping makes "centre the image" mean "centre the
            // crosshair" for the user.
            byte[]? pngBytes = null;
            try
            {
                using var skBitmap = SkiaSharp.SKBitmap.Decode(bytes);
                if (skBitmap != null)
                {
                    using var cropped = ImageFormatDetection.AutoCropTransparentBorders(skBitmap);
                    using var skImage = SkiaSharp.SKImage.FromBitmap(cropped ?? skBitmap);
                    using var skData = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    pngBytes = skData.ToArray();
                }
            }
            catch (Exception skEx)
            {
                _logger.LogWarning(skEx, "SkiaSharp transcode failed for {File}", fileName);
            }

            if (pngBytes == null || pngBytes.Length == 0)
            {
                _notifications.ShowError(_localizer.T("crosshair.import.error.decode", fileName));
                return null;
            }

            return await SaveImportedAsync(pngBytes, ".png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image import failed for {File}", fileName);
            _notifications.ShowError(_localizer.T("crosshair.import.error.image", ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Re-encode <paramref name="bytes"/> with its fully-transparent border removed, or null when
    /// there is nothing to trim (already tight, fully transparent, or undecodable) so the caller
    /// keeps the original bytes untouched rather than paying for a pointless transcode.
    /// </summary>
    private byte[]? TryTrimTransparentBorders(byte[] bytes)
    {
        try
        {
            using var decoded = SkiaSharp.SKBitmap.Decode(bytes);
            if (decoded == null) return null;

            using var cropped = ImageFormatDetection.AutoCropTransparentBorders(decoded);
            if (cropped == null) return null;

            using var image = SkiaSharp.SKImage.FromBitmap(cropped);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            var result = data?.ToArray();
            return result is { Length: > 0 } ? result : null;
        }
        catch (Exception ex)
        {
            // A decode failure here is not fatal — storing the original bytes is still correct,
            // it just leaves the image padded.
            _logger.LogWarning(ex, "Auto-crop of an imported PNG failed; storing it unchanged");
            return null;
        }
    }

    private async Task<string?> SaveImportedAsync(byte[] bytes, string ext)
    {
        Directory.CreateDirectory(_imagesDir);
        var dest = Path.Combine(_imagesDir, $"{Guid.NewGuid():N}{ext}");
        await File.WriteAllBytesAsync(dest, bytes);

        // Append to the in-memory library cache and notify subscribers. Avoids a full
        // disk re-enumeration just to learn about the one file we just wrote.
        lock (_libraryLock) { _libraryCache.Insert(0, dest); }
        LibraryChanged?.Invoke();

        return dest;
    }
}
