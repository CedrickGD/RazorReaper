using System.Globalization;

namespace RazorReaper.Services.Media;

/// <summary>Encode settings for one conversion. Everything is optional bar the quality.</summary>
public sealed record ConversionOptions
{
    /// <summary>0–100. Drives CRF/qscale for video and the bitrate ladder for audio.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>x264/x265 speed preset. Null means "medium".</summary>
    public string? Preset { get; init; }

    /// <summary>"1920x1080" style, or null to keep the source size.</summary>
    public string? Resolution { get; init; }

    public int? Fps { get; init; }

    /// <summary>Explicit video bitrate such as "4000k". Ignored when TargetSizeMb is set.</summary>
    public string? Bitrate { get; init; }

    /// <summary>Aim for roughly this output size; overrides the quality-derived rate control.</summary>
    public double? TargetSizeMb { get; init; }

    public double? TrimStartSeconds { get; init; }
    public double? TrimEndSeconds { get; init; }

    public bool StripAudio { get; init; }

    /// <summary>Playback rate multiplier (1.0 = unchanged).</summary>
    public double? Speed { get; init; }

    /// <summary>Audio gain multiplier (1.0 = unchanged).</summary>
    public double? Volume { get; init; }

    public int? Rotate { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
}

/// <summary>
/// Builds the ffmpeg argument list for one conversion. Ported from Convert-X
/// (packages/desktop/src-tauri/src/ffmpeg.rs — build_ffmpeg_args and friends) so the two
/// apps produce byte-identical commands for the same settings.
///
/// Pure: it only assembles strings, so it is trivially testable and has no idea where
/// ffmpeg lives or how it is run.
/// </summary>
public static class FfmpegArgBuilder
{
    /// <summary>Audio budget reserved when aiming for a target file size.</summary>
    private const int TargetSizeAudioKbit = 128;

    public static IReadOnlyList<string> Build(
        string inputPath,
        string outputPath,
        string outputFormat,
        MediaKind kind,
        ConversionOptions options,
        double? sourceDurationSeconds = null)
    {
        var format = MediaFormats.Normalize(outputFormat);
        var args = new List<string> { "-y" };

        // A still frame has no timeline, so seek args must never reach an image source —
        // a stale trim against one produces an empty file.
        var allowTrim = kind != MediaKind.Image;

        // -ss goes before -i so the seek is a fast one.
        if (allowTrim && options.TrimStartSeconds is > 0 and var start)
        {
            args.Add("-ss");
            args.Add(Num(start));
        }

        args.Add("-i");
        args.Add(inputPath);

        // -t rather than -to: with -ss ahead of -i, -to would be relative to the output start.
        if (allowTrim && options.TrimEndSeconds is > 0 and var end)
        {
            var from = options.TrimStartSeconds ?? 0;
            var duration = end - from;
            if (duration > 0)
            {
                args.Add("-t");
                args.Add(Num(duration));
            }
        }

        if (format == "gif" && kind is MediaKind.Video or MediaKind.Image)
        {
            BuildGif(args, options);
        }
        else if (MediaFormats.Audio.Contains(format))
        {
            // Decided by the target, not the source: pulling the track out of a video lands
            // here too. The video branch's switch only knows containers, so a video reaching
            // an audio format used to fall straight through it — no -vn, no codec, no
            // bitrate, and the quality slider silently did nothing.
            BuildAudio(args, format, options);
        }
        else
        {
            switch (kind)
            {
                case MediaKind.Video:
                    BuildVideo(args, format, options, sourceDurationSeconds);
                    break;
                case MediaKind.Image:
                    // The ICO muxer hard-refuses anything over 256x256, so an ordinary photo
                    // fails outright without this. Only shrinks, and keeps the aspect ratio —
                    // an image already inside the limit passes through untouched.
                    if (format == "ico")
                    {
                        args.Add("-vf");
                        args.Add("scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease");
                    }
                    else if (!string.IsNullOrWhiteSpace(options.Resolution))
                    {
                        args.Add("-vf");
                        args.Add("scale=" + options.Resolution!.Replace('x', ':'));
                    }
                    break;
            }
        }

        args.Add(outputPath);
        return args;
    }

    // ── Video ────────────────────────────────────────────────────────────────

