using Microsoft.Extensions.Logging;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// One durability row: its read value (null when no read passed the glyph gate) plus where it sat
/// in the region. <paramref name="Top"/> is what lets a caller keep talking about the same armor
/// piece across scans — the list index cannot, because a row that stops rendering shifts every
/// index below it onto the wrong piece.
/// </summary>
public sealed record DurabilityBand(int? Value, int GlyphCount, int Top, int Bottom);

/// <summary>
/// Reads ARK's green durability numbers out of a screen region — the column of figures next
/// to the armor icons — and returns one value per row.
///
/// Windows OCR alone cannot do this: the digits are a ~9px blocky game font over a moving
/// world, and a raw read returns "1 soo", "15" or nothing. The pipeline that survives it,
/// validated offline against a real 1080p frame (expected 1500/120/1500/15000, read 4/4):
///
///  1. Binarize: keep pixels whose green clearly dominates red and blue → black digits on
///     white. Four thresholds, because thin and thick strokes fail at different ones.
///  2. Split into horizontal bands (one per armor piece) on empty pixel rows.
///  3. Segment each band into digit glyphs. Neighbouring digits touch through mid-row
///     anti-aliasing bridges, but a separator column is empty in the band's top two AND
///     bottom two ink rows — a 0's hole is covered by its arcs, a bridge is not.
///  4. Read stacked band images (plain and re-kerned with the glyphs spaced apart), with
///     the classic confusions mapped back (o→0, s→5, …).
///  5. The gate that makes it safe: OCR drops glyphs but never invents them, so a read
///     only counts when its digit count equals the segmented glyph count. Majority vote
///     among those; a band with no conforming read reports null rather than a wrong number.
/// </summary>
public sealed class DurabilityReader
{
    // (minGreen, margin) pairs — chosen by an offline parameter sweep on real footage.
    private static readonly (int MinGreen, int Margin)[] Variants =
    {
        (100, 30), (100, 55), (130, 80), (130, 105),
    };

    private const int ReferenceMinGreen = 110;
    private const int ReferenceMargin = 35;

    private const int Scale = 8;      // upscale factor before OCR
    private const int Pad = 30;       // white border the OCR engine needs to see a "page"
    private const int BandGap = 26;   // px between stacked bands, unscaled output pixels
    private const int GlyphGap = 4;   // px inserted between re-kerned glyphs, pre-scale

    private readonly IScreenSampler _sampler;
    private readonly IScreenOcr _ocr;
    private readonly ILogger<DurabilityReader> _logger;

    public DurabilityReader(IScreenSampler sampler, IScreenOcr ocr, ILogger<DurabilityReader> logger)
    {
        _sampler = sampler;
        _ocr = ocr;
        _logger = logger;
    }

    public Task<IReadOnlyList<DurabilityBand>> ReadAsync(Rectangle region, CancellationToken ct = default)
        => ReadAsync(_sampler.CaptureRegion(region), ct);

    /// <summary>Buffer-based overload — this is also what the --flaktest harness feeds from a PNG.</summary>
    public async Task<IReadOnlyList<DurabilityBand>> ReadAsync(ScreenCapture capture, CancellationToken ct = default)
    {
        if (capture.IsEmpty)
        {
            _logger.LogDebug("DurabilityReader: capture came back empty");
            return Array.Empty<DurabilityBand>();
        }

        int w = capture.Width, h = capture.Height;

        // Band boundaries come from one mid-strength reference mask so every variant talks
        // about the same rows; per-variant masks re-measure their own ink inside each band.
        var reference = Binarize(capture.Bgra, w, h, ReferenceMinGreen, ReferenceMargin);
        var bands = FindBands(reference, w, h);
        if (bands.Count == 0)
        {
            // Distinguishes "wrong region" from "capture came back black", which is what a GDI
            // grab returns while the game presents in independent-flip fullscreen. Behind the
            // level check on purpose — it walks every pixel purely to build the message.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                int maxG = 0; long sum = 0;
                for (var p = 0; p < w * h; p++) { var g = capture.Bgra[p * 4 + 1]; if (g > maxG) maxG = g; sum += g; }
                _logger.LogDebug("DurabilityReader: no bands — maxGreen={Max} avgGreen={Avg:0.0}", maxG, sum / (double)(w * h));
            }
            return Array.Empty<DurabilityBand>();
        }

