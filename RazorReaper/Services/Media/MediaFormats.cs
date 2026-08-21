namespace RazorReaper.Services.Media;

/// <summary>What a source file is, which decides the targets it can reach.</summary>
public enum MediaKind
{
    Unknown,
    Video,
    Image,
    Audio,
}

/// <summary>
/// The format catalog, ported from Convert-X (packages/shared/src/core/formats.js). Kept as
/// plain data with no UI or ffmpeg knowledge so the page and the encoder can both read it.
/// </summary>
public static class MediaFormats
{
    public static readonly IReadOnlyList<string> Video =
        new[] { "mp4", "mkv", "avi", "webm", "mov", "flv", "wmv", "ts" };

    public static readonly IReadOnlyList<string> Image =
        new[] { "png", "jpg", "webp", "bmp", "tiff", "ico" };

    public static readonly IReadOnlyList<string> Audio =
        new[] { "mp3", "wav", "flac", "ogg", "aac", "wma", "m4a", "opus" };

    /// <summary>Extensions that mean the same thing as a canonical target above.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = "jpg",
        ["tif"] = "tiff",
        ["m4v"] = "mp4",
    };

    /// <summary>Every extension the picker will accept as a source.</summary>
    public static IReadOnlyList<string> SourceExtensions { get; } =
        Video.Concat(Image).Concat(Audio).Concat(Aliases.Keys)
             .Select(e => "." + e)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
             .ToArray();

    public static string Normalize(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return Aliases.TryGetValue(ext, out var canonical) ? canonical : ext;
    }

    public static MediaKind KindOf(string? pathOrExtension)
    {
        var ext = Normalize(Path.GetExtension(pathOrExtension) is { Length: > 0 } e ? e : pathOrExtension);
        if (Video.Contains(ext)) return MediaKind.Video;
        if (Image.Contains(ext)) return MediaKind.Image;
        if (Audio.Contains(ext)) return MediaKind.Audio;
        return MediaKind.Unknown;
    }

    /// <summary>
    /// Whether a source of this kind can produce that target. Video can also reach the audio
    /// formats — that is a track extraction (-vn), not a conversion mistake. gif is reachable
    /// from video and image both, which is why it is not in any of the three lists.
    /// </summary>
    public static bool IsCompatible(MediaKind kind, string format)
    {
        format = Normalize(format);
        if (format == "gif") return kind is MediaKind.Video or MediaKind.Image;
        return kind switch
        {
            MediaKind.Video => Video.Contains(format) || Audio.Contains(format),
            MediaKind.Image => Image.Contains(format),
            MediaKind.Audio => Audio.Contains(format),
            _ => false,
        };
    }

    /// <summary>Targets worth offering for a given source, in a stable display order.</summary>
    public static IReadOnlyList<string> TargetsFor(MediaKind kind) => kind switch
    {
        MediaKind.Video => Video.Append("gif").Concat(Audio).ToArray(),
        MediaKind.Image => Image.Append("gif").ToArray(),
        MediaKind.Audio => Audio.ToArray(),
        _ => Array.Empty<string>(),
    };
}
