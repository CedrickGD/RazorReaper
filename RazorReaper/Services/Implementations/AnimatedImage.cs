using System.Drawing;
using System.Drawing.Imaging;
using Bitmap = System.Drawing.Bitmap;
using Graphics = System.Drawing.Graphics;
using Image = System.Drawing.Image;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Holds a static image or an animated multi-frame image (GIF, animated PNG) with no quality loss.
///
/// Two storage modes, chosen at load time:
///  • <b>Eager</b> (used for single-frame images): the bitmap is decoded once into memory and the
///    source stream is closed immediately. Fast subsequent FrameAt() calls.
///  • <b>Lazy</b> (used for animated images): the compressed source bytes are kept in memory and
///    each frame is decoded on demand. Memory at any moment ≈ compressed size + one decoded
///    frame, which is the only way to honour the "no quality loss" rule without OOMing on the
///    huge GIFs the user occasionally throws in.
///
/// Disposal frees the eager frames (or the source + cached frame) — nothing else holds them.
/// </summary>
internal sealed class AnimatedImage : IDisposable
{
    private const int PropertyTagFrameDelay = 0x5100;

    // Eager mode storage
    private readonly Bitmap[]? _eagerFrames;

    // Lazy mode storage. The MemoryStream must outlive the Image — GDI+ reads from it lazily on
    // SelectActiveFrame, so closing the stream would invalidate the source.
    private readonly Image? _lazySource;
    private readonly MemoryStream? _lazyStream;
    private readonly object _lazyLock = new();
    private int _lazyCurrentIndex = -1;
    private Bitmap? _lazyCurrentFrame;

    public int FrameCount { get; }
    public int[] DelaysMs { get; }
    public int TotalMs { get; }
    public bool IsAnimated => FrameCount > 1;
    public int Width { get; }
    public int Height { get; }

    private AnimatedImage(Bitmap[] frames, int[] delays)
    {
        _eagerFrames = frames;
        DelaysMs = delays;
        TotalMs = Math.Max(1, delays.Sum());
        FrameCount = frames.Length;
        Width = frames[0].Width;
        Height = frames[0].Height;
    }

    private AnimatedImage(Image source, MemoryStream stream, int frameCount, int[] delays)
    {
        _lazySource = source;
        _lazyStream = stream;
        FrameCount = frameCount;
        DelaysMs = delays;
        TotalMs = Math.Max(1, delays.Sum());
        Width = source.Width;
        Height = source.Height;
    }

    public Bitmap FrameAt(DateTime startUtc, double speedMultiplier = 1.0)
    {
        if (FrameCount <= 1)
        {
            return _eagerFrames != null ? _eagerFrames[0] : GetLazyFrame(0);
        }

        // speedMultiplier > 1 plays faster, < 1 plays slower. Clamp lower so a near-zero
        // multiplier doesn't freeze the GIF entirely; upper bound stops a runaway slider from
        // burning through frames faster than the animation tick can render them.
        var mul = Math.Clamp(speedMultiplier, 0.1, 10.0);
        var elapsedMs = (DateTime.UtcNow - startUtc).TotalMilliseconds * mul;
        var elapsed = (long)Math.Max(0, elapsedMs);
        var t = elapsed % TotalMs;
        long acc = 0;
        int frameIdx = FrameCount - 1;
        for (int i = 0; i < FrameCount; i++)
        {
            acc += DelaysMs[i];
            if (t < acc) { frameIdx = i; break; }
        }
        return _eagerFrames != null ? _eagerFrames[frameIdx] : GetLazyFrame(frameIdx);
    }

    private Bitmap GetLazyFrame(int index)
    {
        lock (_lazyLock)
        {
            if (_lazyCurrentIndex == index && _lazyCurrentFrame != null)
                return _lazyCurrentFrame;

            _lazyCurrentFrame?.Dispose();
            _lazySource!.SelectActiveFrame(FrameDimension.Time, index);
            _lazyCurrentFrame = CopyTo32bpp(_lazySource);
            _lazyCurrentIndex = index;
            return _lazyCurrentFrame;
        }
    }

