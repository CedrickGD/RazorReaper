using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Media;

/// <summary>What a source file turned out to be, for the details line under the preview.</summary>
public sealed record MediaInfo(
    MediaKind Kind,
    TimeSpan? Duration,
    int? Width,
    int? Height,
    string? VideoCodec,
    string? AudioCodec,
    long SizeBytes)
{
    public string SizeLabel => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{SizeBytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{SizeBytes / (double)(1L << 10):0} KB",
        _ => $"{SizeBytes} B",
    };

    public string? DurationLabel => Duration is not { } d
        ? null
        : d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");

    public string? SizeOnScreen => Width is > 0 && Height is > 0 ? $"{Width}×{Height}" : null;
}

/// <summary>
/// Reads a file's shape and renders a thumbnail for it, both through the bundled ffmpeg.
/// Kept apart from <see cref="IMediaConverter"/> because this is what the page shows you
/// before you commit to anything — it never writes next to your file.
/// </summary>
public interface IMediaProbe
{
    Task<MediaInfo?> InspectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// A PNG data URI to drop straight into an img tag: a frame for video, the picture
    /// itself for an image, a waveform for audio. Null when it could not be produced.
    /// </summary>
    Task<string?> ThumbnailAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class MediaProbe : IMediaProbe
{
    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex VideoStreamRegex =
        new(@"Stream #\d+:\d+.*?: Video:\s*([a-zA-Z0-9_]+).*?,\s*(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
    private static readonly Regex AudioStreamRegex =
        new(@"Stream #\d+:\d+.*?: Audio:\s*([a-zA-Z0-9_]+)", RegexOptions.Compiled);

    private readonly ILogger<MediaProbe> _logger;
    private readonly IFfmpegProvider _ffmpeg;

    public MediaProbe(ILogger<MediaProbe> logger, IFfmpegProvider ffmpeg)
    {
        _logger = logger;
        _ffmpeg = ffmpeg;
    }

    public async Task<MediaInfo?> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;

        var kind = MediaFormats.KindOf(path);
        var size = new FileInfo(path).Length;

        var banner = await ReadBannerAsync(path, cancellationToken);
        if (banner is null) return new MediaInfo(kind, null, null, null, null, null, size);

        TimeSpan? duration = DurationRegex.Match(banner) is { Success: true } dm
            ? TimeSpan.FromSeconds(
                double.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
              + double.Parse(dm.Groups[2].Value, CultureInfo.InvariantCulture) * 60
              + double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture))
            : null;

        var vm = VideoStreamRegex.Match(banner);
        var am = AudioStreamRegex.Match(banner);

        return new MediaInfo(
            kind,
            duration,
            vm.Success ? int.Parse(vm.Groups[2].Value, CultureInfo.InvariantCulture) : null,
            vm.Success ? int.Parse(vm.Groups[3].Value, CultureInfo.InvariantCulture) : null,
            vm.Success ? vm.Groups[1].Value : null,
            am.Success ? am.Groups[1].Value : null,
            size);
    }

    public async Task<string?> ThumbnailAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;

        var ffmpegPath = _ffmpeg.FfmpegPath;
        if (!File.Exists(ffmpegPath)) return null;

        var kind = MediaFormats.KindOf(path);
        var temp = Path.Combine(Path.GetTempPath(), $"rr-thumb-{Guid.NewGuid():N}.png");

        try
        {
            var args = new List<string> { "-hide_banner", "-y" };

            if (kind == MediaKind.Video)
            {
                // Seek a little in: the first frame of a phone clip is very often black.
                var info = await InspectAsync(path, cancellationToken);
                var seek = info?.Duration is { } d && d.TotalSeconds > 4 ? d.TotalSeconds * 0.1 : 0;
                if (seek > 0)
                {
                    args.Add("-ss");
                    args.Add(seek.ToString("0.###", CultureInfo.InvariantCulture));
                }
                args.Add("-i"); args.Add(path);
                args.Add("-frames:v"); args.Add("1");
                args.Add("-vf"); args.Add("scale=520:-2:flags=lanczos");
            }
            else if (kind == MediaKind.Image)
            {
                args.Add("-i"); args.Add(path);
                args.Add("-frames:v"); args.Add("1");
                // Only ever shrink — upscaling a small icon to fill the box looks awful.
                args.Add("-vf"); args.Add("scale='min(520,iw)':-2:flags=lanczos");
            }
            else if (kind == MediaKind.Audio)
            {
                // No picture to show, so draw the waveform instead. Reads as "this is audio,
                // and here is roughly what is in it" at a glance.
                args.Add("-i"); args.Add(path);
                args.Add("-filter_complex"); args.Add("showwavespic=s=520x130:colors=#8b5cf6");
                args.Add("-frames:v"); args.Add("1");
            }
            else
            {
                return null;
            }

            args.Add(temp);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return null;

            _ = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0 || !File.Exists(temp)) return null;

            var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
            if (bytes.Length == 0) return null;

            return "data:image/png;base64," + System.Convert.ToBase64String(bytes);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not build a thumbnail for {Path}", path);
            return null;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* temp file */ }
        }
    }

    /// <summary>ffmpeg with no output prints the stream info and exits non-zero; that's the banner.</summary>
    private async Task<string?> ReadBannerAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var ffmpegPath = _ffmpeg.FfmpegPath;
            if (!File.Exists(ffmpegPath)) return null;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return stderr;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect {Path}", path);
            return null;
        }
    }
}
