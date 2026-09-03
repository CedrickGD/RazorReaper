using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RazorReaper.Configuration;
using RazorReaper.Models;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Fetches active announcements from the admin panel's public endpoint for the Home banner.
/// Failures are reported without throwing so the banner can retain its last-known good state.
/// </summary>
public class AnnouncementService : IAnnouncementService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<AppConfiguration> _options;
    private readonly ILogger<AnnouncementService> _logger;

    public AnnouncementService(
        HttpClient httpClient,
        IOptions<AppConfiguration> options,
        ILogger<AnnouncementService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<AnnouncementFetchResult> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var settings = _options.Value.AdminPanel;
        var baseUrl = settings.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return AnnouncementFetchResult.Failure;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/announcements/active");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 3, 60)));

            using var response = await _httpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Announcements fetch returned HTTP {Status}.", (int)response.StatusCode);
                return AnnouncementFetchResult.Failure;
            }

            var payload = await response.Content.ReadFromJsonAsync<AnnouncementsResponse>(cts.Token);
            if (payload is { Ok: true, Announcements: not null })
            {
                return AnnouncementFetchResult.Success(payload.Announcements);
            }

            _logger.LogInformation("Announcements fetch returned an invalid response payload.");
            return AnnouncementFetchResult.Failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline / DNS / timeout: preserve the banner's last-known good state.
            _logger.LogInformation(ex, "Failed to fetch announcements.");
            return AnnouncementFetchResult.Failure;
        }
    }

    private sealed class AnnouncementsResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("announcements")]
        public List<Announcement>? Announcements { get; set; }
    }
}