        var masks = Variants.Select(v => Binarize(capture.Bgra, w, h, v.MinGreen, v.Margin)).ToArray();

        var reads = new List<string>[bands.Count];
        var glyphCounts = new List<int>[bands.Count];
        for (var i = 0; i < bands.Count; i++) { reads[i] = new(); glyphCounts[i] = new(); }

        foreach (var mask in masks)
        {
            ct.ThrowIfCancellationRequested();

            var glyphs = bands.Select(b => SegmentGlyphs(mask, w, b.Top, b.Bottom)).ToArray();
            for (var i = 0; i < bands.Count; i++)
            {
                if (glyphs[i].Count > 0) glyphCounts[i].Add(glyphs[i].Count);
            }

            foreach (var kern in new[] { false, true })
            {
                var (image, imgW, imgH, centers) = BuildStack(mask, w, bands, glyphs, kern);
                if (image is null) continue;

                foreach (var line in await _ocr.ReadLinesAsync(image, imgW, imgH))
                {
                    // Single digits count: a piece at 7 durability is exactly the case that must
                    // trigger a swap, and the glyph-count gate already rejects a stray fragment
                    // (a 4-glyph row can never be satisfied by a 1-digit read).
                    var digits = ExtractDigits(line.Text);
                    if (digits.Length == 0) continue;

                    var band = NearestBand(centers, line.CenterY);
                    if (band >= 0) reads[band].Add(digits);
                }
            }
        }

        var result = new DurabilityBand[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            var modal = Modal(glyphCounts[i]);
            result[i] = new DurabilityBand(Vote(reads[i], modal), modal, bands[i].Top, bands[i].Bottom);
            _logger.LogDebug("Durability row {Row}: glyph counts [{Counts}], reads [{Reads}] -> {Value}",
                i + 1, string.Join(",", glyphCounts[i]), string.Join(",", reads[i]), result[i].Value);
        }

        // Second pass only for unresolved bands: a band alone in its image reads more
        // reliably than the stack (nothing for the line segmentation to mix up), but each
        // image is another engine call — so the holdouts alone pay for it.
        for (var i = 0; i < bands.Count; i++)
        {
            if (result[i].Value is not null) continue;
            ct.ThrowIfCancellationRequested();

            var extra = new List<string>();
            foreach (var mask in masks)
            {
                var glyphs = new[] { SegmentGlyphs(mask, w, bands[i].Top, bands[i].Bottom) };
                foreach (var kern in new[] { false, true })
                {
                    var (image, imgW, imgH, _) = BuildStack(mask, w, new List<(int, int)> { bands[i] }, glyphs, kern);
                    if (image is null) continue;

                    foreach (var line in await _ocr.ReadLinesAsync(image, imgW, imgH))
                    {
                        var digits = ExtractDigits(line.Text);
                        if (digits.Length > 0) extra.Add(digits);
                    }
                }
            }
            result[i] = result[i] with { Value = Vote(extra, result[i].GlyphCount) };
            _logger.LogDebug("Durability row {Row} pass 2: reads [{Reads}] -> {Value}",
                i + 1, string.Join(",", extra), result[i].Value);
        }

