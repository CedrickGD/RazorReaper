using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
// Disambiguate from Microsoft.Maui.Graphics implicit usings.
using Color = System.Drawing.Color;
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

    /// <summary>Removes the reference snapshot stored under <paramref name="key"/>.</summary>
    void ClearReference(string key);

    /// <summary>
    /// Recaptures the region and compares it to the stored reference. Returns true when the mean
    /// per-channel difference (0–255 scale, alpha ignored) is at or below <paramref name="tolerance"/>.
    /// Returns false when no reference exists or the dimensions differ.
    /// </summary>
    bool MatchesReference(string key, Rectangle region, double tolerance);

    /// <summary>
    /// Most frequent color in the region (quantized to 16 levels per channel, then averaged inside
    /// the winning bucket). Returns <see cref="Color.Empty"/> on capture failure.
    /// </summary>
    Color DominantColor(Rectangle region);

    /// <summary>Mean color of the region. Returns <see cref="Color.Empty"/> on capture failure.</summary>
    Color AverageColor(Rectangle region);
}

/// <summary>Default <see cref="IScreenSampler"/> implementation.</summary>
public sealed class ScreenSampler : IScreenSampler
{
    private readonly ILogger<ScreenSampler> _logger;
    private readonly ConcurrentDictionary<string, ScreenCapture> _references = new(StringComparer.OrdinalIgnoreCase);

    public ScreenSampler(ILogger<ScreenSampler> logger)
    {
        _logger = logger;
    }

    public ScreenCapture CaptureRegion(Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            return new ScreenCapture(0, 0, Array.Empty<byte>());

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
    }

    public bool HasReference(string key)
        => !string.IsNullOrWhiteSpace(key) && _references.ContainsKey(key);

    public void ClearReference(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        _references.TryRemove(key, out _);
    }

    public bool MatchesReference(string key, Rectangle region, double tolerance)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (!_references.TryGetValue(key, out var reference)) return false;

        var current = CaptureRegion(region);
        if (current.IsEmpty) return false;
        if (current.Width != reference.Width || current.Height != reference.Height) return false;

        var a = reference.Bgra;
        var b = current.Bgra;
        long diffSum = 0;
        var pixels = current.Width * current.Height;
        for (var i = 0; i < pixels * 4; i += 4)
        {
            diffSum += Math.Abs(a[i] - b[i]);         // B
            diffSum += Math.Abs(a[i + 1] - b[i + 1]); // G
            diffSum += Math.Abs(a[i + 2] - b[i + 2]); // R
        }
        var meanDiff = diffSum / (double)(pixels * 3);
        return meanDiff <= tolerance;
    }

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
