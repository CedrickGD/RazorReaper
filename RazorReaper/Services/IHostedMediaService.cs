namespace RazorReaper.Services;

/// <summary>
/// Resolves hosted media (images/videos served from the RazorReaper media CDN)
/// to locally cached, WebView-servable URLs. Content downloads once via
/// <see cref="IMediaCacheService"/> and streams from disk afterwards.
/// </summary>
public interface IHostedMediaService
{
    /// <summary>
    /// Resolves a hosted media path (e.g. "images/building/wall-3-correct.jpg")
    /// to a local virtual-host URL the WebView can load. Returns null while the
    /// file is unavailable (download failed and nothing cached yet).
    /// </summary>
    Task<string?> GetSrcAsync(string relativePath, CancellationToken ct = default);
}
