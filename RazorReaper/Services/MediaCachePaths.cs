namespace RazorReaper.Services;

/// <summary>
/// Shared constants for the hosted-media cache. The WebView maps
/// <see cref="VirtualHost"/> onto <see cref="Directory"/> so cached files
/// (including large videos) stream straight from disk instead of being
/// embedded as base64 data URLs.
/// </summary>
public static class MediaCachePaths
{
    public const string VirtualHost = "rr-media.local";

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RazorReaper",
        "MediaCache");
}
