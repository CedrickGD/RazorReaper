using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Media;

/// <summary>Outcome of a conversion. OutputPath is set only on success.</summary>
public sealed record VideoConvertResult(bool Success, string Message, string? OutputPath);

/// <summary>
/// Transcodes an arbitrary video into one of ARK's movie containers (.mp4 or .wmv) with an
/// optional volume adjustment, using the ffmpeg binary supplied by <see cref="IFfmpegProvider"/>.
/// Reports 0..100 progress by parsing ffmpeg's stderr against the clip's duration.
/// </summary>
public interface IVideoConverter
{
    /// <summary>
    /// Convert <paramref name="sourcePath"/> to <paramref name="outputPath"/> (its extension
    /// decides the container). <paramref name="volumePercent"/> 100 = unchanged, 0 = muted.
    /// </summary>
    Task<VideoConvertResult> ConvertAsync(
        string sourcePath,
        string outputPath,
        int volumePercent,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default);
}

public sealed class VideoConverter : IVideoConverter
{
    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex =
        new(@"time=\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly ILogger<VideoConverter> _logger;
    private readonly IFfmpegProvider _ffmpeg;

    public VideoConverter(ILogger<VideoConverter> logger, IFfmpegProvider ffmpeg)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
    }

    public async Task<VideoConvertResult> ConvertAsync(
        string sourcePath,
        string outputPath,
        int volumePercent,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return new VideoConvertResult(false, "The source video no longer exists.", null);
        }

        var ffmpegPath = _ffmpeg.FfmpegPath;
        if (!File.Exists(ffmpegPath))
        {
            return new VideoConvertResult(false, "ffmpeg is not available. Try again so it can download.", null);
        }

        var targetExt = Path.GetExtension(outputPath).ToLowerInvariant();
        var arguments = BuildArguments(sourcePath, outputPath, targetExt, volumePercent);
        if (arguments is null)
        {
            return new VideoConvertResult(false, $"Unsupported target format '{targetExt}'.", null);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            TryDelete(outputPath);

            var stderrTail = new StringBuilder();
            double totalSeconds = 0;
            var lastPercent = -1;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                lock (stderrTail)
                {
                    stderrTail.AppendLine(e.Data);
                    if (stderrTail.Length > 4000) stderrTail.Remove(0, stderrTail.Length - 4000);
                }

                if (totalSeconds <= 0)
                {
                    var dm = DurationRegex.Match(e.Data);
                    if (dm.Success) totalSeconds = ToSeconds(dm);
                }

                if (totalSeconds > 0 && progress != null)
                {
                    var tm = TimeRegex.Match(e.Data);
                    if (tm.Success)
                    {
                        var current = ToSeconds(tm);
                        var percent = (int)Math.Clamp(current / totalSeconds * 100, 0, 99);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await using (cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to kill ffmpeg on cancellation"); }
            }))
            {
                // Wait without the token so a cancel-triggered Kill still fully tears down and
                // releases the output-file handle before the cleanup below deletes it.
                await process.WaitForExitAsync();
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
            {
                string tail;
                lock (stderrTail) tail = stderrTail.ToString();
                _logger.LogWarning("ffmpeg exited {Code} converting {Source}: {Tail}", process.ExitCode, sourcePath, tail);
                TryDelete(outputPath);
                return new VideoConvertResult(false,
                    "Conversion failed — the source video could not be converted. See the log for details.", null);
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return new VideoConvertResult(false, "Conversion produced no output.", null);
            }

            progress?.Report(100);
            return new VideoConvertResult(true, "Conversion complete.", outputPath);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ffmpeg conversion of {Source} failed", sourcePath);
            TryDelete(outputPath);
            return new VideoConvertResult(false, $"Conversion failed: {ex.Message}", null);
        }
    }

    /// <summary>Builds ffmpeg args for the target container, or null for an unsupported one.</summary>
    private static string? BuildArguments(string source, string output, string targetExt, int volumePercent)
    {
        var volume = Math.Clamp(volumePercent, 0, 400) / 100.0;
        var volumeArg = Math.Abs(volume - 1.0) < 0.001
            ? ""
            : $" -af \"volume={volume.ToString("0.###", CultureInfo.InvariantCulture)}\"";

        // -y overwrite, -map_metadata -1 strips source tags. Video re-encoded to a container ARK plays.
        var codecs = targetExt switch
        {
            ".mp4" => "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart",
            ".wmv" => "-c:v wmv2 -b:v 4M -c:a wmav2 -b:a 192k",
            _ => null,
        };
        if (codecs is null) return null;

        return $"-y -hide_banner -i \"{source}\"{volumeArg} {codecs} -map_metadata -1 \"{output}\"";
    }

    private static double ToSeconds(Match m)
    {
        var h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var min = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var sec = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return h * 3600 + min * 60 + sec;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", path); }
    }
}
