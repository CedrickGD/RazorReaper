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
/// Failures are swallowed (empty list) — a missing/unreachable backend must never break the UI,
/// matching how telemetry treats its own network errors.
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

    public async Task<IReadOnlyList<Announcement>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        var settings = _options.Value.AdminPanel;
        var baseUrl = settings.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Array.Empty<Announcement>();
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
                return Array.Empty<Announcement>();
            }

            var payload = await response.Content.ReadFromJsonAsync<AnnouncementsResponse>(cts.Token);
            if (payload is { Ok: true, Announcements: not null })
            {
                return payload.Announcements;
            }

            return Array.Empty<Announcement>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline / DNS / timeout: banner just stays empty.
            _logger.LogInformation(ex, "Failed to fetch announcements.");
            return Array.Empty<Announcement>();
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
