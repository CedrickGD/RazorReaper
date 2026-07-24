using Microsoft.Extensions.Logging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Rectangle = System.Drawing.Rectangle;

namespace RazorReaper.Services.Automation;

/// <summary>
/// Reads text/numbers from a screen region for HUD-driven scripts (e.g. an Antidote buff timer or
/// an item count). Built on the OCR engine that ships inside Windows 10+ (<c>Windows.Media.Ocr</c>) —
/// fully offline, no bundled native binaries. The interface hides the engine so it can be swapped.
/// </summary>
public interface IScreenOcr
{
    /// <summary>True when an OCR engine is available on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Recognizes all text inside <paramref name="region"/>. Empty string on failure.</summary>
    Task<string> ReadTextAsync(Rectangle region);

    /// <summary>Reads <paramref name="region"/> and returns the digits found as an integer, else null.</summary>
    Task<int?> ReadNumberAsync(Rectangle region);

    /// <summary>
    /// Reads a duration from <paramref name="region"/> as total seconds. Understands "m:ss", "mm:ss",
    /// "12s" and bare numbers (e.g. an ARK buff timer). Null when nothing parseable is read.
    /// </summary>
    Task<double?> ReadSecondsAsync(Rectangle region);
}

/// <summary>Default <see cref="IScreenOcr"/> backed by <see cref="OcrEngine"/>.</summary>
public sealed class ScreenOcr : IScreenOcr
{
    private readonly IScreenSampler _sampler;
    private readonly ILogger<ScreenOcr> _logger;
    private readonly OcrEngine? _engine;

    public ScreenOcr(IScreenSampler sampler, ILogger<ScreenOcr> logger)
    {
        _sampler = sampler;
        _logger = logger;
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (_engine is null)
                _logger.LogWarning("No OCR language pack available; OCR features disabled.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows OCR engine could not be created; OCR features disabled.");
            _engine = null;
        }
    }

    public bool IsAvailable => _engine is not null;

    public async Task<string> ReadTextAsync(Rectangle region)
    {
        if (_engine is null) return string.Empty;

        var capture = _sampler.CaptureRegion(region);
        if (capture.IsEmpty) return string.Empty;

        try
        {
            using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, capture.Width, capture.Height, BitmapAlphaMode.Ignore);
            bitmap.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(capture.Bgra));
            var result = await _engine.RecognizeAsync(bitmap);
            return result?.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OCR read failed for region {Region}", region);
            return string.Empty;
        }
    }

    public async Task<int?> ReadNumberAsync(Rectangle region)
    {
        var text = await ReadTextAsync(region);
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : null;
    }

    public async Task<double?> ReadSecondsAsync(Rectangle region)
    {
        var text = (await ReadTextAsync(region)).Replace(" ", string.Empty);
        if (string.IsNullOrEmpty(text)) return null;

        // "m:ss" / "mm:ss"
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var minsPart = new string(text[..colon].Where(char.IsDigit).ToArray());
            var secsPart = new string(text[(colon + 1)..].Where(char.IsDigit).ToArray());
            if (int.TryParse(minsPart, out var mins) && int.TryParse(secsPart, out var secs))
                return mins * 60 + secs;
        }

        // bare number, optionally with a trailing "s" or a decimal point
        var numeric = new string(text.Where(c => char.IsDigit(c) || c == '.').ToArray());
        return double.TryParse(numeric, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
