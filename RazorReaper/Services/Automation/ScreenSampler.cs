using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>A captured screen region: 32-bit BGRA pixels, row-major, top-down.</summary>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
/// <param name="Bgra">Pixel data, 4 bytes per pixel (Blue, Green, Red, Alpha), length = Width*Height*4.</param>
public sealed record ScreenCapture(int Width, int Height, byte[] Bgra)
{
    /// <summary>True when the capture actually holds pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0 || Bgra.Length == 0;
}

/// <summary>
/// A template bitmap for on-screen matching: 32-bit BGRA (row-major, top-down) with an optional
/// per-pixel <see cref="Mask"/> (false = transparent/ignored during matching). Load ARK HUD icons
/// exported as PNG-with-transparency via <see cref="FromFile"/>.
/// </summary>
public sealed record TemplateImage(int Width, int Height, byte[] Bgra, bool[]? Mask = null)
{
    public bool IsEmpty => Width <= 0 || Height <= 0 || Bgra.Length < Width * Height * 4;

    /// <summary>
    /// Loads a PNG/BMP as a template. Pixels with alpha ≤ <paramref name="alphaThreshold"/> are
    /// masked out (ignored when matching) so transparent icon edges never hurt the score.
    /// Returns null on any load failure.
    /// </summary>
    public static TemplateImage? FromFile(string path, byte alphaThreshold = 16)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using var bmp = new Bitmap(path);
            int w = bmp.Width, h = bmp.Height;
            if (w <= 0 || h <= 0) return null;

            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var raw = new byte[stride * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                var bgra = new byte[w * h * 4];
                var mask = new bool[w * h];
                var hasTransparent = false;
                for (int y = 0; y < h; y++)
                {
                    int rowOff = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int si = rowOff + x * 4;       // 32bppArgb little-endian → B,G,R,A
                        int di = (y * w + x) * 4;
                        byte a = raw[si + 3];
                        bgra[di] = raw[si];
                        bgra[di + 1] = raw[si + 1];
                        bgra[di + 2] = raw[si + 2];
                        bgra[di + 3] = a;
                        var keep = a > alphaThreshold;
                        mask[y * w + x] = keep;
                        if (!keep) hasTransparent = true;
                    }
                }
                return new TemplateImage(w, h, bgra, hasTransparent ? mask : null);
            }
            finally { bmp.UnlockBits(data); }
        }
        catch { return null; }
    }
}

/// <summary>
/// Read-only screen sampling via GDI <c>BitBlt</c> (no game process access — reads the composed
/// desktop like a screenshot tool). Coordinates are physical screen pixels; the capture thread is
/// temporarily switched to per-monitor DPI awareness so regions land correctly on mixed-DPI
/// setups. Reference snapshots let features detect "does this part of the screen still look like
/// it did?" (e.g. is the inventory open) without any memory reading.
/// </summary>
public interface IScreenSampler
{
    /// <summary>Captures a screen region. Returns an empty capture (never null) on failure.</summary>
    ScreenCapture CaptureRegion(Rectangle region);

    /// <summary>Captures the region now and stores it as the reference snapshot under <paramref name="key"/>.</summary>
    void CaptureReference(string key, Rectangle region);

    /// <summary>True when a reference snapshot exists for <paramref name="key"/>.</summary>
    bool HasReference(string key);

    /// <summary>
    /// Captures the region again and masks out every pixel that moved since the reference was
    /// taken, so only the parts that stayed put are compared from then on.
    ///
    /// This is what makes a HUD element sitting on top of the live game world usable at all: the
    /// armour icons on ARK's right edge never change, but the desert behind them does, and a
    /// whole-region mean already exceeds any sane tolerance after a quarter-turn of the camera.
    /// Call it with the same element visible but a different background behind it.
    /// </summary>
    /// <param name="kept">Pixels still compared after the call, whether or not it succeeded.</param>
    /// <param name="tolerance">Per-channel difference above which a pixel counts as background.</param>
    /// <returns>False when the refine was discarded (no reference, capture failed, nothing stayed still).</returns>
    bool RefineReferenceMask(string key, Rectangle region, out int kept, byte tolerance = 12);

    /// <summary>Pixels still compared for <paramref name="key"/>, and how many there are in total.</summary>
    (int Kept, int Total) ReferenceMaskInfo(string key);