    public static AnimatedImage Load(string path)
    {
        // Video imports land as `<guid>.frames` directories full of numbered PNGs plus a
        // small manifest. Load that as a multi-frame sequence in eager mode — the PNGs are
        // already at modest resolution and the frame count is capped at import time, so
        // holding them all in memory is fine and avoids the per-frame disk seek cost the
        // overlay's animation tick would otherwise eat.
        if (Directory.Exists(path) && path.EndsWith(".frames", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFromFramesFolder(path);
        }

        // Read fully into a MemoryStream so the source Image isn't tied to an open file handle.
        // GDI+ requires the stream to stay alive for the lifetime of the Image — owning it here
        // means callers can safely move/delete the file once the import has happened.
        var bytes = File.ReadAllBytes(path);
        var ms = new MemoryStream(bytes, writable: false);
        var src = Image.FromStream(ms);

        var fd = FrameDimension.Time;
        var frameCount = SafeGetFrameCount(src, fd);
        // Some malformed/corrupt files report 0 frames — fall through to the single-frame path
        // rather than divide-by-zero a few lines later when computing TotalMs.
        if (frameCount <= 0) frameCount = 1;

        if (frameCount <= 1)
        {
            // Single frame — decode eagerly and drop the source. No quality loss possible.
            try
            {
                var bmp = CopyTo32bpp(src);
                src.Dispose();
                ms.Dispose();
                return new AnimatedImage(new[] { bmp }, new[] { 0 });
            }
            catch
            {
                src.Dispose();
                ms.Dispose();
                throw;
            }
        }

        // Animated — lazy mode. Source + stream live with the AnimatedImage and only one
        // decoded frame is in memory at a time.
        var delays = ReadFrameDelays(src, frameCount);
        return new AnimatedImage(src, ms, frameCount, delays);
    }

    private static AnimatedImage LoadFromFramesFolder(string folder)
    {
        // Frame files are named with zero-padded indices ("0000.png", "0001.png", …). Sorting
        // by filename gives the correct playback order without parsing.
        var frameFiles = Directory.GetFiles(folder, "*.png")
            .Where(f => Path.GetFileNameWithoutExtension(f).All(char.IsDigit))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
        if (frameFiles.Length == 0)
            throw new InvalidDataException($"No frame PNGs found in '{folder}'.");

        // Manifest carries the per-frame delay (uniform across the sequence in our writer).
        // Tiny ad-hoc parse — no JSON dependency needed for two integer fields.
        int delayMs = 80;
        var manifestPath = Path.Combine(folder, "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"frameDelayMs\"\\s*:\\s*(\\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var ms) && ms > 0)
                    delayMs = Math.Max(20, ms);
            }
            catch { /* keep the 80 ms default */ }
        }

        var frames = new Bitmap[frameFiles.Length];
        var delays = new int[frameFiles.Length];
        for (int i = 0; i < frameFiles.Length; i++)
        {
            // ReadAllBytes + Image.FromStream so we don't hold a file handle on each frame —
            // ImportImageAsync may be re-extracting into the same folder later if we ever
            // support video re-encode.
            var bytes = File.ReadAllBytes(frameFiles[i]);
            using var ms = new MemoryStream(bytes, writable: false);
            using var src = Image.FromStream(ms);
            frames[i] = CopyTo32bpp(src);
            delays[i] = delayMs;
        }
        return new AnimatedImage(frames, delays);
    }

    private static int SafeGetFrameCount(Image src, FrameDimension fd)
    {
        try { return src.GetFrameCount(fd); }
        catch { return 1; }
    }

    private static int[] ReadFrameDelays(Image src, int frameCount)
    {
        // Default to 100ms (10 fps) if delays are missing or zero — that's what most browsers do.
        var delays = new int[frameCount];
        for (int i = 0; i < frameCount; i++) delays[i] = 100;

        try
        {
            if (Array.IndexOf(src.PropertyIdList, PropertyTagFrameDelay) < 0) return delays;
            var p = src.GetPropertyItem(PropertyTagFrameDelay);
            if (p?.Value == null) return delays;
            for (int i = 0; i < frameCount && i * 4 + 4 <= p.Value.Length; i++)
            {
                var cs = BitConverter.ToInt32(p.Value, i * 4);
                if (cs <= 0) continue;
                // centiseconds → ms; floor at 20ms so a malformed GIF can't burn the CPU.
                delays[i] = Math.Max(20, cs * 10);
            }
        }
        catch
        {
            // Use the 100ms defaults.
        }
        return delays;
    }

    private static Bitmap CopyTo32bpp(Image src)
    {
        // Decode at the source's native dimensions — no shrinking. This is the "preserve original
        // quality" path the user explicitly asked for. Memory bound comes from lazy mode keeping
        // only one frame live at a time, not from downsampling.
        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(src, 0, 0, src.Width, src.Height);
        return bmp;
    }

    public void Dispose()
    {
        if (_eagerFrames != null)
        {
            foreach (var f in _eagerFrames) f.Dispose();
        }
        lock (_lazyLock)
        {
            _lazyCurrentFrame?.Dispose();
            _lazyCurrentFrame = null;
            _lazySource?.Dispose();
            _lazyStream?.Dispose();
        }
    }
}
