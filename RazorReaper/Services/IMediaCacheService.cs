namespace RazorReaper.Services;

/// <summary>
/// Downloads remote media (images / videos, e.g. spot previews hosted behind the
/// RazorReaper media proxy) into a local cache under %LOCALAPPDATA%\RazorReaper\MediaCache
/// and surfaces cached files to the Blazor WebView as data: URLs, since wwwroot is not
/// writable at runtime.
/// </summary>
public interface IMediaCacheService
{
    /// <summary>
    /// Returns a data: URL for the remote file, downloading and caching it on first use.
    /// Returns null on any failure (never throws).
    /// </summary>
    Task<string?> GetDataUrlAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Returns the local cached file path for the remote file, downloading it if needed.
    /// Returns null on any failure (never throws).
    /// </summary>
    Task<string?> GetLocalPathAsync(string url, CancellationToken ct = default);

    /// <summary>Total size in bytes of all files in the media cache.</summary>
    Task<long> GetCacheSizeBytesAsync();

    /// <summary>Deletes all files in the media cache.</summary>
    Task ClearCacheAsync();
}