    /// <summary>
    /// How similar the region looks to its reference right now, 0–100. Null when there is no
    /// reference or the capture failed. This is the same number <see cref="MatchesReference"/>
    /// thresholds on — exposed so a page can show it live instead of leaving the user to guess
    /// why a script does or does not fire.
    /// </summary>
    double? SimilarityPercent(string key, Rectangle region);

    /// <summary>Removes the reference snapshot stored under <paramref name="key"/>.</summary>
    void ClearReference(string key);

    /// <summary>
    /// Recaptures the region and compares it to the stored reference. Returns true when the mean
    /// per-channel difference (0–255 scale, alpha ignored) is at or below <paramref name="tolerance"/>.
    /// Returns false when no reference exists or the dimensions differ.
    /// </summary>
    bool MatchesReference(string key, Rectangle region, double tolerance);

    /// <summary>
    /// Slides <paramref name="template"/> across <paramref name="searchRegion"/> and returns the
    /// screen-space top-left of the best match when its <paramref name="score"/> (0..1, 1 = identical)
    /// is at or above <paramref name="threshold"/>, else null. Masked template pixels are ignored, so
    /// a transparent-background icon matches wherever it appears in the (larger) region — this is what
    /// enables buff-strip / HUD-icon detection at a variable position.
    /// </summary>
    Point? FindTemplate(Rectangle searchRegion, TemplateImage template, double threshold, out double score);

    /// <summary>Convenience: true when <see cref="FindTemplate"/> finds a match at/above the threshold.</summary>
    bool ContainsTemplate(Rectangle searchRegion, TemplateImage template, double threshold);

    /// <summary>
    /// Most frequent color in the region (quantized to 16 levels per channel, then averaged inside
    /// the winning bucket). Returns <see cref="Color.Empty"/> on capture failure.
    /// </summary>
    Color DominantColor(Rectangle region);

    /// <summary>Mean color of the region. Returns <see cref="Color.Empty"/> on capture failure.</summary>
    Color AverageColor(Rectangle region);
}

/// <summary>Default <see cref="IScreenSampler"/> implementation.</summary>
public sealed class ScreenSampler : IScreenSampler, IDisposable
{
    private readonly ILogger<ScreenSampler> _logger;
    private readonly ConcurrentDictionary<string, ScreenCapture> _references = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Preferred capture path. GDI cannot see a game presenting in exclusive fullscreen — it
    /// hands back the desktop instead, silently and with plausible-looking pixels — so the
    /// duplication API goes first and GDI is only the fallback for machines where it fails.
    /// </summary>
    private readonly Lazy<DesktopDuplicator> _duplicator;

    /// <summary>Per-reference pixel filter: false = moved between captures, so it is background.</summary>
    private readonly ConcurrentDictionary<string, bool[]> _referenceMasks = new(StringComparer.OrdinalIgnoreCase);

    public ScreenSampler(ILogger<ScreenSampler> logger)
    {
        _logger = logger;
        _duplicator = new Lazy<DesktopDuplicator>(() => new DesktopDuplicator(_logger));
    }

    /// <summary>Releases the D3D11 device and the duplication the sampler may have created.</summary>
    public void Dispose()
    {
        if (_duplicator.IsValueCreated) _duplicator.Value.Dispose();
    }

    public ScreenCapture CaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return new ScreenCapture(0, 0, Array.Empty<byte>());

        // Duplication first — see the field comment. It reports failure honestly, so the GDI
        // path below stays the fallback rather than a silent second source of truth.
        try
        {
            if (_duplicator.Value.TryCapture(region, out var duped))
                return new ScreenCapture(region.Width, region.Height, duped);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Duplication capture threw — using GDI");
        }

        // Per-monitor DPI awareness for the duration of the capture, restored afterwards, so
        // physical coordinates map 1:1 even when this runs on a thread with a different context.
        IntPtr oldCtx = IntPtr.Zero;
        var ctxSet = false;
        try
        {
            oldCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            ctxSet = oldCtx != IntPtr.Zero;
        }
        catch { /* pre-Win10 1607 — capture still works with the process default context */ }

