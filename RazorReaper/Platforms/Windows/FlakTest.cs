using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RazorReaper.Services.Automation;

namespace RazorReaper.WinUI;

/// <summary>
/// Headless verification mode for the Flak durability pipeline:
///
///   RazorReaper.exe --flaktest &lt;region.png&gt; [expected1,expected2,…]
///
/// Loads the PNG (a saved screenshot of the calibrated durability region), runs the exact
/// <see cref="DurabilityReader"/> the Flak script uses, prints one line per detected row to
/// stdout, and — when expectations are given — exits 0 only if every row matches. Wired
/// into Program.Main before any XAML starts, like the ARK watcher.
/// </summary>
internal static class FlakTest
{
    public static bool ShouldRun(string[] args) =>
        args.Any(a => string.Equals(a, "--flaktest", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "--captest", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "--reftest", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// --reftest x,y,w,h out.txt — captures a reference with one sampler, throws it away, and
    /// asks a fresh one whether it still knows the snapshot. That is the whole point of storing
    /// references on disk, and the only way to check it without restarting the app by hand.
    /// </summary>
    private static int RunReferenceTest(string[] args, int index)
    {
        var outPath = index + 2 < args.Length ? args[index + 2] : "reftest.txt";
        var report = new List<string>();
        try
        {
            var parts = (index + 1 < args.Length ? args[index + 1] : "").Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
                || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
            {
                File.WriteAllText(outPath, "usage: --reftest x,y,w,h out.txt");
                return 2;
            }

            const string key = "reftest-region";
            var region = new System.Drawing.Rectangle(x, y, w, h);
            using var factory = LoggerFactory.Create(b => b
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new ListLoggerProvider(report)));

            // First sampler: capture and mask, then drop it entirely.
            var first = new ScreenSampler(factory.CreateLogger<ScreenSampler>());
            first.CaptureReference(key, region);
            var capturedFresh = first.HasReference(key);
            var refined = first.RefineReferenceMask(key, region, out var kept);
            first.Dispose();

            // Second sampler: knows nothing except what is on disk.
            var second = new ScreenSampler(factory.CreateLogger<ScreenSampler>());
            var survived = second.HasReference(key);
            var maskInfo = second.ReferenceMaskInfo(key);
            var matches = second.MatchesReference(key, region, 25.5);
            second.ClearReference(key);
            var cleared = !new ScreenSampler(factory.CreateLogger<ScreenSampler>()).HasReference(key);
            second.Dispose();

            report.Add($"captured={capturedFresh} refined={refined} keptPx={kept}");
            report.Add($"after restart: known={survived} mask={maskInfo.Kept}/{maskInfo.Total} matches={matches}");
            report.Add($"cleared from disk={cleared}");

            var ok = capturedFresh && survived && matches && cleared;
            report.Add(ok ? "PASS" : "FAIL");
            File.WriteAllLines(outPath, report);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            report.Add(ex.ToString());
            try { File.WriteAllLines(outPath, report); } catch { /* headless */ }
            return 2;
        }
    }

    /// <summary>
    /// --captest x,y,w,h out.png — grabs a live screen region through the shipping
    /// <see cref="ScreenSampler"/> and writes it out. This is how the capture path itself is
    /// checked (does duplication see the game at all), separate from the reading pipeline.
    /// </summary>
    private static int RunCaptureTest(string[] args, int index)
    {
        if (index + 2 >= args.Length)
        {
            File.WriteAllText("captest-error.txt", "usage: --captest x,y,w,h out.png");
            return 2;
        }

        var parts = args[index + 1].Split(',', StringSplitOptions.TrimEntries);
        var outPath = args[index + 2];
        if (parts.Length != 4 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)
            || !int.TryParse(parts[2], out var w) || !int.TryParse(parts[3], out var h))
        {
            File.WriteAllText(outPath + ".txt", "bad rect");
            return 2;
        }

        var report = new List<string>();
        using var factory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new ListLoggerProvider(report)));

        var sampler = new ScreenSampler(factory.CreateLogger<ScreenSampler>());
        var capture = sampler.CaptureRegion(new System.Drawing.Rectangle(x, y, w, h));

        if (!capture.IsEmpty)
        {
            using var bmp = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, capture.Width, capture.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var row = 0; row < capture.Height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        capture.Bgra, row * capture.Width * 4, data.Scan0 + row * data.Stride, capture.Width * 4);
                }
            }
            finally { bmp.UnlockBits(data); }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        }

        report.Insert(0, $"captured {capture.Width}x{capture.Height} empty={capture.IsEmpty} -> {outPath}");
        File.WriteAllLines(outPath + ".txt", report);
        return capture.IsEmpty ? 1 : 0;
    }

    public static int Run(string[] args)
    {
        // A WinUI exe has no console to print to, so every line also lands in
        // <region.png>.flaktest.txt next to the input.
        var capIndex = Array.FindIndex(args, a => string.Equals(a, "--captest", StringComparison.OrdinalIgnoreCase));
        if (capIndex >= 0) return RunCaptureTest(args, capIndex);

        var refIndex = Array.FindIndex(args, a => string.Equals(a, "--reftest", StringComparison.OrdinalIgnoreCase));
        if (refIndex >= 0) return RunReferenceTest(args, refIndex);

        var report = new List<string>();
        string? reportPath = null;
        try
        {
            var index = Array.FindIndex(args, a => string.Equals(a, "--flaktest", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length)
            {
                // No console on a WinUI process — the usage line has to land in a file or it
                // goes nowhere and the run looks like a silent hang.
                File.WriteAllText("flaktest-usage.txt", "usage: --flaktest <region.png> [expected1,expected2,...]");
                return 2;
            }

            var path = args[index + 1];
            reportPath = path + ".flaktest.txt";
            var expected = index + 2 < args.Length
                ? args[index + 2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : Array.Empty<string>();

            var capture = LoadPng(path);
            if (capture.IsEmpty)
            {
                File.WriteAllText(reportPath, $"could not load {path}");
                return 2;
            }

            // The sampler is only needed by the screen-capturing overload; the OCR engine
            // and the reader itself work off the in-memory buffer.
            var sampler = new ScreenSampler(NullLogger<ScreenSampler>.Instance);
            var ocr = new ScreenOcr(sampler, NullLogger<ScreenOcr>.Instance);
            using var factory = LoggerFactory.Create(b => b
                .SetMinimumLevel(LogLevel.Debug)
                .AddProvider(new ListLoggerProvider(report)));
            var reader = new DurabilityReader(sampler, ocr, factory.CreateLogger<DurabilityReader>());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bands = reader.ReadAsync(capture).GetAwaiter().GetResult();
            sw.Stop();

            report.Add($"rows={bands.Count} elapsed={sw.ElapsedMilliseconds}ms");
            for (var i = 0; i < bands.Count; i++)
            {
                report.Add($"row{i + 1}: value={(bands[i].Value?.ToString() ?? "?")} glyphs={bands[i].GlyphCount}");
            }

            if (expected.Length == 0)
            {
                File.WriteAllLines(reportPath, report);
                return 0;
            }

            var ok = bands.Count == expected.Length;
            for (var i = 0; ok && i < expected.Length; i++)
            {
                ok = string.Equals(bands[i].Value?.ToString() ?? "?", expected[i], StringComparison.Ordinal);
            }
            report.Add(ok ? "PASS" : $"FAIL (expected {string.Join(",", expected)})");
            File.WriteAllLines(reportPath, report);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(reportPath ?? "flaktest-error.txt", ex.ToString()); } catch { /* headless */ }
            return 2;
        }
    }

    /// <summary>Captures reader debug lines into the report file — this mode has no console.</summary>
    private sealed class ListLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly List<string> _sink;
        public ListLoggerProvider(List<string> sink) => _sink = sink;
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(_sink);
        public void Dispose() { }

        private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly List<string> _sink;
            public ListLogger(List<string> sink) => _sink = sink;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_sink) _sink.Add("  dbg: " + formatter(state, exception));
            }
        }
    }

    private static ScreenCapture LoadPng(string path)
    {
        using var bmp = new Bitmap(path);
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var raw = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);

            var bgra = new byte[bmp.Width * bmp.Height * 4];
            for (var y = 0; y < bmp.Height; y++)
            {
                Buffer.BlockCopy(raw, y * stride, bgra, y * bmp.Width * 4, bmp.Width * 4);
            }
            return new ScreenCapture(bmp.Width, bmp.Height, bgra);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