    private static void BuildVideo(
        List<string> args, string format, ConversionOptions o, double? durationSeconds)
    {
        // Target-size mode replaces the quality knob outright: a fixed -b:v and a
        // quality-derived -crf are conflicting rate controls, so only one set is emitted.
        var sizeKbit = TargetVideoKbit(o, durationSeconds);
        List<string>? sizeRate = sizeKbit is { } k
            ? new List<string>
            {
                "-b:v", $"{k}k",
                "-maxrate", $"{(long)Math.Round(k * 1.45)}k",
                "-bufsize", $"{k * 2}k",
            }
            : null;

        var sizeMode = sizeRate is not null;
        // Size mode budgets a fixed audio bitrate, so state it or the size maths breaks.
        var audioExtra = sizeMode ? new[] { "-b:a", $"{TargetSizeAudioKbit}k" } : Array.Empty<string>();

        // Load-bearing below: a filtered track can never be stream-copied, so mkv has to
        // know that its usual -c:a copy is off the table.
        var audioFiltered = false;

        if (o.StripAudio)
        {
            args.Add("-an");
        }
        else if (BuildAudioFilterChain(o) is { } chain)
        {
            args.Add("-af");
            args.Add(chain);
            audioFiltered = true;
        }

        var filters = BuildEditFilters(o);
        if (!string.IsNullOrWhiteSpace(o.Resolution))
            filters.Add("scale=" + o.Resolution!.Replace('x', ':'));
        if (o.Fps is { } fps)
            filters.Add($"fps={fps}");
        if (SpeedActive(o) is { } speed)
            filters.Add($"setpts=PTS/{Num(speed)}");

        var preset = string.IsNullOrWhiteSpace(o.Preset) ? "medium" : o.Preset!;

        void X264Rate()
        {
            if (sizeRate is not null) args.AddRange(sizeRate);
            else { args.Add("-crf"); args.Add(Crf(o.Quality, 51).ToString(CultureInfo.InvariantCulture)); }
        }

        void QscaleRate()
        {
            if (sizeRate is not null) args.AddRange(sizeRate);
            else { args.Add("-q:v"); args.Add((Crf(o.Quality, 31) + 1).ToString(CultureInfo.InvariantCulture)); }
        }

        switch (format)
        {
            case "mp4":
                args.Add("-c:v"); args.Add("libx264");
                if (sizeRate is not null) args.AddRange(sizeRate);
                else if (!string.IsNullOrWhiteSpace(o.Bitrate)) { args.Add("-b:v"); args.Add(o.Bitrate!); }
                else { args.Add("-crf"); args.Add(Crf(o.Quality, 51).ToString(CultureInfo.InvariantCulture)); }
                args.Add("-preset"); args.Add(preset);
                args.Add("-c:a"); args.Add("aac");
                args.AddRange(audioExtra);
                break;

            case "mkv":
                args.Add("-c:v"); args.Add("libx264");
                X264Rate();
                args.Add("-preset"); args.Add(preset);
                // Stream-copied audio has a size the budget cannot control, so size mode
                // re-encodes it instead of copying — and so does a volume or speed change,
                // which ffmpeg refuses outright next to a copy ("Filtering and streamcopy
                // cannot be used together", exit -22, no output file).
                if (sizeMode || audioFiltered) { args.Add("-c:a"); args.Add("aac"); args.AddRange(audioExtra); }
                else { args.Add("-c:a"); args.Add("copy"); }
                break;

            case "avi":
                args.Add("-c:v"); args.Add("mpeg4");
                QscaleRate();
                args.Add("-c:a"); args.Add("mp3");
                args.AddRange(audioExtra);
                break;

            case "webm":
                args.Add("-c:v"); args.Add("libvpx-vp9");
                if (sizeRate is not null) args.AddRange(sizeRate);
                else
                {
                    args.Add("-crf"); args.Add(Crf(o.Quality, 63).ToString(CultureInfo.InvariantCulture));
                    args.Add("-b:v"); args.Add("0");
                }
                args.Add("-c:a"); args.Add("libopus");
                args.AddRange(audioExtra);
                break;

            case "mov":
                args.Add("-c:v"); args.Add("libx264");
                X264Rate();
                args.Add("-preset"); args.Add(preset);
                args.Add("-c:a"); args.Add("aac");
                args.AddRange(audioExtra);
                break;

            case "flv":
                args.Add("-c:v"); args.Add("libx264");
                X264Rate();
                args.Add("-c:a"); args.Add("aac");
                args.AddRange(audioExtra);
                args.Add("-f"); args.Add("flv");
                break;

            case "wmv":
                args.Add("-c:v"); args.Add("wmv2");
                QscaleRate();
                args.Add("-c:a"); args.Add("wmav2");
                args.AddRange(audioExtra);
                break;

            case "ts":
                args.Add("-c:v"); args.Add("libx264");
                X264Rate();
                args.Add("-c:a"); args.Add("aac");
                args.AddRange(audioExtra);
                args.Add("-f"); args.Add("mpegts");
                break;
        }

        // mp4 set its bitrate above; size mode owns -b:v outright.
        if (!string.IsNullOrWhiteSpace(o.Bitrate) && format != "mp4" && !sizeMode)
        {
            args.Add("-b:v");
            args.Add(o.Bitrate!);
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        // Put the moov atom up front so the QuickTime-family files stream.
        if (format is "mp4" or "mov")
        {
            args.Add("-movflags");
            args.Add("+faststart");
        }
    }

    // ── Audio ────────────────────────────────────────────────────────────────

    private static void BuildAudio(List<string> args, string format, ConversionOptions o)
    {
        var ladder = o.Quality switch
        {
            <= 20 => "64k",
            <= 40 => "96k",
            <= 60 => "128k",
            <= 80 => "192k",
            <= 95 => "256k",
            _ => "320k",
        };
        var br = string.IsNullOrWhiteSpace(o.Bitrate) ? ladder : o.Bitrate!;

        // Drop any video stream — this is an extraction.
        args.Add("-vn");

        if (BuildAudioFilterChain(o) is { } chain)
        {
            args.Add("-af");
            args.Add(chain);
        }

        switch (format)
        {
            case "mp3": args.Add("-c:a"); args.Add("libmp3lame"); args.Add("-b:a"); args.Add(br); break;
            case "wav": args.Add("-c:a"); args.Add("pcm_s16le"); break;
            case "flac":
                args.Add("-c:a"); args.Add("flac");
                args.Add("-compression_level");
                args.Add(Crf(o.Quality, 12).ToString(CultureInfo.InvariantCulture));
                break;
            case "ogg": args.Add("-c:a"); args.Add("libvorbis"); args.Add("-b:a"); args.Add(br); break;
            case "aac": args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add(br); break;
            case "wma": args.Add("-c:a"); args.Add("wmav2"); args.Add("-b:a"); args.Add(br); break;
            case "m4a": args.Add("-c:a"); args.Add("aac"); args.Add("-b:a"); args.Add(br); break;
            case "opus": args.Add("-c:a"); args.Add("libopus"); args.Add("-b:a"); args.Add(br); break;
        }
    }

    // ── GIF ──────────────────────────────────────────────────────────────────

    private static void BuildGif(List<string> args, ConversionOptions o)
    {
        // Two-pass palette in a single graph: generate from the whole clip, then map with
        // it. Without this a GIF is dithered against a generic 256-colour palette and
        // banding is obvious on anything with a gradient.
        var fps = o.Fps ?? 15;
        var colors = Math.Clamp(32 + (int)Math.Round(o.Quality * 2.24), 32, 256);

        var chain = new List<string> { $"fps={fps}" };
        chain.AddRange(BuildEditFilters(o));
        if (!string.IsNullOrWhiteSpace(o.Resolution))
            chain.Add("scale=" + o.Resolution!.Replace('x', ':') + ":flags=lanczos");
        if (SpeedActive(o) is { } speed)
            chain.Add($"setpts=PTS/{Num(speed)}");

        var pre = string.Join(",", chain);
        args.Add("-filter_complex");
        args.Add($"[0:v] {pre},split [a][b];[a] palettegen=max_colors={colors} [p];[b][p] paletteuse=dither=bayer");
        args.Add("-loop");
        args.Add("0");
    }

    // ── Shared filter pieces ─────────────────────────────────────────────────

    private static List<string> BuildEditFilters(ConversionOptions o)
    {
        var filters = new List<string>();

        switch ((o.Rotate ?? 0) % 360)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("transpose=1"); filters.Add("transpose=1"); break;
            case 270: filters.Add("transpose=2"); break;
        }

        if (o.FlipHorizontal) filters.Add("hflip");
        if (o.FlipVertical) filters.Add("vflip");
        return filters;
    }

