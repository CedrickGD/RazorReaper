using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

public sealed class MediaCacheService : IMediaCacheService
{
    private const long MaxDownloadBytes = 512L * 1024 * 1024;
    private const long DataUrlWarnBytes = 32L * 1024 * 1024;
    private const string TempExtension = ".download";
    // Downloads are aborted only when they STALL (no bytes for this long) — a
    // fixed total timeout killed large videos that were merely sharing bandwidth.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    private readonly string _cacheDir;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediaCacheService> _logger;

    // One gate per cache key so concurrent requests for the same URL download only once.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadLocks = new(StringComparer.Ordinal);

    // A media-heavy page requests dozens of files at once. Unbounded parallelism
    // makes every download crawl; two lanes keep images snappy while a couple of
    // large videos stream at full speed.
    private readonly SemaphoreSlim _imageLane = new(4, 4);
    private readonly SemaphoreSlim _videoLane = new(2, 2);

    public MediaCacheService(IHttpClientFactory httpClientFactory, ILogger<MediaCacheService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cacheDir = MediaCachePaths.Directory;
        Directory.CreateDirectory(_cacheDir);
        CleanupStaleTempFiles();
    }

    public async Task<string?> GetDataUrlAsync(string url, CancellationToken ct = default)
    {
        var path = await GetLocalPathAsync(url, null, ct);
        if (path is null)
            return null;

        try
        {
            var length = new FileInfo(path).Length;
            if (length > DataUrlWarnBytes)
            {
                _logger.LogWarning(
                    "Cached media {Path} is {Bytes} bytes; embedding it as a data URL may strain the WebView",
                    path, length);
            }

            var bytes = await File.ReadAllBytesAsync(path, ct);
            var mime = MimeFromExtension(Path.GetExtension(path));
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read cached media file {Path}", path);
            return null;
        }
    }

    public async Task<string?> GetLocalPathAsync(string url, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var key = CacheKeyFor(url);

        // Fast path: already cached, no locking or network needed.
        var cached = FindCachedFile(key);
        if (cached is not null)
            return cached;

        var gate = _downloadLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        try
        {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            // Another request may have finished the download while we waited.
            cached = FindCachedFile(key);
            if (cached is not null)
                return cached;

            try
            {
                return await DownloadAsync(uri, key, progress, ct);
            }
            catch (Exception firstAttempt) when (firstAttempt is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One retry covers transient stalls / dropped connections.
                _logger.LogWarning(firstAttempt, "Retrying media download from {Url}", url);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return await DownloadAsync(uri, key, progress, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download media from {Url}", url);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<long> GetCacheSizeBytesAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(_cacheDir))
                    total += new FileInfo(file).Length;
                return total;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compute media cache size");
                return 0L;
            }
        });
    }

    public Task ClearCacheAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_cacheDir))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete cached media file {Path}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear media cache");
            }
        });
    }

    private async Task<string?> DownloadAsync(Uri uri, string key, IProgress<double>? progress, CancellationToken ct)
    {
        var extensionGuess = ExtensionFromUrl(uri);
        var lane = extensionGuess is ".mp4" or ".webm" ? _videoLane : _imageLane;
        await lane.WaitAsync(ct);
        try
        {
            var client = _httpClientFactory.CreateClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Stall watchdog: re-armed after every received chunk, so a slow but
            // progressing download of any size never gets killed.
            timeoutCts.CancelAfter(StallTimeout);
            var linkedCt = timeoutCts.Token;

            var tempPath = Path.Combine(_cacheDir, key + TempExtension);
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linkedCt);
                response.EnsureSuccessStatusCode();

                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength is { } knownLength && knownLength > MaxDownloadBytes)
                {
                    _logger.LogWarning(
                        "Media at {Url} reports {Bytes} bytes, exceeding the {Limit} byte cache limit; skipping",
                        uri, knownLength, MaxDownloadBytes);
                    return null;
                }

                var extension = extensionGuess
                    ?? ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
                var finalPath = Path.Combine(_cacheDir, key + extension);

                long bytesRead = 0;
                var lastReportedPercent = -1;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(linkedCt))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[65536];
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, linkedCt)) > 0)
                    {
                        timeoutCts.CancelAfter(StallTimeout);
                        bytesRead += read;
                        if (bytesRead > MaxDownloadBytes)
                            throw new InvalidOperationException($"Download from {uri} exceeded the {MaxDownloadBytes} byte cache limit.");

                        await fileStream.WriteAsync(buffer.AsMemory(0, read), linkedCt);

                        if (progress is not null && contentLength is { } total && total > 0)
                        {
                            var percent = (int)(bytesRead * 100 / total);
                            if (percent > lastReportedPercent)
                            {
                                lastReportedPercent = percent;
                                progress.Report(Math.Min(1d, (double)bytesRead / total));
                            }
                        }
                    }
                }

                if (bytesRead == 0)
                {
                    _logger.LogWarning("Media at {Url} returned an empty body; not caching", uri);
                    TryDeleteFile(tempPath);
                    return null;
                }

                File.Move(tempPath, finalPath, overwrite: true);
                _logger.LogDebug("Cached media from {Url} at {Path} ({Bytes} bytes)", uri, finalPath, bytesRead);
                return finalPath;
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }
        finally
        {
            lane.Release();
        }
    }

    private string? FindCachedFile(string key)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDir, key + ".*"))
            {
                if (path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (new FileInfo(path).Length > 0)
                    return path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to probe media cache for key {Key}", key);
        }

        return null;
    }

    private void CleanupStaleTempFiles()
    {
        // Partial downloads left behind by a crash or forced shutdown.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDir, "*" + TempExtension))
                TryDeleteFile(file);
        }
        catch
        {
            // Best effort only.
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete media cache file {Path}", path);
        }
    }

    private static string CacheKeyFor(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant()[..40];
    }

    private static string? ExtensionFromUrl(Uri uri)
    {
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (ext.Length is < 2 or > 10)
            return null;

        for (var i = 1; i < ext.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(ext[i]))
                return null;
        }

        return ext.ToLowerInvariant();
    }

    private static string ExtensionFromContentType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        _ => ".bin"
    };

    private static string MimeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream"
    };
}
