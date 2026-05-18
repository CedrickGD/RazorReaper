using Microsoft.Extensions.Logging;
using RazorReaper.Models;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Inbound import pipelines — turning external bytes (a stream, a workshop folder, a share-code
/// string) into a stored library asset and/or a usable <see cref="CrosshairProfile"/>. Each
/// public method here delegates the heavy lifting to a single-purpose helper
/// (<see cref="ImageFormatDetection"/>, <see cref="VideoFrameExtractor"/>,
/// <see cref="CrosshairWorkshopConfigParser"/>, <see cref="CrosshairCodeParsers"/>) and just
/// connects the pieces to the service's state (library cache, notifications, persistence).
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
                _notifications.ShowError("Image file is empty.");
                return null;
            }

            // Native formats — keep as-is. System.Drawing handles these directly, and that
            // preserves animation for GIFs and alpha for PNGs without a transcode round-trip.
            var nativeExt = ImageFormatDetection.SniffNativeImageExtension(bytes);
            if (nativeExt != null)
            {
                return await SaveImportedAsync(bytes, nativeExt);
            }

            // Video container — extract a frame sequence, save as a folder of PNGs that the
            // AnimatedImage loader knows how to read. We can't transcode straight to animated
            // GIF because System.Drawing's GIF encoder doesn't let us set per-frame delays,
            // so we use our own on-disk frame-sequence format (a `.frames` directory + manifest).
            if (ImageFormatDetection.SniffVideoExtension(bytes) != null)
            {
                _notifications.ShowInfo("Extracting video frames…");
                var framesFolder = await _videoExtractor.ExtractToFolderAsync(bytes, fileName, _imagesDir);
                if (framesFolder == null)
                {
                    _notifications.ShowError($"Couldn't extract frames from '{fileName}'. Try converting it to PNG/GIF first.");
                    return null;
                }
                lock (_libraryLock) { _libraryCache.Insert(0, framesFolder); }
                LibraryChanged?.Invoke();
                _notifications.ShowSuccess("Video imported.");
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
                _notifications.ShowError($"Couldn't decode '{fileName}' — unrecognised image format.");
                return null;
            }

            return await SaveImportedAsync(pngBytes, ".png");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Image import failed for {File}", fileName);
            _notifications.ShowError($"Image import failed: {ex.Message}");
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

    public async Task<CrosshairProfile?> ImportWorkshopAsync(string path)
    {
        try
        {
            string? imageCandidate = null;
            string? configCandidate = null;

            if (File.Exists(path))
            {
                var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (ImageFormatDetection.AllowedImageExtensions.Contains(ext)) imageCandidate = path;
                else if (ImageFormatDetection.ConfigExtensions.Contains(ext)) configCandidate = path;
                else
                {
                    // Some workshop bundles are .zip-ish — try as folder if it's a dir, else give up.
                    _notifications.ShowWarning($"Unrecognized workshop file type: {ext}");
                    return null;
                }
            }
            else if (Directory.Exists(path))
            {
                imageCandidate = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => ImageFormatDetection.AllowedImageExtensions.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
                configCandidate = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => ImageFormatDetection.ConfigExtensions.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
            }
            else
            {
                _notifications.ShowError("Workshop path doesn't exist.");
                return null;
            }

            // Start from the current active profile (so things like monitor/offset/hotkey carry over),
            // then layer image + parsed config on top.
            var profile = ActiveProfile.Clone();
            profile.Id = Guid.NewGuid().ToString("N");
            profile.IsBuiltIn = false;
            profile.Name = Path.GetFileNameWithoutExtension(imageCandidate ?? configCandidate ?? path);
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "Imported";

            if (imageCandidate != null)
            {
                await using var fs = File.OpenRead(imageCandidate);
                var stored = await ImportImageAsync(fs, Path.GetFileName(imageCandidate));
                if (stored != null)
                {
                    profile.Type = CrosshairType.Image;
                    profile.ImagePath = stored;
                    profile.ImageScale = ComputeDefaultImageScale(stored);
                }
            }

            if (configCandidate != null)
            {
                if (!CrosshairWorkshopConfigParser.TryApplyConfig(configCandidate, profile, out var parseError) && parseError != null)
                {
                    _logger.LogWarning(parseError, "Workshop config parse failed for {Path}", configCandidate);
                }
            }

            if (imageCandidate == null && configCandidate == null)
            {
                _notifications.ShowError("No usable image or config found in workshop file.");
                return null;
            }

            return profile;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workshop import failed for {Path}", path);
            _notifications.ShowError($"Workshop import failed: {ex.Message}");
            return null;
        }
    }

    public CrosshairProfile? ImportFromCode(string code)
    {
        try
        {
            var parsed = CrosshairCodeParsers.TryParse(code, ActiveProfile);
            if (parsed == null)
            {
                _notifications.ShowError("Couldn't recognise that code (Valorant or CSGO format expected).");
                return null;
            }
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Code import failed");
            _notifications.ShowError($"Code import failed: {ex.Message}");
            return null;
        }
    }
}
