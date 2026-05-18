using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Pulls a sequence of frames out of an in-memory video payload via Windows.Media.Editing and
/// writes them to a <c>&lt;guid&gt;.frames</c> folder under a target directory. Each frame is
/// decoded once via SkiaSharp and re-encoded as PNG so dimensions and pixel format stay
/// consistent across frames (which the AnimatedImage frame loader relies on).
///
/// Extracted from <see cref="CrosshairService"/> so the service no longer owns ~115 lines of
/// Windows.Media interop. Stateless aside from the injected logger.
/// </summary>
internal sealed class VideoFrameExtractor
{
    // Playback rate for video imports. No cap on total frame count or duration — extraction
    // runs until the end of the clip. 24 fps is the sweet spot between smoothness and disk
    // usage; users who want more can re-encode their source at higher FPS before importing.
    private const int VideoTargetFps = 24;

    private readonly ILogger _logger;

    public VideoFrameExtractor(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Extract all frames from <paramref name="videoBytes"/> at the target FPS, writing
    /// each as a numbered PNG into a fresh <c>&lt;guid&gt;.frames</c> folder under
    /// <paramref name="destinationRoot"/>. Returns the absolute folder path on success, or null
    /// if extraction failed entirely (no frames written).</summary>
    public async Task<string?> ExtractToFolderAsync(
        byte[] videoBytes,
        string sourceFileName,
        string destinationRoot)
    {
        var sourceExt = Path.GetExtension(sourceFileName);
        if (string.IsNullOrEmpty(sourceExt)) sourceExt = ".mp4";
        var tempPath = Path.Combine(Path.GetTempPath(), $"rr_video_import_{Guid.NewGuid():N}{sourceExt}");
        string? destFolder = null;
        try
        {
            await File.WriteAllBytesAsync(tempPath, videoBytes);

            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(tempPath);
            var clip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(storageFile);
            var composition = new Windows.Media.Editing.MediaComposition();
            composition.Clips.Add(clip);

            var duration = clip.OriginalDuration;
            var frameDelayMs = (int)Math.Round(1000.0 / VideoTargetFps);
            // Full clip — no max frame count, no max duration. The user explicitly asked for
            // uncapped video imports; if a 10-minute video pulls 14,000 frames, that's their
            // call (and their disk).
            var frameCount = Math.Max(1, (int)Math.Ceiling(duration.TotalMilliseconds / frameDelayMs));

            Directory.CreateDirectory(destinationRoot);
            destFolder = Path.Combine(destinationRoot, $"{Guid.NewGuid():N}.frames");
            Directory.CreateDirectory(destFolder);

            int? frameWidth = null, frameHeight = null;
            int written = 0;
            for (int i = 0; i < frameCount; i++)
            {
                var ts = TimeSpan.FromMilliseconds((double)i * frameDelayMs);
                if (ts > duration) break;

                byte[]? rawFrame = null;
                try
                {
                    var thumbnail = await composition.GetThumbnailAsync(
                        ts, 0, 0, Windows.Media.Editing.VideoFramePrecision.NearestFrame);
                    using var dataReader = new Windows.Storage.Streams.DataReader(thumbnail.GetInputStreamAt(0));
                    var size = (uint)thumbnail.Size;
                    await dataReader.LoadAsync(size);
                    rawFrame = new byte[size];
                    dataReader.ReadBytes(rawFrame);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Video frame {Index} extraction failed", i);
                    continue;
                }

                try
                {
                    using var skBitmap = SkiaSharp.SKBitmap.Decode(rawFrame);
                    if (skBitmap == null) continue;
                    // Lock all frames to the first frame's dimensions. Some decoders return mildly
                    // different sizes per frame and that would make the renderer flicker between
                    // canvas allocations.
                    frameWidth ??= skBitmap.Width;
                    frameHeight ??= skBitmap.Height;
                    SkiaSharp.SKBitmap? normalized = null;
                    try
                    {
                        if (skBitmap.Width != frameWidth || skBitmap.Height != frameHeight)
                        {
                            normalized = new SkiaSharp.SKBitmap(frameWidth!.Value, frameHeight!.Value, skBitmap.ColorType, skBitmap.AlphaType);
                            using var canvas = new SkiaSharp.SKCanvas(normalized);
                            canvas.Clear(SkiaSharp.SKColors.Transparent);
                            canvas.DrawBitmap(skBitmap,
                                new SkiaSharp.SKRect(0, 0, skBitmap.Width, skBitmap.Height),
                                new SkiaSharp.SKRect(0, 0, frameWidth.Value, frameHeight.Value));
                        }
                        var encodeFrom = normalized ?? skBitmap;
                        using var skImage = SkiaSharp.SKImage.FromBitmap(encodeFrom);
                        using var skData = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        var framePath = Path.Combine(destFolder, $"{i:0000}.png");
                        await File.WriteAllBytesAsync(framePath, skData.ToArray());
                        written++;
                    }
                    finally
                    {
                        normalized?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Video frame {Index} encode failed", i);
                }
            }

            if (written == 0)
            {
                try { Directory.Delete(destFolder, true); } catch { /* best-effort */ }
                return null;
            }

            // Tiny manifest: just the frame delay. The AnimatedImage loader reads it back when
            // building the playback timeline. Lives alongside the PNGs so the folder is self-contained.
            var manifestPath = Path.Combine(destFolder, "manifest.json");
            await File.WriteAllTextAsync(manifestPath,
                $"{{\"frameDelayMs\":{frameDelayMs},\"frameCount\":{written}}}");

            return destFolder;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video frame extraction failed for {File}", sourceFileName);
            if (destFolder != null) { try { Directory.Delete(destFolder, true); } catch { /* best-effort */ } }
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }
}
