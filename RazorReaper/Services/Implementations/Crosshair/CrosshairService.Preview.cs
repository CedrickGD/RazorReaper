using System.Drawing;
using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using Color = System.Drawing.Color;
using Image = System.Drawing.Image;
using Graphics = System.Drawing.Graphics;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Editor-side preview rendering. The page calls <see cref="RenderPreviewPng"/> on a timer; this
/// partial keeps the preview's own image cache, render buffer, and the async loader that decodes
/// image/animation sources without blocking the Blazor render loop.
/// </summary>
public partial class CrosshairService
{
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
            || !ImageFormatDetection.ImageSourceExists(snapshot.ImagePath))
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
}
