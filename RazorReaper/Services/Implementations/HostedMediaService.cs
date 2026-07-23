using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RazorReaper.Services.Implementations;

public sealed class HostedMediaService : IHostedMediaService
{
    private const string DefaultBaseUrl = "https://backend.rr-admin-panel.workers.dev/media/";

    private readonly IMediaCacheService _mediaCache;
    private readonly ILogger<HostedMediaService> _logger;
    private readonly string _baseUrl;

    public HostedMediaService(
        IMediaCacheService mediaCache,
        IConfiguration configuration,
        ILogger<HostedMediaService> logger)
    {
        _mediaCache = mediaCache;
        _logger = logger;
        var configured = configuration["MediaHosting:BaseUrl"]?.Trim();
        _baseUrl = string.IsNullOrEmpty(configured) ? DefaultBaseUrl : configured;
        if (!_baseUrl.EndsWith('/'))
        {
            _baseUrl += "/";
        }
    }

    public async Task<string?> GetSrcAsync(string relativePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var remoteUrl = _baseUrl + string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString));

        var localPath = await _mediaCache.GetLocalPathAsync(remoteUrl, progress, ct);
        if (localPath is null)
        {
            _logger.LogWarning("Hosted media unavailable: {Path}", normalized);
            return null;
        }

        // Cache files are flat (hash-keyed), so the file name alone addresses them
        // under the WebView's virtual-host mapping of the cache directory.
        return $"https://{MediaCachePaths.VirtualHost}/{Path.GetFileName(localPath)}";
    }
}
