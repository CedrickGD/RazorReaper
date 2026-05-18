using System.Drawing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
// Avoid Microsoft.Maui.* implicit usings colliding with System.Drawing types.
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using Graphics = System.Drawing.Graphics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Singleton owner of the crosshair feature: built-in presets, user profiles (JSON in LocalAppData),
/// imported images, hotkey config, and the lifetime of the Win32 overlay window. The Crosshair page
/// is a thin view over this — it never owns state directly.
/// </summary>
public class CrosshairService : ICrosshairService, IDisposable
{
    private readonly ILogger<CrosshairService> _logger;
    private readonly INotificationService _notifications;

    private readonly string _rootDir;
    private readonly string _imagesDir;
    private readonly string _profilesPath;
    private readonly string _settingsPath;

    private readonly object _lock = new();
    private readonly List<CrosshairProfile> _saved = new();
    private CrosshairProfile _active;
    private bool _overlayActive;

    // Session-level library cache. Populated once at startup (one disk enumeration) and
    // mutated explicitly on Import/Delete — never re-scanned just because the active
    // profile changed. Slider tweaks must not touch disk.
    private readonly object _libraryLock = new();
    private List<string> _libraryCache = new();

    private readonly CrosshairOverlayWindow _overlay;

    // Preview-side image cache. Decoded once per ImagePath; reused across rapid preview re-renders
    // (animation timer ticks). Separate from the overlay's cache because both consumers may need to
    // own a frame at the same instant without cross-thread surprises.
    private readonly object _previewImageLock = new();
    private AnimatedImage? _previewImage;
    private string? _previewImagePath;
    private bool _previewLoadInFlight;
    // Paths that already failed to decode (e.g., a file with .png extension but WEBP bytes).
    // Without this set, EnsurePreviewImage would re-fire Changed → page re-renders → fires
    // EnsurePreviewImage again → re-fails in a tight loop that locks the UI thread.
    private readonly HashSet<string> _previewLoadFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTime _previewStart = DateTime.UtcNow;
    // Cap the preview canvas tight. The editor pane is ~260px on screen — rendering at 1024+ just
    // to scale down in the browser makes the per-tick PNG-encode + base64 work an order of
    // magnitude heavier than necessary.
    private const int PreviewMaxBound = 384;
    // Pooled render canvas — same rationale as in CrosshairOverlayWindow. Without this, the
    // page's ~25 Hz preview tick allocates a fresh LOH bitmap each frame and tanks performance
    // once the user starts cranking the Scale slider.
    private Bitmap? _previewRenderBuffer;
    private int _previewRenderBufferSize;

    // Hotkey state — persisted to settings.json alongside profiles.
    private string _hotkeyLabel = "F8";
    private int _hotkeyVk = 0x77; // F8
    private bool _hotkeyCtrl, _hotkeyAlt, _hotkeyShift;

    public event Action? Changed;
    public event Action? LibraryChanged;
    public event Action? ShowAppRequested;
    public event Action? QuitRequested;

    public bool IsOverlayActive => _overlayActive;
    public CrosshairProfile ActiveProfile { get { lock (_lock) return _active; } }