        return result;
    }

    // ── Pipeline steps ──────────────────────────────────────────────────────────────────

    private static bool[] Binarize(byte[] bgra, int w, int h, int minGreen, int margin)
    {
        var mask = new bool[w * h];
        for (var p = 0; p < w * h; p++)
        {
            var i = p * 4;
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            mask[p] = g > minGreen && g - Math.Max(r, b) > margin;
        }
        return mask;
    }

    private static List<(int Top, int Bottom)> FindBands(bool[] mask, int w, int h)
    {
        var bands = new List<(int, int)>();
        int start = -1, last = -1;
        for (var y = 0; y < h; y++)
        {
            var ink = false;
            for (var x = 0; x < w; x++)
            {
                if (mask[y * w + x]) { ink = true; break; }
            }
            if (!ink) continue;

            if (start < 0) { start = y; }
            else if (y - last > 3)
            {
                if (last - start >= 3) bands.Add((start, last));
                start = y;
            }
            last = y;
        }
        if (start >= 0 && last - start >= 3) bands.Add((start, last));
        return bands;
    }

    /// <summary>Glyph column ranges of one band, cut at columns empty in the top and bottom ink rows.</summary>
    private static List<(int Left, int Right)> SegmentGlyphs(bool[] mask, int w, int top, int bottom)
    {
        int inkTop = -1, inkBot = -1;
        for (var y = top; y <= bottom; y++)
        {
            var has = false;
            for (var x = 0; x < w; x++)
            {
                if (mask[y * w + x]) { has = true; break; }
            }
            if (has) { if (inkTop < 0) inkTop = y; inkBot = y; }
        }

        var glyphs = new List<(int, int)>();
        if (inkTop < 0) return glyphs;

        int t2 = Math.Min(inkTop + 1, bottom), b2 = Math.Max(inkBot - 1, top);
        var start = -1;
        for (var x = 0; x < w; x++)
        {
            var separator = !mask[inkTop * w + x] && !mask[t2 * w + x]
                         && !mask[b2 * w + x] && !mask[inkBot * w + x];
            if (!separator)
            {
                if (start < 0) start = x;
            }
            else if (start >= 0) { glyphs.Add((start, x - 1)); start = -1; }
        }
        if (start >= 0) glyphs.Add((start, w - 1));
        return glyphs;
    }

    /// <summary>
    /// Renders the bands as black-on-white BGRA, stacked vertically, upscaled by
    /// <see cref="Scale"/>. Returns the image, its size, and each band's centre Y so OCR
    /// lines can be mapped back. Nearest-neighbour scaling on purpose: the engine copes
    /// better with crisp blocks than with smeared 9px strokes.
    /// </summary>
    private static (byte[]? Image, int W, int H, double[] Centers) BuildStack(
        bool[] mask, int w, List<(int Top, int Bottom)> bands,
        List<(int Left, int Right)>[] glyphs, bool kern)
    {
        var widths = new int[bands.Count];
        var maxW = 0;
        for (var i = 0; i < bands.Count; i++)
        {
            if (kern)
            {
                if (glyphs[i].Count == 0) { widths[i] = 0; continue; }
                widths[i] = glyphs[i].Sum(g => g.Right - g.Left + 1) + (glyphs[i].Count - 1) * GlyphGap;
            }
            else
            {
                widths[i] = w;
            }
            maxW = Math.Max(maxW, widths[i]);
        }
        if (maxW == 0) return (null, 0, 0, Array.Empty<double>());

        var imgW = maxW * Scale + 2 * Pad;
        var imgH = bands.Sum(b => (b.Bottom - b.Top + 1) * Scale) + (bands.Count - 1) * BandGap + 2 * Pad;
        var image = new byte[imgW * imgH * 4];
        Array.Fill(image, (byte)0xFF); // white, alpha included — the engine ignores alpha

        var centers = new double[bands.Count];
        var cy = Pad;
        for (var i = 0; i < bands.Count; i++)
        {
            var bandH = bands[i].Bottom - bands[i].Top + 1;
            if (widths[i] > 0)
            {
                if (kern)
                {
                    var dx = 0;
                    foreach (var (left, right) in glyphs[i])
                    {
                        BlitScaled(mask, w, left, right, bands[i].Top, bands[i].Bottom, image, imgW, Pad + dx * Scale, cy);
                        dx += right - left + 1 + GlyphGap;
                    }
                }
                else
                {
                    BlitScaled(mask, w, 0, w - 1, bands[i].Top, bands[i].Bottom, image, imgW, Pad, cy);
                }
                centers[i] = cy + bandH * Scale / 2.0;
            }
            else
            {
                centers[i] = -1;
            }
            cy += bandH * Scale + BandGap;
        }

        // The engine was trained on print, not on 8×8 pixel blocks: the validated offline
        // pipeline scaled with bicubic smoothing and read reliably, the same shapes as
        // hard blocks often return nothing at all. Two box-blur passes ≈ a Gaussian and
        // turn the blocks into strokes with soft edges.
        BoxBlur(image, imgW, imgH);
        BoxBlur(image, imgW, imgH);

        return (image, imgW, imgH, centers);
    }

    /// <summary>3×3 box blur over the grayscale image (all channels equal, alpha untouched).</summary>
    private static void BoxBlur(byte[] image, int w, int h)
    {
        var src = new byte[w * h];
        for (var p = 0; p < w * h; p++) src[p] = image[p * 4];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int sum = 0, n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        sum += src[yy * w + xx];
                        n++;
                    }
                }
                var v = (byte)(sum / n);
                var i = (y * w + x) * 4;
                image[i] = v; image[i + 1] = v; image[i + 2] = v;
            }
        }
    }

    /// <summary>Draws mask pixels [left..right]×[top..bottom] as Scale×Scale black blocks.</summary>
    private static void BlitScaled(
        bool[] mask, int w, int left, int right, int top, int bottom,
        byte[] image, int imgW, int dstX, int dstY)
    {
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (!mask[y * w + x]) continue;

                var bx = dstX + (x - left) * Scale;
                var by = dstY + (y - top) * Scale;
                for (var sy = 0; sy < Scale; sy++)
                {
                    var row = (by + sy) * imgW + bx;
                    for (var sx = 0; sx < Scale; sx++)
                    {
                        var idx = (row + sx) * 4;
                        image[idx] = 0; image[idx + 1] = 0; image[idx + 2] = 0;
                    }
                }
            }
        }
    }

    // ── Reading helpers ─────────────────────────────────────────────────────────────────

    /// <summary>Digits of an OCR line after mapping the classic confusions back (o→0, s→5, …).</summary>
    private static string ExtractDigits(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            var mapped = c switch
            {
                'o' or 'O' => '0',
                's' or 'S' => '5',
                'l' or 'I' or '|' => '1',
                'b' or 'B' => '8',
                'z' or 'Z' => '2',
                'g' or 'G' => '6',
                'q' or 'Q' => '9',
                _ => c,
            };
            if (mapped is >= '0' and <= '9') buffer[n++] = mapped;
        }
        return new string(buffer[..n]);
    }

    private static int NearestBand(double[] centers, double y)
    {
        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < centers.Length; i++)
        {
            if (centers[i] < 0) continue;
            var d = Math.Abs(centers[i] - y);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Most frequent glyph count; ties go to the LARGER count. A thick binarization fuses
    /// touching digits (5 glyphs read as 4), while splitting one digit in two would need a
    /// column empty at both the top and bottom arcs mid-glyph — far rarer. So when the
    /// variants disagree evenly, the higher count is the honest one.
    /// </summary>
    private static int Modal(List<int> counts) =>
        counts.Count == 0
            ? 0
            : counts.GroupBy(c => c)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .First().Key;

    /// <summary>
    /// Majority vote among the reads whose digit count matches the glyph count. OCR loses
    /// glyphs but never invents them — "1500" comes back as "15", never the other way — so
    /// a length mismatch is proof of a bad read, and no conforming read means null: for a
    /// bot that presses keys, no number is strictly better than a wrong one.
    /// </summary>
    private static int? Vote(List<string> reads, int glyphCount)
    {
        if (glyphCount == 0) return null;
        var winner = reads
            .Where(r => r.Length == glyphCount)
            .GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;
        return winner is not null && int.TryParse(winner, out var v) ? v : null;
    }
}