        var screenDc = IntPtr.Zero;
        var memDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var oldObj = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return new ScreenCapture(0, 0, Array.Empty<byte>());

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return new ScreenCapture(0, 0, Array.Empty<byte>());

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height, // negative → top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                _logger.LogWarning("CreateDIBSection failed (err=0x{Err:X})", Marshal.GetLastWin32Error());
                return new ScreenCapture(0, 0, Array.Empty<byte>());
            }

            oldObj = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.Left, region.Top, SRCCOPY | CAPTUREBLT))
            {
                _logger.LogWarning("BitBlt failed (err=0x{Err:X})", Marshal.GetLastWin32Error());
                return new ScreenCapture(0, 0, Array.Empty<byte>());
            }

            var buffer = new byte[region.Width * region.Height * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return new ScreenCapture(region.Width, region.Height, buffer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Screen capture failed for region {Region}", region);
            return new ScreenCapture(0, 0, Array.Empty<byte>());
        }
        finally
        {
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            if (ctxSet)
            {
                try { SetThreadDpiAwarenessContext(oldCtx); }
                catch { /* restore is best-effort */ }
            }
        }
    }

    public void CaptureReference(string key, Rectangle region)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var capture = CaptureRegion(region);
        if (capture.IsEmpty)
        {
            _logger.LogWarning("Reference capture for '{Key}' produced no pixels", key);
            return;
        }
        _references[key] = capture;
        _referenceMasks.TryRemove(key, out _);
    }

    public bool HasReference(string key)
        => !string.IsNullOrWhiteSpace(key) && _references.ContainsKey(key);

    public bool RefineReferenceMask(string key, Rectangle region, out int kept, byte tolerance = 12)
    {
        kept = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_references.TryGetValue(key, out var reference)) return false;

        var current = CaptureRegion(region);
        if (current.IsEmpty) return false;
        if (current.Width != reference.Width || current.Height != reference.Height) return false;

        var pixels = reference.Width * reference.Height;
        // Start from the mask we already have, so refining twice narrows it further rather than
        // starting over — two different backgrounds catch more of them than one.
        _referenceMasks.TryGetValue(key, out var existing);
        var mask = new bool[pixels];

        var a = reference.Bgra;
        var b = current.Bgra;
        for (var p = 0; p < pixels; p++)
        {
            if (existing is not null && !existing[p]) continue; // already written off as background

            var i = p * 4;
            var stable = Math.Abs(a[i] - b[i]) <= tolerance
                      && Math.Abs(a[i + 1] - b[i + 1]) <= tolerance
                      && Math.Abs(a[i + 2] - b[i + 2]) <= tolerance;
            mask[p] = stable;
            if (stable) kept++;
        }

        // An all-background result would make every later comparison trivially true, which reads
        // as "always matching" rather than as the failure it is. Keep the old mask, and say so —
        // returning the old count as if it were fresh made the caller toast a success.
        if (kept == 0)
        {
            _logger.LogWarning("Mask refine for '{Key}' kept no pixels — ignoring it", key);
            kept = existing?.Count(m => m) ?? 0;
            return false;
        }

        _referenceMasks[key] = mask;
        return true;
    }

    public (int Kept, int Total) ReferenceMaskInfo(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !_references.TryGetValue(key, out var reference))
            return (0, 0);

        var total = reference.Width * reference.Height;
        return _referenceMasks.TryGetValue(key, out var mask)
            ? (mask.Count(m => m), total)
            : (total, total);
    }

    public void ClearReference(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _references.TryRemove(key, out _);
        _referenceMasks.TryRemove(key, out _);
    }

    public bool MatchesReference(string key, Rectangle region, double tolerance)
        => MeanDifference(key, region) is { } meanDiff && meanDiff <= tolerance;

    public double? SimilarityPercent(string key, Rectangle region)
        => MeanDifference(key, region) is { } meanDiff
            ? Math.Clamp(100.0 - meanDiff / 255.0 * 100.0, 0, 100)
            : null;

    /// <summary>
    /// Mean per-channel difference (0–255) between the region now and its reference, over the
    /// unmasked pixels only. Null when there is no reference, the capture failed, or the region
    /// changed size (a resolution change invalidates the snapshot).
    /// </summary>
    private double? MeanDifference(string key, Rectangle region)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (!_references.TryGetValue(key, out var reference)) return null;

        var current = CaptureRegion(region);
        if (current.IsEmpty) return null;
        if (current.Width != reference.Width || current.Height != reference.Height) return null;

        _referenceMasks.TryGetValue(key, out var mask);

        var a = reference.Bgra;
        var b = current.Bgra;
        long diffSum = 0;
        var pixels = current.Width * current.Height;
        var compared = 0;
        for (var p = 0; p < pixels; p++)
        {
            if (mask is not null && !mask[p]) continue;

            var i = p * 4;
            diffSum += Math.Abs(a[i] - b[i]);         // B
            diffSum += Math.Abs(a[i + 1] - b[i + 1]); // G
            diffSum += Math.Abs(a[i + 2] - b[i + 2]); // R
            compared++;
        }

        if (compared == 0) return null;
        return diffSum / (double)(compared * 3);
    }

    public Point? FindTemplate(Rectangle searchRegion, TemplateImage template, double threshold, out double score)
    {
        score = 0;
        if (template is null || template.IsEmpty) return null;
        if (searchRegion.Width < template.Width || searchRegion.Height < template.Height) return null;

        var capture = CaptureRegion(searchRegion);
        if (capture.IsEmpty) return null;

        int sw = capture.Width, sh = capture.Height, tw = template.Width, th = template.Height;
        byte[] sd = capture.Bgra, td = template.Bgra;
        bool[]? mask = template.Mask;

        long valid = 0;
        if (mask is null) valid = (long)tw * th;
        else { for (var i = 0; i < mask.Length; i++) if (mask[i]) valid++; }
        if (valid == 0) return null;

        double best = -1;
        int bestX = -1, bestY = -1;
        for (var oy = 0; oy <= sh - th; oy++)
        {
            for (var ox = 0; ox <= sw - tw; ox++)
            {
                long diff = 0;
                for (var y = 0; y < th; y++)
                {
                    var sBase = ((oy + y) * sw + ox) * 4;
                    var tBase = y * tw * 4;
                    var mBase = y * tw;
                    for (var x = 0; x < tw; x++)
                    {
                        if (mask is not null && !mask[mBase + x]) continue;
                        var si = sBase + x * 4;
                        var ti = tBase + x * 4;
                        diff += Math.Abs(sd[si] - td[ti]);
                        diff += Math.Abs(sd[si + 1] - td[ti + 1]);
                        diff += Math.Abs(sd[si + 2] - td[ti + 2]);
                    }
                }
                var mean = diff / (double)(valid * 3); // 0..255
                var s = 1.0 - mean / 255.0;
                if (s > best) { best = s; bestX = ox; bestY = oy; }
            }
            if (best >= 0.999) break; // effectively perfect — stop early
        }

        score = best < 0 ? 0 : best;
        if (best >= threshold && bestX >= 0)
            return new Point(searchRegion.Left + bestX, searchRegion.Top + bestY);
        return null;
    }

    public bool ContainsTemplate(Rectangle searchRegion, TemplateImage template, double threshold)
        => FindTemplate(searchRegion, template, threshold, out _) is not null;

    public Color DominantColor(Rectangle region)
    {
        var capture = CaptureRegion(region);
        if (capture.IsEmpty) return Color.Empty;

        // Quantize each channel to 4 bits (16 levels), tally buckets, then average the winning
        // bucket's members so the returned color is a real on-screen shade, not a bucket centre.
        var counts = new Dictionary<int, (long Count, long R, long G, long B)>();
        var data = capture.Bgra;
        for (var i = 0; i < data.Length; i += 4)
        {
            int blue = data[i], green = data[i + 1], red = data[i + 2];
            var bucket = ((red >> 4) << 8) | ((green >> 4) << 4) | (blue >> 4);
            counts.TryGetValue(bucket, out var entry);
            counts[bucket] = (entry.Count + 1, entry.R + red, entry.G + green, entry.B + blue);
        }

        var best = counts.MaxBy(kv => kv.Value.Count).Value;
        if (best.Count == 0) return Color.Empty;
        return Color.FromArgb(
            (int)(best.R / best.Count),
            (int)(best.G / best.Count),
            (int)(best.B / best.Count));
    }

    public Color AverageColor(Rectangle region)
    {
        var capture = CaptureRegion(region);
        if (capture.IsEmpty) return Color.Empty;

        long r = 0, g = 0, b = 0;
        var data = capture.Bgra;
        var pixels = capture.Width * capture.Height;
        for (var i = 0; i < data.Length; i += 4)
        {
            b += data[i];
            g += data[i + 1];
            r += data[i + 2];
        }
        return Color.FromArgb((int)(r / pixels), (int)(g / pixels), (int)(b / pixels));
    }

    // ─── Win32 interop ─────────────────────────────────────────────────────────

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint DIB_RGB_COLORS = 0;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // unused for 32bpp BI_RGB but required by the layout
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
