using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Media;

public sealed record MediaConvertResult(bool Success, string Message, string? OutputPath);

/// <summary>
/// General-purpose file conversion for the Convert page: any supported source to any
/// compatible target, using the argument matrix in <see cref="FfmpegArgBuilder"/>.
///
/// Deliberately separate from <see cref="IVideoConverter"/>, which exists to put a video
/// into one of ARK's two movie containers with a volume tweak. That one is load-bearing for
/// the Loading Screen page and is left alone.
/// </summary>
public interface IMediaConverter
{
    /// <summary>
    /// Convert one file. The target format decides the output extension; the file lands next
    /// to <paramref name="sourcePath"/> unless <paramref name="outputDirectory"/> says otherwise,
    /// and an existing name is never overwritten — a numbered suffix is used instead.
    /// </summary>
    Task<MediaConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        ConversionOptions options,
        string? outputDirectory,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default);
}

public sealed class MediaConverter : IMediaConverter
{
    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex =
        new(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly ILogger<MediaConverter> _logger;
    private readonly IFfmpegProvider _ffmpeg;

    public MediaConverter(ILogger<MediaConverter> logger, IFfmpegProvider ffmpeg)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
    }

    public async Task<MediaConvertResult> ConvertAsync(
        string sourcePath,
        string targetFormat,
        ConversionOptions options,
        string? outputDirectory,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new MediaConvertResult(false, "That file no longer exists.", null);

        var format = MediaFormats.Normalize(targetFormat);
        if (string.IsNullOrEmpty(format))
            return new MediaConvertResult(false, "Pick a format to convert to.", null);

        var kind = MediaFormats.KindOf(sourcePath);
        if (kind == MediaKind.Unknown)
            return new MediaConvertResult(false, $"{Path.GetExtension(sourcePath)} files aren't supported.", null);

        if (!MediaFormats.IsCompatible(kind, format))
            return new MediaConvertResult(false, $"A {kind.ToString().ToLowerInvariant()} can't be converted to {format}.", null);

        var ffmpegPath = _ffmpeg.FfmpegPath;
        if (!File.Exists(ffmpegPath))
            return new MediaConvertResult(false, "The converter isn't ready yet — give it a moment to set up.", null);

        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(sourcePath)!
            : outputDirectory!;
        var outputPath = ResolveCollision(
            Path.Combine(directory, Path.GetFileNameWithoutExtension(sourcePath) + "." + format));

        try
        {
            Directory.CreateDirectory(directory);

            // Duration is only needed for the target-size bitrate maths; everything else
            // works without it, so a failed probe is not fatal.
            var duration = options.TargetSizeMb is > 0
                ? await ProbeDurationAsync(ffmpegPath, sourcePath, cancellationToken)
                : null;

            var args = FfmpegArgBuilder.Build(sourcePath, outputPath, format, kind, options, duration);

            var stderrTail = new StringBuilder();
            double totalSeconds = duration ?? 0;
            var lastPercent = -1;

            using var process = new Process { StartInfo = BuildStartInfo(ffmpegPath, args) };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                lock (stderrTail)
                {
                    stderrTail.AppendLine(e.Data);
                    if (stderrTail.Length > 4000) stderrTail.Remove(0, stderrTail.Length - 4000);
                }

                if (totalSeconds <= 0 && DurationRegex.Match(e.Data) is { Success: true } dm)
                    totalSeconds = ToSeconds(dm);

                if (totalSeconds > 0 && progress is not null && TimeRegex.Match(e.Data) is { Success: true } tm)
                {
                    var percent = (int)Math.Clamp(ToSeconds(tm) / totalSeconds * 100, 0, 99);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await using (cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not stop ffmpeg on cancel"); }
            }))
            {
                // Waited without the token on purpose: a cancel-triggered Kill still has to
                // finish tearing down so the output handle is released before we delete it.
                await process.WaitForExitAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderrTail) tail = stderrTail.ToString();
                _logger.LogWarning("ffmpeg exited {Code} converting {Source} to {Format}: {Tail}",
                    process.ExitCode, sourcePath, format, tail);
                TryDelete(outputPath);
                return new MediaConvertResult(false, $"Couldn't convert to {format}. The file may be damaged or use an unusual codec.", null);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                TryDelete(outputPath);
                return new MediaConvertResult(false, "The conversion produced an empty file.", null);
            }

            progress?.Report(100);
            return new MediaConvertResult(true, $"Saved as {Path.GetFileName(outputPath)}.", outputPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Converting {Source} to {Format} failed", sourcePath, format);
            TryDelete(outputPath);
            return new MediaConvertResult(false, $"Conversion failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// ArgumentList rather than a single string: paths here come from a file picker and can
    /// contain spaces and quotes, and .NET does the escaping correctly per argument.
    /// </summary>
    private static ProcessStartInfo BuildStartInfo(string ffmpegPath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Reads the clip length off ffmpeg's own banner; no ffprobe needed.</summary>
    private static async Task<double?> ProbeDurationAsync(string ffmpegPath, string source, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(source);

            using var probe = Process.Start(psi);
            if (probe is null) return null;

            // ffmpeg with no output target exits non-zero after printing the stream info,
            // which is exactly the banner the duration lives in.
            var stderr = await probe.StandardError.ReadToEndAsync(ct);
            await probe.WaitForExitAsync(ct);

            var m = DurationRegex.Match(stderr);
            return m.Success ? ToSeconds(m) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Never clobber an existing file — "clip.mp4" becomes "clip (2).mp4".</summary>
    private static string ResolveCollision(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static double ToSeconds(Match m)
        => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
         + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
         + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", path); }
    }
}