    public CrosshairService(ILogger<CrosshairService> logger, INotificationService notifications)
    {
        _logger = logger;
        _notifications = notifications;

        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RazorReaper", "Crosshairs");
        _imagesDir = Path.Combine(_rootDir, "Images");
        _profilesPath = Path.Combine(_rootDir, "profiles.json");
        _settingsPath = Path.Combine(_rootDir, "settings.json");

        try
        {
            Directory.CreateDirectory(_imagesDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create crosshair storage dir at {Dir}", _rootDir);
        }

        // One eager disk scan at startup. From here on the library list lives in memory and
        // is only mutated by Import/Delete — no re-enumeration on every Changed event.
        RebuildLibraryCache();

        _active = BuiltIns[0].Clone();
        _active.IsBuiltIn = false;
        _active.Name = "Custom";

        LoadSaved();
        LoadSettings();

        _overlay = new CrosshairOverlayWindow(
            logger,
            onHotkeyToggle: OnHotkeyToggle,
            onTrayShowApp: () => ShowAppRequested?.Invoke(),
            onTrayQuit: () => QuitRequested?.Invoke(),
            isOverlayActive: () => _overlayActive);
        _overlay.Start();
        _overlay.RegisterHotkey(_hotkeyVk, _hotkeyCtrl, _hotkeyAlt, _hotkeyShift);
    }

    private void OnHotkeyToggle()
    {
        ToggleOverlay();
        try
        {
            if (_overlayActive)
                _notifications.ShowInfo("Crosshair overlay enabled.");
            else
                _notifications.ShowInfo("Crosshair overlay disabled.");
        }
        catch { /* notifications can fail in odd shutdown paths */ }
    }

    public IReadOnlyList<CrosshairProfile> GetBuiltInPresets()
    {
        // Return cloned copies — Clone() mints a fresh GUID, so re-pin the original ID so the
        // caller can still compare against the built-in's stable id, and re-set IsBuiltIn so
        // the page can recognise them. The static BuiltIns list itself is never exposed; this
        // prevents any caller from accidentally mutating a base preset.
        return BuiltIns.Select(p =>
        {
            var c = p.Clone();
            c.Id = p.Id;
            c.IsBuiltIn = true;
            return c;
        }).ToList();
    }

    public IReadOnlyList<CrosshairProfile> GetSavedProfiles()
    {
        lock (_lock)
        {
            // Clone() generates a NEW id — but the delete code matches by id, so we must
            // re-pin each clone's id to its source. Without this, "Delete" silently does
            // nothing because the id you click never matches anything in _saved.
            return _saved.Select(p =>
            {
                var c = p.Clone();
                c.Id = p.Id;
                return c;
            }).ToList();
        }
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => _overlay.GetMonitors();

    public void UpdateActive(CrosshairProfile profile)
    {
        // Snapshot — the caller (the page) holds a long-lived reference to its slider-bound
        // profile object. If we stored that reference directly, any field tweak from the page
        // would mutate _active right under any in-flight render path (and worse, if the caller
        // ever handed us a built-in preset reference, we'd be writing to the static list).
        var snapshot = profile.Clone();
        snapshot.Id = profile.Id;
        snapshot.IsBuiltIn = false;
        lock (_lock) { _active = snapshot; }
        if (_overlayActive) _overlay.Show(snapshot);
        Changed?.Invoke();
    }

    public void LoadProfile(CrosshairProfile profile)
    {
        // We always make a *copy* — built-in presets are immutable, and saved profiles shouldn't
        // mutate just because the user is tweaking sliders on the editor page.
        var copy = profile.Clone();
        copy.IsBuiltIn = false;
        lock (_lock) { _active = copy; }
        if (_overlayActive) _overlay.Show(copy);
        Changed?.Invoke();
    }

    public void StartOverlay()
    {
        CrosshairProfile snapshot;
        lock (_lock) { snapshot = _active; _overlayActive = true; }
        _overlay.Show(snapshot);
        Changed?.Invoke();
    }

    public void StopOverlay()
    {
        lock (_lock) { _overlayActive = false; }
        _overlay.Hide();
        Changed?.Invoke();
    }

    public void ToggleOverlay()
    {
        if (_overlayActive) StopOverlay();
        else StartOverlay();
    }

    public async Task<bool> SaveAsAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        CrosshairProfile copy;
        lock (_lock)
        {
            copy = _active.Clone();
            copy.Name = name.Trim();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.IsBuiltIn = false;
            // De-dupe by name — overwrite an existing saved profile if names collide
            _saved.RemoveAll(p => string.Equals(p.Name, copy.Name, StringComparison.OrdinalIgnoreCase));
            _saved.Add(copy);
        }
        var ok = await PersistSavedAsync();
        if (ok) Changed?.Invoke();
        return ok;
    }

    public async Task<bool> DeleteSavedAsync(string id)
    {
        lock (_lock)
        {
            var removed = _saved.RemoveAll(p => p.Id == id);
            if (removed == 0) return false;
        }
        var ok = await PersistSavedAsync();
        if (ok) Changed?.Invoke();
        return ok;
    }

    public byte[] RenderPreviewPng(double phase = 0.25)
    {
        CrosshairProfile snapshot;
        lock (_lock) snapshot = _active.Clone();

        EnsurePreviewImage(snapshot);

        lock (_previewImageLock)
        {
            var frame = _previewImage?.FrameAt(_previewStart);
            // PreviewMaxBound keeps the editor pane render cheap. Source images render at native
            // resolution into a small canvas via DrawImage — bicubic downscale is fast enough that
            // the per-tick cost stays comfortably under one frame's budget.
            var canvasSize = CrosshairRenderer.ComputeCanvasSize(snapshot, frame, PreviewMaxBound);
            if (_previewRenderBuffer == null || _previewRenderBufferSize != canvasSize)
            {
                _previewRenderBuffer?.Dispose();
                _previewRenderBuffer = new Bitmap(canvasSize, canvasSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                _previewRenderBufferSize = canvasSize;
            }
            CrosshairRenderer.RenderInto(_previewRenderBuffer, snapshot, phase, frame);

            using var ms = new MemoryStream();
            _previewRenderBuffer.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
    }

    /// <summary>
    /// Make sure the preview-side AnimatedImage matches the active profile's ImagePath. Loading
    /// happens off the caller's thread — synchronously decoding a large GIF here would freeze the
    /// Blazor render loop. The render proceeds with whatever the cache currently has; once the
    /// background load lands, Changed fires and the page re-renders.
    /// </summary>
    private void EnsurePreviewImage(CrosshairProfile snapshot)
    {
        if (snapshot.Type != CrosshairType.Image
            || string.IsNullOrWhiteSpace(snapshot.ImagePath)
            || !ImageSourceExists(snapshot.ImagePath))
        {
            lock (_previewImageLock)
            {
                if (_previewImage != null)
                {
                    _previewImage.Dispose();
                    _previewImage = null;
                    _previewImagePath = null;
                }
            }
            return;
        }

        string? loadPath = null;
        lock (_previewImageLock)
        {
            var matches = string.Equals(_previewImagePath, snapshot.ImagePath, StringComparison.OrdinalIgnoreCase);
            // Skip the load attempt entirely if this path has already failed once — re-attempting
            // would re-fire Changed and trigger another EnsurePreviewImage on every render tick.
            var alreadyFailed = _previewLoadFailed.Contains(snapshot.ImagePath);
            if (!matches && !_previewLoadInFlight && !alreadyFailed)
            {
                _previewLoadInFlight = true;
                loadPath = snapshot.ImagePath;
            }
        }

        if (loadPath != null)
        {
            Task.Run(() =>
            {
                AnimatedImage? loaded = null;
                Exception? err = null;
                try { loaded = AnimatedImage.Load(loadPath); }
                catch (Exception ex) { err = ex; }

                bool firstFailure = false;
                lock (_previewImageLock)
                {
                    _previewImage?.Dispose();
                    _previewImage = loaded;
                    _previewImagePath = loaded != null ? loadPath : null;
                    _previewLoadInFlight = false;
                    if (loaded == null)
                    {
                        firstFailure = _previewLoadFailed.Add(loadPath);
                    }
                }

                if (err != null && firstFailure)
                {
                    // Log + notify on the FIRST failure only. Subsequent EnsurePreviewImage calls
                    // with the same path now short-circuit via _previewLoadFailed.
                    _logger.LogWarning(err, "Preview image load failed for {Path}", loadPath);
                    try { _notifications.ShowError($"Couldn't load image: {err.Message}"); } catch { }
                }

                // Only re-render when we actually loaded something. A failed load already cleared
                // the cache; firing Changed on failure would just trigger another render cycle
                // (and previously another decode attempt) for no benefit.
                if (loaded != null) Changed?.Invoke();
            });
        }
    }

    public bool HasAnimatedActiveImage
    {
        get
        {
            lock (_previewImageLock)
            {
                return _previewImage?.IsAnimated == true;
            }
        }
    }

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
            var nativeExt = SniffNativeImageExtension(bytes);
            if (nativeExt != null)
            {
                return await SaveImportedAsync(bytes, nativeExt);
            }

            // Video container — extract a frame sequence via Windows.Media, save as a folder of
            // PNGs that the AnimatedImage loader knows how to read. We can't transcode straight to
            // animated GIF because System.Drawing's GIF encoder doesn't let us set per-frame delays,
            // so we use our own on-disk frame-sequence format (a `.frames` directory + manifest).
            if (SniffVideoExtension(bytes) != null)
            {
                _notifications.ShowInfo("Extracting video frames…");
                var framesFolder = await TryExtractVideoFramesToFolderAsync(bytes, fileName);
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
                    using var cropped = AutoCropTransparentBorders(skBitmap);
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

    /// <summary>Crop fully-transparent rows/columns off the edges of an SKBitmap. Returns a new
    /// bitmap containing just the bounding box of opaque pixels, or null if the source is fully
    /// transparent or already tight (in which case the caller keeps using the original). This is
    /// how we make "image bounds == content bounds" for the crosshair preview/overlay.</summary>
    private static SkiaSharp.SKBitmap? AutoCropTransparentBorders(SkiaSharp.SKBitmap src)
    {
        if (src.Width == 0 || src.Height == 0) return null;
        // No alpha channel → every pixel is opaque → nothing to crop.
        if (src.ColorType != SkiaSharp.SKColorType.Rgba8888
            && src.ColorType != SkiaSharp.SKColorType.Bgra8888
            && src.AlphaType == SkiaSharp.SKAlphaType.Opaque)
            return null;

        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;

        // Find the tight bounding box of non-transparent pixels. We pull pixel data once
        // via GetPixels() rather than calling GetPixel() in a hot loop — that API resolves
        // colours through a colour-management pipeline and is several orders of magnitude
        // slower for a per-pixel sweep.
        var pixels = src.Pixels; // SKColor[] — RGBA, premultiplied or unpremultiplied per AlphaType.
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

    /// <summary>Detect common video container formats by magic bytes. Returns the canonical
    /// extension when the payload looks like a video we can ask Windows.Media.Editing to read,
    /// or null otherwise. We don't try to be exhaustive — just the formats people actually drop
    /// onto a crosshair editor.</summary>
    private static string? SniffVideoExtension(byte[] bytes)
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
        {
            return ".webm";
        }
        // AVI — RIFF…AVI .
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x41 && bytes[9] == 0x56 && bytes[10] == 0x49 && bytes[11] == 0x20)
        {
            return ".avi";
        }
        return null;
    }

    // Playback rate for video imports. No cap on total frame count or duration — extraction
    // runs until the end of the clip. 24 fps is the sweet spot between smoothness and disk
    // usage; users who want more can re-encode their source at higher FPS before importing.
    private const int VideoTargetFps = 24;

    /// <summary>Pull a sequence of frames out of an in-memory video payload via Windows.Media.Editing
    /// and write them to a per-import folder named <c>&lt;guid&gt;.frames</c> under the library directory.
    /// Each frame is decoded once via SkiaSharp and re-encoded as PNG so dimensions and pixel format
    /// stay consistent across frames (which the AnimatedImage frame loader relies on).
    /// Returns the absolute folder path on success, or null if extraction failed entirely.</summary>
    private async Task<string?> TryExtractVideoFramesToFolderAsync(byte[] videoBytes, string sourceFileName)
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

            Directory.CreateDirectory(_imagesDir);
            destFolder = Path.Combine(_imagesDir, $"{Guid.NewGuid():N}.frames");
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

    /// <summary>True if <paramref name="path"/> is an extracted video-frame folder we own
    /// (created by <see cref="TryExtractVideoFramesToFolderAsync"/>).</summary>
    private static bool IsFramesFolder(string path)
        => !string.IsNullOrEmpty(path)
           && path.EndsWith(".frames", StringComparison.OrdinalIgnoreCase)
           && Directory.Exists(path);

    /// <summary>True if the path points at something we still know how to render — either a
    /// regular image file or a frames folder. Replaces ad-hoc File.Exists checks scattered
    /// through the service so adding more storage modes later only touches this one method.</summary>
    private static bool ImageSourceExists(string path)
        => !string.IsNullOrEmpty(path) && (File.Exists(path) || IsFramesFolder(path));

    /// <summary>For a regular image path, returns the path itself. For a frames folder, returns
    /// the path of the first frame PNG (used by single-frame consumers like the thumbnail and
    /// default-scale calculator). Returns null if nothing usable is on disk.</summary>
    private static string? FirstFrameFile(string path)
    {
        if (File.Exists(path)) return path;
        if (!IsFramesFolder(path)) return null;
        var first = Directory.EnumerateFiles(path, "*.png")
            .Where(f => Path.GetFileNameWithoutExtension(f).All(char.IsDigit))
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        return first;
    }

    /// <summary>Detect the real format of an image payload by its magic bytes. Returns the
    /// canonical extension (".png" / ".jpg" / ".gif" / ".bmp") on match, or null if the
    /// bytes don't look like one of the formats System.Drawing handles natively. Non-native
    /// formats (WEBP, HEIF, AVIF, TIFF, ICO …) take the SkiaSharp transcode path instead.</summary>
    private static string? SniffNativeImageExtension(byte[] bytes)
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

    public async Task<CrosshairProfile?> ImportWorkshopAsync(string path)
    {
        try
        {
            string? imageCandidate = null;
            string? configCandidate = null;

            if (File.Exists(path))
            {
                var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
                if (AllowedImageExt.Contains(ext)) imageCandidate = path;
                else if (ConfigExt.Contains(ext)) configCandidate = path;
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
                    .FirstOrDefault(f => AllowedImageExt.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
                configCandidate = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => ConfigExt.Contains((Path.GetExtension(f) ?? "").ToLowerInvariant()));
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
                TryApplyConfig(configCandidate, profile);
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

    public IReadOnlyList<string> GetImportedImagePaths()
    {
        // Serve from the in-memory cache. No disk I/O on the hot path — callers like the
        // editor page hit this repeatedly during normal interaction and we don't want a
        // folder enumeration on every slider tweak.
        lock (_libraryLock)
        {
            return _libraryCache.ToList();
        }
    }

    /// <summary>One-time disk scan that seeds the in-memory library cache. Called from the
    /// constructor at startup and (if ever needed) on explicit user-initiated refresh.
    /// Import/Delete mutate the cache directly — they don't go through here.</summary>
    private void RebuildLibraryCache()
    {
        List<string> scanned;
        try
        {
            if (Directory.Exists(_imagesDir))
            {
                var files = Directory.EnumerateFiles(_imagesDir)
                    .Where(f => AllowedImageExt.Contains(Path.GetExtension(f).ToLowerInvariant()));
                // Video imports land as `<guid>.frames` subdirectories — include them so they show up
                // in the library alongside regular image files.
                var folders = Directory.EnumerateDirectories(_imagesDir)
                    .Where(d => d.EndsWith(".frames", StringComparison.OrdinalIgnoreCase));
                scanned = files.Concat(folders)
                    .OrderByDescending(p => Directory.Exists(p)
                        ? Directory.GetLastWriteTimeUtc(p)
                        : File.GetLastWriteTimeUtc(p))
                    .ToList();
            }
            else
            {
                scanned = new List<string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list imported images");
            scanned = new List<string>();
        }
        lock (_libraryLock) { _libraryCache = scanned; }
        LibraryChanged?.Invoke();
    }

    public byte[]? RenderThumbnailPng(string imagePath, int size = 72)
    {
        try
        {
            // For a frames folder, thumbnail off the first frame. For a regular image, use it directly.
            var actual = FirstFrameFile(imagePath);
            if (actual == null) return null;
            using var fs = File.OpenRead(actual);
            using var src = Image.FromStream(fs);
            // For animated images we just thumbnail the first frame — keeps the grid snappy and the
            // library card from ballooning the DOM with megabytes of base64 GIF data.
            using var thumb = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Aspect-preserving letterbox into the square thumb.
            var aspect = (double)src.Width / src.Height;
            int w, h;
            if (aspect >= 1) { w = size; h = (int)(size / aspect); }
            else { h = size; w = (int)(size * aspect); }
            var x = (size - w) / 2;
            var y = (size - h) / 2;
            g.DrawImage(src, x, y, w, h);

            using var ms = new MemoryStream();
            thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render thumbnail for {Path}", imagePath);
            return null;
        }
    }

    public string ImportsFolderPath => _imagesDir;

    public async Task<bool> CopyImportsFolderPathAsync()
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(_imagesDir);
            _notifications.ShowSuccess($"Copied: {_imagesDir}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed");
            _notifications.ShowError($"Couldn't copy path: {ex.Message}");
            return false;
        }
    }

    public bool DeleteImportedImage(string path)
    {
        try
        {
            var isFolder = IsFramesFolder(path);
            if (!isFolder && !File.Exists(path)) return false;

            // Clear preview cache if it points at this file — otherwise we'd hold a file lock
            // (and the file would be re-rendered with a stale image hanging in memory).
            lock (_previewImageLock)
            {
                if (string.Equals(_previewImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _previewImage?.Dispose();
                    _previewImage = null;
                    _previewImagePath = null;
                }
                // Also forget any prior failure for this path — a re-import to the same GUID
                // would be a brand-new asset.
                _previewLoadFailed.Remove(path);
            }

            if (isFolder)
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);

            // Drop from the library cache and notify subscribers.
            lock (_libraryLock)
            {
                _libraryCache.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            }
            LibraryChanged?.Invoke();

            // If the active profile was using this image, drop it so the overlay stops trying to draw a
            // deleted file. We don't switch type — user may want to import a replacement right after.
            bool wasActive = false;
            CrosshairProfile snapshot;
            lock (_lock)
            {
                if (string.Equals(_active.ImagePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _active.ImagePath = null;
                    wasActive = true;
                }
                snapshot = _active;
            }
            if (wasActive)
            {
                _overlay.Update(snapshot, _overlayActive);
                Changed?.Invoke();
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete imported image {Path}", path);
            _notifications.ShowError($"Delete failed: {ex.Message}");
            return false;
        }
    }

    public void UseImportedImage(string path)
    {
        if (!ImageSourceExists(path))
        {
            _notifications.ShowError("That image is no longer on disk.");
            return;
        }
        CrosshairProfile snapshot;
        lock (_lock)
        {
            _active.Type = CrosshairType.Image;
            _active.ImagePath = path;
            // Right-size on each library/picker selection — a 1080p source at 100% scale would render
            // multiple-monitors wide. If the user wants it bigger they bump the Scale slider.
            _active.ImageScale = ComputeDefaultImageScale(path);
            snapshot = _active;
        }
        if (_overlayActive) _overlay.Show(snapshot);
        Changed?.Invoke();
    }

    /// <summary>Pick an ImageScale that renders ~128px on the longest side for fresh selections.
    /// Small source images (≤128px) stay at 100%. Cropped/cleaned-up tiny crosshair PNGs render
    /// untouched; oversized photos shrink to a sensible default the user can scale up from.</summary>
    private int ComputeDefaultImageScale(string path)
    {
        try
        {
            var actual = FirstFrameFile(path);
            if (actual == null) return 100;
            using var fs = File.OpenRead(actual);
            using var src = Image.FromStream(fs);
            var maxDim = Math.Max(src.Width, src.Height);
            if (maxDim <= 128) return 100;
            return Math.Clamp((int)Math.Round(128.0 / maxDim * 100.0), 1, 100);
        }
        catch
        {
            return 100;
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

    private void TryApplyConfig(string configPath, CrosshairProfile profile)
    {
        try
        {
            var text = File.ReadAllText(configPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            // Try JSON first; fall back to simple key=value/INI parsing.
            Dictionary<string, string>? kv = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                kv = FlattenJson(doc.RootElement);
            }
            catch { /* not JSON — fine */ }

            kv ??= ParseKeyValue(text);

            if (kv.TryGetValue("color", out var color)) profile.Color = NormalizeColor(color) ?? profile.Color;
            if (kv.TryGetValue("outlinecolor", out var oc)) profile.OutlineColor = NormalizeColor(oc) ?? profile.OutlineColor;
            if (kv.TryGetValue("outline", out var ot) && int.TryParse(ot, out var oti)) profile.OutlineThickness = Math.Clamp(oti, 0, 6);
            if (kv.TryGetValue("size", out var sz) && int.TryParse(sz, out var szi)) profile.Size = Math.Clamp(szi, 1, 200);
            if (kv.TryGetValue("length", out var ln) && int.TryParse(ln, out var lni)) profile.Size = Math.Clamp(lni, 1, 200);
            if (kv.TryGetValue("thickness", out var th) && int.TryParse(th, out var thi)) profile.Thickness = Math.Clamp(thi, 1, 20);
            if (kv.TryGetValue("gap", out var gp) && int.TryParse(gp, out var gpi)) profile.Gap = Math.Clamp(gpi, 0, 100);
            if (kv.TryGetValue("opacity", out var op) && int.TryParse(op, out var opi)) profile.Opacity = Math.Clamp(opi, 0, 100);
            if (kv.TryGetValue("rotation", out var rt) && int.TryParse(rt, out var rti)) profile.Rotation = ((rti % 360) + 360) % 360;
            if (kv.TryGetValue("dot", out var dt) && bool.TryParse(dt, out var dtb)) profile.ShowDot = dtb;
            if (kv.TryGetValue("centerdot", out var cd) && bool.TryParse(cd, out var cdb)) profile.ShowDot = cdb;
            if (kv.TryGetValue("dotsize", out var ds) && int.TryParse(ds, out var dsi)) profile.DotSize = Math.Clamp(dsi, 1, 50);
            if (kv.TryGetValue("type", out var ty)) profile.Type = ParseType(ty) ?? profile.Type;
            if (kv.TryGetValue("style", out var st)) profile.Type = ParseType(st) ?? profile.Type;
            if (kv.TryGetValue("name", out var nm) && !string.IsNullOrWhiteSpace(nm)) profile.Name = nm.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workshop config parse failed for {Path}", configPath);
        }
    }

    private static Dictionary<string, string> FlattenJson(JsonElement el)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Walk(el);
        return dict;

        void Walk(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(prop.Value);
                    else
                        dict[prop.Name] = prop.Value.ToString() ?? "";
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray()) Walk(item);
            }
        }
    }

    private static Dictionary<string, string> ParseKeyValue(string text)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("[")) continue;
            var eq = line.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            dict[key] = value;
        }
        return dict;
    }

    private static string? NormalizeColor(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("#")) return raw;
        if (raw.Length is 6 or 8 && raw.All(c => Uri.IsHexDigit(c))) return "#" + raw;
        // rgb(r,g,b)
        if (raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var inner = raw[(raw.IndexOf('(') + 1)..raw.LastIndexOf(')')];
            var parts = inner.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        return null;
    }

    private static CrosshairType? ParseType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "cross" or "classic" or "default" or "plus" => CrosshairType.Cross,
            "dot" or "point" => CrosshairType.Dot,
            "circle" or "ring" or "o" => CrosshairType.Circle,
            "t" or "tstyle" or "t-style" => CrosshairType.TStyle,
            "image" or "custom" or "png" => CrosshairType.Image,
            _ => null
        };
    }

    public void SetHotkey(string displayLabel, int virtualKey, bool ctrl, bool alt, bool shift)
    {
        _hotkeyLabel = displayLabel;
        _hotkeyVk = virtualKey;
        _hotkeyCtrl = ctrl;
        _hotkeyAlt = alt;
        _hotkeyShift = shift;

        _overlay.UnregisterHotkey();
        if (virtualKey > 0)
            _overlay.RegisterHotkey(virtualKey, ctrl, alt, shift);

        _ = PersistSettingsAsync();
        Changed?.Invoke();
    }

    public (string Label, int VirtualKey, bool Ctrl, bool Alt, bool Shift) GetHotkey()
        => (_hotkeyLabel, _hotkeyVk, _hotkeyCtrl, _hotkeyAlt, _hotkeyShift);

    // ─── Persistence ──────────────────────────────────────────────────────────────

    private void LoadSaved()
    {
        try
        {
            if (!File.Exists(_profilesPath)) return;
            var json = File.ReadAllText(_profilesPath);
            var list = JsonSerializer.Deserialize<List<CrosshairProfile>>(json);
            if (list != null)
            {
                lock (_lock)
                {
                    _saved.Clear();
                    foreach (var p in list) _saved.Add(p);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crosshair profiles");
        }
    }

    private async Task<bool> PersistSavedAsync()
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            List<CrosshairProfile> snapshot;
            lock (_lock) snapshot = _saved.Select(p => p.Clone()).ToList();
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_profilesPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist crosshair profiles");
            _notifications.ShowError($"Saving profile failed: {ex.Message}");
            return false;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("hotkeyLabel", out var l) && l.ValueKind == JsonValueKind.String) _hotkeyLabel = l.GetString() ?? _hotkeyLabel;
            if (root.TryGetProperty("hotkeyVk", out var vk) && vk.TryGetInt32(out var vki)) _hotkeyVk = vki;
            if (root.TryGetProperty("hotkeyCtrl", out var c)) _hotkeyCtrl = c.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("hotkeyAlt", out var a)) _hotkeyAlt = a.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("hotkeyShift", out var s)) _hotkeyShift = s.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load crosshair settings");
        }
    }

    private async Task PersistSettingsAsync()
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            var payload = new
            {
                hotkeyLabel = _hotkeyLabel,
                hotkeyVk = _hotkeyVk,
                hotkeyCtrl = _hotkeyCtrl,
                hotkeyAlt = _hotkeyAlt,
                hotkeyShift = _hotkeyShift
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist crosshair settings");
        }
    }

    public void Dispose()
    {
        try { _overlay.Dispose(); } catch { /* swallow on shutdown */ }
        lock (_previewImageLock)
        {
            _previewImage?.Dispose();
            _previewImage = null;
            _previewRenderBuffer?.Dispose();
            _previewRenderBuffer = null;
        }
    }

    // Native (System.Drawing-decodable) first, SkiaSharp-decoded fallback next, video
    // containers last. Used to filter library-folder scans and workshop-bundle imports
    // so we don't try to pull in random non-image files. ImportImageAsync transcodes
    // non-native formats to PNG on the import path; video files have a single frame
    // pulled out via Windows.Media.Editing before going through that same path.
    private static readonly HashSet<string> AllowedImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif",
          ".webp", ".tiff", ".tif", ".ico", ".heic", ".heif", ".avif",
          ".mp4", ".webm", ".mov", ".avi", ".mkv", ".m4v" };

    private static readonly HashSet<string> ConfigExt = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".ini", ".cfg", ".txt", ".crosshair" };

    // ─── Built-in presets ─────────────────────────────────────────────────────────

    private static readonly List<CrosshairProfile> BuiltIns = new()
    {
        new CrosshairProfile
        {
            Id = "builtin-valorant",
            Name = "Valorant",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FF66",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 6,
            Thickness = 2,
            Gap = 3,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-cs",
            Name = "CS Classic",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#00FFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 1,
            Gap = 4,
            Opacity = 100,
            ShowDot = false,
        },
        new CrosshairProfile
        {
            Id = "builtin-sniper",
            Name = "Sniper",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF1744",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 40,
            Thickness = 1,
            Gap = 6,
            Opacity = 100,
            ShowDot = true,
            DotSize = 1,
        },
        new CrosshairProfile
        {
            Id = "builtin-dot",
            Name = "Tactical Dot",
            IsBuiltIn = true,
            Type = CrosshairType.Dot,
            Color = "#FFEE00",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            DotSize = 3,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-circle",
            Name = "Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#8b5cf6",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 2,
            Opacity = 90,
            ShowDot = true,
            DotSize = 2,
        },
        new CrosshairProfile
        {
            Id = "builtin-t",
            Name = "T-Style",
            IsBuiltIn = true,
            Type = CrosshairType.TStyle,
            Color = "#FFFFFF",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 8,
            Thickness = 2,
            Gap = 4,
            Opacity = 100,
        },
        new CrosshairProfile
        {
            Id = "builtin-rainbow",
            Name = "Rainbow Pulse",
            IsBuiltIn = true,
            Type = CrosshairType.Cross,
            Color = "#FF0000",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 10,
            Thickness = 3,
            Gap = 5,
            Opacity = 100,
            ShowDot = true,
            DotSize = 2,
            Animation = CrosshairAnimation.Pulse,
            AnimationSpeed = 6,
            Rainbow = true,
        },
        new CrosshairProfile
        {
            Id = "builtin-breath",
            Name = "Breathing Ring",
            IsBuiltIn = true,
            Type = CrosshairType.Circle,
            Color = "#a78bfa",
            OutlineColor = "#000000",
            OutlineThickness = 1,
            Size = 14,
            Thickness = 2,
            Opacity = 100,
            Animation = CrosshairAnimation.Breath,
            AnimationSpeed = 3,
        },
    };
}
