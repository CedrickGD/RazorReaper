using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RazorReaper.Services;

namespace RazorReaper.Services.Implementations;

public class UsageGateService : IUsageGateService
{
    private const string ApiBaseUrl = "https://rr-admin-panel.pages.dev";
    private static readonly TimeSpan StatusCacheDuration = TimeSpan.FromSeconds(30);
    // The gate sits directly in front of user actions, so "fail open on network trouble" has
    // to happen fast — the default 100s HttpClient timeout would freeze a feature for minutes.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);

    private readonly HttpClient _httpClient;
    private readonly IHwidService _hwidService;
    private readonly ILicenseService _licenseService;

    private IReadOnlyDictionary<string, FeatureUsage>? _cachedStatus;
    private DateTimeOffset _cachedStatusAt = DateTimeOffset.MinValue;

    public event Action? OnUsageChanged;

    public UsageGateService(HttpClient httpClient, IHwidService hwidService, ILicenseService licenseService)
    {
        _httpClient = httpClient;
        _hwidService = hwidService;
        _licenseService = licenseService;
        // A license flip in either direction changes what the chips should show.
        _licenseService.OnLicenseStateChanged += () => { _cachedStatusAt = DateTimeOffset.MinValue; OnUsageChanged?.Invoke(); };
    }

    public async Task<UsageGateResult> TryConsumeAsync(string feature)
    {
        if (_licenseService.IsPremium)
        {
            return UsageGateResult.UnlimitedResult;
        }

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            var payload = new { hwid = _hwidService.GetHardwareId(), feature };
            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/usage/consume", payload, cts.Token);
            var result = await response.Content.ReadFromJsonAsync<ConsumeResponse>(cancellationToken: cts.Token);

            if (!response.IsSuccessStatusCode || result is null || !result.Ok)
            {
                Debug.WriteLine($"[UsageGate] consume '{feature}' got HTTP {(int)response.StatusCode} — failing open");
                return new UsageGateResult(true, false, null, null);
            }

            if (result.Unlimited)
            {
                return UsageGateResult.UnlimitedResult;
            }

            _cachedStatusAt = DateTimeOffset.MinValue; // chips must re-fetch the new count
            OnUsageChanged?.Invoke();
            return new UsageGateResult(result.Allowed, false, result.Remaining, result.Limit);
        }
        catch (Exception ex)
        {
            // Fail open: a monthly quota is a nudge toward premium, not a kill switch. Offline
            // or flaky-server users keep working; the server never counted this use.
            Debug.WriteLine($"[UsageGate] consume '{feature}' unreachable — failing open ({ex.Message})");
            return new UsageGateResult(true, false, null, null);
        }
    }

    public async Task<IReadOnlyDictionary<string, FeatureUsage>?> GetStatusAsync()
    {
        if (_licenseService.IsPremium)
        {
            return null;
        }

        if (_cachedStatus is not null && DateTimeOffset.UtcNow - _cachedStatusAt < StatusCacheDuration)
        {
            return _cachedStatus;
        }

        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            var hwid = Uri.EscapeDataString(_hwidService.GetHardwareId());
            var result = await _httpClient.GetFromJsonAsync<StatusResponse>($"{ApiBaseUrl}/api/usage/status?hwid={hwid}", cts.Token);
            if (result is null || !result.Ok || result.Unlimited || result.Features is null)
            {
                return null;
            }

            _cachedStatus = result.Features.ToDictionary(kv => kv.Key, kv => new FeatureUsage(kv.Value.Used, kv.Value.Limit));
            _cachedStatusAt = DateTimeOffset.UtcNow;
            return _cachedStatus;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UsageGate] status unreachable ({ex.Message})");
            return null;
        }
    }

    private class ConsumeResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("unlimited")]
        public bool Unlimited { get; set; }

        [JsonPropertyName("allowed")]
        public bool Allowed { get; set; }

        [JsonPropertyName("remaining")]
        public int? Remaining { get; set; }

        [JsonPropertyName("limit")]
        public int? Limit { get; set; }
    }

    private class StatusResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("unlimited")]
        public bool Unlimited { get; set; }

        [JsonPropertyName("features")]
        public Dictionary<string, StatusFeature>? Features { get; set; }
    }

    private class StatusFeature
    {
        [JsonPropertyName("used")]
        public int Used { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }
    }
}