    private static string? BuildAudioFilterChain(ConversionOptions o)
    {
        var parts = new List<string>();

        if (o.Volume is { } vol && Math.Abs(vol - 1.0) > 0.001)
            parts.Add($"volume={Num(vol)}");

        // atempo only accepts 0.5–2.0, so a bigger change is chained.
        if (SpeedActive(o) is { } speed)
            foreach (var step in SplitAtempo(speed))
                parts.Add($"atempo={Num(step)}");

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static double? SpeedActive(ConversionOptions o)
        => o.Speed is { } s && Math.Abs(s - 1.0) > 0.001 ? s : null;

    private static IEnumerable<double> SplitAtempo(double speed)
    {
        var remaining = speed;
        while (remaining > 2.0) { yield return 2.0; remaining /= 2.0; }
        while (remaining < 0.5) { yield return 0.5; remaining /= 0.5; }
        yield return remaining;
    }

    private static long? TargetVideoKbit(ConversionOptions o, double? durationSeconds)
    {
        if (o.TargetSizeMb is not { } mb || mb <= 0) return null;
        if (durationSeconds is not { } seconds || seconds <= 0) return null;

        var totalKbit = mb * 8192.0;                    // MB -> kilobit
        var videoKbit = totalKbit / seconds - TargetSizeAudioKbit;
        return videoKbit > 1 ? (long)Math.Round(videoKbit) : null;
    }

    /// <summary>Maps 0–100 quality onto a codec's 0..max scale, where lower means better.</summary>
    private static int Crf(int quality, int max)
        => (int)((100 - Math.Clamp(quality, 0, 100)) * max / 100.0);

    /// <summary>ffmpeg only ever parses a dot decimal, whatever the user's locale.</summary>
    private static string Num(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
