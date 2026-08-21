using Microsoft.Extensions.Logging;
using RazorReaper.Configuration;
using RazorReaper.Services.Implementations;

namespace RazorReaper.Services.Http;

/// <summary>
/// Adds the rr.install.v1 signature headers to every request aimed at an allow-listed backend
/// host, except the registration call itself. Requests stay unsigned (and are still sent) when
/// no key is available, so legacy-tolerant routes keep working.
/// </summary>
public sealed class SignedRequestHandler : DelegatingHandler
{
    private readonly Func<IInstallIdentityService> _identityAccessor;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _allowedHosts;
    private readonly ILogger<SignedRequestHandler> _logger;

    /// <param name="identityAccessor">
    /// Resolved lazily: the identity service depends on services that own an HttpClient carrying
    /// this handler, so taking it eagerly would form a construction cycle.
    /// </param>
    public SignedRequestHandler(
        Func<IInstallIdentityService> identityAccessor,
        TimeProvider timeProvider,
        IEnumerable<string> allowedHosts,
        ILogger<SignedRequestHandler> logger)
    {
        _identityAccessor = identityAccessor ?? throw new ArgumentNullException(nameof(identityAccessor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _allowedHosts = new HashSet<string>(
            (allowedHosts ?? throw new ArgumentNullException(nameof(allowedHosts)))
                .Where(static h => !string.IsNullOrWhiteSpace(h))
                .Select(static h => h.Trim()),
            StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Telemetry endpoint host + admin panel host, as configured.</summary>
    public static IReadOnlyList<string> AllowedHostsFrom(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hosts = new List<string>(2);
        AddHost(hosts, configuration.Telemetry?.Endpoint);
        AddHost(hosts, configuration.AdminPanel?.BaseUrl);
        return hosts;

        static void AddHost(List<string> hosts, string? url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                hosts.Add(uri.Host);
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldSign(request.RequestUri))
        {
            await TrySignAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldSign(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || !_allowedHosts.Contains(uri.Host))
        {
            return false;
        }

        return !string.Equals(uri.AbsolutePath, InstallIdentityService.RegisterPath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task TrySignAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var body = await BufferBodyAsync(request, cancellationToken).ConfigureAwait(false);
            var headers = await _identityAccessor()
                .SignAsync(request.Method, request.RequestUri!, body, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (headers is null)
            {
                return;
            }

            SetHeader(request, SignedRequestHeaders.InstallHeaderName, headers.InstallId);
            SetHeader(request, SignedRequestHeaders.TimestampHeaderName, headers.Timestamp);
            SetHeader(request, SignedRequestHeaders.SignatureHeaderName, headers.Signature);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request signing failed for {Uri}; sending unsigned.", request.RequestUri);
        }
    }

    /// <summary>
    /// Reads the body once so it can be hashed, then swaps in a byte-array copy carrying the same
    /// content headers — non-seekable stream bodies would otherwise be consumed by the hash.
    /// </summary>
    private static async Task<byte[]> BufferBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original = request.Content;
        if (original is null)
        {
            return [];
        }

        var bytes = await original.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var buffered = new ByteArrayContent(bytes);
        foreach (var header in original.Headers)
        {
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = buffered;
        original.Dispose();
        return bytes;
    }

    private static void SetHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
