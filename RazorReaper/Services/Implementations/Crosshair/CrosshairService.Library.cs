using System.Drawing;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using Image = System.Drawing.Image;
using Graphics = System.Drawing.Graphics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// In-memory library cache and the operations that read/mutate it: enumerate, delete, set
/// active, render thumbnails. Imports themselves live in <c>CrosshairService.Imports.cs</c>;
/// this partial only deals with the state of "what's already on disk under the imports folder".
/// </summary>
public partial class CrosshairService
{
    public string ImportsFolderPath => _imagesDir;

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
                    .Where(f => ImageFormatDetection.AllowedImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
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
            var actual = ImageFormatDetection.FirstFrameFile(imagePath);
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
            var isFolder = ImageFormatDetection.IsFramesFolder(path);
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
                ScheduleActiveProfileSave();
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
        if (!ImageFormatDetection.ImageSourceExists(path))
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
        ScheduleActiveProfileSave();
        Changed?.Invoke();
    }

    /// <summary>Pick an ImageScale that renders ~128px on the longest side for fresh selections.
    /// Small source images (≤128px) stay at 100%. Cropped/cleaned-up tiny crosshair PNGs render
    /// untouched; oversized photos shrink to a sensible default the user can scale up from.</summary>
    private int ComputeDefaultImageScale(string path)
    {
        try
        {
            var actual = ImageFormatDetection.FirstFrameFile(path);
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
}
